using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Reprocessing attendance must never delete, replace or duplicate an impact payroll has already consumed.
///
/// <para>The day's impacts used to be replaced wholesale whatever their Status. Nothing writes
/// <c>AttendanceLockPeriods</c>, so <c>IsLocked</c> is always false and any HR process run over a paid period deleted
/// the <c>Processed</c> impacts and raised fresh <c>PendingPayroll</c> ones — which an off-cycle run would deduct again,
/// and which the void/reopen restore (keyed on the consumed impact's id) could no longer find.</para>
/// </summary>
public class AttendancePaidImpactPreservationTests
{
    internal static readonly DateOnly Day = new(2026, 8, 18); // a Tuesday; UTC tenant, default policy, 09:00–18:00

    [Fact]
    public async Task ReprocessingAPaidDay_LeavesItsProcessedImpactsUntouched_RaisesNoNewDeduction_AndFlagsTheDifference()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        var punchIn = AddPunch(db, tenantId, employee.Id, "In", 9, 30);   // 30 min late, 10 grace: 20 late minutes
        var punchOut = AddPunch(db, tenantId, employee.Id, "Out", 18, 0);
        await db.SaveChangesAsync();
        await ProcessAsync(db, tenantId, employee.Id);
        var paid = await db.AttendancePayrollImpacts.SingleAsync();
        paid.ImpactType.Should().Be(AttendanceImpactTypes.LateDeduction);
        paid.Minutes.Should().Be(20);
        await MarkProcessedAsPayrollDoes(db, paid);

        // The punches change after the period was paid: later in, earlier out.
        await MovePunchAsync(db, punchIn, 9, 50);   // 40 late minutes
        await MovePunchAsync(db, punchOut, 17, 0);  // 60 min early, 15 threshold: 45 early-exit minutes
        await ProcessAsync(db, tenantId, employee.Id);
        await ProcessAsync(db, tenantId, employee.Id); // and again: still one flag, nothing duplicated

        var impacts = await db.AttendancePayrollImpacts.ToListAsync();
        impacts.Should().ContainSingle("a paid day gets no new pending deduction an off-cycle run could take again");
        impacts[0].Id.Should().Be(paid.Id, "the consumed impact is never deleted and replaced");
        impacts[0].Status.Should().Be("Processed");
        impacts[0].Minutes.Should().Be(20, "what payroll paid is history");

        var flag = (await db.AttendanceExceptions.Where(x => x.ExceptionType == AttendanceDerivedArtifacts.PaidAttendanceChanged).ToListAsync())
            .Should().ContainSingle().Subject;
        flag.IsResolved.Should().BeFalse();
        flag.Severity.Should().Be("High");
        flag.Details.Should().Contain("Late deduction paid 20 min, recomputed 40 min")
            .And.Contain("Early exit deduction paid 0 min, recomputed 45 min")
            .And.Contain("payroll adjustment");
    }

    [Fact]
    public async Task ReprocessingAnUnpaidDay_StillReplacesItsPendingImpacts()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        var punchIn = AddPunch(db, tenantId, employee.Id, "In", 9, 30);
        AddPunch(db, tenantId, employee.Id, "Out", 18, 0);
        await db.SaveChangesAsync();
        await ProcessAsync(db, tenantId, employee.Id);
        var first = await db.AttendancePayrollImpacts.SingleAsync();

        await MovePunchAsync(db, punchIn, 9, 50);
        await ProcessAsync(db, tenantId, employee.Id);

        var impacts = await db.AttendancePayrollImpacts.ToListAsync();
        impacts.Should().ContainSingle();
        impacts[0].Id.Should().NotBe(first.Id, "pending impacts are replaced wholesale, as before");
        impacts[0].Minutes.Should().Be(40);
        impacts[0].Status.Should().Be("PendingPayroll");
        (await db.AttendanceExceptions.AnyAsync(x => x.ExceptionType == AttendanceDerivedArtifacts.PaidAttendanceChanged)).Should().BeFalse();
    }

    [Fact]
    public async Task OnAPaidDay_OvertimePayable_WhichPayrollNeverConsumes_IsStillRecomputed()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        AddPunch(db, tenantId, employee.Id, "In", 9, 30);
        var punchOut = AddPunch(db, tenantId, employee.Id, "Out", 19, 30); // 600 - 60 = 540 worked: 60 OT
        await db.SaveChangesAsync();
        await ProcessAsync(db, tenantId, employee.Id);
        var late = await db.AttendancePayrollImpacts.SingleAsync(x => x.ImpactType == AttendanceImpactTypes.LateDeduction);
        await MarkProcessedAsPayrollDoes(db, late);

        await MovePunchAsync(db, punchOut, 20, 30); // 120 OT
        await ProcessAsync(db, tenantId, employee.Id);

        var overtime = await db.AttendancePayrollImpacts.SingleAsync(x => x.ImpactType == AttendanceImpactTypes.OvertimePayable);
        overtime.Minutes.Should().Be(120);
        overtime.Status.Should().Be("PendingPayroll");
        (await db.AttendancePayrollImpacts.SingleAsync(x => x.ImpactType == AttendanceImpactTypes.LateDeduction)).Id.Should().Be(late.Id);
        (await db.AttendanceExceptions.AnyAsync(x => x.ExceptionType == AttendanceDerivedArtifacts.PaidAttendanceChanged))
            .Should().BeFalse("nothing payroll paid has changed");
    }

    // ── round 5: "paid" is payroll coverage, and the exception's lifecycle ──────────────────────────────

    [Fact]
    public async Task ACleanPaidDay_WithNoImpacts_IsStillPaid_ARecomputedAbsenceIsFlaggedNotRaised()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        var punchIn = AddPunch(db, tenantId, employee.Id, "In", 9, 0);
        var punchOut = AddPunch(db, tenantId, employee.Id, "Out", 18, 0);
        await db.SaveChangesAsync();
        await ProcessAsync(db, tenantId, employee.Id);
        (await db.AttendancePayrollImpacts.AnyAsync()).Should().BeFalse("premise: a clean day, paid in full, has no impacts");
        await AddPayrollRunCoveringAsync(db, tenantId, employee.Id, "Processed");

        // Both punches turn out to be another employee's and are removed: the day now reads Absent.
        db.AttendanceRawEvents.RemoveRange(await db.AttendanceRawEvents.Where(r => r.Id == punchIn.Id || r.Id == punchOut.Id).ToListAsync());
        await db.SaveChangesAsync();
        await ProcessAsync(db, tenantId, employee.Id);

        (await db.AttendancePayrollImpacts.AnyAsync())
            .Should().BeFalse("the day was paid in full by a processed run; a fresh pending deduction would be taken by an off-cycle run");
        (await OpenFlagsAsync(db)).Should().ContainSingle().Which.Details.Should().Contain("Absence deduction paid 0 min, recomputed 480 min");
    }

    [Theory]
    [InlineData("Voided", true)]       // a voided run paid nothing
    [InlineData("Draft", true)]        // nor has a draft
    [InlineData("Processed", false)]
    public async Task OnlyANonVoidedProcessedRunThatPaysRecurringSalary_MakesTheDayPaid(string runStatus, bool absenceRaised)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        await AddPayrollRunCoveringAsync(db, tenantId, employee.Id, runStatus);
        await ProcessAsync(db, tenantId, employee.Id); // no punches: Absent

        (await db.AttendancePayrollImpacts.AnyAsync(x => x.ImpactType == AttendanceImpactTypes.AbsenceDeduction)).Should().Be(absenceRaised);
    }

    [Fact]
    public async Task ABonusOnlyRun_ThatConsumesNoAttendance_DoesNotMakeTheDayPaid()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        await AddPayrollRunCoveringAsync(db, tenantId, employee.Id, "Processed", includesRecurringPay: false);
        await ProcessAsync(db, tenantId, employee.Id);

        (await db.AttendancePayrollImpacts.AnyAsync(x => x.ImpactType == AttendanceImpactTypes.AbsenceDeduction)).Should().BeTrue();
    }

    [Fact]
    public async Task WhenThePaidDayMatchesWhatWasPaidAgain_TheOpenFlagIsClosed()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        var punchIn = await PaidLateDayAsync(db, tenantId, employee.Id);

        await MovePunchAsync(db, punchIn, 9, 50);
        await ProcessAsync(db, tenantId, employee.Id);
        (await OpenFlagsAsync(db)).Should().ContainSingle();

        await MovePunchAsync(db, punchIn, 9, 30); // corrected back: matches what payroll paid
        await ProcessAsync(db, tenantId, employee.Id);

        (await OpenFlagsAsync(db)).Should().BeEmpty("a difference that no longer exists must not sit open with stale text");
        (await db.AttendanceExceptions.SingleAsync(x => x.ExceptionType == AttendanceDerivedArtifacts.PaidAttendanceChanged)).IsResolved.Should().BeTrue();
    }

    [Fact]
    public async Task ADifferenceHrResolved_IsNotRaisedAgain_ButAChangedDifferenceIs()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = AddEmployee(db, tenantId);
        var punchIn = await PaidLateDayAsync(db, tenantId, employee.Id);

        await MovePunchAsync(db, punchIn, 9, 50);
        await ProcessAsync(db, tenantId, employee.Id);
        var flag = (await OpenFlagsAsync(db)).Single();
        (await db.AttendanceExceptions.FindAsync(flag.Id))!.IsResolved = true; // HR made the payroll adjustment
        await db.SaveChangesAsync();

        await ProcessAsync(db, tenantId, employee.Id); // the same difference again
        (await OpenFlagsAsync(db)).Should().BeEmpty("HR already adjusted payroll for exactly this difference; a second flag invites a second adjustment");

        await MovePunchAsync(db, punchIn, 10, 10); // a NEW difference: 60 late minutes
        await ProcessAsync(db, tenantId, employee.Id);
        (await OpenFlagsAsync(db)).Should().ContainSingle().Which.Details.Should().Contain("Late deduction paid 20 min, recomputed 60 min");
    }

    [Fact]
    public void PayrollValidation_WarnsForUnresolvedPaidAttendanceChanges_WithoutBlocking()
    {
        var run = new PayrollRun { TenantId = Guid.NewGuid(), Year = 2026, Month = 8 };
        var ctx = new PayrollValidationContext(run, [], [], [], [], [], [], null)
        {
            UnresolvedPaidAttendanceChanges = [new PaidAttendanceChange(42, Day, "Late deduction paid 20 min, recomputed 40 min")],
        };

        var warning = PayrollValidationEngine.Run(ctx).Where(r => r.Code == PayrollValidationEngine.PaidAttendanceChanged).Should().ContainSingle().Subject;
        warning.Severity.Should().Be("Warning");
        warning.EmployeeId.Should().Be(42);
        warning.Message.Should().Contain("2026-08-18").And.Contain("payroll adjustment");
    }

    private static async Task<AttendanceRawEvent> PaidLateDayAsync(ZayraDbContext db, Guid tenantId, int employeeId)
    {
        var punchIn = AddPunch(db, tenantId, employeeId, "In", 9, 30); // 20 late minutes
        AddPunch(db, tenantId, employeeId, "Out", 18, 0);
        await db.SaveChangesAsync();
        await ProcessAsync(db, tenantId, employeeId);
        await MarkProcessedAsPayrollDoes(db, await db.AttendancePayrollImpacts.SingleAsync());
        await AddPayrollRunCoveringAsync(db, tenantId, employeeId, "Processed");
        return punchIn;
    }

    private static Task<List<AttendanceException>> OpenFlagsAsync(ZayraDbContext db) =>
        db.AttendanceExceptions.Where(x => x.ExceptionType == AttendanceDerivedArtifacts.PaidAttendanceChanged && !x.IsResolved).ToListAsync();

    internal static async Task<PayrollRun> AddPayrollRunCoveringAsync(ZayraDbContext db, Guid tenantId, int employeeId, string status, bool includesRecurringPay = true)
    {
        var run = new PayrollRun { TenantId = tenantId, Year = Day.Year, Month = Day.Month, Status = status, IncludesRecurringPay = includesRecurringPay };
        db.PayrollRuns.Add(run);
        db.PayrollSlips.Add(new PayrollSlip { TenantId = tenantId, RunId = run.Id, EmployeeId = employeeId, Status = status == "Voided" ? "Voided" : "Processed" });
        await db.SaveChangesAsync();
        return run;
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What PayrollController's run does to the impacts it consumed (it uses ExecuteUpdate on Postgres).</summary>
    internal static async Task MarkProcessedAsPayrollDoes(ZayraDbContext db, AttendancePayrollImpact impact)
    {
        (await db.AttendancePayrollImpacts.FindAsync(impact.Id))!.Status = "Processed";
        await db.SaveChangesAsync();
    }

    /// <summary>Re-stamps a punch (re-read by id: processing clears the change tracker).</summary>
    internal static async Task MovePunchAsync(ZayraDbContext db, AttendanceRawEvent punch, int hour, int minute)
    {
        (await db.AttendanceRawEvents.FindAsync(punch.Id))!.PunchTimestampUtc = Utc(hour, minute);
        await db.SaveChangesAsync();
    }

    internal static DateTime Utc(int hour, int minute) => Day.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Utc);

    internal static AttendanceRawEvent AddPunch(ZayraDbContext db, Guid tenantId, int employeeId, string direction, int hour, int minute)
    {
        var raw = new AttendanceRawEvent { TenantId = tenantId, EmployeeId = employeeId, PunchDirection = direction, PunchTimestampUtc = Utc(hour, minute) };
        db.AttendanceRawEvents.Add(raw);
        return raw;
    }

    internal static Task ProcessAsync(ZayraDbContext db, Guid tenantId, int employeeId) =>
        Service(db).ProcessAsync(tenantId, new ProcessAttendanceRequest(Day, Day, employeeId),
            new RequestContext(null, null, Guid.NewGuid(), tenantId), CancellationToken.None);

    internal static Employee AddEmployee(ZayraDbContext db, Guid tenantId)
    {
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"PI-{Guid.NewGuid():N}"[..16], FullName = "Paid Impact Person",
            Status = EmployeeStatuses.Active, JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    internal static AttendanceService Service(ZayraDbContext db) => new(db, new NullNotifications(), new NullHttpClients());

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullHttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

/// <summary>
/// The void/reopen restore finds the impacts a run consumed by their ids (the run's witness rows). Reprocessing the
/// paid day in between used to delete those rows, so the restore released nothing and the voided month's deductions
/// were silently lost from the re-run.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AttendancePaidImpactVoidRestorePostgresTests
{
    private readonly PostgresFixture _fx;
    public AttendancePaidImpactVoidRestorePostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task AfterReprocessingAPaidDay_ReopeningTheRun_RecomputesTheDay_SoTheReRunDeductsWhatTheDayNowSays()
    {
        // Paid: 20 late minutes. After payroll the In punch moves to 09:50 (40 late minutes) and HR reprocesses: the
        // paid impact stays at 20 and a flag is raised. Reopening the run used to release the impact AS PAID (20) and
        // close the flag saying the day "is recomputed" when nothing recomputed it, so the re-run deducted 20.
        var (tenantId, employeeId, run) = await SeedPaidLateDayAsync();
        await ReprocessWithInPunchAtAsync(tenantId, employeeId, 9, 50);

        await using (var db = _fx.CreateDb())
        {
            var kit = await GlPostingKit.CreateAsync(_fx.CreateDb, tenantId);
            (await kit.Payroll(db).ReopenRun(run.Id, new Zayra.Api.Controllers.PayrollReasonRequest("punch corrected after payroll"), default))
                .Should().BeOfType<OkObjectResult>();
        }

        await using var verify = _fx.CreateDb();
        var impacts = await verify.AttendancePayrollImpacts.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ToListAsync();
        impacts.Should().ContainSingle("one late deduction for the day, never the paid one plus a new one");
        impacts[0].Status.Should().Be("PendingPayroll", "the reopened run no longer pays the day");
        impacts[0].Minutes.Should().Be(40, "the re-run must deduct what the day says now, not what the undone run paid");
        var flag = await verify.AttendanceExceptions.IgnoreQueryFilters()
            .SingleAsync(x => x.TenantId == tenantId && x.ExceptionType == AttendanceDerivedArtifacts.PaidAttendanceChanged);
        flag.IsResolved.Should().BeTrue("the day was recomputed, so nothing is left for a payroll adjustment");
    }

    [Fact]
    public async Task AfterAPaidDayBecomesAbsent_VoidingTheRun_RaisesTheAbsenceForTheReplacementRun()
    {
        Guid tenantId;
        int employeeId;
        var run = new PayrollRun { Year = 2026, Month = 8, Status = "Processed", IncludesRecurringPay = true };
        await using (var db = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            employeeId = AttendancePaidImpactPreservationTests.AddEmployee(db, tenantId).Id;
            AttendancePaidImpactPreservationTests.AddPunch(db, tenantId, employeeId, "In", 9, 0);
            AttendancePaidImpactPreservationTests.AddPunch(db, tenantId, employeeId, "Out", 18, 0);
            await db.SaveChangesAsync();
            await AttendancePaidImpactPreservationTests.ProcessAsync(db, tenantId, employeeId); // a clean day: no impacts
            run.TenantId = tenantId;
            db.PayrollRuns.Add(run);
            db.PayrollSlips.Add(new PayrollSlip { TenantId = tenantId, RunId = run.Id, EmployeeId = employeeId, Status = "Processed" });
            await db.SaveChangesAsync();
        }
        await using (var db = _fx.CreateDb())
        {
            // Both punches were another employee's: the paid day is now Absent. Flagged, not deducted.
            await db.AttendanceRawEvents.IgnoreQueryFilters().Where(r => r.TenantId == tenantId).ExecuteDeleteAsync();
            await AttendancePaidImpactPreservationTests.ProcessAsync(db, tenantId, employeeId);
            (await db.AttendancePayrollImpacts.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId)).Should().BeFalse("premise: paid");
        }

        await using (var db = _fx.CreateDb())
            (await new PayrollVoidService(db).VoidAsync(run.Id, tenantId, null, "test", "wrong punches", default)).IsVoided.Should().BeTrue();

        await using var verify = _fx.CreateDb();
        var absence = await verify.AttendancePayrollImpacts.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenantId);
        absence.ImpactType.Should().Be(AttendanceImpactTypes.AbsenceDeduction, "the voided run paid a day that was not worked");
        absence.Status.Should().Be("PendingPayroll");
        (await verify.AttendanceExceptions.IgnoreQueryFilters()
                .SingleAsync(x => x.TenantId == tenantId && x.ExceptionType == AttendanceDerivedArtifacts.PaidAttendanceChanged))
            .IsResolved.Should().BeTrue();
    }

    [Fact]
    public async Task AFlagOnAPaidDayThatBecomesHalfAPair_SaysTheDeductionIsUndetermined_NotTheOldDifference()
    {
        // Paid at 20 late minutes; the In moves to 09:50 (flag: paid 20, recomputed 40); then the Out punch is removed.
        // With half a pair the late deduction is held, so "recomputed 40" is no longer true and must not stay on the flag.
        var (tenantId, employeeId, _) = await SeedPaidLateDayAsync();
        await ReprocessWithInPunchAtAsync(tenantId, employeeId, 9, 50);
        await using (var db = _fx.CreateDb())
        {
            await db.AttendanceRawEvents.IgnoreQueryFilters().Where(r => r.TenantId == tenantId && r.PunchDirection == "Out").ExecuteDeleteAsync();
            await AttendancePaidImpactPreservationTests.ProcessAsync(db, tenantId, employeeId);
        }

        await using var verify = _fx.CreateDb();
        var flag = await verify.AttendanceExceptions.IgnoreQueryFilters()
            .SingleAsync(x => x.TenantId == tenantId && x.ExceptionType == AttendanceDerivedArtifacts.PaidAttendanceChanged);
        flag.IsResolved.Should().BeFalse("the day does not match what was paid; it is undetermined");
        var text = AttendanceDerivedArtifacts.DescribePaidAttendanceChange(flag.Details);
        text.Should().Contain("undetermined until the punch is corrected").And.NotContain("recomputed 40");
        (await verify.AttendancePayrollImpacts.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenantId)).Minutes
            .Should().Be(20, "what payroll paid stays as paid");
    }

    private async Task<(Guid TenantId, int EmployeeId, PayrollRun Run)> SeedPaidLateDayAsync()
    {
        Guid tenantId;
        int employeeId;
        var run = new PayrollRun { Year = 2026, Month = 8, Status = "Processed", IncludesRecurringPay = true };
        await using var db = _fx.CreateDb();
        tenantId = await PostgresFixture.SeedMinimalTenant(db);
        employeeId = AttendancePaidImpactPreservationTests.AddEmployee(db, tenantId).Id;
        AttendancePaidImpactPreservationTests.AddPunch(db, tenantId, employeeId, "In", 9, 30);
        AttendancePaidImpactPreservationTests.AddPunch(db, tenantId, employeeId, "Out", 18, 0);
        await db.SaveChangesAsync();
        await AttendancePaidImpactPreservationTests.ProcessAsync(db, tenantId, employeeId);

        // As PayrollController's run does: a witness per consumed impact, then the impacts marked Processed.
        run.TenantId = tenantId;
        db.PayrollRuns.Add(run);
        db.PayrollSlips.Add(new PayrollSlip { TenantId = tenantId, RunId = run.Id, EmployeeId = employeeId, Status = "Processed" });
        var consumed = await db.AttendancePayrollImpacts.Where(x => x.TenantId == tenantId).ToListAsync();
        consumed.Should().ContainSingle().Which.Minutes.Should().Be(20);
        foreach (var impact in consumed)
            db.PayrollRunConsumptions.Add(new PayrollRunConsumption
            {
                TenantId = tenantId, PayrollRunId = run.Id, ArtifactType = PayrollConsumptionArtifacts.AttendanceImpact,
                ArtifactId = impact.Id, EmployeeId = employeeId, PriorStatus = impact.Status,
            });
        await db.SaveChangesAsync();
        await db.AttendancePayrollImpacts.Where(x => x.TenantId == tenantId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Processed"));
        return (tenantId, employeeId, run);
    }

    private async Task ReprocessWithInPunchAtAsync(Guid tenantId, int employeeId, int hour, int minute)
    {
        await using var db = _fx.CreateDb();
        var raw = await db.AttendanceRawEvents.IgnoreQueryFilters().SingleAsync(r => r.TenantId == tenantId && r.PunchDirection == "In");
        raw.PunchTimestampUtc = AttendancePaidImpactPreservationTests.Utc(hour, minute);
        await db.SaveChangesAsync();
        await AttendancePaidImpactPreservationTests.ProcessAsync(db, tenantId, employeeId); // HR reprocesses the paid day
        (await db.AttendanceExceptions.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId
            && x.ExceptionType == AttendanceDerivedArtifacts.PaidAttendanceChanged && !x.IsResolved)).Should().Be(1, "premise: flagged");
        (await db.AttendancePayrollImpacts.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenantId)).Minutes.Should().Be(20, "premise: paid as 20");
    }
}
