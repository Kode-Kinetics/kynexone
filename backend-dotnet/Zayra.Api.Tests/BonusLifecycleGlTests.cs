using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// POD-B1b — bonus double-count + company-first GL routing.
///
/// The defect these tests lock down: a bonus paid through payroll was expensed TWICE — once by
/// <c>ApproveBatch</c> (DR 6100 / CR 2300) and again by the payroll Lock journal's earnings loop — and
/// the 2300 credit could never be cleared, because <c>MarkBatchPaid</c> 409s the moment Process sets
/// <c>IsLockedByPayroll</c>. Net effect per bonus: P&amp;L overstated by ~1×, and a permanent orphan
/// credit on the balance sheet.
///
/// Invariants proven here, on every terminal path:
///   • bonus expense (6100) is debited EXACTLY ONCE across the lifecycle;
///   • Bonus Payable (2300) nets to ZERO;
///   • every journal balances, is idempotent, and is reversible via contra;
///   • accounts + currency resolve company-first, and a per-company period close blocks only that company.
/// </summary>
public class BonusLifecycleGlTests
{
    private const string ExpenseAcct = "6100";
    private const string PayableAcct = "2300";
    private const string CashAcct    = "1000";
    private const string TaxAcct     = "2102";

    // ── Harness ─────────────────────────────────────────────────────────────────

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static ClaimsPrincipal Principal(Guid tenantId, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "b1b-user"),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static PayrollController MakePayrollCtrl(ZayraDbContext db, Guid tenantId)
    {
        var httpCtx = new DefaultHttpContext { User = Principal(tenantId, "payroll.lock", "payroll.export") };
        var ctrl = new PayrollController(
            db, new _B1bScope(), new _B1bHttp(httpCtx), new _B1bNotifications(),
            new _B1bKsaResolver(), _B1bRules.Rules, new _B1bLetters(), new _B1bDocs(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private static BonusesController MakeBonusCtrl(ZayraDbContext db, Guid tenantId)
    {
        var httpCtx = new DefaultHttpContext { User = Principal(tenantId, "payroll.write", "payroll.approve") };
        var ctrl = new BonusesController(db, new _B1bScope());
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private static FinanceGlController MakeGlCtrl(ZayraDbContext db, Guid tenantId)
    {
        var httpCtx = new DefaultHttpContext { User = Principal(tenantId, "finance.gl.manage", "finance.gl.read") };
        var ctrl = new FinanceGlController(db);
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    // Σ over EVERY posted line (bonus module + payroll), regardless of journal. A contra is a real
    // posting; IsReversed is an audit link, not a balance exclusion.
    private static async Task<List<FinanceGlEntry>> AllGl(ZayraDbContext db, Guid tenantId) =>
        await db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId).ToListAsync();

    private static decimal Debits(IEnumerable<FinanceGlEntry> gl, string acct) =>
        gl.Where(l => l.DebitAccount.Contains(acct)).Sum(l => l.Amount);
    private static decimal Credits(IEnumerable<FinanceGlEntry> gl, string acct) =>
        gl.Where(l => l.CreditAccount.Contains(acct)).Sum(l => l.Amount);
    /// <summary>Carrying balance of a liability: Σ credits − Σ debits. Zero ⇒ fully cleared.</summary>
    private static decimal NetLiability(IEnumerable<FinanceGlEntry> gl, string acct) =>
        Credits(gl, acct) - Debits(gl, acct);

    private static void AssertBalanced(IEnumerable<FinanceGlEntry> gl)
    {
        var list = gl.ToList();
        var dr = list.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);
        var cr = list.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount);
        dr.Should().Be(cr, "every journal ever posted must keep the ledger balanced");
    }

    // ── Seed helpers ────────────────────────────────────────────────────────────

    private static async Task<Company> SeedCompany(
        ZayraDbContext db, Guid tenantId, string name = "Co A", string currency = "SAR")
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = name, CountryCode = "SAU",
            Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = currency,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company;
    }

    private static async Task<Employee> SeedEmployee(
        ZayraDbContext db, Guid tenantId, Guid companyId, string code, decimal basic = 10_000m)
    {
        var structure = await db.SalaryStructures.FirstOrDefaultAsync(s => s.TenantId == tenantId);
        if (structure is null)
        {
            structure = new SalaryStructure
            {
                TenantId = tenantId, Code = "STR-BASE", Name = "Base",
                Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
            };
            db.SalaryStructures.Add(structure);
            await db.SaveChangesAsync();
        }
        var emp = new Employee
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeCode = code, FullName = $"Emp {code}",
            Status = "Active", JoiningDate = new DateTime(2023, 1, 1),
            WorkEmail = $"{code}@test.com", Nationality = "SAU", ContractType = "Indefinite",
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id,
            BasicSalary = basic, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id,
            Iban = "SA4420000001234567891234", MolId = $"MOL-{code}", SalaryCurrency = "SAR",
        });
        await db.SaveChangesAsync();
        return emp;
    }

    private static async Task<PayrollRun> SeedRun(
        ZayraDbContext db, Guid tenantId, Guid companyId, int year = 2026, int month = 6)
    {
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = companyId, Year = year, Month = month,
            Status = "Draft", TotalNetSalary = 0m, TotalGrossSalary = 0m,
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    /// <summary>Builds a PendingApproval batch whose children carry the given gross + tax, ready for
    /// the real ApproveBatch endpoint (so the accrual under test is the PRODUCTION one).</summary>
    private static async Task<BonusBatch> SeedPendingBatch(
        ZayraDbContext db, Guid tenantId, string period, string number,
        params (Employee Emp, decimal Gross, decimal Tax)[] children)
    {
        var type = await db.BonusTypes.FirstOrDefaultAsync(t => t.TenantId == tenantId);
        if (type is null)
        {
            type = new BonusType
            {
                TenantId = tenantId, Code = "PERF", NameEn = "Performance",
                IsIncludedInGosiBase = false, IsIncludedInWps = false, IsIncludedInEosb = false,
                TaxRegion = "GCC", IsActive = true,
            };
            db.BonusTypes.Add(type);
            await db.SaveChangesAsync();
        }
        var batch = new BonusBatch
        {
            TenantId = tenantId, BonusTypeId = type.Id, BonusTypeName = type.NameEn,
            BatchNumber = number, BatchName = $"Batch {number}", PaymentPeriod = period,
            PaymentDate = new DateOnly(2026, 6, 25), Status = "PendingApproval",
            EmployeeCount = children.Length,
            TotalAmount = children.Sum(c => c.Gross - c.Tax),   // batch total is NET (unchanged)
        };
        db.BonusBatches.Add(batch);
        await db.SaveChangesAsync();
        foreach (var (emp, gross, tax) in children)
            db.EmployeeBonuses.Add(new EmployeeBonus
            {
                TenantId = tenantId, CompanyId = emp.CompanyId, BonusBatchId = batch.Id,
                EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id, EmployeeName = emp.FullName,
                BonusTypeId = type.Id, BonusTypeName = type.NameEn, BasicSalary = 10_000m,
                CalculationMethod = "Fixed", CalculationValue = gross,
                GrossBonusAmount = gross, TaxWithheld = tax, BonusAmount = gross - tax,
                PaymentPeriod = period, Status = "Draft", TaxRegion = "GCC",
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return batch;
    }

    private static async Task LockRun(ZayraDbContext db, PayrollController ctrl, PayrollRun run)
    {
        (await ctrl.Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var tracked = await db.PayrollRuns.FirstAsync(r => r.Id == run.Id);
        tracked.Status = "Approved";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await ctrl.Lock(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
    }

    // ══════════════════════════════════════════════════════════════════════════════
    //  1. Headline — approve → payroll → lock: expense once, payable to zero
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApproveThenPayThroughPayroll_ExpensesOnce_AndClearsBonusPayableToZero()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var company = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, company.Id, "E001");
        var run = await SeedRun(db, tid, company.Id);
        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-1", (emp, 5_000m, 0m));

        // Approve → accrual.
        (await MakeBonusCtrl(db, tid).ApproveBatch(batch.Id, new BatchApproveRequest("ok"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var afterApprove = await AllGl(db, tid);
        Debits(afterApprove, ExpenseAcct).Should().Be(5_000m, "approval recognises the expense, at GROSS");
        NetLiability(afterApprove, PayableAcct).Should().Be(5_000m, "…against Bonus Payable");
        AssertBalanced(afterApprove);

        // Process + Lock → the run PAYS it: clears the payable, does NOT re-expense.
        await LockRun(db, MakePayrollCtrl(db, tid), run);

        var gl = await AllGl(db, tid);
        AssertBalanced(gl);
        Debits(gl, ExpenseAcct).Should().Be(5_000m,
            "THE defect: before B1b the payroll Lock journal debited 6100 a SECOND time for the same bonus");
        NetLiability(gl, PayableAcct).Should().Be(0m,
            "the payroll run must clear the accrual it is paying, not orphan it on the balance sheet");
        // The money still rides out through Salaries Payable exactly as before.
        NetLiability(gl, "2100").Should().BeGreaterThan(0m);
        gl.Any(l => l.EventType == GlEventTypes.BonusPayrollClearing).Should().BeTrue();
    }

    // ══════════════════════════════════════════════════════════════════════════════
    //  2. Manual path — approve → MarkBatchPaid
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApproveThenMarkPaid_ExpensesOnce_ClearsPayable_AndCreditsCashNet()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var company = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, company.Id, "E001");
        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-M", (emp, 4_000m, 880m)); // 22% US-style

        var bonusCtrl = MakeBonusCtrl(db, tid);
        (await bonusCtrl.ApproveBatch(batch.Id, new BatchApproveRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await bonusCtrl.MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await AllGl(db, tid);
        AssertBalanced(gl);
        Debits(gl, ExpenseAcct).Should().Be(4_000m, "expense recognised once, at accrual, at gross");
        NetLiability(gl, PayableAcct).Should().Be(0m, "payment clears the accrual in full");
        Credits(gl, TaxAcct).Should().Be(880m, "withholding is a liability to the tax authority, not cash");
        Credits(gl, CashAcct).Should().Be(3_120m, "only the NET actually leaves the bank");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    //  3. Back-compat — a bonus with NO accrual is still expensed by the run
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UnaccruedBonus_IsExpensedByPayroll_AndNeverTouchesBonusPayable()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var company = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, company.Id, "E001");
        var run = await SeedRun(db, tid, company.Id);

        // Injected straight into the run, exactly as the demo seeders and FinanceP1BonusGlTests do —
        // Status=Approved with no ApproveBatch call, therefore no accrual.
        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-RAW", (emp, 3_000m, 0m));
        await db.BonusBatches.Where(b => b.Id == batch.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, "Approved"));
        await db.EmployeeBonuses.Where(b => b.BonusBatchId == batch.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, "Approved"));
        db.ChangeTracker.Clear();

        await LockRun(db, MakePayrollCtrl(db, tid), run);

        var gl = await AllGl(db, tid);
        AssertBalanced(gl);
        Debits(gl, ExpenseAcct).Should().Be(3_000m, "with nothing accrued, the run is where the expense lands");
        Debits(gl, PayableAcct).Should().Be(0m);
        Credits(gl, PayableAcct).Should().Be(0m, "no accrual existed, so 2300 must never be touched");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    //  4. Legacy NET accrual — the min() cap is what makes this migration-free
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LegacyNetAccrual_ClearsNet_AndExpensesOnlyTheNeverAccruedTax()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var company = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, company.Id, "E001");
        var run = await SeedRun(db, tid, company.Id);
        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-LEG", (emp, 5_000m, 1_100m));
        await db.BonusBatches.Where(b => b.Id == batch.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, "Approved"));
        await db.EmployeeBonuses.Where(b => b.BonusBatchId == batch.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, "Approved"));

        // A pre-B1b accrual: EventType "BonusApproval", amount = batch NET, no CompanyId stamp.
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tid, SourceModule = "Bonus", SourceEntityId = batch.Id,
            SourceEntityRef = batch.BatchNumber, EventType = GlEventTypes.BonusAccrualLegacy,
            DebitAccount = "6100 - Employee Bonus Expense", CreditAccount = "2300 - Bonus Payable",
            Amount = 3_900m, Currency = "SAR",
            EntryDate = new DateOnly(2026, 6, 1), Period = "2026-06",
            Description = "Bonus approval: legacy",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await LockRun(db, MakePayrollCtrl(db, tid), run);

        var gl = await AllGl(db, tid);
        AssertBalanced(gl);
        NetLiability(gl, PayableAcct).Should().Be(0m, "the legacy NET accrual is cleared in full");
        Debits(gl, ExpenseAcct).Should().Be(5_000m,
            "3,900 was expensed at approval; the run expenses only the 1,100 tax slice that was never accrued");
        Credits(gl, TaxAcct).Should().Be(1_100m, "and the withholding now accrues to 2102 so Lock can balance");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    //  5. Remap immunity — clearing DRs the STORED account
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BonusPayableRemappedBetweenApproveAndLock_StillClearsToZero()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var company = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, company.Id, "E001");
        var run = await SeedRun(db, tid, company.Id);
        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-RM", (emp, 6_000m, 0m));

        (await MakeBonusCtrl(db, tid).ApproveBatch(batch.Id, new BatchApproveRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // Finance remaps BONUS_PAYABLE to a different account AFTER the accrual was posted.
        var newAcct = new GlAccount { TenantId = tid, CompanyId = null, Code = "2301", Name = "Bonus Payable (new)", AccountType = "Liability" };
        db.GlAccounts.Add(newAcct);
        db.GlAccountMappings.Add(new GlAccountMapping { TenantId = tid, CompanyId = null, DriverKey = "BONUS_PAYABLE", AccountId = newAcct.Id });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await LockRun(db, MakePayrollCtrl(db, tid), run);

        var gl = await AllGl(db, tid);
        AssertBalanced(gl);
        NetLiability(gl, PayableAcct).Should().Be(0m,
            "the clearing must DR the account the accrual actually credited, not a freshly resolved one");
        NetLiability(gl, "2301").Should().Be(0m, "and the remapped account must never be touched for this batch");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    //  6. Cancel after approval — contra, not an orphan
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CancelAfterApproval_ContrasTheAccrual_ExpenseAndPayableBothReturnToZero()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var company = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, company.Id, "E001");
        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-X", (emp, 7_500m, 0m));

        var bonusCtrl = MakeBonusCtrl(db, tid);
        (await bonusCtrl.ApproveBatch(batch.Id, new BatchApproveRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await bonusCtrl.RejectBatch(batch.Id, new RejectRequest("budget pulled"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await AllGl(db, tid);
        AssertBalanced(gl);
        NetLiability(gl, PayableAcct).Should().Be(0m, "a cancelled bonus leaves nothing owed");
        (Debits(gl, ExpenseAcct) - Credits(gl, ExpenseAcct)).Should().Be(0m,
            "and nothing expensed — before B1b the accrual just stood there forever");
        gl.Any(l => l.EventType == GlEventTypes.BonusAccrualReversal).Should().BeTrue();
    }

    // ══════════════════════════════════════════════════════════════════════════════
    //  7. Idempotency — every endpoint replayed
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReplayingApproveAndLock_PostsNothingExtra()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var company = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, company.Id, "E001");
        var run = await SeedRun(db, tid, company.Id);
        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-ID", (emp, 2_500m, 0m));

        var bonusCtrl = MakeBonusCtrl(db, tid);
        (await bonusCtrl.ApproveBatch(batch.Id, new BatchApproveRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // Force the batch back to PendingApproval and re-approve: the accrual must NOT post twice.
        await db.BonusBatches.Where(b => b.Id == batch.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, "PendingApproval"));
        db.ChangeTracker.Clear();
        (await bonusCtrl.ApproveBatch(batch.Id, new BatchApproveRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var payroll = MakePayrollCtrl(db, tid);
        await LockRun(db, payroll, run);
        var afterFirstLock = await AllGl(db, tid);
        // Re-lock the same run (Lock only accepts an Approved run, so put it back) — the GL must not move.
        await db.PayrollRuns.Where(r => r.Id == run.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Approved"));
        db.ChangeTracker.Clear();
        (await payroll.Lock(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await AllGl(db, tid);
        gl.Count.Should().Be(afterFirstLock.Count, "re-locking must not duplicate any GL");
        AssertBalanced(gl);
        Debits(gl, ExpenseAcct).Should().Be(2_500m, "expense stays at exactly one recognition");
        NetLiability(gl, PayableAcct).Should().Be(0m);
    }

    // ══════════════════════════════════════════════════════════════════════════════
    //  8. Void — reversible via contra, and the payable RE-OPENS
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task VoidingTheRun_ReversesTheClearing_AndReopensTheBonusPayable()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var company = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, company.Id, "E001");
        var run = await SeedRun(db, tid, company.Id);
        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-V", (emp, 4_500m, 0m));

        (await MakeBonusCtrl(db, tid).ApproveBatch(batch.Id, new BatchApproveRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        await LockRun(db, MakePayrollCtrl(db, tid), run);

        (await new PayrollVoidService(db).VoidAsync(run.Id, tid, null, "tester", "rerun needed"))
            .IsVoided.Should().BeTrue();
        db.ChangeTracker.Clear();

        var gl = await AllGl(db, tid);
        AssertBalanced(gl);
        NetLiability(gl, PayableAcct).Should().Be(4_500m,
            "voiding the run un-pays the bonus, so the accrual is owed again");
        Debits(gl, ExpenseAcct).Should().Be(4_500m, "…and the expense is still recognised exactly once");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    //  9. Company-first routing: account, currency and CompanyId stamp
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Accrual_UsesPerCompanyAccountOverride_CompanyCurrency_AndStampsCompanyId()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var companyA = await SeedCompany(db, tid, "Co A", "SAR");
        var companyB = await SeedCompany(db, tid, "Co B", "AED");
        var empA = await SeedEmployee(db, tid, companyA.Id, "A001");
        var empB = await SeedEmployee(db, tid, companyB.Id, "B001");

        // Company B remaps BONUS_PAYABLE to its own account.
        var bAcct = new GlAccount { TenantId = tid, CompanyId = companyB.Id, Code = "2390", Name = "B Bonus Payable", AccountType = "Liability" };
        db.GlAccounts.Add(bAcct);
        db.GlAccountMappings.Add(new GlAccountMapping { TenantId = tid, CompanyId = companyB.Id, DriverKey = "BONUS_PAYABLE", AccountId = bAcct.Id });
        await db.SaveChangesAsync();

        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-MC", (empA, 1_000m, 0m), (empB, 2_000m, 0m));
        (await MakeBonusCtrl(db, tid).ApproveBatch(batch.Id, new BatchApproveRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await AllGl(db, tid);
        gl.Should().HaveCount(2, "a batch spanning two legal entities accrues once per entity");
        AssertBalanced(gl);

        var a = gl.Single(l => l.CompanyId == companyA.Id);
        a.Amount.Should().Be(1_000m);
        a.CreditAccount.Should().Be("2300 - Bonus Payable", "company A has no override → tenant default");
        a.Currency.Should().Be("SAR");

        var b = gl.Single(l => l.CompanyId == companyB.Id);
        b.Amount.Should().Be(2_000m);
        b.CreditAccount.Should().Be("2390 - B Bonus Payable", "company B's own mapping must win");
        b.Currency.Should().Be("AED", "…and the line is stamped in that entity's currency");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 10. Per-company period close blocks only that company
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CompanyPeriodClose_BlocksThatCompanysAccrual_OtherCompanyStillPosts()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var companyA = await SeedCompany(db, tid, "Co A");
        var companyB = await SeedCompany(db, tid, "Co B");
        var empA = await SeedEmployee(db, tid, companyA.Id, "A001");
        var empB = await SeedEmployee(db, tid, companyB.Id, "B001");

        (await MakeGlCtrl(db, tid).ClosePeriod("2026-06", companyA.Id, new GlPeriodActionRequest("A year-end"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var bonusCtrl = MakeBonusCtrl(db, tid);
        var batchA = await SeedPendingBatch(db, tid, "2026-06", "BON-A", (empA, 1_000m, 0m));
        var act = async () => await bonusCtrl.ApproveBatch(batchA.Id, new BatchApproveRequest(null), CancellationToken.None);
        await act.Should().ThrowAsync<PeriodClosedException>("company A's period is closed");
        db.ChangeTracker.Clear();
        (await AllGl(db, tid)).Should().BeEmpty("the guard throws BEFORE SaveChanges — nothing commits");

        var batchB = await SeedPendingBatch(db, tid, "2026-06", "BON-B", (empB, 2_000m, 0m));
        (await bonusCtrl.ApproveBatch(batchB.Id, new BatchApproveRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>("company B's period is still open");
        db.ChangeTracker.Clear();
        (await AllGl(db, tid)).Should().ContainSingle();
    }

    [Fact]
    public async Task GroupWideClose_StillBlocksACompanyScopedBonusAccrual()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var company = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, company.Id, "E001");
        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-G", (emp, 1_000m, 0m));

        (await MakeGlCtrl(db, tid).ClosePeriod("2026-06", null, new GlPeriodActionRequest("group freeze"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var act = async () => await MakeBonusCtrl(db, tid)
            .ApproveBatch(batch.Id, new BatchApproveRequest(null), CancellationToken.None);
        await act.Should().ThrowAsync<PeriodClosedException>(
            "tightening the guard to per-company must not weaken the group-wide close");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 11. Loan/advance: company-first routing + receivable amortises to zero
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LoanDisbursement_UsesCompanyOverrideAccount_AndPostsOnce()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var company = await SeedCompany(db, tid, "Co A", "SAR");
        var emp = await SeedEmployee(db, tid, company.Id, "E001");

        var acct = new GlAccount { TenantId = tid, CompanyId = company.Id, Code = "1401", Name = "Co A Loans", AccountType = "Asset" };
        db.GlAccounts.Add(acct);
        db.GlAccountMappings.Add(new GlAccountMapping { TenantId = tid, CompanyId = company.Id, DriverKey = "LOAN_RECEIVABLE", AccountId = acct.Id });
        var loanType = new LoanType
        {
            TenantId = tid, Code = "GEN", NameEn = "General", MaxAmount = 100_000m, MaxInstallments = 24,
            RepaymentFrequency = "Monthly", IsInterestFree = true, RequiresApproval = false, IsActive = true,
        };
        db.LoanTypes.Add(loanType);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var httpCtx = new DefaultHttpContext { User = Principal(tid, "loans.write") };
        var loans = new LoansController(db, new _B1bScope())
        { ControllerContext = new ControllerContext { HttpContext = httpCtx } };

        (await loans.CreateLoan(
            new CreateLoanRequest(Guid.Empty, emp.FullName, loanType.Id, 12_000m, 12, null, emp.Id),
            CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await AllGl(db, tid);
        gl.Should().ContainSingle();
        gl[0].DebitAccount.Should().Be("1401 - Co A Loans", "the company's own receivable mapping must win");
        gl[0].CreditAccount.Should().Be("1000 - Cash/Bank");
        gl[0].CompanyId.Should().Be(company.Id, "the journal is attributed to the employee's legal entity");
        gl[0].Currency.Should().Be("SAR");
    }

    [Fact]
    public async Task LoanFullCycle_RemitCreditsTheReceivable_NotCashTwice()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var company = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, company.Id, "E001");
        var run = await SeedRun(db, tid, company.Id);

        // Employer-granted loan, disbursed (cash left the bank once), repaid via one payroll EMI.
        db.EmployeeLoans.Add(new EmployeeLoan
        {
            TenantId = tid, CompanyId = company.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
            EmployeeName = emp.FullName, LoanNumber = "LN-1", LoanTypeName = "General",
            ApprovedAmount = 1_200m, ApprovedInstallments = 12, InstallmentAmount = 100m,
            OutstandingBalance = 1_200m, Status = "Active",
        });
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tid, CompanyId = company.Id, SourceModule = "Loan", SourceEntityId = Guid.NewGuid(),
            SourceEntityRef = "LN-1", EventType = "Disbursement",
            DebitAccount = "1400 - Employee Loans Receivable", CreditAccount = "1000 - Cash/Bank",
            Amount = 1_200m, Currency = "SAR", EntryDate = new DateOnly(2026, 5, 1), Period = "2026-05",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var payroll = MakePayrollCtrl(db, tid);
        await LockRun(db, payroll, run);

        var cashAfterLock = Credits(await AllGl(db, tid), CashAcct);
        (await payroll.RemitStatutory(run.Id, new RemitStatutoryRequest("LOAN", "EMI", new DateOnly(2026, 7, 5)), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await AllGl(db, tid);
        AssertBalanced(gl);
        NetLiability(gl, "2107").Should().Be(0m, "the loan-deduction control account clears");
        Credits(gl, CashAcct).Should().Be(cashAfterLock,
            "an EMI withheld from pay is NOT a cash outflow — cash already left at disbursement");
        Credits(gl, "1400").Should().Be(100m, "the receivable amortises by the instalment withheld");
        (Debits(gl, "1400") - Credits(gl, "1400")).Should().Be(1_100m, "1,200 lent − 100 repaid");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 12. Taxable bonus through payroll: Lock no longer 422s, net pay unchanged
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TaxableBonusThroughPayroll_LocksSuccessfully_AndNetPayIsUnchanged()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var company = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, company.Id, "E001");
        var run = await SeedRun(db, tid, company.Id);
        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-TX", (emp, 10_000m, 2_200m));

        (await MakeBonusCtrl(db, tid).ApproveBatch(batch.Id, new BatchApproveRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // Before B1b this returned 422 gl_unbalanced: the bonus earning was DR'd GROSS while net pay
        // carried it NET and the withholding was never emitted as a deduction at all.
        await LockRun(db, MakePayrollCtrl(db, tid), run);

        var gl = await AllGl(db, tid);
        AssertBalanced(gl);
        Debits(gl, ExpenseAcct).Should().Be(10_000m, "expense once, at gross");
        NetLiability(gl, PayableAcct).Should().Be(0m);
        Credits(gl, TaxAcct).Should().Be(2_200m, "the withholding accrues to Income Tax Payable");

        // Take-home is the pre-change number: 13,000 salary + 7,800 net bonus − 1,170 GOSI EE.
        var slip = await db.PayrollSlips.AsNoTracking().FirstAsync(s => s.RunId == run.Id);
        slip.NetSalary.Should().Be(13_000m + 7_800m - 1_170m,
            "carrying the bonus gross and deducting its tax must not move net pay by a single halala");
        slip.GrossSalary.Should().Be(23_000m, "the payslip now shows the bonus GROSS, which is what was earned");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 13. Partial batch: one child via payroll, one via MarkBatchPaid
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PartialBatch_PayrollClearsOneChild_ManualPayClearsTheOther_ExpenseStillOnce()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var company = await SeedCompany(db, tid);
        var empA = await SeedEmployee(db, tid, company.Id, "E001");
        var run = await SeedRun(db, tid, company.Id);

        // Employee B is Terminated → excluded from the run, so their bonus stays Approved.
        var empB = new Employee
        {
            TenantId = tid, CompanyId = company.Id, EmployeeCode = "E002", FullName = "Sara Lee",
            Status = "Terminated", JoiningDate = new DateTime(2023, 1, 1),
            WorkEmail = "sara@test.com", Nationality = "GBR", ContractType = "Indefinite",
        };
        db.Employees.Add(empB);
        await db.SaveChangesAsync();

        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-P", (empA, 2_000m, 0m), (empB, 1_500m, 0m));
        var bonusCtrl = MakeBonusCtrl(db, tid);
        (await bonusCtrl.ApproveBatch(batch.Id, new BatchApproveRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        await LockRun(db, MakePayrollCtrl(db, tid), run);
        (await bonusCtrl.MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await AllGl(db, tid);
        AssertBalanced(gl);
        Debits(gl, ExpenseAcct).Should().Be(3_500m, "the whole batch is expensed exactly once, at approval");
        NetLiability(gl, PayableAcct).Should().Be(0m, "both halves of the accrual are cleared, neither twice");
        Credits(gl, CashAcct).Should().Be(1_500m, "only the manually-paid child leaves the bank here");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    //  Company-attribution mismatch — the two ways an accrual and its clearing can
    //  disagree about the legal entity. Either one silently resurrects the original
    //  double-count, so both directions are pinned.
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Run carries NO CompanyId (Process accepts one via legacySingleCompanyScope,
    /// PayrollController.cs:557-560; AuthSeeder.cs:849 and DemoDataSeeder.cs:783 both create them) while
    /// the accrual it pays IS company-stamped. Matching null→null only would find no accrual and
    /// re-expense the bonus — the exact defect this pod exists to kill.
    /// </summary>
    [Fact]
    public async Task LegacyRunWithNoCompanyId_StillClearsTheCompanyStampedAccrual_AndExpensesOnce()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var company = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, company.Id, "E001");

        var run = new PayrollRun
        {
            TenantId = tid, CompanyId = null, Year = 2026, Month = 6,
            Status = "Draft", TotalNetSalary = 0m, TotalGrossSalary = 0m,
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-LEGRUN", (emp, 5_000m, 0m));
        (await MakeBonusCtrl(db, tid).ApproveBatch(batch.Id, new BatchApproveRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // The accrual is stamped with the company; the run is not. They must still meet.
        var accrual = (await AllGl(db, tid)).Single(l => l.EventType == GlEventTypes.BonusAccrual);
        accrual.CompanyId.Should().Be(company.Id);

        await LockRun(db, MakePayrollCtrl(db, tid), run);

        var gl = await AllGl(db, tid);
        AssertBalanced(gl);
        Debits(gl, ExpenseAcct).Should().Be(5_000m,
            "expense lands exactly once even when the run carries no CompanyId");
        NetLiability(gl, PayableAcct).Should().Be(0m,
            "the payable must still clear — a null-company run must not orphan a company-stamped accrual");

        var clearing = gl.Single(l => l.EventType == GlEventTypes.BonusPayrollClearing);
        clearing.CompanyId.Should().Be(company.Id,
            "the clearing is stamped with the ACCRUAL's entity, so the sub-ledger matches it to that position");

        var positions = await BonusGlLedger.LoadPositionsAsync(db, tid, new[] { batch.Id }, CancellationToken.None);
        positions.Sum(p => p.Remaining).Should().Be(0m, "the payable sub-ledger must agree with the raw ledger");
    }

    /// <summary>
    /// The mirror image: a PRE-B1b accrual carries no CompanyId, but the payment that retires it is
    /// stamped with one. If the sub-ledger cannot match them the accrual reads as permanently
    /// outstanding — and, worse, stays clearable a SECOND time.
    /// </summary>
    [Fact]
    public async Task UnattributedLegacyAccrual_ClearedByCompanyStampedPayment_RegistersAsCleared()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var company = await SeedCompany(db, tid);
        var emp = await SeedEmployee(db, tid, company.Id, "E001");
        var batch = await SeedPendingBatch(db, tid, "2026-06", "BON-LEG", (emp, 3_000m, 0m));

        // Hand-post the pre-B1b shape: EventType=BonusAccrualLegacy, CompanyId=null.
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tid, CompanyId = null,
            SourceModule = BonusGlLedger.SourceModule, SourceEntityId = batch.Id,
            SourceEntityRef = batch.BatchNumber, EventType = GlEventTypes.BonusAccrualLegacy,
            DebitAccount = "6100 - Bonus Expense", CreditAccount = "2300 - Bonus Payable",
            Amount = 3_000m, Currency = "SAR",
            EntryDate = new DateOnly(2026, 6, 1), Period = "2026-06",
            Description = "Legacy bonus accrual",
        });
        var tracked = await db.BonusBatches.FirstAsync(b => b.Id == batch.Id);
        tracked.Status = "Approved";
        foreach (var b in await db.EmployeeBonuses.Where(x => x.BonusBatchId == batch.Id).ToListAsync())
            b.Status = "Approved";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await MakeBonusCtrl(db, tid).MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await AllGl(db, tid);
        AssertBalanced(gl);
        Debits(gl, ExpenseAcct).Should().Be(3_000m,
            "the legacy accrual already expensed it — payment must not expense it again");
        NetLiability(gl, PayableAcct).Should().Be(0m, "the legacy payable is retired in full");
        Credits(gl, CashAcct).Should().Be(3_000m);

        var positions = await BonusGlLedger.LoadPositionsAsync(db, tid, new[] { batch.Id }, CancellationToken.None);
        positions.Sum(p => p.Remaining).Should().Be(0m,
            "a company-stamped payment must register against the unattributed accrual it retired, " +
            "otherwise the sub-ledger would let it be cleared twice");
    }
}

// ── Test doubles (file-scoped; mirror SettlementPeriodCloseTests) ───────────────

file static class _B1bRules
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

file sealed class _B1bScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _B1bHttp : IHttpContextAccessor
{
    public _B1bHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _B1bNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _B1bKsaResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_B1bRules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _B1bLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _B1bDocs : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
