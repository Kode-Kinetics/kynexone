using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostAccountingKit;

namespace Zayra.Api.Tests;

/// <summary>
/// WP-B on real PostgreSQL with the production provider options (retrying execution strategy, advisory-lock guard):
/// the company advisory lock that serialises cost-attribution writers (S-012, S-046 for WP-B's writers), the savepoint-free
/// transaction around each settings write, and the ledger guard's batched reads and sync/async paths against Npgsql.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class CostAccountingPostgresTests
{
    private readonly PostgresFixture _fx;
    public CostAccountingPostgresTests(PostgresFixture fx) => _fx = fx;

    private sealed record Seeded(Guid Tid, Guid CompanyId, Guid BasicAccountId, Guid SellingAccountId);

    private async Task<Seeded> SeedAsync(bool costing = false)
    {
        await using var db = _fx.CreateDb();
        var tid = Guid.NewGuid();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tid, default);
        var co = new Company { TenantId = tid, LegalNameEn = "Co", DefaultCurrency = "SAR", CostAccountingEnabled = costing };
        db.Companies.Add(co);
        var a1 = new GlAccount { TenantId = tid, CompanyId = co.Id, Code = "5001", Name = "Basic", AccountType = "Expense" };
        var a2 = new GlAccount { TenantId = tid, CompanyId = co.Id, Code = "5011", Name = "Selling basic", AccountType = "Expense" };
        db.GlAccounts.AddRange(a1, a2);
        await db.SaveChangesAsync();
        return new Seeded(tid, co.Id, a1.Id, a2.Id);
    }

    private FinanceGlController Ctrl(ZayraDbContext db, Guid tid, Guid? uid = null) => Gl(db, tid, ManagePerms, uid: uid);

    // ── the lock ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AMappingSave_WaitsForTheCompanyLock_AndThenSeesTheRunThatApprovedMeanwhile()
    {
        var s = await SeedAsync();
        var held = new TaskCompletionSource();

        // "Approve": takes the SAME company lock WP-C's Approve takes, holds it, then commits an Approved run.
        await using var approveDb = _fx.CreateDb();
        var approve = FinanceDecisionSerializer.SerializeAsync(approveDb, CostAccountingGuards.LockScope, s.Tid, s.CompanyId, async () =>
        {
            held.SetResult();
            await Task.Delay(1200);
            approveDb.PayrollRuns.Add(new PayrollRun { TenantId = s.Tid, CompanyId = s.CompanyId, Status = "Approved", Year = 2026, Month = 9 });
            await approveDb.SaveChangesAsync();
            return true;
        });

        await held.Task;
        await using var saveDb = _fx.CreateDb();
        var res = await Ctrl(saveDb, s.Tid).SetMappings(s.CompanyId, new() { new GlMappingRequest("EARN:BASIC", s.BasicAccountId) }, default, "r");
        await approve;

        Status(res).Should().Be(409, "the save queued behind the lock and read the committed Approved run");
        ErrorCode(res).Should().Be("locked_into_approved_run");
        await using var check = _fx.CreateDb();
        (await check.GlAccountMappings.IgnoreQueryFilters().CountAsync(m => m.CompanyId == s.CompanyId)).Should().Be(0);
    }

    [Fact]
    public async Task ASwitchChange_WaitsForTheCompanyLock_AndIsRefusedWhenARunApprovedMeanwhile()
    {
        var s = await SeedAsync();
        var held = new TaskCompletionSource();
        await using var approveDb = _fx.CreateDb();
        var approve = FinanceDecisionSerializer.SerializeAsync(approveDb, CostAccountingGuards.LockScope, s.Tid, s.CompanyId, async () =>
        {
            held.SetResult();
            await Task.Delay(1200);
            approveDb.PayrollRuns.Add(new PayrollRun { TenantId = s.Tid, CompanyId = s.CompanyId, Status = "PendingFinanceReview", Year = 2026, Month = 9 });
            await approveDb.SaveChangesAsync();
            return true;
        });

        await held.Task;
        await using var db = _fx.CreateDb();
        var res = await Ctrl(db, s.Tid).SetCostAccounting(s.CompanyId, new CostAccountingSettingsRequest(Enabled: true, Reason: "go"), default);
        await approve;

        Status(res).Should().Be(409);
        await using var check = _fx.CreateDb();
        (await check.Companies.SingleAsync(c => c.Id == s.CompanyId)).CostAccountingEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task TwoWritersOfDifferentCompanies_DoNotBlockEachOther()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        var held = new TaskCompletionSource();
        await using var holder = _fx.CreateDb();
        var hold = FinanceDecisionSerializer.SerializeAsync(holder, CostAccountingGuards.LockScope, a.Tid, a.CompanyId, async () =>
        {
            held.SetResult();
            await Task.Delay(1500);
            return true;
        });
        await held.Task;

        await using var db = _fx.CreateDb();
        var started = DateTime.UtcNow;
        var res = await Ctrl(db, b.Tid).SetMappings(b.CompanyId, new() { new GlMappingRequest("EARN:BASIC", b.BasicAccountId) }, default);
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(1.2), "another company's lock is not ours");
        Status(res).Should().Be(200);
        await hold;
    }

    [Fact]
    public async Task ATenantDefaultSave_TakesEveryCompanyLock_AndWaitsForAnyOfThem()
    {
        var s = await SeedAsync();
        Guid groupAccount;   // the tenant-default chart is seeded with the drivers
        await using (var q = _fx.CreateDb()) groupAccount = (await q.GlAccounts.IgnoreQueryFilters().Where(a => a.TenantId == s.Tid && a.CompanyId == null && a.AccountType == "Expense").OrderBy(a => a.Code).FirstAsync()).Id;

        var held = new TaskCompletionSource();
        await using var holder = _fx.CreateDb();
        var hold = FinanceDecisionSerializer.SerializeAsync(holder, CostAccountingGuards.LockScope, s.Tid, s.CompanyId, async () =>
        {
            held.SetResult();
            await Task.Delay(1000);
            return true;
        });
        await held.Task;

        await using var db = _fx.CreateDb();
        var started = DateTime.UtcNow;
        var res = await Ctrl(db, s.Tid).SetMappings(null, new() { new GlMappingRequest("EARN:BASIC", groupAccount) }, default);
        (DateTime.UtcNow - started).Should().BeGreaterThan(TimeSpan.FromMilliseconds(500), "a tenant-default write queues behind a company's attribution writer");
        Status(res).Should().Be(200);
        await hold;
    }

    // ── the settings write, end to end ──────────────────────────────────────

    [Fact]
    public async Task EnablingTheSwitch_OnPostgres_CommitsTheFlagAndItsAuditRowTogether()
    {
        var s = await SeedAsync();
        var user = Guid.NewGuid();
        await using var db = _fx.CreateDb();

        var res = await Ctrl(db, s.Tid, user).SetCostAccounting(s.CompanyId, new CostAccountingSettingsRequest(Enabled: true, Reason: "Pilot go-live"), default);

        Status(res).Should().Be(200);
        await using var check = _fx.CreateDb();
        (await check.Companies.SingleAsync(c => c.Id == s.CompanyId)).CostAccountingEnabled.Should().BeTrue();
        var audit = await check.AuditLogs.IgnoreQueryFilters().SingleAsync(a => a.CompanyId == s.CompanyId && a.Action == "finance.gl.cost_accounting.switch_changed");
        (audit.UserId, audit.TenantId).Should().Be((user, (Guid?)s.Tid));
    }

    [Fact]
    public async Task Readiness_OnPostgres_RunsAndReturnsEveryCheck()
    {
        var s = await SeedAsync();
        await using var db = _fx.CreateDb();

        var res = await Gl(db, s.Tid, ReadPerms).CostAccountingReadiness(s.CompanyId, default);

        Status(res).Should().Be(200);
        Body(res).P("checks").GetArrayLength().Should().Be(CostAccountingReadinessService.CheckKeys.Length);
    }

    [Fact]
    public async Task Sod_OnPostgres_SeesTheMappingChange_AndTheLockWindow()
    {
        var s = await SeedAsync(costing: true);
        var u = Guid.NewGuid();
        await using (var db = _fx.CreateDb())
            Status(await Ctrl(db, s.Tid, u).SetMappings(s.CompanyId, new() { new GlMappingRequest("EARN:BASIC", s.BasicAccountId) }, default, "r")).Should().Be(200);

        await using var q = _fx.CreateDb();
        (await CostAccountingSod.HasConflictAsync(q, s.Tid, s.CompanyId, "2026-09", u, default)).Should().BeTrue();
        (await CostAccountingSod.HasConflictAsync(q, s.Tid, s.CompanyId, "2026-09", Guid.NewGuid(), default)).Should().BeFalse();

        // An August run locked AFTER the change reviewed it: no conflict for September.
        q.PayrollRuns.Add(new PayrollRun { TenantId = s.Tid, CompanyId = s.CompanyId, Status = "Locked", Year = 2026, Month = 8, LockedAtUtc = DateTime.UtcNow.AddMinutes(1) });
        await q.SaveChangesAsync();
        (await CostAccountingSod.HasConflictAsync(q, s.Tid, s.CompanyId, "2026-09", u, default)).Should().BeFalse();
    }

    // ── the ledger guard on Npgsql ──────────────────────────────────────────

    private static FinanceGlEntry Line(Seeded s, string debit, string credit, decimal amount, CostCenter? cc, Guid? company = null) => new()
    {
        TenantId = s.Tid, CompanyId = company ?? s.CompanyId, SourceModule = "Payroll", SourceEntityId = Guid.NewGuid(), SourceEntityRef = "RUN",
        EventType = "PayrollAccrual", DebitAccount = debit, CreditAccount = credit, Amount = amount, Currency = "SAR",
        EntryDate = new DateOnly(2026, 9, 30), Period = "2026-09", Description = "t",
        CostCenterId = cc?.Id, CostCenterCode = cc?.Code, CostCenterName = cc?.Name,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheGuard_OnPostgres_EnforcesRequired_Reversals_AndSums(bool sync)
    {
        var s = await SeedAsync(costing: true);
        await using var db = _fx.CreateDb();
        var req = await db.GlAccounts.IgnoreQueryFilters().SingleAsync(a => a.TenantId == s.Tid && a.CompanyId == s.CompanyId && a.Code == "5001");
        req.CostCenterRule = "Required";
        var liab = new GlAccount { TenantId = s.Tid, CompanyId = s.CompanyId, Code = "2101", Name = "Payable", AccountType = "Liability", CostCenterRule = "NotAllowed" };
        var cc = new CostCenter { TenantId = s.Tid, CompanyId = s.CompanyId, Code = "SALES", Name = "Sales" };
        db.GlAccounts.Add(liab); db.CostCenters.Add(cc);
        await db.SaveChangesAsync();

        async Task Save() { if (sync) db.SaveChanges(); else await db.SaveChangesAsync(); }

        db.FinanceGlEntries.Add(Line(s, "5001 - Basic", "2101 - Payable", 100m, null));
        var act = () => Save();
        (await act.Should().ThrowAsync<GlCostCentreRuleException>()).Which.Code.Should().Be("cost_centre_required");
        db.ChangeTracker.Clear();

        var orig = Line(s, "5001 - Basic", "2101 - Payable", 100m, cc);
        db.FinanceGlEntries.Add(orig);
        await Save();

        FinanceGlEntry Contra(decimal amt) => new()
        {
            TenantId = s.Tid, CompanyId = s.CompanyId, SourceModule = "Payroll", SourceEntityId = orig.SourceEntityId, SourceEntityRef = "RUN", EventType = "Rev",
            DebitAccount = orig.CreditAccount, CreditAccount = orig.DebitAccount, Amount = amt, Currency = "SAR", EntryDate = orig.EntryDate, Period = orig.Period,
            Description = "rev", ReversalOfEntryId = orig.Id, CostCenterId = orig.CostCenterId, CostCenterCode = orig.CostCenterCode, CostCenterName = orig.CostCenterName,
        };
        db.FinanceGlEntries.Add(Contra(60m));
        await Save();
        db.FinanceGlEntries.Add(Contra(40.01m));
        var over = () => Save();
        (await over.Should().ThrowAsync<GlCostCentreRuleException>()).Which.Code.Should().Be("reversal_invalid");
    }
}
