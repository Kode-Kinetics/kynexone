using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostingLockKit;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-accounting v1, WP-C3 (AM-8, AM-36): Approve, Lock, mapping saves, master-data changes and the switch race on REAL PostgreSQL with real
/// concurrent transactions (one connection per contender) and the company advisory lock.
/// S-011 (an employee change committing during Lock), S-012 (a mapping save against Approve, both commit orders), S-015 (the hash does not
/// depend on the caller's company scope), S-046 (a switch flip during Lock), and two simultaneous Locks of a costed run.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class CostAccountingRacesPostgresTests
{
    private readonly PostgresFixture _fx;
    public CostAccountingRacesPostgresTests(PostgresFixture fx) => _fx = fx;

    private static readonly (string, string, string, decimal, bool)[] NoDeductions = [];

    private async Task<CostingLockKit> KitAsync(bool costing = true)
    {
        Guid tenantId;
        await using (var db = _fx.CreateDb()) tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var k = await CostingLockKit.CreateAsync(costing, costing, _fx.CreateDb, tenantId);
        await k.AddCc("CC-110", "G_AND_A");
        await k.AddCc("CC-200", "SELLING");
        await k.AddDept("D-FIN", "CC-110");
        await k.AddDept("D-SAL", "CC-200");
        await k.AddEmployee("E1", "D-SAL");
        await k.AddEmployee("E2", "D-FIN");
        k.Db.GlAccounts.Add(new GlAccount { TenantId = tenantId, CompanyId = k.CompanyId, Code = "5011", Name = "Alt basic", AccountType = "Expense", CostCenterRule = costing ? "Required" : "Optional" });
        await k.Db.SaveChangesAsync();
        k.Db.ChangeTracker.Clear();
        return k;
    }

    private static Task<PayrollRun> RunAsync(CostingLockKit k, int month) =>
        k.SeedRunAsync(new[] { E("E1", "BASIC", 3000m), E("E2", "BASIC", 2000m) }, NoDeductions, month: month);

    private static async Task<IActionResult> ApproveOn(CostingLockKit k, Guid runId, ZayraDbContext? db = null)
    {
        await using var own = db is null ? k.K.NewContext() : null;
        return await k.K.Payroll(db ?? own, k.ApproverId, "Admin").Approve(runId, new PayrollDecisionRequest("ok"), default);
    }

    private static async Task<IActionResult> LockOn(CostingLockKit k, Guid runId, Guid? as_ = null, ZayraDbContext? db = null)
    {
        await using var own = db is null ? k.K.NewContext() : null;
        return await k.K.Payroll(db ?? own, as_ ?? k.LockerId, "Admin").Lock(runId, default);
    }

    private static async Task<IActionResult> MapOn(CostingLockKit k, Guid accountId, Guid by)
    {
        await using var db = k.K.NewContext();
        return await CostAccountingKit.Gl(db, k.TenantId, CostAccountingKit.ManagePerms, uid: by)
            .SetMappings(k.CompanyId, new() { new GlMappingRequest("EARN:BASIC", accountId) }, default, "race");
    }

    /// <summary>Starts every actor behind one gate so they hit the database together, each on its own connection.</summary>
    private static async Task<IActionResult[]> Together(params Func<Task<IActionResult>>[] actors)
    {
        var gate = new TaskCompletionSource();
        var tasks = actors.Select(a => Task.Run(async () => { await gate.Task; return await a(); })).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static async Task<List<FinanceGlEntry>> LedgerOf(CostingLockKit k, Guid runId)
    {
        await using var read = k.K.NewContext();
        return await read.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking().Where(l => l.TenantId == k.TenantId && l.SourceEntityId == runId && l.SourceModule == "Payroll").ToListAsync();
    }

    private static string BasicAccountOf(IEnumerable<FinanceGlEntry> lines) =>
        lines.Where(l => l.DebitAccount.StartsWith("5001") || l.DebitAccount.StartsWith("5011")).Select(l => l.DebitAccount[..4]).Distinct().Single();

    // ── S-012: a mapping save against Approve, both commit orders ────────────

    [Fact]
    public async Task S012_AMappingSave_RacingApprove_NeverLetsLockPostWithAMappingOtherThanTheOneHashed()
    {
        await using var k = await KitAsync();
        await using var q = _fx.CreateDb();
        var defaultBasic = (await q.GlAccounts.IgnoreQueryFilters().SingleAsync(a => a.TenantId == k.TenantId && a.CompanyId == null && a.Code == "5001")).Id;
        var altBasic = (await q.GlAccounts.IgnoreQueryFilters().SingleAsync(a => a.TenantId == k.TenantId && a.CompanyId == k.CompanyId && a.Code == "5011")).Id;
        var mapUser = Guid.NewGuid();
        var inForce = "5001";
        var mappingWon = 0; var approveWon = 0;

        for (var i = 0; i < 12; i++)
        {
            var run = await RunAsync(k, month: i + 1);
            var (target, targetId) = inForce == "5001" ? ("5011", altBasic) : ("5001", defaultBasic);

            var results = await Together(() => ApproveOn(k, run.Id), () => MapOn(k, targetId, mapUser));

            Status(results[0]).Should().Be(200, $"iteration {i}: Approve never fails because of a concurrent mapping save");
            if (Status(results[1]) == 200) { inForce = target; mappingWon++; }
            else { Status(results[1]).Should().Be(409, $"iteration {i}: the loser is told the run is locked in"); Err(results[1]).Should().Be("locked_into_approved_run"); approveWon++; }

            var locked = await LockOn(k, run.Id);
            Status(locked).Should().Be(200, $"iteration {i}: the hash sealed by Approve describes the mapping in force, so Lock is never sent back");
            BasicAccountOf(await LedgerOf(k, run.Id)).Should().Be(inForce, $"iteration {i}: Lock posted with the mapping that was hashed");
        }
        (mappingWon + approveWon).Should().Be(12);
    }

    // ── three-way: Lock of one run, Approve of the next, and a mapping save ──

    [Fact]
    public async Task S011_LockOfOneRun_ApproveOfTheNext_AndAMappingSave_SerialiseOnTheCompanyLock()
    {
        await using var k = await KitAsync();
        await using var q = _fx.CreateDb();
        var altBasic = (await q.GlAccounts.IgnoreQueryFilters().SingleAsync(a => a.TenantId == k.TenantId && a.CompanyId == k.CompanyId && a.Code == "5011")).Id;
        var mapUser = Guid.NewGuid();
        var first = await RunAsync(k, 1);
        (await ApproveOn(k, first.Id)).Should().BeOfType<OkObjectResult>();
        var second = await RunAsync(k, 2);

        var results = await Together(() => LockOn(k, first.Id), () => ApproveOn(k, second.Id), () => MapOn(k, altBasic, mapUser));

        Status(results[0]).Should().Be(200);
        Status(results[1]).Should().Be(200);
        var mappingSaved = Status(results[2]) == 200;
        if (!mappingSaved) Err(results[2]).Should().Be("locked_into_approved_run");
        // The first run was Approved before the mapping could commit, so it posts the OLD mapping whatever the order.
        BasicAccountOf(await LedgerOf(k, first.Id)).Should().Be("5001");
        // The second run's hash was sealed against whichever state committed first: Lock is not sent back, and posts that state.
        var lockedSecond = await LockOn(k, second.Id);
        Status(lockedSecond).Should().Be(200);
        BasicAccountOf(await LedgerOf(k, second.Id)).Should().Be(mappingSaved ? "5011" : "5001");
    }

    // ── two Locks ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TwoSimultaneousLocks_OfACostedRun_PostExactlyOneCostedSet_TheLoserIsRefused()
    {
        await using var k = await KitAsync();
        var run = await RunAsync(k, 9);
        (await ApproveOn(k, run.Id)).Should().BeOfType<OkObjectResult>();

        var results = await Together(() => LockOn(k, run.Id, Guid.NewGuid()), () => LockOn(k, run.Id, Guid.NewGuid()));

        results.Count(r => Status(r) == 200).Should().Be(1, "exactly one Lock wins");
        results.Single(r => Status(r) != 200).Should().Match<IActionResult>(r => Status(r) == 409);
        var ledger = await LedgerOf(k, run.Id);
        ledger.Where(l => l.DebitAccount.StartsWith("5001")).Sum(l => l.Amount).Should().Be(5000m, "one set of lines, not two");
        ledger.Where(l => l.PostingKey != null).Select(l => l.PostingKey).Should().OnlyHaveUniqueItems();
        ledger.Where(l => l.DebitAccount.StartsWith("5001")).Should().OnlyContain(l => l.CostCenterId != null);
    }

    // ── S-046: a switch flip during Lock ─────────────────────────────────────

    [Fact]
    public async Task S046_SwitchingCostingOff_WhileLockRuns_NeverLeavesAHalfCostedRun()
    {
        await using var k = await KitAsync(costing: true);
        var run = await RunAsync(k, 9);
        (await ApproveOn(k, run.Id)).Should().BeOfType<OkObjectResult>();
        var admin = Guid.NewGuid();

        var results = await Together(
            () => LockOn(k, run.Id),
            async () => { await using var db = k.K.NewContext(); return await CostAccountingKit.Gl(db, k.TenantId, CostAccountingKit.ManagePerms, uid: admin)
                .SetCostAccounting(k.CompanyId, new CostAccountingSettingsRequest(Enabled: false, Reason: "race"), default); });

        Status(results[0]).Should().Be(200, "the flip cannot take effect under a run that awaits Lock, and after Lock a costed run keeps the switch on");
        Status(results[1]).Should().BeOneOf(409, 422);
        await using var read = k.K.NewContext();
        (await read.Companies.AsNoTracking().SingleAsync(c => c.Id == k.CompanyId)).CostAccountingEnabled.Should().BeTrue();
        var ledger = await LedgerOf(k, run.Id);
        ledger.Where(l => l.DebitAccount.StartsWith("5001")).Should().OnlyContain(l => l.CostCenterId != null, "every expense line of the run is costed");
        (await read.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).CostAccountingApplied.Should().BeTrue();
    }

    [Fact]
    public async Task S046_SwitchingCostingOn_WhileLockRuns_NeverLeavesAHalfCostedRun()
    {
        await using var k = await KitAsync(costing: false);
        var run = await RunAsync(k, 9);
        (await ApproveOn(k, run.Id)).Should().BeOfType<OkObjectResult>();
        var admin = Guid.NewGuid();

        var results = await Together(
            () => LockOn(k, run.Id),
            async () => { await using var db = k.K.NewContext(); return await CostAccountingKit.Gl(db, k.TenantId, CostAccountingKit.ManagePerms, uid: admin)
                .SetCostAccounting(k.CompanyId, new CostAccountingSettingsRequest(Enabled: true, Reason: "race"), default); });

        Status(results[0]).Should().Be(200, "a run approved with costing off locks with costing off, whichever commits first");
        var ledger = await LedgerOf(k, run.Id);
        ledger.Should().NotBeEmpty();
        ledger.Should().OnlyContain(l => l.CostCenterId == null, "no run ends half costed, half uncosted");
        await using var read = k.K.NewContext();
        (await read.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).CostAccountingApplied.Should().BeFalse();
    }

    // ── S-011: an employee change committing DURING Lock ─────────────────────

    private sealed class FireOnceBeforeCommandInterceptor(string marker, Func<Task> action) : DbCommandInterceptor
    {
        private int _fired;
        public int Fired => _fired;
        private async Task MaybeAsync(DbCommand c)
        {
            if (c.CommandText.Contains(marker, StringComparison.Ordinal) && Interlocked.Exchange(ref _fired, 1) == 0) await action();
        }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r, CancellationToken ct = default)
        { await MaybeAsync(c); return r; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<int> r, CancellationToken ct = default)
        { await MaybeAsync(c); return r; }
    }

    private ZayraDbContext DbWith(DbCommandInterceptor extra) => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
        .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
        .AddInterceptors(Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance)
        .AddInterceptors(extra).Options);

    private async Task MoveE1ToFinanceAsync(CostingLockKit k)
    {
        await using var db = k.K.NewContext();
        await db.Employees.Where(e => e.Id == k.Emp["E1"].Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.DepartmentId, k.Dept["D-FIN"].Id));
    }

    [Fact]
    public async Task S011_AnEmployeeMove_CommittedJustBeforeTheStamperReads_SendsTheRunBack_AndPostsNothing()
    {
        await using var k = await KitAsync();
        var run = await RunAsync(k, 9);
        (await ApproveOn(k, run.Id)).Should().BeOfType<OkObjectResult>();
        var interceptor = new FireOnceBeforeCommandInterceptor("FROM employees", () => MoveE1ToFinanceAsync(k));

        IActionResult res;
        await using (var lockDb = DbWith(interceptor)) res = await LockOn(k, run.Id, db: lockDb);

        interceptor.Fired.Should().Be(1, "the competing transaction really committed in the middle of Lock");
        Status(res).Should().Be(409);
        Err(res).Should().Be("cost_centre_changed_after_approval");
        (await LedgerOf(k, run.Id)).Should().BeEmpty("no mixed result: nothing posted");
        await using var read = k.K.NewContext();
        (await read.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).Status.Should().Be("Processed");
    }

    [Fact]
    public async Task S011_AnEmployeeMove_CommittedAfterTheJournalWasBuilt_DoesNotChangeWhatPosts()
    {
        await using var k = await KitAsync();
        var run = await RunAsync(k, 9);
        (await ApproveOn(k, run.Id)).Should().BeOfType<OkObjectResult>();
        var interceptor = new FireOnceBeforeCommandInterceptor("INSERT INTO finance_gl_entries", () => MoveE1ToFinanceAsync(k));

        IActionResult res;
        await using (var lockDb = DbWith(interceptor)) res = await LockOn(k, run.Id, db: lockDb);

        interceptor.Fired.Should().Be(1);
        Status(res).Should().Be(200, "the lines are the hash-verified resolution of ONE pass; a later commit is the next run's business");
        var basic = (await LedgerOf(k, run.Id)).Where(l => l.DebitAccount.StartsWith("5001")).Select(l => (l.CostCenterCode, l.Amount)).OrderBy(x => x.CostCenterCode).ToList();
        basic.Should().Equal(("CC-110", 2000m), ("CC-200", 3000m));
    }

    // ── S-015: the hash does not depend on the caller's scope ────────────────

    private static ClaimsPrincipal Caller(Guid tenant, Guid? onlyCompany) => new(new ClaimsIdentity(new[]
    {
        new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Name, "caller"),
        new Claim(ClaimTypes.Role, onlyCompany is null ? "Admin" : "HR Manager"),
    }.Concat(onlyCompany is Guid c ? new[] { new Claim("entity_access", JsonSerializer.Serialize(new { c, r = "HR Manager" })) } : Array.Empty<Claim>()), "test"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S015_TheApprovalHash_IsTheSame_WhoeverRecomputesIt_WhateverTheirCompanyScope(bool scopedApproves)
    {
        await using var k = await KitAsync();
        // a group-level department (no company) in the parent chain: visible to a scoped caller by the config-scope filter, and to a group caller
        var group = new Department { TenantId = k.TenantId, CompanyId = null, Code = "D-GRP", NameEn = "Group", CostCenterId = k.Cc["CC-110"].Id };
        k.Db.Departments.Add(group);
        await k.Db.SaveChangesAsync();
        await k.Db.Departments.Where(d => d.Id == k.Dept["D-FIN"].Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.ParentDepartmentId, (Guid?)group.Id).SetProperty(d => d.CostCenterId, (Guid?)null));
        k.Db.ChangeTracker.Clear();
        var run = await RunAsync(k, 9);

        await using var scoped = _fx.CreateDbWithAccessor(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Caller(k.TenantId, k.CompanyId) } });
        await using var wide = _fx.CreateDbWithAccessor(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Caller(k.TenantId, null) } });
        var (approver, locker) = scopedApproves ? (scoped, wide) : (wide, scoped);

        (await ApproveOn(k, run.Id, approver)).Should().BeOfType<OkObjectResult>();
        var res = await LockOn(k, run.Id, db: locker);

        Status(res).Should().Be(200, Json(res).ToString());
        (await LedgerOf(k, run.Id)).Where(l => l.DebitAccount.StartsWith("5001")).Select(l => (l.CostCenterCode, l.Amount)).OrderBy(x => x.CostCenterCode)
            .Should().Equal(("CC-110", 2000m), ("CC-200", 3000m));
    }
}
