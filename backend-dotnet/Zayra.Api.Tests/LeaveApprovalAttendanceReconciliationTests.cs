using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// AN APPROVED LEAVE DAY IS NOT AN ABSENCE.
///
/// <para><c>AttendanceService.ProcessEmployeeDay</c> consults approved leave exactly ONCE — when the
/// day is processed. If no approved leave covers the day at that instant, it writes
/// <c>Status = "Absent"</c> and an <c>AttendancePayrollImpact{"Absence deduction", 480}</c>. Approval
/// arriving afterwards is the other order of events, and nothing reconciled it: leave approval moved
/// the balance, wrote its own payroll impact and notified the employee, and never looked at
/// attendance. The stale charge stayed <c>PendingPayroll</c> and the next run docked a day's basic as
/// loss of pay for a day the employer had approved off.</para>
///
/// <para>The existing coverage — <c>AttendanceBusinessInvariantTests
/// .ApprovedLeave_WithNoPunches_DoesNotCreateAbsenceDeduction</c> — seeds the approved leave FIRST and
/// then processes, so it proves only the one ordering the defect does not occur in. These tests are
/// that test with the two steps swapped, which is the ordering that reaches real tenants: leave
/// submission has no past-date guard, and sick leave is reported after the fact by definition.</para>
/// </summary>
public class LeaveApprovalAttendanceReconciliationTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record Fixture(
        Guid TenantId, Employee Employee, Guid HrUserId, LeaveType LeaveType, DateOnly Day);

    /// <summary>
    /// Seeds an employee whose <paramref name="day"/> has ALREADY been processed as Absent, with the
    /// 480-minute absence charge sitting in <c>PendingPayroll</c> and the legacy projection agreeing.
    /// </summary>
    private static async Task<Fixture> SeedAbsentDayAsync(
        ZayraDbContext db, DateOnly day, bool payrollLocked = false, string impactStatus = "PendingPayroll")
    {
        var tenantId = Guid.NewGuid();
        var hrUserId = Guid.NewGuid();
        var leaveType = new LeaveType { TenantId = tenantId, Code = "SICK", NameEn = "Sick Leave", Category = "Sick", IsActive = true, IsPaid = true };
        var hr = new Employee
        {
            TenantId = tenantId, UserAccountId = hrUserId, EmployeeCode = "HR-1", FullName = "HR Approver",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-5)
        };
        db.LeaveTypes.Add(leaveType);
        db.Employees.Add(hr);
        await db.SaveChangesAsync();

        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = "EMP-1", FullName = "Backdated Sick",
            UserAccountId = Guid.NewGuid(), Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-2)
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            LeaveTypeId = leaveType.Id, LeaveTypeName = leaveType.NameEn, Year = day.Year, Entitled = 30
        });

        var daily = new AttendanceDailyRecord
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
            WorkDate = day, Status = AttendanceStatuses.Absent, TotalWorkedMinutes = 0,
            UndertimeMinutes = 480, IsPayrollLocked = payrollLocked,
        };
        db.AttendanceDailyRecords.Add(daily);
        db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
        {
            TenantId = tenantId, EmployeeId = employee.Id, WorkDate = day,
            ImpactType = AttendanceImpactTypes.AbsenceDeduction, Minutes = 480,
            Status = impactStatus, DailyRecordId = daily.Id,
        });
        db.AttendanceRecords.Add(new AttendanceRecord
        {
            TenantId = tenantId, EmployeeId = employee.Id, WorkDate = day, Status = AttendanceStatuses.Absent,
        });
        await db.SaveChangesAsync();

        return new Fixture(tenantId, employee, hrUserId, leaveType, day);
    }

    private static async Task<LeaveRequest> SubmitAndApproveAsync(ZayraDbContext db, Fixture f)
    {
        var service = await TestApprovalConfig.LeaveServiceAsync(db, f.TenantId, approverType: "HR");
        var submitted = await service.SubmitRequestAsync(f.TenantId, new LeaveRequest
        {
            TenantId = f.TenantId, EmployeeId = f.Employee.Id, LeaveTypeId = f.LeaveType.Id,
            StartDate = f.Day, EndDate = f.Day, DayType = "Full",
        });
        return await service.ApproveRequestAsync(
            f.TenantId, submitted.Id, f.HrUserId, "HR Approver", "backdated sick note received");
    }

    /// <summary>
    /// THE DEFECT, with the money.
    ///
    /// <para>Employee on 9,000.00 basic, KSA default loss-of-pay divisor of 30. One backdated sick day
    /// already processed as Absent leaves a 480-minute "Absence deduction" row. Payroll's LOP bucket
    /// turns that into 480 ÷ 480 = 1.0000 LOP days at 9,000 ÷ 30 = 300.00 a day, so the payslip carries
    /// a 300.00 deduction for a day the employer approved as paid sick leave. The charge must be gone
    /// the moment the approval commits, or the next run takes the money.</para>
    /// </summary>
    [Fact]
    public async Task ApprovingLeaveOverADayAlreadyMarkedAbsent_ClearsTheLossOfPayCharge()
    {
        await using var db = CreateDb();
        var day = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-3));
        var f = await SeedAbsentDayAsync(db, day);

        (await db.AttendancePayrollImpacts.SingleAsync()).Status.Should().Be("PendingPayroll",
            "the fixture must start armed, or this test proves nothing");

        var approved = await SubmitAndApproveAsync(db, f);
        approved.Status.Should().Be("Approved");

        (await db.AttendancePayrollImpacts
            .AnyAsync(x => x.ImpactType == AttendanceImpactTypes.AbsenceDeduction))
            .Should().BeFalse("the day is approved leave, so there is no absence left to charge for");

        var daily = await db.AttendanceDailyRecords.SingleAsync();
        daily.Status.Should().Be(AttendanceStatuses.OnLeave);
        daily.UndertimeMinutes.Should().Be(0, "an approved leave day owes no hours");

        (await db.AttendanceRecords.SingleAsync()).Status.Should().Be(AttendanceStatuses.OnLeave,
            "the legacy projection is what the dashboard reads; leaving it Absent keeps the day " +
            "counted against the employee there");
    }

    /// <summary>
    /// A day a closed payroll run has already charged is LEFT ALONE and flagged. Deleting the impact
    /// would erase the evidence of a deduction the employee has already been paid net of, and would
    /// desynchronise it from the <c>PayrollRunConsumption</c> witness the void path unwinds from.
    /// Recovering that money is a payroll adjustment, not a silent delete.
    /// </summary>
    [Fact]
    public async Task ADayAlreadyChargedByAProcessedRun_IsNotAltered_ButIsFlagged()
    {
        await using var db = CreateDb();
        var day = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-40));
        var f = await SeedAbsentDayAsync(db, day, impactStatus: "Processed");

        await SubmitAndApproveAsync(db, f);

        var impact = await db.AttendancePayrollImpacts.SingleAsync();
        impact.Status.Should().Be("Processed");
        impact.ImpactType.Should().Be(AttendanceImpactTypes.AbsenceDeduction,
            "a settled deduction is unwound through payroll, never by deleting the row beneath it");

        var audit = await db.LeaveAuditLogs.SingleAsync(a => a.Action == "Approved");
        audit.Reason.Should().Contain("[FLAG-PAYROLL]")
            .And.Contain("under-paid", "the approver has to be told money is owed");
    }

    /// <summary>A payroll-locked day is skipped and flagged — approving leave must never start failing
    /// because a period is locked, and must never write into a locked period either.</summary>
    [Fact]
    public async Task APayrollLockedDay_IsSkippedAndFlagged_AndTheApprovalStillSucceeds()
    {
        await using var db = CreateDb();
        var day = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-40));
        var f = await SeedAbsentDayAsync(db, day, payrollLocked: true);

        var approved = await SubmitAndApproveAsync(db, f);
        approved.Status.Should().Be("Approved", "a lock may not refuse the leave decision");

        (await db.AttendanceDailyRecords.SingleAsync()).Status.Should().Be(AttendanceStatuses.Absent);
        (await db.AttendancePayrollImpacts.SingleAsync()).Status.Should().Be("PendingPayroll");

        var audit = await db.LeaveAuditLogs.SingleAsync(a => a.Action == "Approved");
        audit.Reason.Should().Contain("payroll-locked");
    }

    /// <summary>
    /// The reconciliation touches only days the approval actually covers, and only days that were
    /// Absent. A Present day inside the range, and an Absent day outside it, are both left as they are.
    /// </summary>
    [Fact]
    public async Task OnlyAbsentDaysInsideTheApprovedRangeAreTouched()
    {
        await using var db = CreateDb();
        var day = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-3));
        var f = await SeedAbsentDayAsync(db, day);

        var outside = day.AddDays(-5);
        db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
        {
            TenantId = f.TenantId, EmployeeId = f.Employee.Id, WorkDate = outside,
            Status = AttendanceStatuses.Absent, TotalWorkedMinutes = 0,
        });
        db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
        {
            TenantId = f.TenantId, EmployeeId = f.Employee.Id, WorkDate = outside,
            ImpactType = AttendanceImpactTypes.AbsenceDeduction, Minutes = 480, Status = "PendingPayroll",
        });
        db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
        {
            TenantId = f.TenantId, EmployeeId = f.Employee.Id, WorkDate = day,
            ImpactType = AttendanceImpactTypes.LateDeduction, Minutes = 15, Status = "PendingPayroll",
        });
        await db.SaveChangesAsync();

        await SubmitAndApproveAsync(db, f);

        (await db.AttendanceDailyRecords.SingleAsync(d => d.WorkDate == outside)).Status
            .Should().Be(AttendanceStatuses.Absent, "that day is not covered by the approved request");
        (await db.AttendancePayrollImpacts
            .AnyAsync(x => x.WorkDate == outside && x.ImpactType == AttendanceImpactTypes.AbsenceDeduction))
            .Should().BeTrue();
        (await db.AttendancePayrollImpacts
            .AnyAsync(x => x.WorkDate == day && x.ImpactType == AttendanceImpactTypes.LateDeduction))
            .Should().BeTrue("only the absence charge is invalidated by the approval");
    }

    /// <summary>
    /// No attendance to reconcile must be silent. A future-dated request over unprocessed days writes
    /// no attendance rows and adds no note, so the ordinary case is unchanged.
    /// </summary>
    [Fact]
    public async Task NoProcessedAttendanceInRange_ChangesNothingAndAddsNoNote()
    {
        await using var db = CreateDb();
        var absentDay = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-3));
        var f = await SeedAbsentDayAsync(db, absentDay);

        // Approve a FUTURE range instead, which has no attendance rows at all.
        var future = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(10));
        var service = await TestApprovalConfig.LeaveServiceAsync(db, f.TenantId, approverType: "HR");
        var submitted = await service.SubmitRequestAsync(f.TenantId, new LeaveRequest
        {
            TenantId = f.TenantId, EmployeeId = f.Employee.Id, LeaveTypeId = f.LeaveType.Id,
            StartDate = future, EndDate = future, DayType = "Full",
        });
        await service.ApproveRequestAsync(f.TenantId, submitted.Id, f.HrUserId, "HR Approver", "planned");

        (await db.AttendanceDailyRecords.SingleAsync()).Status.Should().Be(AttendanceStatuses.Absent,
            "an unrelated past absence is none of this approval's business");
        (await db.AttendancePayrollImpacts.SingleAsync()).Status.Should().Be("PendingPayroll");

        var audit = await db.LeaveAuditLogs.SingleAsync(a => a.Action == "Approved");
        audit.Reason.Should().NotContain("Attendance reconciled");
    }
}
