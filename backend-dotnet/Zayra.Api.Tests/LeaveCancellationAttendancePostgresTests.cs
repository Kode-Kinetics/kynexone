using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;
using static Zayra.Api.Tests.AttendancePayrollPostgresKit;

namespace Zayra.Api.Tests;

/// <summary>
/// Leave state reaches attendance. A leave awaiting a decision on its cancellation is still leave (one shared rule,
/// LeaveTakenStatuses), and once a cancellation commits, the leave's settled days are recomputed: an unpaid day becomes
/// Absent with its deduction, a paid day is flagged for a payroll adjustment instead.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class LeaveCancellationAttendancePostgresTests
{
    private readonly PostgresFixture _fx;
    public LeaveCancellationAttendancePostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task ALeaveAwaitingACancellationDecision_StillKeepsTheDayOnLeave()
    {
        var k = await CreateAsync(_fx.CreateDb);
        await SeedLeaveAsync(k, "CancellationRequested");
        await ProcessDayAsync(k);

        await using var check = k.NewContext();
        (await check.AttendanceDailyRecords.IgnoreQueryFilters().SingleAsync(x => x.TenantId == k.TenantId && x.WorkDate == Day))
            .Status.Should().Be("On leave", "the leave stands until its cancellation is approved");
        (await k.ImpactsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ApprovingTheCancellation_RecomputesTheLeaveDay_IntoAnAbsenceWithItsDeduction()
    {
        var k = await CreateAsync(_fx.CreateDb);
        var leave = await SeedLeaveAsync(k, "Approved");
        await ProcessDayAsync(k);
        await RequestAndApproveCancellationAsync(k, leave);

        await using var check = k.NewContext();
        (await check.AttendanceDailyRecords.IgnoreQueryFilters().SingleAsync(x => x.TenantId == k.TenantId && x.WorkDate == Day))
            .Status.Should().Be("Absent", "the leave no longer covers a day with no punch");
        (await k.ImpactsAsync()).Should().ContainSingle(x => x.ImpactType == AttendanceImpactTypes.AbsenceDeduction && x.Status == "PendingPayroll");
    }

    [Fact]
    public async Task CancellingALeaveOnAPaidDay_FlagsTheDifference_RaisesNoDeduction()
    {
        var k = await CreateAsync(_fx.CreateDb);
        var leave = await SeedLeaveAsync(k, "Approved");
        await ProcessDayAsync(k);
        await using (var db = k.NewContext())
            await AttendancePaidImpactPreservationTests.AddPayrollRunCoveringAsync(db, k.TenantId, k.Employee.Id, "Processed");
        await RequestAndApproveCancellationAsync(k, leave);

        (await k.ImpactsAsync()).Should().BeEmpty("payroll already paid the day; an off-cycle run would deduct a new impact");
        await using var check = k.NewContext();
        (await check.AttendanceExceptions.IgnoreQueryFilters().SingleAsync(x => x.TenantId == k.TenantId
                && x.ExceptionType == AttendanceDerivedArtifacts.PaidAttendanceChanged && !x.IsResolved))
            .Details.Should().Contain("Absence deduction paid 0 min, recomputed 480 min");
    }

    private static async Task<LeaveRequest> SeedLeaveAsync(AttendancePayrollPostgresKit k, string status)
    {
        await using var db = k.NewContext();
        var leave = new LeaveRequest
        {
            TenantId = k.TenantId, CompanyId = k.Company.Id, EmployeeId = k.Employee.Id, EmployeeName = k.Employee.FullName,
            LeaveTypeId = Guid.NewGuid(), LeaveTypeName = "Annual", StartDate = Day, EndDate = Day, TotalDays = 1m, Status = status,
        };
        db.LeaveRequests.Add(leave);
        await db.SaveChangesAsync();
        return leave;
    }

    private static async Task RequestAndApproveCancellationAsync(AttendancePayrollPostgresKit k, LeaveRequest leave)
    {
        await using (var db = k.NewContext())
        {
            var row = await db.LeaveRequests.IgnoreQueryFilters().SingleAsync(x => x.Id == leave.Id);
            row.Status = "CancellationRequested";
            db.LeaveCancellationRequests.Add(new LeaveCancellationRequest
            {
                TenantId = k.TenantId, LeaveRequestId = leave.Id, EmployeeId = k.Employee.Id, Reason = "plans changed", Status = "Pending",
            });
            await db.SaveChangesAsync();
        }
        await using (var db = k.NewContext())
            (await new LeaveService(db, new ApprovalRouter(db)).ApproveCancellationAsync(k.TenantId, leave.Id, "HR", "ok"))
                .Status.Should().Be("Cancelled");
    }

    private static async Task ProcessDayAsync(AttendancePayrollPostgresKit k)
    {
        await using var db = k.NewContext();
        await AttendanceService.ForRecompute(db).ProcessAsync(k.TenantId, new ProcessAttendanceRequest(Day, Day, k.Employee.Id),
            new RequestContext(null, null, Guid.NewGuid(), k.TenantId), default);
        (await db.AttendanceDailyRecords.IgnoreQueryFilters().AnyAsync(x => x.TenantId == k.TenantId && x.WorkDate == Day)).Should().BeTrue();
    }
}
