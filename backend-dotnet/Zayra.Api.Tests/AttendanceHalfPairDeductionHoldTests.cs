using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using static Zayra.Api.Tests.AttendancePaidImpactPreservationTests;

namespace Zayra.Api.Tests;

/// <summary>
/// On a missing-punch (half-pair) day the late-arrival and early-exit deductions are HELD, not raised, until the pair is
/// complete. Pay is never deducted on a guessed value: an In-only day's "early exit" is unknown, an Out-only day's late
/// arrival is unknown, and a reversed pair proves neither. The minutes stay on the daily record for information and the
/// High missing-punch exception names them; completing the pair (the Out arriving, or an approved correction) releases
/// them exactly once.
/// </summary>
public class AttendanceHalfPairDeductionHoldTests
{
    // Default policy, UTC tenant, Day = Tue 2026-08-18: shift 09:00–18:00, 10 min grace, 15 min early-exit threshold.

    [Fact]
    public async Task AnInOnlyDay_WithALateIn_RaisesNoLateDeduction_AndTheMissingPunchExceptionNamesTheHeldMinutes()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        AddPunch(db, tenantId, employee.Id, "In", 9, 40); // 30 late minutes, if the day turns out to be a day
        await db.SaveChangesAsync();

        await ProcessAsync(db, tenantId, employee.Id);
        await ProcessAsync(db, tenantId, employee.Id); // a re-run holds the same way

        var daily = await db.AttendanceDailyRecords.SingleAsync();
        daily.MissingPunch.Should().BeTrue();
        daily.LateMinutes.Should().Be(30, "the computed minutes are kept on the record for information");
        (await db.AttendancePayrollImpacts.AnyAsync(x => x.ImpactType == AttendanceImpactTypes.LateDeduction))
            .Should().BeFalse("a late deduction on a day whose hours are unknown is a guess");
        (await db.AttendancePayrollImpacts.AnyAsync()).Should().BeFalse();
        var exception = (await db.AttendanceExceptions.Where(x => x.ExceptionType == "MissingPunch" && !x.IsResolved).ToListAsync())
            .Should().ContainSingle().Subject;
        exception.Severity.Should().Be("High");
        exception.Details.Should().Contain("30 late minutes").And.Contain("Held, not deducted");
    }

    [Fact]
    public async Task AnOutOnlyDay_WithAnEarlyOut_RaisesNoEarlyExitDeduction()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        AddPunch(db, tenantId, employee.Id, "Out", 17, 0); // 60 min early, 15 threshold: 45 early-exit minutes
        await db.SaveChangesAsync();

        await ProcessAsync(db, tenantId, employee.Id);

        var daily = await db.AttendanceDailyRecords.SingleAsync();
        daily.MissingPunch.Should().BeTrue();
        daily.EarlyExitMinutes.Should().Be(45);
        (await db.AttendancePayrollImpacts.AnyAsync()).Should().BeFalse("no early-exit, late or absence deduction on a half-pair day");
        (await db.AttendanceExceptions.SingleAsync(x => x.ExceptionType == "MissingPunch" && !x.IsResolved))
            .Details.Should().Contain("45 early-exit minutes");
    }

    [Fact]
    public async Task AReversedPair_HoldsBothLateAndEarlyExit()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        AddPunch(db, tenantId, employee.Id, "Out", 9, 30);
        AddPunch(db, tenantId, employee.Id, "In", 9, 45);
        await db.SaveChangesAsync();

        await ProcessAsync(db, tenantId, employee.Id);

        (await db.AttendanceDailyRecords.SingleAsync()).MissingPunch.Should().BeTrue();
        (await db.AttendancePayrollImpacts.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ACorrectionThatCompletesThePair_RaisesTheHeldDeductionsOnce_AndClosesTheMissingPunch()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        AddPunch(db, tenantId, employee.Id, "In", 9, 40); // 30 late minutes
        await db.SaveChangesAsync();
        await ProcessAsync(db, tenantId, employee.Id);
        (await db.AttendancePayrollImpacts.AnyAsync()).Should().BeFalse("premise: held");

        // The employee forgot to clock out at 17:00 (45 early-exit minutes); HR approves the correction.
        var service = Service(db);
        var requester = new RequestContext(null, null, Guid.NewGuid(), tenantId);
        var reg = await service.CreateRegularizationAsync(tenantId,
            new RegularizationRequestDto(employee.Id, Day, "Missed punch", null, Utc(17, 0), "Forgot to clock out"), requester, default);
        await service.ApproveRegularizationAsync(tenantId, reg.Id, new RegularizationDecisionRequest("ok"),
            new RequestContext(null, null, Guid.NewGuid(), tenantId), default);
        await Service(db).ApproveRegularizationAsync(tenantId, reg.Id, new RegularizationDecisionRequest("ok"),
            new RequestContext(null, null, Guid.NewGuid(), tenantId), default);

        await ProcessAsync(db, tenantId, employee.Id); // a re-run raises nothing new
        await ProcessAsync(db, tenantId, employee.Id);

        var impacts = await db.AttendancePayrollImpacts.ToListAsync();
        impacts.Should().HaveCount(2);
        impacts.Should().ContainSingle(x => x.ImpactType == AttendanceImpactTypes.LateDeduction && x.Minutes == 30);
        impacts.Should().ContainSingle(x => x.ImpactType == AttendanceImpactTypes.EarlyExitDeduction && x.Minutes == 45);
        (await db.AttendanceDailyRecords.SingleAsync()).MissingPunch.Should().BeFalse();
        (await db.AttendanceExceptions.AnyAsync(x => x.ExceptionType == "MissingPunch" && !x.IsResolved))
            .Should().BeFalse("the missing punch was corrected; an open flag would ask HR to resolve it twice");
    }

    [Fact]
    public async Task TheOutPunchArrivingLater_ReleasesTheHold_LikeACorrection()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        AddPunch(db, tenantId, employee.Id, "In", 9, 40);
        await db.SaveChangesAsync();
        await ProcessAsync(db, tenantId, employee.Id);

        AddPunch(db, tenantId, employee.Id, "Out", 18, 0);
        await db.SaveChangesAsync();
        await ProcessAsync(db, tenantId, employee.Id);

        (await db.AttendancePayrollImpacts.SingleAsync()).Should().Match<Zayra.Api.Models.AttendancePayrollImpact>(
            x => x.ImpactType == AttendanceImpactTypes.LateDeduction && x.Minutes == 30);
        (await db.AttendanceExceptions.AnyAsync(x => x.ExceptionType == "MissingPunch" && !x.IsResolved)).Should().BeFalse();
    }

    [Fact]
    public async Task APaidHalfPairDay_IsLeftAsPaid_NoDeductionRaised_NoPaidChangeFlagForTheHeldMinutes()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        AddPunch(db, tenantId, employee.Id, "In", 9, 40);
        await db.SaveChangesAsync();
        await ProcessAsync(db, tenantId, employee.Id);
        await AddPayrollRunCoveringAsync(db, tenantId, employee.Id, "Processed");

        await ProcessAsync(db, tenantId, employee.Id);
        await ProcessAsync(db, tenantId, employee.Id);

        (await db.AttendancePayrollImpacts.AnyAsync()).Should().BeFalse();
        (await db.AttendanceExceptions.AnyAsync(x => x.ExceptionType == AttendanceDerivedArtifacts.PaidAttendanceChanged))
            .Should().BeFalse("held minutes are undetermined, not a difference from what payroll paid");

        // The pair is completed after payroll: still no new deduction, and the difference is flagged for an adjustment.
        AddPunch(db, tenantId, employee.Id, "Out", 18, 0);
        await db.SaveChangesAsync();
        await ProcessAsync(db, tenantId, employee.Id);

        (await db.AttendancePayrollImpacts.AnyAsync()).Should().BeFalse("a paid day gets no new pending deduction");
        (await db.AttendanceExceptions.SingleAsync(x => x.ExceptionType == AttendanceDerivedArtifacts.PaidAttendanceChanged && !x.IsResolved))
            .Details.Should().Contain("Late deduction paid 0 min, recomputed 30 min");
    }

    [Fact]
    public async Task APaidDeduction_OnADayThatBecomesHalfAPair_StaysAsPaid_AndIsNotFlaggedAsADifference()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        AddPunch(db, tenantId, employee.Id, "In", 9, 30); // 20 late minutes
        var punchOut = AddPunch(db, tenantId, employee.Id, "Out", 18, 0);
        await db.SaveChangesAsync();
        await ProcessAsync(db, tenantId, employee.Id);
        var paid = await db.AttendancePayrollImpacts.SingleAsync();
        await MarkProcessedAsPayrollDoes(db, paid);
        await AddPayrollRunCoveringAsync(db, tenantId, employee.Id, "Processed");

        // The Out punch turns out to be another employee's and is removed: the day is now half a pair.
        db.AttendanceRawEvents.Remove((await db.AttendanceRawEvents.FindAsync(punchOut.Id))!);
        await db.SaveChangesAsync();
        await ProcessAsync(db, tenantId, employee.Id);

        var impact = await db.AttendancePayrollImpacts.SingleAsync();
        impact.Id.Should().Be(paid.Id);
        impact.Status.Should().Be("Processed");
        impact.Minutes.Should().Be(20);
        (await db.AttendanceExceptions.AnyAsync(x => x.ExceptionType == AttendanceDerivedArtifacts.PaidAttendanceChanged)).Should().BeFalse();
        (await db.AttendanceExceptions.AnyAsync(x => x.ExceptionType == "MissingPunch" && !x.IsResolved)).Should().BeTrue();
    }

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
