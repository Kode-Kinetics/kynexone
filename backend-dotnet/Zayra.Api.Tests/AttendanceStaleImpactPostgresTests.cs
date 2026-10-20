using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Infrastructure.Attendance;
using static Zayra.Api.Tests.AttendancePayrollPostgresKit;

namespace Zayra.Api.Tests;

/// <summary>
/// ERR_ATTENDANCE_IMPACT_STALE — payroll refuses to deduct pending attendance impacts that no longer match their days,
/// and recomputes exactly those days on request. Covers data written before the work-date fix (no deploy-time rewrite):
/// an Out-only day recorded as a zero-length "complete" day with a 480-minute absence, an In-only day with a late
/// deduction, a migration-imported day (no raw punches), and a Saudi tenant's punches dated by the old UTC fallback.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AttendanceStaleImpactPostgresTests
{
    private readonly PostgresFixture _fx;
    public AttendanceStaleImpactPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task AnOldOutOnlyDayWithAPendingAbsence_BlocksProcess_UntilItIsRecomputed_ThenNoAbsenceIsDeducted()
    {
        var k = await CreateAsync(_fx.CreateDb);
        var outAt = Utc(Day, 17, 0);
        await k.PunchAsync(outAt, "Out");
        // What the old processor wrote: first-in fell back to the Out punch, a zero-length "complete" day, Absent.
        await k.SeedDayAsync(Day, outAt, outAt, "Absent", missingPunch: false, pendingImpacts: (AttendanceImpactTypes.AbsenceDeduction, 480));

        await using (var db = k.NewContext())
        {
            var refused = (await k.Payroll(db).Process(k.Run.Id, default)).Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
            Prop<string>(refused.Value!, "code").Should().Be(AttendanceImpactReconciliation.StaleCode);
            Prop<int>(refused.Value!, "dayCount").Should().Be(1);
            Prop<int>(refused.Value!, "employeeCount").Should().Be(1);
        }
        await using (var db = k.NewContext())
            (await db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == k.Run.Id)).Status.Should().Be("Draft", "nothing was saved");

        await using (var db = k.NewContext())
        {
            var done = (await k.Payroll(db).RecomputeStaleAttendance(k.Run.Id, default)).Should().BeOfType<OkObjectResult>().Subject;
            Prop<int>(done.Value!, "recomputed").Should().Be(1);
            Prop<int>(done.Value!, "remaining").Should().Be(0);
        }
        (await k.ImpactsAsync()).Should().BeEmpty("an Out-only day is a missing punch: no absence, and its early exit is held");

        await using (var db = k.NewContext())
            (await k.Payroll(db).Process(k.Run.Id, default)).Should().BeOfType<OkObjectResult>();
        await using (var db = k.NewContext())
            (await db.PayrollDeductions.AsNoTracking().AnyAsync(d => d.PayrollRunId == k.Run.Id && d.Source == "Attendance"))
                .Should().BeFalse("the run deducts nothing for a day the employee attended");
    }

    [Fact]
    public async Task AnInOnlyDayWithAPendingLateDeduction_IsStale_AndTheRecomputeHoldsIt()
    {
        var k = await CreateAsync(_fx.CreateDb);
        var inAt = Utc(Day, 9, 40);
        await k.PunchAsync(inAt, "In");
        await k.SeedDayAsync(Day, inAt, null, "Present", missingPunch: true, lateMinutes: 30,
            pendingImpacts: (AttendanceImpactTypes.LateDeduction, 30));

        await using (var db = k.NewContext())
        {
            var found = (await k.Payroll(db).AttendanceReconciliation(k.Run.Id, default)).Should().BeOfType<OkObjectResult>().Subject.Value!;
            var stale = Prop<object>(found, "stale");
            Prop<int>(stale, "dayCount").Should().Be(1);
        }
        await using (var db = k.NewContext())
            (await k.Payroll(db).RecomputeStaleAttendance(k.Run.Id, default)).Should().BeOfType<OkObjectResult>();

        (await k.ImpactsAsync()).Should().BeEmpty();
        await using var check = k.NewContext();
        var day = await check.AttendanceDailyRecords.IgnoreQueryFilters().SingleAsync(x => x.TenantId == k.TenantId);
        day.LateMinutes.Should().Be(30, "the minutes stay on the record, held");
        day.FirstInUtc.Should().Be(inAt);
    }

    [Fact]
    public async Task AnImportedDayWithNoRawPunches_KeepsItsRecord_OnlyItsDeductionsAreReDerived()
    {
        var k = await CreateAsync(_fx.CreateDb);
        var inAt = Utc(Day, 9, 40);
        // A migration import: a half-pair day with a late deduction, and no raw punch at all.
        await k.SeedDayAsync(Day, inAt, null, "Present", missingPunch: true, lateMinutes: 30,
            pendingImpacts: (AttendanceImpactTypes.LateDeduction, 30));

        await using (var db = k.NewContext())
            (await k.Payroll(db).RecomputeStaleAttendance(k.Run.Id, default)).Should().BeOfType<OkObjectResult>()
                .Which.Value.Should().Match(v => Prop<int>(v!, "recomputed") == 1);

        (await k.ImpactsAsync()).Should().BeEmpty("the held late deduction is not raised");
        await using var check = k.NewContext();
        var day = await check.AttendanceDailyRecords.IgnoreQueryFilters().SingleAsync(x => x.TenantId == k.TenantId);
        day.FirstInUtc.Should().Be(inAt, "rebuilding from punches would have wiped an imported day");
        day.Status.Should().Be("Present");
    }

    [Fact]
    public async Task ACleanMonth_IsNotStale_AndProcesses()
    {
        var k = await CreateAsync(_fx.CreateDb);
        await k.PunchAsync(Utc(Day, 9, 30), "In");
        await k.PunchAsync(Utc(Day, 18, 0), "Out");
        await using (var db = k.NewContext())
            await AttendanceService.ForRecompute(db).ProcessAsync(k.TenantId, new ProcessAttendanceRequest(Day, Day, k.Employee.Id),
                new Zayra.Api.Application.Auth.RequestContext(null, null, Guid.NewGuid(), k.TenantId), default);
        (await k.ImpactsAsync()).Should().ContainSingle(x => x.ImpactType == AttendanceImpactTypes.LateDeduction && x.Minutes == 20);

        await using (var db = k.NewContext())
            (await AttendanceImpactReconciliation.FindStaleDaysAsync(db, k.TenantId, [k.Employee.Id], Day.AddDays(-17), Day.AddDays(13), k.Run.Id, default))
                .Should().BeEmpty("what the processor writes today is never stale");
        await using (var db = k.NewContext())
            (await k.Payroll(db).Process(k.Run.Id, default)).Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData("America/New_York", 405)]
    [InlineData("UTC", 165)]
    public async Task ASaudiTenantsDayTimedByAStoredNonRiyadhZone_HasItsFalseEarlyExitCaught_AndTheRecomputeClearsIt(string storedZone, int falseEarlyExit)
    {
        // 09:00–18:00 in Riyadh is 06:00–15:00 UTC: one date in every zone, so no punch moves. But the shift's end was
        // placed in the stored zone (18:00 New York = 22:00 UTC; 18:00 UTC), so the old processor recorded an early exit
        // the employee never made, and raised a deduction for it. The stored minutes must be checked, not trusted.
        var k = await CreateAsync(_fx.CreateDb, storedZone);
        var inAt = Utc(Day, 6, 0);
        var outAt = Utc(Day, 15, 0);
        await k.PunchAsync(inAt, "In");
        await k.PunchAsync(outAt, "Out");
        await k.SeedDayAsync(Day, inAt, outAt, "Present", missingPunch: false, earlyExitMinutes: falseEarlyExit,
            pendingImpacts: (AttendanceImpactTypes.EarlyExitDeduction, falseEarlyExit));

        await using (var db = k.NewContext())
            (await AttendanceImpactReconciliation.FindStaleDaysAsync(db, k.TenantId, [k.Employee.Id],
                    new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), k.Run.Id, default))
                .Should().ContainSingle().Which.Should().Be(
                    new StaleAttendanceDay(k.Employee.Id, Day, AttendanceImpactReconciliation.MinutesDiffer, FromPunches: true));

        await using (var db = k.NewContext())
            (await k.Payroll(db).RecomputeStaleAttendance(k.Run.Id, default)).Should().BeOfType<OkObjectResult>()
                .Which.Value.Should().Match(v => Prop<int>(v!, "remaining") == 0);
        (await k.ImpactsAsync()).Should().BeEmpty("09:00–18:00 in Riyadh is neither late nor early");
        await using var check = k.NewContext();
        (await check.AttendanceDailyRecords.IgnoreQueryFilters().SingleAsync(x => x.TenantId == k.TenantId)).EarlyExitMinutes.Should().Be(0);
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("America/New_York")]
    public async Task ASaudiTenantsDaysDatedByAStoredNonRiyadhZone_AreCaughtInTheOpenMonth(string storedZone)
    {
        // A night at 22:00–02:00 Riyadh is 19:00–23:00 UTC: one UTC date. Before attendance followed TenantClock, a Saudi
        // tenant whose stored zone was UTC had it recorded as one complete day; under Riyadh time (no shift rostered) the
        // In is on the 18th and the Out on the 19th. Both days must be caught before payroll deducts the old figures.
        var k = await CreateAsync(_fx.CreateDb, storedZone);
        var inAt = Utc(Day, 19, 0);
        var outAt = Utc(Day, 23, 0);
        await k.PunchAsync(inAt, "In");
        await k.PunchAsync(outAt, "Out");
        await k.SeedDayAsync(Day, inAt, outAt, "Late", missingPunch: false, lateMinutes: 590,
            pendingImpacts: (AttendanceImpactTypes.LateDeduction, 590));

        await using var db = k.NewContext();
        var stale = await AttendanceImpactReconciliation.FindStaleDaysAsync(db, k.TenantId, [k.Employee.Id],
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), k.Run.Id, default);
        stale.Select(d => d.WorkDate).Should().BeEquivalentTo([Day, Day.AddDays(1)]);
        stale.Should().OnlyContain(d => d.FromPunches);
    }
}
