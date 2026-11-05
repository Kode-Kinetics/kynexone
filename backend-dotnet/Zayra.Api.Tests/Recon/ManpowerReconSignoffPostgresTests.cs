using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Recon;

/// <summary>
/// The sign-off against real Postgres: the table's own guards, the row lock that orders a sign-off against a re-process, the
/// approval gate through the production transaction path, and the supersede hooks of reopen, send-back, void and process.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class ManpowerReconSignoffPostgresTests
{
    private readonly PostgresFixture _fx;
    public ManpowerReconSignoffPostgresTests(PostgresFixture fx) => _fx = fx;

    /// <summary>A prior and a current run with one continuing employee and one held out, on a fresh tenant.</summary>
    internal sealed class Scenario
    {
        public required GlPostingKit Kit { get; init; }
        public required PayrollRun Prior { get; init; }
        public required PayrollRun Current { get; init; }
        public Guid TenantId => Kit.TenantId;
        public ZayraDbContext Db => Kit.Db;
        public Guid Maker { get; } = Guid.NewGuid();

        public PayrollReconController Recon(string role = "Finance Approver", Guid? user = null, ZayraDbContext? db = null) =>
            ReconHarness.Recon(db ?? Db, TenantId, role, ReconHarness.SignPermissions, user);

        public PayrollController Payroll(string role, ZayraDbContext? db = null, params string[] perms) =>
            ReconHarness.Payroll(db ?? Db, TenantId, role, perms.Length == 0 ? new[] { "payroll.read", "payroll.approve", "payroll.lock" } : perms);
    }

    internal static async Task<Scenario> NewScenarioAsync(PostgresFixture fx, string currentStatus = "Processed", bool withPrior = true)
    {
        Guid tenantId;
        await using (var seed = fx.CreateDb()) tenantId = await PostgresFixture.SeedMinimalTenant(seed);
        var kit = await GlPostingKit.CreateAsync(fx.CreateDb, tenantId);
        var db = kit.Db;
        var e1 = new Employee { TenantId = tenantId, CompanyId = kit.CompanyId, EmployeeCode = "P1", FullName = "Employee One", Status = "Active", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var e2 = new Employee { TenantId = tenantId, CompanyId = kit.CompanyId, EmployeeCode = "P2", FullName = "Employee Two", Status = "Active", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        db.Employees.AddRange(e1, e2);
        await db.SaveChangesAsync();

        var maker = Guid.NewGuid();
        var prior = new PayrollRun { TenantId = tenantId, CompanyId = kit.CompanyId, Year = 2026, Month = 9, Status = "Locked", ProcessVersion = 1 };
        var current = new PayrollRun { TenantId = tenantId, CompanyId = kit.CompanyId, Year = 2026, Month = 10, Status = currentStatus, ProcessVersion = 1, CreatedByUserId = maker, ProcessedByUserId = maker };
        if (withPrior) db.PayrollRuns.Add(prior);
        db.PayrollRuns.Add(current);
        void Slip(PayrollRun run, Employee e, decimal basic)
        {
            db.PayrollSlips.Add(new PayrollSlip { TenantId = tenantId, CompanyId = kit.CompanyId, RunId = run.Id, EmployeeId = e.Id, EmployeeCode = e.EmployeeCode, EmployeeName = e.FullName, BasicSalary = basic, GrossSalary = basic, NetSalary = basic });
            db.PayrollEarnings.Add(new PayrollEarning { TenantId = tenantId, PayrollRunId = run.Id, EmployeeId = e.Id, ComponentCode = "BASIC", ComponentName = "Basic salary", Amount = basic, Source = "Salary" });
        }
        if (withPrior) { Slip(prior, e1, 1000m); Slip(prior, e2, 800m); }
        Slip(current, e1, 1000m);
        db.PayrollRunEmployeeSelections.Add(new PayrollRunEmployeeSelection
        { TenantId = tenantId, CompanyId = kit.CompanyId, PayrollRunId = current.Id, EmployeeId = e2.Id, Mode = "Exclude", Reason = "hold", Outcome = PayrollRunSelectionOutcomes.Excluded });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Scenario { Kit = kit, Prior = prior, Current = current };
    }

    private static int StatusOf(IActionResult r) => PayrollReconAccessTests.Status(r);
    private static string ErrorOf(IActionResult r) => PayrollReconAccessTests.Error(r);

    private static async Task<IActionResult> SignAsync(Scenario s, ReconSignoffRequest? req = null, string role = "Finance Approver", Guid? user = null)
    {
        var ctrl = s.Recon(role, user);
        req ??= await ReconHarness.ValidRequestAsync(ctrl, s.Current.Id);
        return await ctrl.Signoff(s.Current.Id, req, default);
    }

    // ── The table's own guards ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePartialUniqueIndex_IsOnTheActiveRowsOnly_AndTheDatabaseEnforcesIt()
    {
        var s = await NewScenarioAsync(_fx);
        (await _fx.GetIndexDefinitionAsync("ux_payroll_run_recon_signoffs__run_active")).Should().Contain("WHERE").And.Contain("Active");

        await using var a = _fx.CreateDb();
        PayrollRunReconSignoff Row(string status) => new()
        {
            TenantId = s.TenantId, CompanyId = s.Kit.CompanyId, PayrollRunId = s.Current.Id, ProcessVersion = 1, SnapshotHash = new string('c', 64),
            SnapshotJson = "{}", SignedOffByName = "x", Status = status,
            SupersededAtUtc = status == "Superseded" ? DateTime.UtcNow : null, SupersededReason = status == "Superseded" ? "Reprocessed" : null,
        };
        a.PayrollRunReconSignoffs.AddRange(Row("Active"), Row("Superseded"), Row("Superseded"));
        await a.SaveChangesAsync();

        await using var b = _fx.CreateDb();
        b.PayrollRunReconSignoffs.Add(Row("Active"));
        var act = async () => await b.SaveChangesAsync();
        var ex = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        ((PostgresException)ex.InnerException!).SqlState.Should().Be("23505");
        ((PostgresException)ex.InnerException!).ConstraintName.Should().Be("ux_payroll_run_recon_signoffs__run_active");
    }

    [Theory]
    [InlineData("Active", "Reprocessed", false)]        // an Active row cannot carry a superseded reason
    [InlineData("Superseded", null, true)]               // a Superseded row must say why
    [InlineData("Superseded", "Because", true)]          // and the reason must be one of the six
    [InlineData("Pending", null, false)]                 // and the status one of two
    public async Task TheChecks_RefuseAnIncoherentStatusReasonOrPair(string status, string? reason, bool stamped)
    {
        var s = await NewScenarioAsync(_fx);
        await using var db = _fx.CreateDb();
        db.PayrollRunReconSignoffs.Add(new PayrollRunReconSignoff
        {
            TenantId = s.TenantId, CompanyId = s.Kit.CompanyId, PayrollRunId = s.Current.Id, ProcessVersion = 1, SnapshotHash = new string('c', 64),
            SnapshotJson = "{}", SignedOffByName = "x", Status = status, SupersededReason = reason, SupersededAtUtc = stamped ? DateTime.UtcNow : null,
        });
        var act = async () => await db.SaveChangesAsync();
        var ex = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        ((PostgresException)ex.InnerException!).SqlState.Should().Be("23514");
    }

    [Fact]
    public async Task ARunWithSignoffs_CannotBeDeleted_ButDeletingASignoffTakesItsExplanations()
    {
        var s = await NewScenarioAsync(_fx);
        await using var db = _fx.CreateDb();
        var so = new PayrollRunReconSignoff
        {
            TenantId = s.TenantId, CompanyId = s.Kit.CompanyId, PayrollRunId = s.Current.Id, ProcessVersion = 1, SnapshotHash = new string('c', 64),
            SnapshotJson = "{}", SignedOffByName = "x",
        };
        so.Explanations.Add(new PayrollRunReconExplanation { TenantId = s.TenantId, SignoffId = so.Id, Scope = "Item", Key = "BASIC", Text = "A reason that is long enough" });
        db.PayrollRunReconSignoffs.Add(so);
        await db.SaveChangesAsync();

        await using var del = _fx.CreateDb();
        var deleteRun = async () => await del.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM payroll_runs WHERE id = {s.Current.Id}");
        var ex = (await deleteRun.Should().ThrowAsync<PostgresException>()).Which;
        ex.SqlState.Should().Be("23503", "the foreign key to payroll_runs is RESTRICT");

        await del.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM payroll_run_recon_signoffs WHERE id = {so.Id}");
        (await del.PayrollRunReconExplanations.CountAsync(e => e.SignoffId == so.Id)).Should().Be(0);
    }

    [Fact]
    public async Task AnExplanation_IsUniquePerSignoffScopeAndKey_AndTheScopeIsChecked()
    {
        var s = await NewScenarioAsync(_fx);
        await using var db = _fx.CreateDb();
        var so = new PayrollRunReconSignoff
        {
            TenantId = s.TenantId, CompanyId = s.Kit.CompanyId, PayrollRunId = s.Current.Id, ProcessVersion = 1, SnapshotHash = new string('c', 64),
            SnapshotJson = "{}", SignedOffByName = "x",
        };
        db.PayrollRunReconSignoffs.Add(so);
        await db.SaveChangesAsync();
        db.PayrollRunReconExplanations.Add(new PayrollRunReconExplanation { TenantId = s.TenantId, SignoffId = so.Id, Scope = "Item", Key = "BASIC", Text = "first reason, long enough" });
        await db.SaveChangesAsync();

        await using var dup = _fx.CreateDb();
        dup.PayrollRunReconExplanations.Add(new PayrollRunReconExplanation { TenantId = s.TenantId, SignoffId = so.Id, Scope = "Item", Key = "BASIC", Text = "second reason, long enough" });
        Func<Task> saveDup = async () => await dup.SaveChangesAsync();
        var ex1 = (await saveDup.Should().ThrowAsync<DbUpdateException>()).Which;
        ((PostgresException)ex1.InnerException!).SqlState.Should().Be("23505");

        await using var bad = _fx.CreateDb();
        bad.PayrollRunReconExplanations.Add(new PayrollRunReconExplanation { TenantId = s.TenantId, SignoffId = so.Id, Scope = "Galaxy", Key = "X", Text = "reason that is long enough" });
        Func<Task> saveBad = async () => await bad.SaveChangesAsync();
        var ex2 = (await saveBad.Should().ThrowAsync<DbUpdateException>()).Which;
        ((PostgresException)ex2.InnerException!).SqlState.Should().Be("23514");
    }

    // ── Approve, through the production transaction path ─────────────────────────────────────────

    [Fact]
    public async Task ApproveWithoutASignoff_Is409_AndTheRunStaysProcessed()
    {
        var s = await NewScenarioAsync(_fx);
        var res = await s.Payroll("Admin").Approve(s.Current.Id, new PayrollDecisionRequest("ok"), default);
        StatusOf(res).Should().Be(409);
        ErrorOf(res).Should().Be("reconciliation_not_reviewed");
        await using var check = _fx.CreateDb();
        (await check.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == s.Current.Id)).Status.Should().Be("Processed");
    }

    [Fact]
    public async Task WithASignoff_BothApprovalLevelsPass()
    {
        var finance = await NewScenarioAsync(_fx);
        StatusOf(await SignAsync(finance)).Should().Be(201);
        // The scenario holds one employee out of the run on purpose, so the approver states that count as well.
        (await finance.Payroll("Finance Approver").Approve(finance.Current.Id, new PayrollDecisionRequest("ok", ExpectedExcludedCount: 1), default)).Should().BeOfType<OkObjectResult>();
        await using (var check = _fx.CreateDb())
            (await check.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == finance.Current.Id)).Status.Should().Be("Approved");

        var hr = await NewScenarioAsync(_fx);
        StatusOf(await SignAsync(hr)).Should().Be(201);
        (await hr.Payroll("HR Manager").Approve(hr.Current.Id, new PayrollDecisionRequest("ok", ExpectedExcludedCount: 1), default)).Should().BeOfType<OkObjectResult>();
        await using (var check = _fx.CreateDb())
            (await check.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == hr.Current.Id)).Status.Should().Be("PendingFinanceReview");
    }

    [Fact]
    public async Task WhenThePriorRunIsReprocessedAfterTheSignoff_ApproveSaysPriorChanged_AndRecordsIt()
    {
        var s = await NewScenarioAsync(_fx);
        StatusOf(await SignAsync(s)).Should().Be(201);
        await using (var bump = _fx.CreateDb())
            await bump.Database.ExecuteSqlInterpolatedAsync($"UPDATE payroll_runs SET process_version = process_version + 1 WHERE id = {s.Prior.Id}");

        var res = await s.Payroll("Admin").Approve(s.Current.Id, new PayrollDecisionRequest("ok"), default);
        StatusOf(res).Should().Be(409);
        ReconHarness.Json(((ObjectResult)res).Value).GetProperty("reason").GetString().Should().Be("PriorChanged");
        await using var check = _fx.CreateDb();
        var stored = await check.PayrollRunReconSignoffs.AsNoTracking().SingleAsync(x => x.PayrollRunId == s.Current.Id);
        stored.Status.Should().Be("Superseded");
        stored.SupersededReason.Should().Be("PriorChanged");
    }

    // ── Sign-off against the row lock ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASignoffRequestWithAStaleVersion_IsRefused()
    {
        var s = await NewScenarioAsync(_fx);
        var ctrl = s.Recon();
        var req = await ReconHarness.ValidRequestAsync(ctrl, s.Current.Id);
        var res = await ctrl.Signoff(s.Current.Id, req with { ExpectedProcessVersion = 0 }, default);
        StatusOf(res).Should().Be(409);
        ErrorOf(res).Should().Be("signoff_stale");
    }

    [Fact]
    public async Task ASignoff_ThatWaitsOnAReprocess_ThenFindsTheNewVersion_IsRefused_AndNothingStaysActive()
    {
        var s = await NewScenarioAsync(_fx);
        var req = await ReconHarness.ValidRequestAsync(s.Recon(), s.Current.Id);

        // A re-process mid-flight: holds the run's row lock, raises the version, has not committed yet.
        await using var processDb = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseNpgsql(_fx.ConnectionString).Options);
        await using var processTx = await processDb.Database.BeginTransactionAsync();
        await processDb.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM payroll_runs WHERE id = {s.Current.Id} FOR UPDATE");
        await processDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE payroll_runs SET process_version = process_version + 1 WHERE id = {s.Current.Id}");

        var sign = Task.Run(async () =>
        {
            await using var db = _fx.CreateDb();
            return await s.Recon(db: db).Signoff(s.Current.Id, req, default);
        });
        await WaitUntilABackendWaitsOnALockAsync(sign);
        await processTx.CommitAsync();
        var res = await sign;

        StatusOf(res).Should().Be(409);
        ErrorOf(res).Should().Be("signoff_stale");
        await using var check = _fx.CreateDb();
        (await check.PayrollRunReconSignoffs.AsNoTracking().CountAsync(x => x.PayrollRunId == s.Current.Id && x.Status == "Active")).Should().Be(0);
    }

    [Fact]
    public async Task TwoSignoffsAtOnce_LeaveExactlyOneActive()
    {
        var s = await NewScenarioAsync(_fx);
        var req = await ReconHarness.ValidRequestAsync(s.Recon(), s.Current.Id);
        for (var round = 0; round < 3; round++)
        {
            var results = await RaceAsync(
                async () => { await using var db = _fx.CreateDb(); return await s.Recon(db: db).Signoff(s.Current.Id, req, default); },
                async () => { await using var db = _fx.CreateDb(); return await s.Recon(db: db).Signoff(s.Current.Id, req, default); });
            results.Should().NotContainNulls("the loser fails cleanly with a 409, never an unhandled error");
            results.Select(r => StatusOf(r!)).OrderBy(x => x).Should().Equal(201, 409);
            await using var check = _fx.CreateDb();
            (await check.PayrollRunReconSignoffs.AsNoTracking().CountAsync(x => x.PayrollRunId == s.Current.Id && x.Status == "Active")).Should().Be(1);

            // Reset for the next round: withdraw so the next pair races from a clean state.
            await s.Recon(db: _fx.CreateDb()).WithdrawSignoff(s.Current.Id, new ReconWithdrawRequest("Resetting for the next round."), default);
        }
    }

    [Fact]
    public async Task ASignoff_AndAReopen_RaceCleanly_NeverLeavingAnActiveSignoffOnADraftRun()
    {
        for (var round = 0; round < 3; round++)
        {
            var s = await NewScenarioAsync(_fx);
            var req = await ReconHarness.ValidRequestAsync(s.Recon(), s.Current.Id);
            await RaceAsync(
                async () => { await using var db = _fx.CreateDb(); return await s.Recon(db: db).Signoff(s.Current.Id, req, default); },
                async () => { await using var db = _fx.CreateDb(); return await s.Payroll("Admin", db).ReopenRun(s.Current.Id, new PayrollReasonRequest("racing a sign-off"), default); });

            await using var check = _fx.CreateDb();
            var run = await check.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == s.Current.Id);
            var actives = await check.PayrollRunReconSignoffs.AsNoTracking().CountAsync(x => x.PayrollRunId == s.Current.Id && x.Status == "Active");
            if (run.Status == "Draft") actives.Should().Be(0, $"round {round}: a reopened run must not keep a live sign-off");
        }
    }

    // ── The supersede hooks, with the real transactions ──────────────────────────────────────────

    [Fact]
    public async Task Reopen_SendBack_AndVoid_EachSupersedeTheActiveSignoff()
    {
        var reopen = await NewScenarioAsync(_fx);
        StatusOf(await SignAsync(reopen)).Should().Be(201);
        await using (var db = _fx.CreateDb())
            (await reopen.Payroll("Admin", db).ReopenRun(reopen.Current.Id, new PayrollReasonRequest("fix the inputs"), default)).Should().BeOfType<OkObjectResult>();
        await AssertSupersededAsync(reopen, "Reopened");

        var sendBack = await NewScenarioAsync(_fx, "Processed");
        StatusOf(await SignAsync(sendBack)).Should().Be(201);
        await using (var db = _fx.CreateDb())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE payroll_runs SET status = 'PendingFinanceReview' WHERE id = {sendBack.Current.Id}");
        await using (var db = _fx.CreateDb())
            (await sendBack.Payroll("Admin", db).SendBack(sendBack.Current.Id, new PayrollDecisionRequest("numbers look off"), default)).Should().BeOfType<OkObjectResult>();
        await AssertSupersededAsync(sendBack, "SentBack");

        var voided = await NewScenarioAsync(_fx);
        StatusOf(await SignAsync(voided)).Should().Be(201);
        await using (var db = _fx.CreateDb())
            (await voided.Payroll("Admin", db).VoidRun(voided.Current.Id, new PayrollDecisionRequest("duplicate run"), default)).Should().BeOfType<OkObjectResult>();
        await AssertSupersededAsync(voided, "Voided");
    }

    private async Task AssertSupersededAsync(Scenario s, string reason)
    {
        await using var check = _fx.CreateDb();
        var rows = await check.PayrollRunReconSignoffs.AsNoTracking().Where(x => x.PayrollRunId == s.Current.Id).ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Status.Should().Be("Superseded");
        rows[0].SupersededReason.Should().Be(reason);
        (await check.PayrollAuditLogs.AsNoTracking().CountAsync(a => a.Action == "payroll.recon.superseded" && a.EntityId == s.Current.Id.ToString())).Should().Be(1);
    }

    [Fact]
    public async Task Process_RaisesTheVersion_AndSupersedesAnActiveSignoff_OnPostgres()
    {
        var kit = await NewKitAsync();
        var (run, _) = await SeedDraftRunAsync(kit);
        await using (var db = _fx.CreateDb())
        {
            db.PayrollRunReconSignoffs.Add(new PayrollRunReconSignoff
            {
                TenantId = kit.TenantId, CompanyId = kit.CompanyId, PayrollRunId = run.Id, ProcessVersion = 0, SnapshotHash = new string('d', 64),
                SnapshotJson = "{}", SignedOffByName = "x",
            });
            await db.SaveChangesAsync();
        }
        await using (var db = _fx.CreateDb())
            (await ReconHarness.Payroll(db, kit.TenantId, "Admin", new[] { "payroll.read", "payroll.approve", "payroll.lock" }).Process(run.Id, default))
                .Should().BeOfType<OkObjectResult>();

        await using var check = _fx.CreateDb();
        (await check.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).ProcessVersion.Should().Be(1);
        var so = await check.PayrollRunReconSignoffs.AsNoTracking().SingleAsync(x => x.PayrollRunId == run.Id);
        so.Status.Should().Be("Superseded");
        so.SupersededReason.Should().Be("Reprocessed");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private async Task<GlPostingKit> NewKitAsync()
    {
        Guid tenantId;
        await using (var db = _fx.CreateDb()) tenantId = await PostgresFixture.SeedMinimalTenant(db);
        return await GlPostingKit.CreateAsync(_fx.CreateDb, tenantId);
    }

    private static async Task<(PayrollRun Run, Employee Emp)> SeedDraftRunAsync(GlPostingKit kit)
    {
        var structure = new SalaryStructure { TenantId = kit.TenantId, Code = "BASE", Name = "Base", Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true };
        kit.Db.SalaryStructures.Add(structure);
        await kit.Db.SaveChangesAsync();
        var emp = new Employee
        {
            TenantId = kit.TenantId, CompanyId = kit.CompanyId, EmployeeCode = "R001", FullName = "Process Subject", Status = "Active",
            JoiningDate = new DateTime(2022, 3, 1, 0, 0, 0, DateTimeKind.Utc), WorkEmail = "r001@recon.test", Nationality = "SAU", ContractType = "Indefinite",
        };
        kit.Db.Employees.Add(emp);
        await kit.Db.SaveChangesAsync();
        kit.Db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = kit.TenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id, Currency = "SAR",
            BasicSalary = 12_000m, HousingAllowance = 3_000m, TransportAllowance = 800m, EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        });
        kit.Db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile { TenantId = kit.TenantId, EmployeeId = emp.Id, Iban = "SA4420000001234567891234", MolId = "MOL-R001", SalaryCurrency = "SAR" });
        var now = DateTime.UtcNow;
        var run = new PayrollRun { TenantId = kit.TenantId, CompanyId = kit.CompanyId, Year = now.Year, Month = now.Month, Status = "Draft", CreatedByUserId = Guid.NewGuid() };
        kit.Db.PayrollRuns.Add(run);
        await kit.Db.SaveChangesAsync();
        kit.Db.ChangeTracker.Clear();
        return (run, emp);
    }

    /// <summary>Polls until some backend of this database is blocked on a lock, or the contender finished first.</summary>
    private async Task WaitUntilABackendWaitsOnALockAsync(Task contender)
    {
        await using var db = _fx.CreateDb();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (contender.IsCompleted) return;
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", conn);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("The contender never reached the run's row lock.");
    }

    private static async Task<T?[]> RaceAsync<T>(Func<Task<T>> first, Func<Task<T>> second) where T : class
    {
        var ready = new SemaphoreSlim(0, 2);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<T?> Run(Func<Task<T>> contender)
        {
            await Task.Yield();
            ready.Release();
            await go.Task;
            try { return await contender(); }
            catch { return null; }
        }
        var a = Task.Run(() => Run(first));
        var b = Task.Run(() => Run(second));
        await ready.WaitAsync();
        await ready.WaitAsync();
        go.SetResult();
        return await Task.WhenAll(a, b);
    }
}
