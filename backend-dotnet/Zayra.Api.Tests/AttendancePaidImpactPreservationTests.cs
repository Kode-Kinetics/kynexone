using FluentAssertions;
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
    private static readonly DateOnly Day = new(2026, 8, 18); // a Tuesday; UTC tenant, default policy, 09:00–18:00

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
    public async Task AfterReprocessingAPaidDay_VoidingTheRun_StillReleasesTheImpactsItConsumed()
    {
        Guid tenantId;
        int employeeId;
        var run = new PayrollRun { Year = 2026, Month = 8, Status = "Processed" };
        await using (var db = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            employeeId = AttendancePaidImpactPreservationTests.AddEmployee(db, tenantId).Id;
            AttendancePaidImpactPreservationTests.AddPunch(db, tenantId, employeeId, "In", 9, 30);
            AttendancePaidImpactPreservationTests.AddPunch(db, tenantId, employeeId, "Out", 18, 0);
            await db.SaveChangesAsync();
            await AttendancePaidImpactPreservationTests.ProcessAsync(db, tenantId, employeeId);

            // As PayrollController's run does: a witness per consumed impact, then the impacts marked Processed.
            run.TenantId = tenantId;
            db.PayrollRuns.Add(run);
            var consumed = await db.AttendancePayrollImpacts.Where(x => x.TenantId == tenantId).ToListAsync();
            consumed.Should().ContainSingle();
            foreach (var impact in consumed)
                db.PayrollRunConsumptions.Add(new PayrollRunConsumption
                {
                    TenantId = tenantId, PayrollRunId = run.Id, ArtifactType = PayrollConsumptionArtifacts.AttendanceImpact,
                    ArtifactId = impact.Id, EmployeeId = employeeId, PriorStatus = impact.Status,
                });
            await db.SaveChangesAsync();
            await db.AttendancePayrollImpacts.Where(x => x.TenantId == tenantId)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Processed"));
        }

        await using (var db = _fx.CreateDb())
        {
            var raw = await db.AttendanceRawEvents.IgnoreQueryFilters().SingleAsync(r => r.TenantId == tenantId && r.PunchDirection == "In");
            raw.PunchTimestampUtc = AttendancePaidImpactPreservationTests.Utc(9, 50);
            await db.SaveChangesAsync();
            await AttendancePaidImpactPreservationTests.ProcessAsync(db, tenantId, employeeId); // HR reprocesses the paid day
        }

        await using (var db = _fx.CreateDb())
        {
            var tracked = await db.PayrollRuns.IgnoreQueryFilters().SingleAsync(r => r.Id == run.Id);
            var restored = await new PayrollVoidService(db).RestoreOperationalStateAsync(tenantId, tracked, run.Id, default);
            await db.SaveChangesAsync();
            restored.ImpactsReleased.Should().Be(1, "the restore must still find the impact the run consumed");
        }

        await using var verify = _fx.CreateDb();
        var impacts = await verify.AttendancePayrollImpacts.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ToListAsync();
        impacts.Should().ContainSingle("no duplicate was raised while the day was paid");
        impacts[0].Status.Should().Be("PendingPayroll", "the void hands the consumed impact back to payroll");
    }
}
