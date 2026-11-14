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
/// S1 — cancelling an APPROVED leave directly (HR cancels it; no cancellation request) must re-derive the leave's attendance
/// days exactly as approving a cancellation does: a day that was "On leave" becomes what it now is, with its deduction, and a
/// day payroll already paid is flagged instead of rewritten.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class LeaveDirectCancelAttendancePostgresTests
{
    private readonly PostgresFixture _fx;
    public LeaveDirectCancelAttendancePostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task CancellingAnApprovedLeaveDirectly_RecomputesTheDay_IntoAnAbsenceWithItsDeduction()
    {
        var k = await CreateAsync(_fx.CreateDb);
        var leave = await SeedApprovedLeaveAsync(k);
        await ProcessDayAsync(k);
        await using (var check = k.NewContext())
            (await check.AttendanceDailyRecords.IgnoreQueryFilters().SingleAsync(x => x.TenantId == k.TenantId && x.WorkDate == Day))
                .Status.Should().Be("On leave");

        await using (var db = k.NewContext())
            (await new LeaveService(db, new ApprovalRouter(db)).CancelRequestAsync(k.TenantId, leave.Id, "HR", "plans changed"))
                .Status.Should().Be("Cancelled");

        await using var after = k.NewContext();
        (await after.AttendanceDailyRecords.IgnoreQueryFilters().SingleAsync(x => x.TenantId == k.TenantId && x.WorkDate == Day))
            .Status.Should().Be("Absent", "the leave no longer covers a day with no punch");
        (await k.ImpactsAsync()).Should().ContainSingle(x => x.ImpactType == AttendanceImpactTypes.AbsenceDeduction && x.Status == "PendingPayroll");
    }

    [Fact]
    public async Task CancellingAnApprovedLeaveDirectly_OnADayPayrollPaid_FlagsTheDifference_RaisesNoDeduction()
    {
        var k = await CreateAsync(_fx.CreateDb);
        var leave = await SeedApprovedLeaveAsync(k);
        await ProcessDayAsync(k);
        await k.MarkRunPaidAsync();

        await using (var db = k.NewContext())
            await new LeaveService(db, new ApprovalRouter(db)).CancelRequestAsync(k.TenantId, leave.Id, "HR", "plans changed");

        (await k.ImpactsAsync()).Should().BeEmpty("payroll already paid the day");
        await using var check = k.NewContext();
        (await check.AttendanceExceptions.IgnoreQueryFilters().SingleAsync(x => x.TenantId == k.TenantId
                && x.ExceptionType == AttendanceDerivedArtifacts.PaidAttendanceChanged && !x.IsResolved))
            .Details.Should().Contain("Absence deduction paid 0 min, recomputed 480 min");
    }

    private static async Task<LeaveRequest> SeedApprovedLeaveAsync(AttendancePayrollPostgresKit k)
    {
        await using var db = k.NewContext();
        var leave = new LeaveRequest
        {
            TenantId = k.TenantId, CompanyId = k.Company.Id, EmployeeId = k.Employee.Id, EmployeeName = k.Employee.FullName,
            LeaveTypeId = Guid.NewGuid(), LeaveTypeName = "Annual", StartDate = Day, EndDate = Day, TotalDays = 1m, Status = "Approved",
        };
        db.LeaveRequests.Add(leave);
        await db.SaveChangesAsync();
        return leave;
    }

    private static async Task ProcessDayAsync(AttendancePayrollPostgresKit k)
    {
        await using var db = k.NewContext();
        await AttendanceService.ForRecompute(db).ProcessAsync(k.TenantId, new ProcessAttendanceRequest(Day, Day, k.Employee.Id),
            new RequestContext(null, null, Guid.NewGuid(), k.TenantId), default);
    }
}
