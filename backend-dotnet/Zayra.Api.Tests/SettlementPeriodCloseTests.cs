using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// POD-B1 — GL settlement &amp; period-close. Proves the payroll control accounts become CLEARABLE and the
/// month CLOSEABLE with ZERO manual journal entries:
///   Lock (accrue) → settle (net pay) → remit (statutory) leaves 2100/2101/2106/2102/2107 at ZERO and
///   1000 Cash/Bank carrying the actual outflow; a closed period rejects further postings.
/// Uses the hand-worked KSA figures from FinanceP1BonusGlTests #9:
///   Earnings DR 13,000 | GOSI EE 1,170 (2101) | GOSI ER 1,170 (2106) | ER expense 1,170 (5101) |
///   Net 11,830 (2100) | accrual total DR==CR==14,170.
/// </summary>
public class SettlementPeriodCloseTests
{
    // ── Harness ─────────────────────────────────────────────────────────────────

    private static (ZayraDbContext db, SqliteConnection conn) CreateSqliteDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static PayrollController MakeKsaPayrollCtrl(ZayraDbContext db, Guid tenantId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "test-user"),
            new("permission", "payroll.export"),
            new("permission", "payroll.lock"),
        };
        var httpCtx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var ctrl = new PayrollController(
            db, new _SpUnrestrictedScope(), new _SpHttpAccessor(httpCtx), new _SpNullNotifications(),
            new _SpKsaPackResolver(), _SpKsaRules.Rules, new _SpNullLetterService(), new _SpNullDocStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private static FinanceGlController MakeGlCtrl(ZayraDbContext db, Guid tenantId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "finance-user"),
            new("permission", "finance.gl.manage"),
            new("permission", "finance.gl.read"),
        };
        var httpCtx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var ctrl = new FinanceGlController(db);
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    // Drives a run from scratch to Locked (accrual posted) and returns (run, batch) with the batch
    // already Accepted (bank confirmed) so settle() can run. KSA pack → non-zero statutory lines.
    private static async Task<(PayrollRun run, PayrollPaymentBatch batch)> SeedLockedRunWithAcceptedBatch(
        ZayraDbContext db, Guid tenantId, PayrollController ctrl, int year = 2026, int month = 6)
    {
        var (_, run, _) = await SeedMinimalRun(db, tenantId, year, month);
        await SeedKsaCompany(db, tenantId, run);

        (await ctrl.Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        run.Status = "Approved";
        await db.SaveChangesAsync();
        (await ctrl.Lock(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await ctrl.CreatePaymentBatch(run.Id, new PayrollPaymentBatchRequest("WPS", "SAR"), CancellationToken.None))
            .Should().BeOfType<CreatedResult>();
        var batch = await db.PayrollPaymentBatches.FirstAsync(b => b.TenantId == tenantId && b.PayrollRunId == run.Id);
        // Fast-forward the WPS lifecycle: bank/Mudad accepted the file (money instructed).
        batch.WpsStatus = WpsStatuses.Accepted;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        run = await db.PayrollRuns.FirstAsync(r => r.Id == run.Id);
        batch = await db.PayrollPaymentBatches.FirstAsync(b => b.Id == batch.Id);
        return (run, batch);
    }

    private static async Task<List<FinanceGlEntry>> RunGl(ZayraDbContext db, Guid runId) =>
        await db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.SourceModule == "Payroll" && x.SourceEntityId == runId).ToListAsync();

    // Σ(credits to acct) − Σ(debits to acct) over ALL lines = the account's true carrying balance.
    // A contra is a real posting; the original's IsReversed flag is an audit link, not a balance
    // exclusion — original CR + contra DR cancel here exactly as they do in the ledger.
    private static decimal NetLiability(IEnumerable<FinanceGlEntry> gl, string acctCodeFragment)
    {
        var list = gl.ToList();
        var cr = list.Where(l => l.CreditAccount.Contains(acctCodeFragment)).Sum(l => l.Amount);
        var dr = list.Where(l => l.DebitAccount.Contains(acctCodeFragment)).Sum(l => l.Amount);
        return cr - dr;
    }

    private static (decimal dr, decimal cr) Totals(IEnumerable<FinanceGlEntry> gl)
    {
        var list = gl.ToList();
        var dr = list.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);
        var cr = list.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount);
        return (dr, cr);
    }

    // ── 1. Headline: full cycle nets the control accounts to zero ────────────────

    [Fact]
    public async Task FullCycle_LockSettleRemit_ClearsControlAccountsToZero()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var ctrl = MakeKsaPayrollCtrl(db, tenantId);
        var (run, batch) = await SeedLockedRunWithAcceptedBatch(db, tenantId, ctrl);

        // Pay: settle net.
        (await ctrl.SettlePaymentBatch(batch.Id, new SettlePaymentBatchRequest("WPS-REF", new DateOnly(2026, 7, 15)), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        // Remit: all applicable statutory (KSA → GOSI only; TAX/LOAN have nothing to remit).
        (await ctrl.RemitStatutory(run.Id, new RemitStatutoryRequest("All", "GOSI-REF", new DateOnly(2026, 7, 20)), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var gl = await RunGl(db, run.Id);
        var (dr, cr) = Totals(gl);
        dr.Should().Be(cr, "the whole run ledger must stay balanced across accrual + settlement + remittance");

        NetLiability(gl, "2100").Should().Be(0m, "net pay settlement must clear Salaries Payable");
        NetLiability(gl, "2101").Should().Be(0m, "GOSI remittance must clear employee social-insurance payable");
        NetLiability(gl, "2106").Should().Be(0m, "GOSI remittance must clear employer social-insurance payable");

        // Cash/Bank (asset) is credited on every outflow: net 11,830 + statutory (EE 1,170 + ER 1,410
        // = 2,580) = 14,410. (KSA employer side = 9% annuity + 0.75% SANED + 2% occupational hazard.)
        NetLiability(gl, "1000 - Cash/Bank").Should().Be(14_410m, "Cash/Bank carries the actual total outflow");
        // Employer expense (P&L) is intentionally NOT cleared by remittance.
        var expenseDr = gl.Where(l => l.DebitAccount.Contains("5101")).Sum(l => l.Amount);
        expenseDr.Should().Be(1_410m, "employer statutory EXPENSE (5101) is a P&L line and stays booked");
    }

    // ── 2. Net-pay settlement clears 2100 and flips the batch to Paid ────────────

    [Fact]
    public async Task Settle_PostsDr2100Cr1000_ForNet_AndSetsBatchPaid()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var ctrl = MakeKsaPayrollCtrl(db, tenantId);
        var (run, batch) = await SeedLockedRunWithAcceptedBatch(db, tenantId, ctrl);

        (await ctrl.SettlePaymentBatch(batch.Id, new SettlePaymentBatchRequest(), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var settlement = (await RunGl(db, run.Id)).Where(l => l.EventType == GlEventTypes.NetSettlement).ToList();
        settlement.Should().HaveCount(2, "exactly one DR 2100 and one CR 1000 line");
        settlement.Single(l => !string.IsNullOrEmpty(l.DebitAccount)).DebitAccount.Should().Contain("2100");
        settlement.Single(l => !string.IsNullOrEmpty(l.CreditAccount)).CreditAccount.Should().Contain("1000 - Cash/Bank");
        settlement.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount).Should().Be(11_830m);

        NetLiability(await RunGl(db, run.Id), "2100").Should().Be(0m);
        (await db.PayrollPaymentBatches.AsNoTracking().FirstAsync(b => b.Id == batch.Id))
            .WpsStatus.Should().Be(WpsStatuses.Paid);
    }

    // ── 3. Settlement is idempotent (second settle is a no-op → 409) ─────────────

    [Fact]
    public async Task Settle_Twice_SecondIsConflict_NoDoublePost()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var ctrl = MakeKsaPayrollCtrl(db, tenantId);
        var (run, batch) = await SeedLockedRunWithAcceptedBatch(db, tenantId, ctrl);

        (await ctrl.SettlePaymentBatch(batch.Id, new SettlePaymentBatchRequest(), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        var countAfterFirst = (await RunGl(db, run.Id)).Count(l => l.EventType == GlEventTypes.NetSettlement);

        // The status guard (batch is now Paid) shadows the GL idempotency guard. Force the batch back to
        // Accepted to exercise the idempotency guard DIRECTLY: it must still refuse to double-post.
        var reload = await db.PayrollPaymentBatches.FirstAsync(b => b.Id == batch.Id);
        reload.WpsStatus = WpsStatuses.Accepted;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await ctrl.SettlePaymentBatch(batch.Id, new SettlePaymentBatchRequest(), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>("re-settling must not double-post (GL idempotency guard)");

        (await RunGl(db, run.Id)).Count(l => l.EventType == GlEventTypes.NetSettlement)
            .Should().Be(countAfterFirst);
    }

    // ── 4. Settlement reversal re-opens 2100 and reverts the batch to Accepted ───

    [Fact]
    public async Task SettleReverse_ReopensPayable_AndRevertsBatch()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var ctrl = MakeKsaPayrollCtrl(db, tenantId);
        var (run, batch) = await SeedLockedRunWithAcceptedBatch(db, tenantId, ctrl);

        await ctrl.SettlePaymentBatch(batch.Id, new SettlePaymentBatchRequest(), CancellationToken.None);
        NetLiability(await RunGl(db, run.Id), "2100").Should().Be(0m);

        (await ctrl.ReverseSettlement(batch.Id, new PayrollReasonRequest("bank recall"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        // 2100 back to its accrued balance (11,830), settlement no longer live.
        NetLiability(await RunGl(db, run.Id), "2100").Should().Be(11_830m);
        (await db.PayrollPaymentBatches.AsNoTracking().FirstAsync(b => b.Id == batch.Id))
            .WpsStatus.Should().Be(WpsStatuses.Accepted);
    }

    // ── 5. Void after settle+remit reverses EVERYTHING (P1-5 linkage) ───────────

    [Fact]
    public async Task Void_AfterSettleAndRemit_ReversesAll_AndRevertsBatch()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var ctrl = MakeKsaPayrollCtrl(db, tenantId);
        var (run, batch) = await SeedLockedRunWithAcceptedBatch(db, tenantId, ctrl);

        await ctrl.SettlePaymentBatch(batch.Id, new SettlePaymentBatchRequest(), CancellationToken.None);
        await ctrl.RemitStatutory(run.Id, new RemitStatutoryRequest("All"), CancellationToken.None);

        (await ctrl.VoidRun(run.Id, new PayrollDecisionRequest("wrong run"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var gl = await RunGl(db, run.Id);
        var (dr, cr) = Totals(gl);
        dr.Should().Be(cr, "including contras the ledger stays balanced");
        // Nothing live remains on any account.
        foreach (var acct in new[] { "2100", "2101", "2106", "1000 - Cash/Bank", "5101" })
            NetLiability(gl, acct).Should().Be(0m, $"void must fully unwind {acct}");
        // P1-5 — operational state matches the returned-to-zero ledger.
        (await db.PayrollPaymentBatches.AsNoTracking().FirstAsync(b => b.Id == batch.Id))
            .WpsStatus.Should().Be(WpsStatuses.Accepted);
    }

    // ── 6. Remit GOSI clears only 2101+2106; idempotent ─────────────────────────

    [Fact]
    public async Task Remit_Gosi_ClearsOnlyGosiLiabilities_AndIsIdempotent()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var ctrl = MakeKsaPayrollCtrl(db, tenantId);
        var (run, batch) = await SeedLockedRunWithAcceptedBatch(db, tenantId, ctrl);

        (await ctrl.RemitStatutory(run.Id, new RemitStatutoryRequest("GOSI", "REF1"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var gl = await RunGl(db, run.Id);
        NetLiability(gl, "2101").Should().Be(0m);
        NetLiability(gl, "2106").Should().Be(0m);
        // 5101 employer EXPENSE untouched by remittance.
        gl.Where(l => l.DebitAccount.Contains("5101") && l.EventType == GlEventTypes.Accrual).Sum(l => l.Amount)
            .Should().Be(1_410m);
        gl.Any(l => l.EventType == GlEventTypes.RemitPrefix + "GOSI" && l.DebitAccount.Contains("5101"))
            .Should().BeFalse("remittance clears the 2106 liability, never the 5101 expense");

        var countAfterFirst = gl.Count(l => l.EventType == GlEventTypes.RemitPrefix + "GOSI");
        (await ctrl.RemitStatutory(run.Id, new RemitStatutoryRequest("GOSI", "REF1"), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>("re-remitting a group is a no-op");
        (await RunGl(db, run.Id)).Count(l => l.EventType == GlEventTypes.RemitPrefix + "GOSI")
            .Should().Be(countAfterFirst);
    }

    // ── 7. Remap-immunity (P0-1): a CoA remap between Lock and Pay still nets to 0 ─

    [Fact]
    public async Task Settle_AfterNetPayableRemap_StillClearsTheOriginalAccrualAccount()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var ctrl = MakeKsaPayrollCtrl(db, tenantId);
        var (run, batch) = await SeedLockedRunWithAcceptedBatch(db, tenantId, ctrl);

        // Finance remaps NET_PAYABLE to a DIFFERENT account AFTER the accrual posted.
        var newPayable = new GlAccount { TenantId = tenantId, CompanyId = run.CompanyId, Code = "2199X", Name = "Reworked Payable", AccountType = "Liability" };
        db.GlAccounts.Add(newPayable);
        await db.SaveChangesAsync();
        db.GlAccountMappings.Add(new GlAccountMapping { TenantId = tenantId, CompanyId = run.CompanyId, DriverKey = "NET_PAYABLE", AccountId = newPayable.Id });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await ctrl.SettlePaymentBatch(batch.Id, new SettlePaymentBatchRequest(), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var gl = await RunGl(db, run.Id);
        // Settlement DR'd the ORIGINAL accrual account (2100), not the remapped 2199X → 2100 nets to zero.
        NetLiability(gl, "2100").Should().Be(0m, "settlement clears the stored accrual account, immune to remap");
        gl.Any(l => l.EventType == GlEventTypes.NetSettlement && l.DebitAccount.Contains("2199X"))
            .Should().BeFalse("settlement must not chase the post-accrual remap");
    }

    // ── 8. Company-first Cash remap governs the settlement credit line ──────────

    [Fact]
    public async Task Settle_WithCompanyCashOverride_CreditsTheCompanyAccount()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var ctrl = MakeKsaPayrollCtrl(db, tenantId);
        var (run, batch) = await SeedLockedRunWithAcceptedBatch(db, tenantId, ctrl);

        var companyBank = new GlAccount { TenantId = tenantId, CompanyId = run.CompanyId, Code = "1050", Name = "Company Bank", AccountType = "Asset" };
        db.GlAccounts.Add(companyBank);
        await db.SaveChangesAsync();
        db.GlAccountMappings.Add(new GlAccountMapping { TenantId = tenantId, CompanyId = run.CompanyId, DriverKey = "CASH_BANK", AccountId = companyBank.Id });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await ctrl.SettlePaymentBatch(batch.Id, new SettlePaymentBatchRequest(), CancellationToken.None);

        var settlement = (await RunGl(db, run.Id)).Single(l => l.EventType == GlEventTypes.NetSettlement && !string.IsNullOrEmpty(l.CreditAccount));
        settlement.CreditAccount.Should().Be("1050 - Company Bank", "company-first CASH_BANK override governs the cash line");
    }

    // ── 9. Balance guard still catches a tampered clearing journal ──────────────
    // (Exercised implicitly by every settle/remit; here we assert an un-accrued run cannot settle.)

    [Fact]
    public async Task Settle_UnaccruedRun_IsRejected()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var ctrl = MakeKsaPayrollCtrl(db, tenantId);
        var (_, run, _) = await SeedMinimalRun(db, tenantId);
        await SeedKsaCompany(db, tenantId, run);
        await ctrl.Process(run.Id, CancellationToken.None);
        // Force a batch on a NON-locked run (no accrual GL).
        var batch = new PayrollPaymentBatch { TenantId = tenantId, PayrollRunId = run.Id, BatchNumber = "PAY-X", WpsStatus = WpsStatuses.Accepted, TotalAmount = 100m };
        db.PayrollPaymentBatches.Add(batch);
        await db.SaveChangesAsync();

        var res = await ctrl.SettlePaymentBatch(batch.Id, new SettlePaymentBatchRequest(), CancellationToken.None);
        res.Should().BeOfType<BadRequestObjectResult>("cannot settle a run that is not Locked / not accrued");
    }

    // ── 10. Period close blocks Lock; reopen re-enables ─────────────────────────

    [Fact]
    public async Task ClosedPeriod_BlocksLock_ReopenReenables()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var payroll = MakeKsaPayrollCtrl(db, tenantId);
        var gl = MakeGlCtrl(db, tenantId);
        var (_, run, _) = await SeedMinimalRun(db, tenantId, 2026, 6);
        await SeedKsaCompany(db, tenantId, run);
        await payroll.Process(run.Id, CancellationToken.None);
        run.Status = "Approved";
        await db.SaveChangesAsync();

        // Close 2026-06 for the run's company.
        (await gl.ClosePeriod("2026-06", run.CompanyId, new GlPeriodActionRequest("year-end freeze"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var blocked = await payroll.Lock(run.Id, CancellationToken.None);
        blocked.Should().BeOfType<UnprocessableEntityObjectResult>();
        (await db.FinanceGlEntries.CountAsync(x => x.SourceModule == "Payroll" && x.SourceEntityId == run.Id))
            .Should().Be(0, "a closed period must post no accrual");

        // Reopen (reason required) then Lock succeeds.
        (await gl.ReopenPeriod("2026-06", run.CompanyId, new GlPeriodActionRequest("correction window"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await payroll.Lock(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await db.FinanceGlEntries.CountAsync(x => x.SourceModule == "Payroll" && x.SourceEntityId == run.Id))
            .Should().BeGreaterThan(0);
    }

    // ── 11. Period close blocks settle; reopen re-enables ───────────────────────

    [Fact]
    public async Task ClosedPeriod_BlocksSettle_ReopenReenables()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var payroll = MakeKsaPayrollCtrl(db, tenantId);
        var gl = MakeGlCtrl(db, tenantId);
        var (run, batch) = await SeedLockedRunWithAcceptedBatch(db, tenantId, payroll);

        (await gl.ClosePeriod("2026-07", run.CompanyId, new GlPeriodActionRequest("freeze"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var blocked = await payroll.SettlePaymentBatch(batch.Id, new SettlePaymentBatchRequest("R", new DateOnly(2026, 7, 15)), CancellationToken.None);
        blocked.Should().BeOfType<UnprocessableEntityObjectResult>();
        (await RunGl(db, run.Id)).Any(l => l.EventType == GlEventTypes.NetSettlement).Should().BeFalse();

        (await gl.ReopenPeriod("2026-07", run.CompanyId, new GlPeriodActionRequest("reopen"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await payroll.SettlePaymentBatch(batch.Id, new SettlePaymentBatchRequest("R", new DateOnly(2026, 7, 15)), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
    }

    // ── 12. Reopen requires a reason and both close/reopen are audited ──────────

    [Fact]
    public async Task Reopen_RequiresReason_AndActionsAreAudited()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var gl = MakeGlCtrl(db, tenantId);

        (await gl.ClosePeriod("2026-06", null, new GlPeriodActionRequest("group close"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await gl.ReopenPeriod("2026-06", null, new GlPeriodActionRequest(null), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>("reopen without a reason must be rejected");
        (await gl.ReopenPeriod("2026-06", null, new GlPeriodActionRequest("auditor request"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var audits = await db.AuditLogs.AsNoTracking().Where(a => a.TenantId == tenantId).Select(a => a.Action).ToListAsync();
        audits.Should().Contain("finance.gl.period.closed");
        audits.Should().Contain("finance.gl.period.reopened");
    }

    // ── 13. Group-wide (tenant) close blocks a company run too ──────────────────

    [Fact]
    public async Task GroupWideClose_BlocksCompanyRunLock()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var payroll = MakeKsaPayrollCtrl(db, tenantId);
        var gl = MakeGlCtrl(db, tenantId);
        var (_, run, _) = await SeedMinimalRun(db, tenantId, 2026, 6);
        await SeedKsaCompany(db, tenantId, run);
        await payroll.Process(run.Id, CancellationToken.None);
        run.Status = "Approved";
        await db.SaveChangesAsync();

        // Group-wide close (companyId null) — must block the company-scoped run.
        (await gl.ClosePeriod("2026-06", null, new GlPeriodActionRequest("group freeze"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await payroll.Lock(run.Id, CancellationToken.None)).Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    // ── 14. WPS transition table stays additive (guards WpsTests #509/#527) ─────

    [Fact]
    public void WpsTransitions_PaidEdgesAreAdditive_ReconciledStillTerminal()
    {
        WpsTransitions.IsAllowed(WpsStatuses.Accepted, WpsStatuses.Paid).Should().BeTrue();
        WpsTransitions.IsAllowed(WpsStatuses.Paid, WpsStatuses.Reconciled).Should().BeTrue();
        WpsTransitions.IsAllowed(WpsStatuses.Accepted, WpsStatuses.Reconciled).Should().BeTrue("the legacy edge is preserved");
        WpsTransitions.AllowedFrom(WpsStatuses.Reconciled).Should().BeEmpty("Reconciled stays terminal");
    }

    // ── 15. Reconciled is gated on settlement GL (P1-6) ─────────────────────────

    [Fact]
    public async Task Reconcile_BeforeSettlement_IsBlocked_AfterSettlement_IsAllowed()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn; await using var __ = db;
        var tenantId = Guid.NewGuid();
        var ctrl = MakeKsaPayrollCtrl(db, tenantId);
        var (run, batch) = await SeedLockedRunWithAcceptedBatch(db, tenantId, ctrl);

        // A generated WPS file is required by UpdateWpsStatus — seed a minimal one.
        db.WPSFileBatches.Add(new WPSFileBatch { TenantId = tenantId, PaymentBatchId = batch.Id, Status = WpsStatuses.Accepted, SifFileName = "f.sif" });
        await db.SaveChangesAsync();

        // Accepted → Reconciled before any settlement GL → blocked.
        (await ctrl.UpdateWpsStatus(batch.Id, new WpsStatusRequest(WpsStatuses.Reconciled, null, "ACK"), CancellationToken.None))
            .Should().BeOfType<UnprocessableEntityObjectResult>("cannot reconcile with Salaries Payable still open");

        // Settle → Paid, then Paid → Reconciled is allowed.
        await ctrl.SettlePaymentBatch(batch.Id, new SettlePaymentBatchRequest(), CancellationToken.None);
        (await ctrl.UpdateWpsStatus(batch.Id, new WpsStatusRequest(WpsStatuses.Reconciled, null, "ACK"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
    }

    // ── Shared seed helpers (mirrors FinanceP1BonusGlTests) ─────────────────────

    private static async Task<(Employee emp, PayrollRun run, SalaryStructure str)> SeedMinimalRun(
        ZayraDbContext db, Guid tenantId, int year = 2026, int month = 6)
    {
        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = "STR-BASE", Name = "Base",
            Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        };
        db.SalaryStructures.Add(structure);

        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E001", FullName = "Ali Hassan",
            Status = "Active", JoiningDate = new DateTime(2023, 1, 1),
            WorkEmail = "ali@test.com", Nationality = "SAU", ContractType = "Indefinite",
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id,
            BasicSalary = 10_000m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id,
            Iban = "SA4420000001234567891234", MolId = "MOL-E001", SalaryCurrency = "SAR",
        });

        var run = new PayrollRun
        {
            TenantId = tenantId, Year = year, Month = month, Status = "Draft",
            TotalNetSalary = 0m, TotalGrossSalary = 0m,
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return (emp, run, structure);
    }

    private static async Task SeedKsaCompany(ZayraDbContext db, Guid tenantId, PayrollRun run)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Test KSA Co",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR",
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        run.CompanyId = company.Id;
        await db.SaveChangesAsync();
    }
}

// ── Test doubles (file-scoped; mirror FinanceP1BonusGlTests) ────────────────────

file static class _SpKsaRules
{
    internal static readonly StubRuleReader Rules = new StubRuleReader()
        .Set("gosi.saudi_employee_rate",            0.09m)
        .Set("gosi.saudi_employer_rate",            0.09m)
        .Set("gosi.saned_rate",                     0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar",       45_000m)
        .Set("ot.standard_multiplier",              1.5m)
        .Set("ot.standard_monthly_hours",           240m)
        .Set("lop.monthly_day_divisor",             30m)
        .Set("lop.standard_work_minutes_per_day",   480m);
}

file sealed class _SpUnrestrictedScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _SpHttpAccessor : IHttpContextAccessor
{
    public _SpHttpAccessor(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _SpNullNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _SpKsaPackResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_SpKsaRules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _SpNullLetterService : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _SpNullDocStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
