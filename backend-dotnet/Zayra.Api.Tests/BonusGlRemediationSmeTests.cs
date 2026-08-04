using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Zayra.Api.Application.Common;
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
/// POD-B1b-FIX — INDEPENDENT test suite (SDE Test SME). Written against the REVIEW FINDINGS, not the
/// implementation: every number below is hand-worked from the seeded facts, and every scenario is one
/// the review named as a live production shape (~55 tenants whose bonus accruals all carry
/// CompanyId = NULL; loans that predate the disbursement GL; seeded Active loans with no GL at all).
///
/// Deliberately DISJOINT from BonusGlRemediationTests.cs (the implementer's own suite): where that file
/// proves the happy fix, this one attacks the fix from the other side — three companies instead of two,
/// mixed exact/fallback accruals, pre-existing partial consumption, a real EmployeeLoan (1400) rather
/// than only an advance (1410), the receivable exhausting ACROSS runs, re-consumption by a corrected
/// payroll run rather than a manual payment, the endpoints the review said would still leak
/// (payroll-pending, RejectBatch under a partial scope), and a two-component pro-rata remainder.
///
/// Account matching is on the CODE TOKEN ("2300 "), never a substring, so a line posted to 12300 or
/// 23000 can never satisfy a 2300 assertion.
///
/// Seed arithmetic (KSA pack, Saudi national, basic 10,000 / housing 2,000 / transport 1,000;
/// GOSI covered wage = 12,000): salary earnings DR 13,000 | GOSI EE 1,170 | GOSI ER 1,410 | ER exp 1,410.
/// </summary>
public class BonusGlRemediationSmeTests
{
    // ── Ledger helpers ──────────────────────────────────────────────────────────

    private static bool IsAcct(string? label, string code) =>
        !string.IsNullOrEmpty(label) && label.StartsWith(code + " ", StringComparison.Ordinal);

    private static decimal Dr(IEnumerable<FinanceGlEntry> gl, string code) =>
        gl.Where(l => IsAcct(l.DebitAccount, code)).Sum(l => l.Amount);

    private static decimal Cr(IEnumerable<FinanceGlEntry> gl, string code) =>
        gl.Where(l => IsAcct(l.CreditAccount, code)).Sum(l => l.Amount);

    /// <summary>Liability carrying balance: Σ CR − Σ DR. NEGATIVE ⇒ the liability sits in DEBIT (illegal).</summary>
    private static decimal Liability(IEnumerable<FinanceGlEntry> gl, string code) => Cr(gl, code) - Dr(gl, code);

    /// <summary>Asset carrying balance: Σ DR − Σ CR. NEGATIVE ⇒ the asset sits in CREDIT (illegal).</summary>
    private static decimal Asset(IEnumerable<FinanceGlEntry> gl, string code) => Dr(gl, code) - Cr(gl, code);

    private static void AssertBalanced(IEnumerable<FinanceGlEntry> gl, string because)
    {
        var list = gl.ToList();
        list.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount)
            .Should().Be(list.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount), because);
    }

    private static Task<List<FinanceGlEntry>> Ledger(ZayraDbContext db, Guid tid) =>
        db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == tid).ToListAsync();

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    // ── Harness ─────────────────────────────────────────────────────────────────

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    /// <param name="scopedToCompany">Emits the v2 entity_scope claim, which is what makes the
    /// EmployeeBonus company query filter (ICompanyScopedOperational) actually bite.</param>
    private static DefaultHttpContext Ctx(Guid tid, Guid? scopedToCompany = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tid.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "sme-user"),
            new("permission", "payroll.lock"),
            new("permission", "payroll.export"),
            new("permission", "payroll.write"),
            new("permission", "payroll.approve"),
            new("permission", "loans.write"),
            new("permission", "finance.gl.manage"),
            new("permission", "finance.gl.read"),
        };
        if (scopedToCompany is Guid cid)
            claims.Add(new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new
            {
                v = 2, m = EntityScopeModes.Companies, c = new[] { cid },
            })));
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
    }

    private static PayrollController Payroll(ZayraDbContext db, Guid tid, HttpContext? http = null)
    {
        http ??= Ctx(tid);
        return new PayrollController(
            db, new _SmeScope(), new _SmeHttp(http), new _SmeNotifications(),
            new _SmeKsaResolver(), _SmeRules.Rules, new _SmeLetters(), new _SmeDocs(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static BonusesController Bonuses(ZayraDbContext db, Guid tid, HttpContext? http = null)
    {
        http ??= Ctx(tid);
        return new BonusesController(db, new _SmeScope())
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    // ── Seed ────────────────────────────────────────────────────────────────────

    private static async Task<Company> Co(ZayraDbContext db, Guid tid, string name, string currency = "SAR")
    {
        var c = new Company
        {
            TenantId = tid, LegalNameEn = name, CountryCode = "SAU",
            Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = currency,
        };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    private static async Task<Employee> Emp(ZayraDbContext db, Guid tid, Guid companyId, string code)
    {
        var structure = await db.SalaryStructures.FirstOrDefaultAsync(s => s.TenantId == tid);
        if (structure is null)
        {
            structure = new SalaryStructure
            {
                TenantId = tid, Code = "STR", Name = "Base", Currency = "SAR",
                EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
            };
            db.SalaryStructures.Add(structure);
            await db.SaveChangesAsync();
        }
        var e = new Employee
        {
            TenantId = tid, CompanyId = companyId, EmployeeCode = code, FullName = $"Emp {code}",
            Status = "Active", JoiningDate = new DateTime(2023, 1, 1),
            WorkEmail = $"{code}@sme.test", Nationality = "SAU", ContractType = "Indefinite",
        };
        db.Employees.Add(e);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tid, EmployeeId = e.Id, SalaryStructureId = structure.Id,
            BasicSalary = 10_000m, HousingAllowance = 2_000m, TransportAllowance = 1_000m,
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tid, EmployeeId = e.Id,
            Iban = "SA4420000001234567891234", MolId = $"MOL-{code}", SalaryCurrency = "SAR",
        });
        await db.SaveChangesAsync();
        return e;
    }

    private static async Task<PayrollRun> Run(ZayraDbContext db, Guid tid, Guid companyId, int year, int month)
    {
        var r = new PayrollRun
        {
            TenantId = tid, CompanyId = companyId, Year = year, Month = month,
            Status = "Draft", TotalNetSalary = 0m, TotalGrossSalary = 0m,
        };
        db.PayrollRuns.Add(r);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return r;
    }

    private static async Task<BonusType> BonusTypeOf(ZayraDbContext db, Guid tid, string code, string name)
    {
        var existing = await db.BonusTypes.FirstOrDefaultAsync(t => t.TenantId == tid && t.Code == code);
        if (existing is not null) return existing;
        var type = new BonusType
        {
            TenantId = tid, Code = code, NameEn = name,
            IsIncludedInGosiBase = false, IsIncludedInWps = false, IsIncludedInEosb = false,
            TaxRegion = "GCC", IsActive = true,
        };
        db.BonusTypes.Add(type);
        await db.SaveChangesAsync();
        return type;
    }

    /// <summary>A batch in the given status whose children carry (gross, tax) and the given child status.
    /// CompanyId is set explicitly because the unit-test DbContext has no HttpContext and therefore does
    /// not run the server-side company stamping (ZayraDbContext.EnforceCompanyScopeOnWritesAsync).</summary>
    private static async Task<BonusBatch> Batch(
        ZayraDbContext db, Guid tid, BonusType type, string period, string number,
        string batchStatus, string childStatus,
        params (Employee Emp, decimal Gross, decimal Tax)[] children)
    {
        var b = new BonusBatch
        {
            TenantId = tid, BonusTypeId = type.Id, BonusTypeName = type.NameEn,
            BatchNumber = number, BatchName = $"Batch {number}", PaymentPeriod = period,
            PaymentDate = new DateOnly(2026, 6, 25), Status = batchStatus,
            EmployeeCount = children.Length, TotalAmount = children.Sum(c => c.Gross - c.Tax),
        };
        db.BonusBatches.Add(b);
        await db.SaveChangesAsync();
        foreach (var (emp, gross, tax) in children)
            db.EmployeeBonuses.Add(new EmployeeBonus
            {
                TenantId = tid, CompanyId = emp.CompanyId, BonusBatchId = b.Id,
                EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id, EmployeeName = emp.FullName,
                BonusTypeId = type.Id, BonusTypeName = type.NameEn, BasicSalary = 10_000m,
                CalculationMethod = "Fixed", CalculationValue = gross,
                GrossBonusAmount = gross, TaxWithheld = tax, BonusAmount = gross - tax,
                PaymentPeriod = period, Status = childStatus, TaxRegion = "GCC",
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return b;
    }

    /// <summary>An accrual exactly as it sits on disk for the ~55 live tenants: EventType "BonusApproval",
    /// CompanyId NULL. <paramref name="companyId"/> non-null models a batch re-accrued after B1b.</summary>
    private static async Task Accrual(
        ZayraDbContext db, Guid tid, BonusBatch batch, decimal amount, string period, Guid? companyId = null)
    {
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tid, CompanyId = companyId,
            SourceModule = BonusGlLedger.SourceModule, SourceEntityId = batch.Id,
            SourceEntityRef = batch.BatchNumber,
            EventType = companyId is null ? GlEventTypes.BonusAccrualLegacy : GlEventTypes.BonusAccrual,
            DebitAccount = "6100 - Employee Bonus Expense", CreditAccount = "2300 - Bonus Payable",
            Amount = amount, Currency = "SAR",
            EntryDate = new DateOnly(2026, 6, 1), Period = period,
            Description = "Bonus accrual",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<LoanType> LoanTypeOf(ZayraDbContext db, Guid tid)
    {
        var existing = await db.LoanTypes.FirstOrDefaultAsync(t => t.TenantId == tid);
        if (existing is not null) return existing;
        var t = new LoanType
        {
            TenantId = tid, Code = "GEN", NameEn = "General", MaxAmount = 100_000m, MaxInstallments = 24,
            RepaymentFrequency = "Monthly", IsInterestFree = true, RequiresApproval = false, IsActive = true,
        };
        db.LoanTypes.Add(t);
        await db.SaveChangesAsync();
        return t;
    }

    /// <param name="disbursedToGl">null ⇒ NO disbursement GL at all (the AuthSeeder.cs:1132 /
    /// pre-72d3983 shape); a value ⇒ that much was actually debited to the receivable.</param>
    private static async Task<EmployeeLoan> Loan(
        ZayraDbContext db, Guid tid, Company co, Employee emp, string number,
        decimal outstanding, decimal installment, decimal? disbursedToGl,
        string receivableLabel = "1400 - Employee Loans Receivable")
    {
        var type = await LoanTypeOf(db, tid);
        var loan = new EmployeeLoan
        {
            TenantId = tid, CompanyId = co.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
            EmployeeName = emp.FullName, LoanTypeId = type.Id, LoanTypeName = type.NameEn,
            LoanNumber = number, RequestedAmount = outstanding, ApprovedAmount = outstanding,
            RequestedInstallments = 1, ApprovedInstallments = 1, InstallmentAmount = installment,
            OutstandingBalance = outstanding, Status = "Active",
        };
        db.EmployeeLoans.Add(loan);
        if (disbursedToGl is decimal amt && amt > 0m)
            db.FinanceGlEntries.Add(new FinanceGlEntry
            {
                TenantId = tid, CompanyId = co.Id, SourceModule = "Loan", SourceEntityId = loan.Id,
                SourceEntityRef = number, EventType = "Disbursement",
                DebitAccount = receivableLabel, CreditAccount = "1000 - Cash/Bank",
                Amount = amt, Currency = "SAR", EntryDate = new DateOnly(2026, 5, 1), Period = "2026-05",
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return loan;
    }

    private static async Task Lock(ZayraDbContext db, PayrollController ctrl, Guid runId)
    {
        (await ctrl.Process(runId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        await db.PayrollRuns.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Approved"));
        db.ChangeTracker.Clear();
        (await ctrl.Lock(runId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // (a) P0-1 — a shared, unattributed accrual is METERED across every company
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THREE companies against ONE pre-B1b (CompanyId = NULL) accrual that does not cover them all.
    /// PRE-FIX: each group re-read the same immutable snapshot, so PositionFor handed the SAME 900
    /// accrual to A, B and C — DR 2300 = 1,500 against a 900 credit ⇒ a 600 DEBIT balance on a liability,
    /// and ZERO un-accrued expense (every group's `clearable` equalled its own gross), understating the
    /// P&amp;L by 600. Remaining's Math.Max(0, …) clamp made all of it invisible.
    /// </summary>
    [Fact]
    public async Task A1_P0_1_ThreeCompaniesOneUnattributedAccrual_DebitsThePayableAtMostOnce_AndExpensesTheUncoveredSlice()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var coA = await Co(db, tid, "Co A");
        var coB = await Co(db, tid, "Co B");
        var coC = await Co(db, tid, "Co C");
        var empA = await Emp(db, tid, coA.Id, "A001");
        var empB = await Emp(db, tid, coB.Id, "B001");
        var empC = await Emp(db, tid, coC.Id, "C001");
        var type = await BonusTypeOf(db, tid, "PERF", "Performance");

        var batch = await Batch(db, tid, type, "2026-06", "BON-3CO", "Approved", "Approved",
            (empA, 500m, 0m), (empB, 500m, 0m), (empC, 500m, 0m));
        await Accrual(db, tid, batch, 900m, "2026-06");   // CompanyId NULL — the live-tenant shape

        (await Bonuses(db, tid).MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "every company's payment journal must balance");

        Dr(gl, "2300").Should().Be(900m,
            "the shared accrual may be debited at most to what was accrued — 1,500 of gross chasing a " +
            "900 accrual must stop at 900 (pre-fix this was 1,500)");
        Dr(gl, "2300").Should().BeLessOrEqualTo(Cr(gl, "2300"),
            "a payable can never be debited for more than it was ever credited");
        Liability(gl, "2300").Should().Be(0m, "Bonus Payable lands exactly on zero");

        Dr(gl, "6100").Should().Be(1_500m,
            "900 recognised at accrual + 600 un-accrued recognised at payment = the full gross paid; " +
            "pre-fix the 600 was NEVER expensed");
        Cr(gl, "1000").Should().Be(1_500m, "and the full 1,500 net really leaves the bank");

        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty("no control account may be sign-inverted");

        var positions = await BonusGlLedger.LoadPositionsAsync(db, tid, new[] { batch.Id }, CancellationToken.None);
        positions.Sum(p => p.Remaining).Should().Be(0m, "the production sub-ledger agrees the accrual is spent");

        // Every emitted payment row is two-sided, so each legal entity's own trial balance nets to zero.
        foreach (var byCompany in gl.Where(l => l.EventType == GlEventTypes.BonusPayment).GroupBy(l => l.CompanyId))
            AssertBalanced(byCompany, $"company {byCompany.Key} must balance on its own books");
    }

    /// <summary>
    /// A MIXED population — the realistic mid-migration shape: company A's accrual was re-posted WITH
    /// attribution (400) while the legacy unattributed accrual (600) still backs everyone else. The
    /// exact match must win for A, and the fallback must then be metered across B and C.
    /// PRE-FIX: A took its own 400, then B and C EACH took 400 from the un-metered 600 fallback ⇒
    /// DR 2300 = 1,200 against 1,000 accrued.
    /// </summary>
    [Fact]
    public async Task A2_P0_1_MixedExactAndFallbackAccruals_TheFallbackIsMeteredAcrossEveryCompanyThatClaimsIt()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var coA = await Co(db, tid, "Co A");
        var coB = await Co(db, tid, "Co B");
        var coC = await Co(db, tid, "Co C");
        var empA = await Emp(db, tid, coA.Id, "A001");
        var empB = await Emp(db, tid, coB.Id, "B001");
        var empC = await Emp(db, tid, coC.Id, "C001");
        var type = await BonusTypeOf(db, tid, "PERF", "Performance");

        var batch = await Batch(db, tid, type, "2026-06", "BON-MIX", "Approved", "Approved",
            (empA, 400m, 0m), (empB, 400m, 0m), (empC, 400m, 0m));
        await Accrual(db, tid, batch, 400m, "2026-06", coA.Id);   // exact match for A
        await Accrual(db, tid, batch, 600m, "2026-06");           // unattributed fallback for B and C

        (await Bonuses(db, tid).MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "the mixed-attribution journal must balance");
        Dr(gl, "2300").Should().Be(1_000m,
            "400 (A's own accrual) + 600 (the fallback, shared by B and C) — never 1,200");
        Liability(gl, "2300").Should().Be(0m);
        Dr(gl, "6100").Should().Be(1_200m, "the 200 no accrual covered is still real expense");
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();

        // The exact-match precedence must not have been sacrificed to fix the metering: A's OWN accrual
        // is the one that got retired, so its (batch, company) position is closed.
        var positions = await BonusGlLedger.LoadPositionsAsync(db, tid, new[] { batch.Id }, CancellationToken.None);
        positions.Should().HaveCount(2);
        positions.Should().OnlyContain(p => p.Remaining == 0m,
            "both the attributed and the unattributed accrual must end fully retired");
    }

    /// <summary>
    /// The root cause, isolated: <see cref="BonusGlLedger.PositionFor"/> is a STATELESS probe. Two
    /// consecutive calls hand the same legacy accrual to two different companies at FULL remaining value —
    /// which is exactly what MarkBatchPaid used to do, once per company group. The cursor is the only
    /// safe way to walk a snapshot more than once.
    /// </summary>
    [Fact]
    public void A3_P0_1_TheStaticProbeIsUnmetered_WhichIsPreciselyWhyTheCursorExists()
    {
        var batchId = Guid.NewGuid();
        var coA = Guid.NewGuid();
        var coB = Guid.NewGuid();
        var legacy = new BonusAccrualPosition(batchId, null, "2300 - Bonus Payable", "SAR", 1_000m, 0m);
        var snapshot = new[] { legacy };

        // The un-metered probe: both companies are told the whole 1,000 is available.
        BonusGlLedger.PositionFor(snapshot, batchId, coA)!.Remaining.Should().Be(1_000m);
        BonusGlLedger.PositionFor(snapshot, batchId, coB)!.Remaining.Should().Be(1_000m,
            "the probe has NO memory — a caller that loops over company groups with it over-clears");

        // The cursor: the same fallback precedence, but over what is genuinely left.
        var cursor = new BonusAccrualCursor(snapshot);
        var takes = new[] { coA, coB, Guid.NewGuid() }.Select(c => cursor.Take(batchId, c, 600m).Taken).ToList();
        takes.Should().BeEquivalentTo(new[] { 600m, 400m, 0m }, o => o.WithStrictOrdering());
        takes.Sum().Should().Be(legacy.Accrued, "never more than was accrued, in total");
        cursor.RemainingOn(legacy).Should().Be(0m).And.BeGreaterOrEqualTo(0m);

        // An exact company match still beats the fallback, and pre-existing consumption is respected.
        var attributed = new BonusAccrualPosition(batchId, coA, "2300 - Bonus Payable", "SAR", 500m, 200m);
        var mixed = new BonusAccrualCursor(new[] { attributed, legacy });
        mixed.Take(batchId, coA, 1_000m).Should().BeEquivalentTo((Position: attributed, Taken: 300m),
            "300 is what is LEFT on A's own accrual (500 accrued − 200 already cleared)");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // (b) P0-2 — the receivable credit is CAPPED at what the receivable really holds
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A real EmployeeLoan (1400, not just the advance 1410 the implementer's suite covers) with NO
    /// disbursement GL — AuthSeeder.cs:1132 and every loan Active before 72d3983.
    /// PRE-FIX: the LOAN remit credited 1400 for the whole EMI, driving an ASSET to a 2,000 CREDIT
    /// balance that BuildLiabilityClearingGl's balanced-by-construction 422 could never catch.
    /// </summary>
    [Fact]
    public async Task B1_P0_2_LoanWithNoDisbursementGl_CreditsCashAndNeverDrivesTheReceivableIntoCredit()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);
        await Loan(db, tid, co, emp, "LN-LEGACY", outstanding: 2_000m, installment: 2_000m, disbursedToGl: null);

        var payroll = Payroll(db, tid);
        await Lock(db, payroll, run.Id);
        (await payroll.RemitStatutory(run.Id, new RemitStatutoryRequest("LOAN", "EMI"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "accrual + remittance must balance");
        Cr(gl, "1400").Should().Be(0m,
            "nothing may be credited to a receivable that was never debited at disbursement");
        Asset(gl, "1400").Should().Be(0m).And.BeGreaterOrEqualTo(0m,
            "an ASSET must never carry a credit balance");
        Cr(gl, "1000").Should().Be(2_000m,
            "the uncovered withholding falls back to the exact pre-B1b treatment: Cash/Bank");
        Liability(gl, "2107").Should().Be(0m, "the loan-deduction control account still clears to zero");
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();
    }

    /// <summary>The other half of the requirement: a properly-disbursed loan must STILL relieve 1400 —
    /// the cap must not have thrown POD-B1b's legitimate behaviour away.</summary>
    [Fact]
    public async Task B2_P0_2_ProperlyDisbursedLoan_StillAmortisesTheReceivable_AndCashLeavesExactlyOnce()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);
        await Loan(db, tid, co, emp, "LN-OK", outstanding: 2_000m, installment: 2_000m, disbursedToGl: 2_000m);

        var payroll = Payroll(db, tid);
        await Lock(db, payroll, run.Id);
        (await payroll.RemitStatutory(run.Id, new RemitStatutoryRequest("LOAN", "EMI"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "disbursement + accrual + remittance must balance");
        Cr(gl, "1400").Should().Be(2_000m, "the EMI relieves the receivable it was actually lent against");
        Asset(gl, "1400").Should().Be(0m, "…amortising it to exactly zero");
        Cr(gl, "1000").Should().Be(2_000m,
            "cash left the bank ONCE, at disbursement — an EMI withheld from pay is not a second outflow");
        Liability(gl, "2107").Should().Be(0m);
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();
    }

    /// <summary>
    /// The population every live tenant actually has: one loan booked as a receivable, one not, both
    /// withheld in the SAME run — and payroll emits ONE aggregate LOAN_EMI accrual line for the pair.
    /// The split therefore has to happen inside a single line or 1400 goes negative anyway.
    /// </summary>
    [Fact]
    public async Task B3_P0_2_MixedPopulation_SplitsOneAggregateAccrualLineBetweenReceivableAndCash()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var empBooked = await Emp(db, tid, co.Id, "E001");
        var empLegacy = await Emp(db, tid, co.Id, "E002");
        var run = await Run(db, tid, co.Id, 2026, 6);
        await Loan(db, tid, co, empBooked, "LN-BOOKED", 1_200m, 1_200m, disbursedToGl: 1_200m);
        await Loan(db, tid, co, empLegacy, "LN-LEGACY", 800m, 800m, disbursedToGl: null);

        var payroll = Payroll(db, tid);
        await Lock(db, payroll, run.Id);
        (await payroll.RemitStatutory(run.Id, new RemitStatutoryRequest("LOAN", "EMI"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "the split journal must stay balanced by construction");

        var remit = gl.Where(l => l.EventType == GlEventTypes.Remit(RemitGroups.Loan)).ToList();
        remit.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount).Should().Be(2_000m,
            "the whole withholding still clears the 2107 control account");
        Cr(remit, "1400").Should().Be(1_200m, "capped at the receivable's REAL carrying balance");
        Cr(remit, "1000").Should().Be(800m, "the uncovered 800 is a genuine cash payout — pre-B1b behaviour");

        Asset(gl, "1400").Should().Be(0m).And.BeGreaterOrEqualTo(0m,
            "pre-fix the whole 2,000 hit 1400 and left the asset 800 in CREDIT");
        Liability(gl, "2107").Should().Be(0m);
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();
    }

    /// <summary>
    /// The cap must be metered ACROSS remittances, not just within one: after run 1 amortises the
    /// receivable to zero, run 2's instalment has no asset left to relieve. Only a real balance read
    /// (GlControlAccounts.BalanceAsync) can know that — the in-call splitter alone cannot.
    /// This is the review's third uncovered shape: an ApprovedAmount raised past the post-once probe,
    /// so the operational outstanding (2,000) is double the amount ever booked to 1400 (1,000).
    /// </summary>
    [Fact]
    public async Task B4_P0_2_SecondRemittanceOnceTheReceivableIsExhausted_GoesEntirelyToCash()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        await Loan(db, tid, co, emp, "LN-RAISED", outstanding: 2_000m, installment: 1_000m, disbursedToGl: 1_000m);

        var june = await Run(db, tid, co.Id, 2026, 6);
        var payrollJune = Payroll(db, tid);
        await Lock(db, payrollJune, june.Id);
        (await payrollJune.RemitStatutory(june.Id, new RemitStatutoryRequest("LOAN", "EMI-1"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var afterJune = await Ledger(db, tid);
        Cr(afterJune, "1400").Should().Be(1_000m, "run 1 relieves the receivable in full");
        Asset(afterJune, "1400").Should().Be(0m);

        var july = await Run(db, tid, co.Id, 2026, 7);
        var payrollJuly = Payroll(db, tid);
        await Lock(db, payrollJuly, july.Id);
        (await payrollJuly.RemitStatutory(july.Id, new RemitStatutoryRequest("LOAN", "EMI-2"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "two runs of accruals + remittances must balance");
        Cr(gl, "1400").Should().Be(1_000m,
            "the SECOND instalment must not credit 1400 again — the receivable was already exhausted");
        Asset(gl, "1400").Should().Be(0m).And.BeGreaterOrEqualTo(0m,
            "pre-fix the second remittance drove the asset to a 1,000 CREDIT balance");
        Cr(gl, "1000").Should().Be(2_000m, "1,000 disbursed + 1,000 genuinely paid out in month 2");
        Liability(gl, "2107").Should().Be(0m, "both control-account accruals clear");
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();
    }

    /// <summary>
    /// RESIDUAL, documented not celebrated: the cap resolves the receivable label FRESH
    /// (GlAccountResolver.AccountLabel("LOAN_RECEIVABLE", …)) while the disbursement sits on the label
    /// as POSTED. After a chart-of-accounts remap the two disagree, the balance reads 0, and the whole
    /// EMI falls back to Cash — the old 1400 balance is left for a manual reclass. That is a
    /// RECONCILIATION gap, not a corruption: the invariant this pod exists to protect (no receivable
    /// ever driven into credit) still holds, which is what this test locks down.
    /// </summary>
    [Fact]
    public async Task B5_P0_2_ChartOfAccountsRemapAfterDisbursement_FallsBackToCash_AndStillNeverCreditsAReceivableItCannotSee()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);

        // Disbursed onto the OLD label…
        await Loan(db, tid, co, emp, "LN-REMAP", 1_500m, 1_500m, disbursedToGl: 1_500m);

        // …then finance remaps LOAN_RECEIVABLE onto a new account.
        var newAccount = new GlAccount
        {
            TenantId = tid, CompanyId = null, Code = "1401", Name = "Employee Loans Receivable (2026 CoA)",
            AccountType = "Asset", IsActive = true,
        };
        db.GlAccounts.Add(newAccount);
        await db.SaveChangesAsync();
        db.GlAccountMappings.Add(new GlAccountMapping
        {
            TenantId = tid, CompanyId = null, DriverKey = "LOAN_RECEIVABLE",
            AccountId = newAccount.Id, IsActive = true,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var payroll = Payroll(db, tid);
        await Lock(db, payroll, run.Id);
        (await payroll.RemitStatutory(run.Id, new RemitStatutoryRequest("LOAN", "EMI"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "a remap must not unbalance the journal");
        Cr(gl, "1401").Should().Be(0m,
            "the NEW receivable was never debited, so it must not be credited either");
        Asset(gl, "1401").Should().Be(0m).And.BeGreaterOrEqualTo(0m);
        Asset(gl, "1400").Should().Be(1_500m,
            "the old-label receivable is left standing for a manual reclass — documented residual, " +
            "not a sign violation");
        Cr(gl, "1000").Should().Be(1_500m + 1_500m, "disbursement, plus the uncovered EMI as cash");
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // (c) P0-3 — the replacement guard measures something REAL
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The old guard compared `clearable + unaccrued` with `taxTotal + netTotal` — both identically
    /// grossTotal, so it was 0 == 0 for every conceivable input. The replacement compares the PERSISTED
    /// rows and the sub-ledger's own outstanding figure, which are demonstrably different numbers: here
    /// the payable is debited 2,500 while 4,000 of gross is paid and 880 of tax is withheld. If the
    /// guard's operands were still tautological these three could not diverge.
    /// </summary>
    [Fact]
    public async Task C1_P0_3_TheGuardOperandsAreIndependent_PayableDebitTracksTheAccrual_NotTheGross()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var type = await BonusTypeOf(db, tid, "PERF", "Performance");

        var batch = await Batch(db, tid, type, "2026-06", "BON-GUARD", "Approved", "Approved", (emp, 4_000m, 880m));
        await Accrual(db, tid, batch, 2_500m, "2026-06");

        (await Bonuses(db, tid).MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await Ledger(db, tid);
        var payment = gl.Where(l => l.EventType == GlEventTypes.BonusPayment).ToList();

        payment.Sum(l => l.Amount).Should().Be(4_000m, "the emitted rows tie to the gross being paid");
        Dr(payment, "2300").Should().Be(2_500m,
            "the payable debit follows the SUB-LEDGER (2,500 outstanding), not the 4,000 gross — the two " +
            "operands the replaced guard compares are genuinely independent quantities");
        Dr(payment, "6100").Should().Be(1_500m, "and the 1,500 no accrual covered is expensed here");
        Cr(payment, "2102").Should().Be(880m, "tax withheld");
        Cr(payment, "1000").Should().Be(3_120m, "net actually paid out");

        Liability(gl, "2300").Should().Be(0m);
        Dr(gl, "6100").Should().Be(4_000m, "expense lands exactly once, at full gross, across the lifecycle");
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();
    }

    /// <summary>
    /// The boundary the guard has to get right: debited == outstanding EXACTLY (zero margin). A guard
    /// written with the wrong comparison direction, or without the 0.01 tolerance, trips here and blocks
    /// a perfectly legal payment. It must return Ok and close the sub-ledger at zero.
    /// </summary>
    [Fact]
    public async Task C2_P0_3_ExactExhaustionBoundary_DoesNotFalselyTrip_AndTheSubLedgerClosesAtZero()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var coA = await Co(db, tid, "Co A");
        var coB = await Co(db, tid, "Co B");
        var empA = await Emp(db, tid, coA.Id, "A001");
        var empB = await Emp(db, tid, coB.Id, "B001");
        var type = await BonusTypeOf(db, tid, "PERF", "Performance");

        // 333.33 + 666.67 == 1,000.00 exactly: the accrual is consumed to the last halala across two
        // companies, so every take lands on the zero-margin edge of the guard.
        var batch = await Batch(db, tid, type, "2026-06", "BON-EDGE", "Approved", "Approved",
            (empA, 333.33m, 0m), (empB, 666.67m, 0m));
        await Accrual(db, tid, batch, 1_000m, "2026-06");

        var result = await Bonuses(db, tid).MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>(
            "clearing exactly what is outstanding is legal — the guard must fire only on an OVER-clear");
        db.ChangeTracker.Clear();

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "a to-the-halala exhaustion must still balance");
        Dr(gl, "2300").Should().Be(1_000m);
        Liability(gl, "2300").Should().Be(0m);
        Dr(gl, "6100").Should().Be(1_000m, "no un-accrued remainder exists, so no extra expense is booked");
        gl.Should().NotContain(l => l.Description.EndsWith("(un-accrued)", StringComparison.Ordinal));
        (await BonusGlLedger.LoadPositionsAsync(db, tid, new[] { batch.Id }, CancellationToken.None))
            .Sum(p => p.Remaining).Should().Be(0m);
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // (d) P1-4 — a void leaves the bonus RE-CONSUMABLE, not just re-payable
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The review's own words: "so the bonus can be re-consumed or paid". The implementer's suite proves
    /// the manual-payment recovery; this proves the PAYROLL recovery, which is the path an operator who
    /// voided a wrong run actually takes: void, re-run the month, and the bonus must ride the corrected
    /// run and drive 2300 back to zero with the expense STILL recognised exactly once.
    /// PRE-FIX: the bonus stayed PaidInPayroll + PayrollRunId=run1, Process's Approved/null filter skipped
    /// it forever, MarkBatchPaid 409'd on IsLockedByPayroll — the employee's bonus was silently lost and
    /// 2300 carried the credit for ever.
    /// </summary>
    [Fact]
    public async Task D1_P1_4_VoidedRun_TheBonusIsReConsumedByACorrectedRun_PayableReachesZero_ExpenseStillOnce()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var type = await BonusTypeOf(db, tid, "PERF", "Performance");
        var batch = await Batch(db, tid, type, "2026-06", "BON-RC", "PendingApproval", "Draft", (emp, 5_000m, 0m));

        (await Bonuses(db, tid).ApproveBatch(batch.Id, new BatchApproveRequest("ok"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var wrongRun = await Run(db, tid, co.Id, 2026, 6);
        var payroll = Payroll(db, tid);
        await Lock(db, payroll, wrongRun.Id);
        (await payroll.VoidRun(wrongRun.Id, new PayrollDecisionRequest("wrong pay date"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        Liability(await Ledger(db, tid), "2300").Should().Be(5_000m,
            "pre-condition: the contra re-opened the payable because the bonus was not paid");

        // The corrected run for the same month.
        var correctedRun = await Run(db, tid, co.Id, 2026, 6);
        await Lock(db, Payroll(db, tid), correctedRun.Id);

        var earnings = await db.PayrollEarnings.AsNoTracking()
            .Where(e => e.PayrollRunId == correctedRun.Id && e.Source == "Bonus").ToListAsync();
        earnings.Sum(e => e.Amount).Should().Be(5_000m,
            "the corrected run must be able to CONSUME the re-opened bonus");

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "accrual + clearing + void contra + re-clearing must balance");
        Liability(gl, "2300").Should().Be(0m, "the re-opened payable is finally retired by the corrected run");
        Dr(gl, "6100").Should().Be(5_000m, "bonus expense STILL exactly once across void + re-consume");
        gl.Count(l => IsAcct(l.DebitAccount, "6100")).Should().Be(1,
            "exactly one journal LINE may ever debit the bonus expense for this bonus");
        gl.Count(l => l.EventType == GlEventTypes.BonusPayrollClearing && !l.IsReversed).Should().Be(1,
            "one LIVE clearing — the voided run's is flagged reversed");
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();

        var reloaded = await db.BonusBatches.AsNoTracking().FirstAsync(b => b.Id == batch.Id);
        reloaded.IsLockedByPayroll.Should().BeTrue("the corrected run re-locks the batch");
        (await db.EmployeeBonuses.IgnoreQueryFilters().AsNoTracking().Where(b => b.BonusBatchId == batch.Id).ToListAsync())
            .Should().OnlyContain(b => b.Status == "PaidInPayroll" && b.PayrollRunId == correctedRun.Id);
    }

    /// <summary>A void is a per-RUN correction. Re-opening must not reach into a different run's
    /// consumed bonuses — that would un-pay money that legitimately left the building.</summary>
    [Fact]
    public async Task D2_P1_4_VoidIsRunScoped_AnotherRunsConsumedBonusesAreUntouched()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var type = await BonusTypeOf(db, tid, "PERF", "Performance");

        var juneBatch = await Batch(db, tid, type, "2026-06", "BON-JUN", "PendingApproval", "Draft", (emp, 3_000m, 0m));
        var julyBatch = await Batch(db, tid, type, "2026-07", "BON-JUL", "PendingApproval", "Draft", (emp, 4_000m, 0m));
        foreach (var b in new[] { juneBatch, julyBatch })
        {
            (await Bonuses(db, tid).ApproveBatch(b.Id, new BatchApproveRequest(null), CancellationToken.None))
                .Should().BeOfType<OkObjectResult>();
            db.ChangeTracker.Clear();
        }

        var juneRun = await Run(db, tid, co.Id, 2026, 6);
        await Lock(db, Payroll(db, tid), juneRun.Id);
        var julyRun = await Run(db, tid, co.Id, 2026, 7);
        await Lock(db, Payroll(db, tid), julyRun.Id);

        (await Payroll(db, tid).VoidRun(juneRun.Id, new PayrollDecisionRequest("june was wrong"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var bonuses = await db.EmployeeBonuses.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        bonuses.Single(b => b.BonusBatchId == juneBatch.Id).Status.Should().Be("Approved",
            "the voided run's bonus is re-opened");
        bonuses.Single(b => b.BonusBatchId == juneBatch.Id).PayrollRunId.Should().BeNull();
        bonuses.Single(b => b.BonusBatchId == julyBatch.Id).Status.Should().Be("PaidInPayroll",
            "July's run was not voided — its bonus must stay consumed");
        bonuses.Single(b => b.BonusBatchId == julyBatch.Id).PayrollRunId.Should().Be(julyRun.Id);

        (await db.BonusBatches.AsNoTracking().FirstAsync(b => b.Id == julyBatch.Id))
            .IsLockedByPayroll.Should().BeTrue("and July's batch must stay locked");

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "two runs plus one void must balance");
        Liability(gl, "2300").Should().Be(3_000m, "only June's payable re-opens");
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // (e) P1-5 — a cancelled batch is invisible to everything downstream
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// PRE-FIX: RejectBatch contra'd the accrual but left EmployeeBonus.Status == "Approved", and
    /// PayrollController's pendingBonuses query (PayrollController.cs:670-677) keys on the BONUS status
    /// and ignores the batch entirely — so the next run PAID a batch whose expense had just been
    /// reversed. Proven here on BOTH downstream surfaces: the payroll-pending API and a real run.
    /// </summary>
    [Fact]
    public async Task E1_P1_5_CancelledBatch_IsInvisibleToTheNextRun_AndToThePayrollPendingEndpoint()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp1 = await Emp(db, tid, co.Id, "E001");
        var emp2 = await Emp(db, tid, co.Id, "E002");
        var type = await BonusTypeOf(db, tid, "PERF", "Performance");
        var batch = await Batch(db, tid, type, "2026-06", "BON-CAN", "PendingApproval", "Draft",
            (emp1, 3_000m, 0m), (emp2, 2_000m, 0m));

        (await Bonuses(db, tid).ApproveBatch(batch.Id, new BatchApproveRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        Liability(await Ledger(db, tid), "2300").Should().Be(5_000m, "pre-condition: accrued");

        (await Bonuses(db, tid).RejectBatch(batch.Id, new RejectRequest("budget pulled"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await db.EmployeeBonuses.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.BonusBatchId == batch.Id).ToListAsync())
            .Should().OnlyContain(b => b.Status == "Cancelled", "every child must be cancelled, not just the batch");

        // Surface 1 — the API payroll operators read before running.
        var pending = (await Bonuses(db, tid).GetPayrollPendingBonuses("2026-06", CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject;
        using (var doc = JsonDocument.Parse(Json(pending.Value)))
        {
            doc.RootElement.GetProperty("count").GetInt32().Should().Be(0,
                "a cancelled batch's bonuses must not be offered to the next run");
            doc.RootElement.GetProperty("totalAmount").GetDecimal().Should().Be(0m);
        }

        // Surface 2 — an actual run for the same period.
        var run = await Run(db, tid, co.Id, 2026, 6);
        await Lock(db, Payroll(db, tid), run.Id);

        (await db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == run.Id && e.Source == "Bonus").ToListAsync())
            .Should().BeEmpty("the run must pick up nothing from a cancelled batch");

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "accrual + contra + payroll must balance");
        gl.Should().NotContain(l => l.EventType == GlEventTypes.BonusPayrollClearing);
        Liability(gl, "2300").Should().Be(0m, "the contra returns the payable to zero and keeps it there");
        Dr(gl, "6100").Should().Be(5_000m);
        Cr(gl, "6100").Should().Be(5_000m, "…and the expense is fully reversed (DR 5,000 vs CR 5,000)");
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();
    }

    /// <summary>PRE-FIX RejectBatch accepted ANY status, so a second call rewrote history (and, on a
    /// partially-consumed batch, could contra a second time). It must now refuse.</summary>
    [Fact]
    public async Task E2_P1_5_RejectingTwice_IsRefused_SoTheAccrualIsNeverContradTwice()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var type = await BonusTypeOf(db, tid, "PERF", "Performance");
        var batch = await Batch(db, tid, type, "2026-06", "BON-2X", "PendingApproval", "Draft", (emp, 5_000m, 0m));

        (await Bonuses(db, tid).ApproveBatch(batch.Id, new BatchApproveRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Bonuses(db, tid).RejectBatch(batch.Id, new RejectRequest("first"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var before = await Ledger(db, tid);
        var second = await Bonuses(db, tid).RejectBatch(batch.Id, new RejectRequest("second"), CancellationToken.None);
        second.Should().BeOfType<BadRequestObjectResult>("a Cancelled batch cannot be cancelled again");
        Json(((BadRequestObjectResult)second).Value).Should().Contain("invalid_status");
        db.ChangeTracker.Clear();

        (await Ledger(db, tid)).Count.Should().Be(before.Count, "no second contra may be posted");
        Liability(await Ledger(db, tid), "2300").Should().Be(0m,
            "a double contra would have driven the payable into a DEBIT balance");
        GlControlAccounts.FindSignViolations(await Ledger(db, tid)).Should().BeEmpty();
    }

    /// <summary>Regression guard on the implementer's deliberate deviation from the review's literal
    /// wording (which said guard on Status=="Approved"): rejecting a PendingApproval SUBMISSION is this
    /// endpoint's original purpose and must still work — cancelling the children, posting no GL at all.</summary>
    [Fact]
    public async Task E3_P1_5_RejectingAPendingApprovalSubmission_StillWorks_AndCancelsItsChildren()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var type = await BonusTypeOf(db, tid, "PERF", "Performance");
        var batch = await Batch(db, tid, type, "2026-06", "BON-SUB", "PendingApproval", "Draft", (emp, 5_000m, 0m));

        (await Bonuses(db, tid).RejectBatch(batch.Id, new RejectRequest("not this quarter"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>("rejecting a submission is what this endpoint is FOR");
        db.ChangeTracker.Clear();

        (await db.BonusBatches.AsNoTracking().FirstAsync(b => b.Id == batch.Id)).Status.Should().Be("Cancelled");
        (await db.EmployeeBonuses.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.BonusBatchId == batch.Id).ToListAsync())
            .Should().OnlyContain(b => b.Status == "Cancelled");
        (await Ledger(db, tid)).Should().BeEmpty(
            "a never-approved batch carries no accrual, so cancelling it must post nothing");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // (f) P1-6 — refuse a partial view; never half-process
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// PRE-FIX a company-A-scoped user paid A's slice and set Status="Paid" + IsLockedByPayroll on the
    /// WHOLE batch, after which the :679 409 blocked every retry: company B was never paid and B's Bonus
    /// Payable could never clear. The refusal has to be RECOVERABLE, so this test also proves the batch
    /// is left in a state a correctly-scoped caller can still complete.
    /// </summary>
    [Fact]
    public async Task F1_P1_6_ScopedCallerCannotHalfPay_AndAGroupCallerCanThenCompleteTheWholeBatch()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using var _ = conn;
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options;

        await using var groupDb = new ZayraDbContext(options);   // no HttpContext ⇒ group scope
        groupDb.Database.EnsureCreated();
        var tid = Guid.NewGuid();
        var coA = await Co(groupDb, tid, "Co A");
        var coB = await Co(groupDb, tid, "Co B");
        var empA = await Emp(groupDb, tid, coA.Id, "A001");
        var empB = await Emp(groupDb, tid, coB.Id, "B001");
        var type = await BonusTypeOf(groupDb, tid, "PERF", "Performance");
        var batch = await Batch(groupDb, tid, type, "2026-06", "BON-SCOPE", "Approved", "Approved",
            (empA, 600m, 0m), (empB, 600m, 0m));
        await Accrual(groupDb, tid, batch, 1_200m, "2026-06");

        var scopedHttp = Ctx(tid, coA.Id);
        await using var scopedDb = new ZayraDbContext(options, new _SmeHttp(scopedHttp));
        scopedDb.EmployeeBonuses.Count(b => b.BonusBatchId == batch.Id).Should().Be(1,
            "sanity: the ICompanyScopedOperational filter really does hide company B's child");

        var refused = await Bonuses(scopedDb, tid, scopedHttp)
            .MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), CancellationToken.None);
        refused.Should().BeOfType<ObjectResult>();
        var refusal = (ObjectResult)refused;
        refusal.StatusCode.Should().Be(403);
        Json(refusal.Value).Should().Contain("company_scope_partial",
            "the refusal must name the failure so an operator knows to switch scope");
        scopedDb.ChangeTracker.Clear();

        var afterRefusal = await Ledger(groupDb, tid);
        afterRefusal.Should().OnlyContain(l => l.EventType == GlEventTypes.BonusAccrualLegacy,
            "nothing may be posted from a partial view");
        var stillOpen = await groupDb.BonusBatches.AsNoTracking().FirstAsync(b => b.Id == batch.Id);
        stillOpen.IsLockedByPayroll.Should().BeFalse("the irreversible lock must NOT have been taken");
        stillOpen.Status.Should().Be("Approved");

        // …and the recovery a group-scoped operator performs must then work in full.
        (await Bonuses(groupDb, tid).MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        groupDb.ChangeTracker.Clear();

        var gl = await Ledger(groupDb, tid);
        AssertBalanced(gl, "the completed payment must balance");
        Dr(gl, "2300").Should().Be(1_200m);
        Liability(gl, "2300").Should().Be(0m, "BOTH companies' slices are paid and the payable clears");
        Cr(gl, "1000").Should().Be(1_200m);
        (await groupDb.EmployeeBonuses.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.BonusBatchId == batch.Id).ToListAsync())
            .Should().OnlyContain(b => b.Status == "PaidInPayroll", "no employee is left behind");
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();
    }

    /// <summary>The guard must not over-block: a company-scoped caller whose scope covers the WHOLE
    /// batch is exactly the normal single-entity case and must still be able to approve and pay.</summary>
    [Fact]
    public async Task F2_P1_6_ScopedCallerCanStillCompleteABatchWhollyInsideItsOwnScope()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using var _ = conn;
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options;

        await using var groupDb = new ZayraDbContext(options);
        groupDb.Database.EnsureCreated();
        var tid = Guid.NewGuid();
        var coA = await Co(groupDb, tid, "Co A");
        await Co(groupDb, tid, "Co B");                       // a second entity exists but owns nothing here
        var empA = await Emp(groupDb, tid, coA.Id, "A001");
        var type = await BonusTypeOf(groupDb, tid, "PERF", "Performance");
        var batch = await Batch(groupDb, tid, type, "2026-06", "BON-OWN", "PendingApproval", "Draft",
            (empA, 900m, 0m));

        var scopedHttp = Ctx(tid, coA.Id);
        await using var scopedDb = new ZayraDbContext(options, new _SmeHttp(scopedHttp));

        (await Bonuses(scopedDb, tid, scopedHttp).ApproveBatch(batch.Id, new BatchApproveRequest("mine"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>("the caller can see every child, so nothing is half-processed");
        scopedDb.ChangeTracker.Clear();
        (await Bonuses(scopedDb, tid, scopedHttp).MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        scopedDb.ChangeTracker.Clear();

        var gl = await Ledger(groupDb, tid);
        AssertBalanced(gl, "the single-entity path is unchanged");
        Dr(gl, "6100").Should().Be(900m, "expense exactly once");
        Liability(gl, "2300").Should().Be(0m, "accrued then paid");
        gl.Should().OnlyContain(l => l.CompanyId == coA.Id, "and every line is stamped with the caller's entity");
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();
    }

    /// <summary>RejectBatch contras EVERY company's accrual and flips batch-level state, so it needs the
    /// same protection as approve/pay. (The review named approve and mark-paid; reject inherited the
    /// identical defect the moment B1b made it an accrual-REVERSAL path.)</summary>
    [Fact]
    public async Task F3_P1_6_RejectBatch_AlsoRefusesAPartialView_AndContrasNothing()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using var _ = conn;
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options;

        await using var groupDb = new ZayraDbContext(options);
        groupDb.Database.EnsureCreated();
        var tid = Guid.NewGuid();
        var coA = await Co(groupDb, tid, "Co A");
        var coB = await Co(groupDb, tid, "Co B");
        var empA = await Emp(groupDb, tid, coA.Id, "A001");
        var empB = await Emp(groupDb, tid, coB.Id, "B001");
        var type = await BonusTypeOf(groupDb, tid, "PERF", "Performance");
        var batch = await Batch(groupDb, tid, type, "2026-06", "BON-RSC", "Approved", "Approved",
            (empA, 700m, 0m), (empB, 300m, 0m));
        await Accrual(groupDb, tid, batch, 1_000m, "2026-06");

        var scopedHttp = Ctx(tid, coA.Id);
        await using var scopedDb = new ZayraDbContext(options, new _SmeHttp(scopedHttp));

        var refused = await Bonuses(scopedDb, tid, scopedHttp)
            .RejectBatch(batch.Id, new RejectRequest("only my half"), CancellationToken.None);
        ((ObjectResult)refused).StatusCode.Should().Be(403);
        scopedDb.ChangeTracker.Clear();

        (await Ledger(groupDb, tid)).Should().ContainSingle()
            .Which.EventType.Should().Be(GlEventTypes.BonusAccrualLegacy, "no contra may be posted");
        (await groupDb.BonusBatches.AsNoTracking().FirstAsync(b => b.Id == batch.Id)).Status.Should().Be("Approved");
        (await groupDb.EmployeeBonuses.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.BonusBatchId == batch.Id).ToListAsync())
            .Should().OnlyContain(b => b.Status == "Approved", "and no child may be cancelled from a partial view");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // (g) P1-7 — the un-accrued remainder keeps PER-COMPONENT driver routing
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// TWO bonus components in one run, one with a custom Phase-2 Earning driver and one without, each
    /// only partly accrued. PRE-FIX every Source=="Bonus" line was dropped from the driver-routed loop
    /// and the whole remainder was re-posted to the hard-coded EARN:BONUS account: the custom driver got
    /// NOTHING and per-component detail collapsed into a single anonymous "BONUS (un-accrued)" line.
    ///
    /// Hand-worked: perf 4,000 (accrued 3,000) + eid 2,000 (accrued 1,000) ⇒ earnings 6,000, cleared
    /// 4,000, remainder 2,000, allocated to each component's OWN SHORTFALL (re-audit #5) —
    /// BONUS_PERFORMANCE 4,000 − 3,000 = 1,000 and BONUS_EID 2,000 − 1,000 = 1,000.
    ///
    /// <para>Pro rata over gross (the first cut of this fix) would have given perf 1,333.33 / eid 666.67:
    /// the right TOTAL on the wrong ACCOUNTS. Here the two shortfalls happen to be equal while the two
    /// grosses are not, so the two allocations are numerically distinguishable and this test pins the
    /// shortfall one.</para>
    /// </summary>
    [Fact]
    public async Task G1_P1_7_TwoBonusComponents_EachUnaccruedSliceRoutesThroughItsOwnDriver_AndTheJournalStaysBalanced()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);

        // A shipped Phase-2 feature: a custom Earning driver matched EXACTLY to one bonus component.
        db.GlDrivers.Add(new GlDriver
        {
            TenantId = tid, CompanyId = null, Key = "EARN:BONUS_PERF", Label = "Performance Bonus",
            Category = GlDriverCategories.Earning, PostingSide = "DR", AccountType = "Expense",
            DefaultCode = "6150", DefaultName = "Performance Bonus Expense",
            MatchSource = "Bonus", MatchMode = GlDriverMatchModes.Exact,
            MatchComponentCode = "BONUS_PERFORMANCE", IsSystem = false, IsActive = true, SortOrder = 10,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var perfType = await BonusTypeOf(db, tid, "PERF", "Performance");
        var eidType = await BonusTypeOf(db, tid, "EID", "Eid");
        var perfBatch = await Batch(db, tid, perfType, "2026-06", "BON-P", "Approved", "Approved", (emp, 4_000m, 0m));
        var eidBatch = await Batch(db, tid, eidType, "2026-06", "BON-E", "Approved", "Approved", (emp, 2_000m, 0m));
        await Accrual(db, tid, perfBatch, 3_000m, "2026-06");
        await Accrual(db, tid, eidBatch, 1_000m, "2026-06");

        await Lock(db, Payroll(db, tid), run.Id);

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "Σ(clearing) + Σ(remainder slices) must equal the bonus earning total");

        var payrollLines = gl.Where(l => l.SourceModule == "Payroll").ToList();
        var unaccrued = payrollLines
            .Where(l => l.Description.EndsWith("(un-accrued)", StringComparison.Ordinal)).ToList();

        unaccrued.Sum(l => l.Amount).Should().Be(2_000m, "the remainder is exactly what no accrual covered");
        unaccrued.Should().HaveCount(2, "one slice per bonus COMPONENT — not one anonymous lump");

        unaccrued.Single(l => l.Description == "Payroll earning: BONUS_PERFORMANCE (un-accrued)")
            .DebitAccount.Should().Be("6150 - Performance Bonus Expense",
                "the tenant's custom driver must stay authoritative for its own component — pre-fix this " +
                "component's remainder was dumped on the hard-coded EARN:BONUS account");
        Dr(payrollLines, "6150").Should().Be(1_000m,
            "perf's OWN shortfall (4,000 gross − 3,000 accrued) — pro rata over gross would have put " +
            "1,333.33 here, overstating the perf account by a third of the other batch's expense");

        unaccrued.Single(l => l.Description == "Payroll earning: BONUS_EID (un-accrued)")
            .DebitAccount.Should().Be("6100 - Employee Bonus Expense", "no custom driver ⇒ the system default");
        Dr(payrollLines, "6100").Should().Be(1_000m,
            "eid's OWN shortfall (2,000 − 1,000), and NOTHING else re-expensed here");

        payrollLines.Should().NotContain(l => l.Description == "Payroll earning: BONUS (un-accrued)",
            "the anonymous collapsed line must be gone");
        Dr(payrollLines, "2300").Should().Be(4_000m, "the accrued slices clear the payable");
        Liability(gl, "2300").Should().Be(0m);
        Dr(gl, "6100").Should().Be(3_000m + 1_000m + 1_000m,
            "6100 carries the two accruals plus only the EID remainder");
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();
    }

    /// <summary>A fully-accrued bonus has no remainder at all, so the run must emit NO earning debit for
    /// it — neither the custom driver's account nor EARN:BONUS. This is the invariant that keeps the
    /// double-count dead while P1-7 changes where the remainder lands.</summary>
    [Fact]
    public async Task G2_P1_7_FullyAccruedBonus_EmitsNoRemainderAtAll()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);

        db.GlDrivers.Add(new GlDriver
        {
            TenantId = tid, CompanyId = null, Key = "EARN:BONUS_PERF", Label = "Performance Bonus",
            Category = GlDriverCategories.Earning, PostingSide = "DR", AccountType = "Expense",
            DefaultCode = "6150", DefaultName = "Performance Bonus Expense",
            MatchSource = "Bonus", MatchMode = GlDriverMatchModes.Exact,
            MatchComponentCode = "BONUS_PERFORMANCE", IsSystem = false, IsActive = true, SortOrder = 10,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var type = await BonusTypeOf(db, tid, "PERF", "Performance");
        var batch = await Batch(db, tid, type, "2026-06", "BON-FULL", "Approved", "Approved", (emp, 5_000m, 0m));
        await Accrual(db, tid, batch, 5_000m, "2026-06");

        await Lock(db, Payroll(db, tid), run.Id);

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "a fully-cleared bonus must leave the journal balanced");
        var payrollLines = gl.Where(l => l.SourceModule == "Payroll").ToList();
        Dr(payrollLines, "6150").Should().Be(0m, "nothing is left to expense");
        Dr(payrollLines, "6100").Should().Be(0m, "and the run must NOT re-expense the bonus");
        payrollLines.Should().NotContain(l => l.Description.EndsWith("(un-accrued)", StringComparison.Ordinal));
        Dr(payrollLines, "2300").Should().Be(5_000m, "the run PAYS the accrual instead");
        Liability(gl, "2300").Should().Be(0m);
        Dr(gl, "6100").Should().Be(5_000m, "expense exactly once, at approval");
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // P2 residuals — sign invariants, the balance primitive, and the migration rollback
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void H1_P2_3_SignInvariants_AreCodeTokenSafe_AndCatchBothFingerprints()
    {
        FinanceGlEntry Row(string dr, string cr, decimal amt) => new()
        { TenantId = Guid.NewGuid(), DebitAccount = dr, CreditAccount = cr, Amount = amt };

        // A neighbouring account whose code merely STARTS or ENDS with the guarded code must never
        // satisfy — nor break — the assertion.
        GlControlAccounts.FindSignViolations(new[]
        {
            Row("12300 - Suspense", "1000 - Cash/Bank", 5_000m),
            Row("23000 - Other Payable", "1000 - Cash/Bank", 5_000m),
            Row("14000 - Prepayments", "1000 - Cash/Bank", 5_000m),
        }).Should().BeEmpty("a substring match would have raised three phantom violations here");

        // P0-1's fingerprint: a liability cleared for more than was ever accrued.
        GlControlAccounts.FindSignViolations(new[]
        {
            Row("6100 - Employee Bonus Expense", "2300 - Bonus Payable", 1_000m),
            Row("2300 - Bonus Payable", "1000 - Cash/Bank", 1_200m),
        }).Should().ContainSingle().Which.Should().Contain("DEBIT balance");

        // P0-2's fingerprint, on BOTH receivables.
        GlControlAccounts.FindSignViolations(new[]
        {
            Row("2107 - Loan & Advance Deductions Payable", "1400 - Employee Loans Receivable", 900m),
            Row("2107 - Loan & Advance Deductions Payable", "1410 - Employee Salary Advances", 300m),
        }).Should().HaveCount(2).And.OnlyContain(v => v.Contains("CREDIT balance"));

        // Exactly-zero is legal on both sides — the invariant is about SIGN, not activity.
        GlControlAccounts.FindSignViolations(new[]
        {
            Row("1400 - Employee Loans Receivable", "1000 - Cash/Bank", 750m),
            Row("2107 - Loan & Advance Deductions Payable", "1400 - Employee Loans Receivable", 750m),
        }).Should().BeEmpty();
    }

    /// <summary>The primitive the P0-2 cap is built on. Two semantics matter and both are load-bearing:
    /// a company sees its OWN rows plus the unattributed rows every pre-B1b posting carries, and reversed
    /// rows are INCLUDED because the contra is itself a persisted row.</summary>
    [Fact]
    public async Task H2_GlControlAccounts_BalanceAsync_CountsUnattributedRowsAndExcludesOtherCompanies()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var coA = await Co(db, tid, "Co A");
        var coB = await Co(db, tid, "Co B");
        const string loan = "1400 - Employee Loans Receivable";

        FinanceGlEntry Row(Guid? companyId, string dr, string cr, decimal amt, bool reversed = false) => new()
        {
            TenantId = tid, CompanyId = companyId, SourceModule = "Loan", SourceEntityId = Guid.NewGuid(),
            EventType = "Disbursement", DebitAccount = dr, CreditAccount = cr, Amount = amt,
            Currency = "SAR", EntryDate = new DateOnly(2026, 5, 1), Period = "2026-05", IsReversed = reversed,
        };
        db.FinanceGlEntries.AddRange(
            Row(coA.Id, loan, "1000 - Cash/Bank", 1_000m),
            Row(null, loan, "1000 - Cash/Bank", 500m),              // pre-B1b, unattributed
            Row(coB.Id, loan, "1000 - Cash/Bank", 9_999m),          // another entity — must NOT count
            Row(coA.Id, "2107 - Loan & Advance Deductions Payable", loan, 300m),
            Row(coA.Id, loan, "1000 - Cash/Bank", 200m, reversed: true));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await GlControlAccounts.BalanceAsync(db, tid, coA.Id, loan, CancellationToken.None))
            .Should().Be(1_400m,
                "1,000 (A) + 500 (unattributed) − 300 (A's credit) + 200 (reversed rows are real postings)");
        (await GlControlAccounts.BalanceAsync(db, tid, coB.Id, loan, CancellationToken.None))
            .Should().Be(9_999m + 500m, "B sees its own rows plus the unattributed ones, never A's");
        // Re-audit #1: a null-company posting owns the UNATTRIBUTED POOL ALONE. Reading the whole tenant
        // here let a legacy run with no legal entity relieve another entity's receivable.
        (await GlControlAccounts.BalanceAsync(db, tid, null, loan, CancellationToken.None))
            .Should().Be(500m, "a null company sees the unattributed pool, never an entity's own rows");
        (await GlControlAccounts.BalanceAsync(db, tid, coA.Id, "9999 - Unmapped", CancellationToken.None))
            .Should().Be(0m, "an account with no rows funds nothing");
        (await GlControlAccounts.BalanceAsync(db, Guid.NewGuid(), coA.Id, loan, CancellationToken.None))
            .Should().Be(0m, "and the read is tenant-contained");

        // ── Re-audit #1 — the CAP is AvailableForRelief, not the scoped balance ────────────────────────
        // Tenant-wide here is 1,000 + 500 + 9,999 − 300 + 200 = 11,399, so neither entity is clamped yet.
        var a = await GlControlAccounts.LoadAsync(db, tid, coA.Id, loan, CancellationToken.None);
        var b = await GlControlAccounts.LoadAsync(db, tid, coB.Id, loan, CancellationToken.None);
        a.TenantWide.Should().Be(11_399m);
        b.TenantWide.Should().Be(11_399m);
        a.AvailableForRelief.Should().Be(1_400m);
        b.AvailableForRelief.Should().Be(10_499m);
    }

    /// <summary>
    /// RE-AUDIT #1 — the cap must be metered across COMPANIES, not only within one call.
    ///
    /// <para>Every pre-B1b disbursement carries CompanyId = NULL (true on all ~55 live tenants), and that
    /// unattributed pool sits inside EVERY company's scoped view while the relief posted against it is
    /// stamped with the relieving company and therefore invisible to the next one. Capping on the scoped
    /// balance let companies A and B each relieve the same 10,000 pool in full — 12,000 of credits against
    /// 10,000 of debits, i.e. a 2,000 CREDIT balance on an ASSET, which is exactly what P0-2 required be
    /// prevented (and which BuildLiabilityClearingGl, balanced by construction, can never 422 on).</para>
    /// </summary>
    [Fact]
    public async Task H2b_ReAudit1_UnattributedReceivablePool_CannotBeRelievedTwiceByTwoCompanies()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var coA = await Co(db, tid, "Co A");
        var coB = await Co(db, tid, "Co B");
        const string loan = "1400 - Employee Loans Receivable";
        const string cash = "1000 - Cash/Bank";

        FinanceGlEntry Row(Guid? companyId, string dr, string cr, decimal amt) => new()
        {
            TenantId = tid, CompanyId = companyId, SourceModule = "Loan", SourceEntityId = Guid.NewGuid(),
            EventType = "Disbursement", DebitAccount = dr, CreditAccount = cr, Amount = amt,
            Currency = "SAR", EntryDate = new DateOnly(2026, 5, 1), Period = "2026-05",
        };

        // One 10,000 unattributed disbursement — the shape every legacy tenant is in.
        db.FinanceGlEntries.Add(Row(null, loan, cash, 10_000m));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Company A remits 6,000 of withheld EMI, capped at what it may relieve, and stamps its own id.
        var aBudget = (await GlControlAccounts.LoadAsync(db, tid, coA.Id, loan, CancellationToken.None)).AvailableForRelief;
        aBudget.Should().Be(10_000m);
        var aSplit = new ReceivableClearingSplitter(cash, new[] { new KeyValuePair<string, decimal>(loan, aBudget) })
            .Split(loan, 6_000m);
        aSplit.Should().ContainSingle().Which.Should().Be((loan, 6_000m));
        db.FinanceGlEntries.Add(Row(coA.Id, "2107 - Loan & Advance Deductions Payable", loan, 6_000m));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Company B remits 6,000 too. Its SCOPED view still reads the whole 10,000 pool (A's relief is
        // stamped A and invisible to B) — the clamp to the tenant-wide balance is the only thing standing
        // between this and an asset in credit.
        var bBalance = await GlControlAccounts.LoadAsync(db, tid, coB.Id, loan, CancellationToken.None);
        bBalance.Scoped.Should().Be(10_000m, "B cannot see A's relief — this is the trap");
        bBalance.TenantWide.Should().Be(4_000m, "…but the tenant-wide balance nets it");
        bBalance.AvailableForRelief.Should().Be(4_000m, "so B may relieve only what is genuinely left");

        var bSplit = new ReceivableClearingSplitter(cash, new[] { new KeyValuePair<string, decimal>(loan, bBalance.AvailableForRelief) })
            .Split(loan, 6_000m);
        bSplit.Should().BeEquivalentTo(new[] { (loan, 4_000m), (cash, 2_000m) },
            "the covered slice amortises the receivable and the uncovered excess is a real cash payout");
        bSplit.Sum(s => s.Amount).Should().Be(6_000m, "Σ(split) == the line amount, so the journal balances");

        foreach (var (acct, amt) in bSplit)
            db.FinanceGlEntries.Add(Row(coB.Id, "2107 - Loan & Advance Deductions Payable", acct, amt));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var gl = await Ledger(db, tid);
        Asset(gl, "1400").Should().Be(0m, "10,000 debited, exactly 10,000 relieved — never a credit balance");
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();
        (await GlControlAccounts.CheckAsync(db, tid, null, CancellationToken.None))
            .Should().OnlyContain(r => r.IsHealthy);
    }

    /// <summary>RE-AUDIT #1 — a run with NO legal entity may spend the unattributed pool and nothing else.
    /// Reading the whole tenant let a legacy null-company run relieve an entity's own receivable.</summary>
    [Fact]
    public async Task H2c_ReAudit1_NullCompanyRun_CannotSpendAnEntitysReceivable()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var coA = await Co(db, tid, "Co A");
        const string loan = "1400 - Employee Loans Receivable";

        db.FinanceGlEntries.AddRange(
            new FinanceGlEntry
            {
                TenantId = tid, CompanyId = coA.Id, SourceModule = "Loan", SourceEntityId = Guid.NewGuid(),
                EventType = "Disbursement", DebitAccount = loan, CreditAccount = "1000 - Cash/Bank",
                Amount = 8_000m, Currency = "SAR", EntryDate = new DateOnly(2026, 5, 1), Period = "2026-05",
            },
            new FinanceGlEntry
            {
                TenantId = tid, CompanyId = null, SourceModule = "Loan", SourceEntityId = Guid.NewGuid(),
                EventType = "Disbursement", DebitAccount = loan, CreditAccount = "1000 - Cash/Bank",
                Amount = 250m, Currency = "SAR", EntryDate = new DateOnly(2026, 5, 1), Period = "2026-05",
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var legacy = await GlControlAccounts.LoadAsync(db, tid, null, loan, CancellationToken.None);
        legacy.Scoped.Should().Be(250m);
        legacy.TenantWide.Should().Be(8_250m);
        legacy.AvailableForRelief.Should().Be(250m,
            "the unattributed pool only — company A's 8,000 receivable is not this run's to relieve");
    }

    /// <summary>P2-5 — Down() must reverse the SCHEMA only. The Up() seeds are NOT EXISTS-guarded, so
    /// nothing records which rows this migration actually inserted; the previous Down() deleted by
    /// (code, name) / driver_key across EVERY tenant and would have destroyed the finance configuration
    /// of any tenant that owned those accounts itself.</summary>
    [Fact]
    public void H3_P2_5_MigrationDown_ReversesTheSchemaOnly_AndDeletesNoTenantOwnedFinanceConfig()
    {
        var migration = new Zayra.Api.Migrations.AddGlEntryCompanyDimension();
        var down = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(Migration).GetMethod("Down", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(migration, new object[] { down });

        down.Operations.OfType<SqlOperation>().Should().BeEmpty(
            "Down() must run no raw SQL at all — every DELETE it could write would target rows it may " +
            "never have inserted");
        down.Operations.Select(o => o.GetType().Name).Should().BeEquivalentTo(
            new[] { "DropIndexOperation", "DropIndexOperation", "DropColumnOperation" },
            "…leaving exactly the additive schema this migration created");

        // And the fix must not have gutted Up(): the seeds are still there for the ~55 live tenants.
        var up = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(Migration).GetMethod("Up", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(migration, new object[] { up });
        var seeds = up.Operations.OfType<SqlOperation>().ToList();
        seeds.Should().HaveCount(3, "gl_accounts, gl_account_mappings, gl_drivers");
        seeds.Should().OnlyContain(s => s.Sql.Contains("NOT EXISTS"),
            "every seed stays idempotent, which is exactly why Down() cannot know what to remove");
        seeds.Should().NotContain(s => s.Sql.Contains("DELETE", StringComparison.OrdinalIgnoreCase));
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // (i) RE-AUDIT REMEDIATION — the findings the independent re-audit left OPEN
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// RE-AUDIT #2 — a void must never resurrect a CANCELLED batch's bonuses.
    ///
    /// <para>The P1-4 re-open keys on the bonus, and pendingBonuses (PayrollController.cs:670-677) keys on
    /// the bonus status alone, so restoring a consumed child to Approved under a Cancelled parent had the
    /// next run PAY a batch finance had explicitly cancelled — after its accrual was already contra'd.
    /// RejectBatch now refuses to cancel while a run holds part of the batch (see I2), so this state can
    /// only be reached by data cancelled on the PRE-FIX code — which is exactly what the ~55 live tenants
    /// may be carrying, and what this test seeds directly.</para>
    /// </summary>
    [Fact]
    public async Task I1_ReAudit2_VoidingARun_RestoresACancelledBatchsBonusesToCancelled_NeverToApproved()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);
        var type = await BonusTypeOf(db, tid, "PERF", "Performance");
        var batch = await Batch(db, tid, type, "2026-06", "BON-1", "Approved", "Approved", (emp, 1_000m, 0m));
        await Accrual(db, tid, batch, 1_000m, "2026-06");

        await Lock(db, Payroll(db, tid), run.Id);
        (await db.EmployeeBonuses.IgnoreQueryFilters().AsNoTracking().FirstAsync(b => b.BonusBatchId == batch.Id))
            .Status.Should().Be("PaidInPayroll");

        // Pre-fix data: the batch was cancelled while the run still held its child.
        await db.BonusBatches.Where(b => b.Id == batch.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, "Cancelled"));
        db.ChangeTracker.Clear();

        (await new PayrollVoidService(db).VoidAsync(run.Id, tid, null, "tester", "correction"))
            .IsVoided.Should().BeTrue();
        db.ChangeTracker.Clear();

        var child = await db.EmployeeBonuses.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(b => b.BonusBatchId == batch.Id);
        child.Status.Should().Be("Cancelled",
            "a void must not hand a cancelled batch's bonus back to the next payroll run");
        child.PayrollRunId.Should().BeNull("the voided run no longer owns it");
        (await db.BonusBatches.IgnoreQueryFilters().AsNoTracking().FirstAsync(b => b.Id == batch.Id))
            .Status.Should().Be("Cancelled", "and the batch stays cancelled");

        // The operational state is now consistent with "nobody will ever be paid this": the bonus cannot
        // be picked up by a run (status) and cannot be marked paid (batch status).
        var picked = await db.EmployeeBonuses.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.TenantId == tid && b.Status == "Approved" && b.PayrollRunId == null).ToListAsync();
        picked.Should().BeEmpty("pendingBonuses keys on exactly this predicate");
    }

    /// <summary>
    /// RE-AUDIT #2 — RejectBatch refuses while a payroll run holds ANY child, which is what stops the
    /// resurrection above from ever being created again. IsLockedByPayroll alone does not cover it: a
    /// PARTIALLY consumed batch is still Approved and unlocked (PayrollController.cs:1161-1169 locks it
    /// only when no approved bonus is left).
    /// </summary>
    [Fact]
    public async Task I2_ReAudit2_RejectBatch_RefusesWhileAPayrollRunHoldsPartOfTheBatch()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);
        var type = await BonusTypeOf(db, tid, "PERF", "Performance");
        var batch = await Batch(db, tid, type, "2026-06", "BON-1", "Approved", "Approved",
            (emp, 600m, 0m), (emp, 400m, 0m));
        await Accrual(db, tid, batch, 1_000m, "2026-06");

        // Push ONE child into a later period so the run consumes only part of the batch and leaves it
        // Approved / IsLockedByPayroll = false — the shape the pre-fix guard could not see.
        var later = await db.EmployeeBonuses.IgnoreQueryFilters()
            .FirstAsync(b => b.BonusBatchId == batch.Id && b.GrossBonusAmount == 400m);
        later.PaymentPeriod = "2026-07";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Lock(db, Payroll(db, tid), run.Id);
        var afterLock = await db.BonusBatches.IgnoreQueryFilters().AsNoTracking().FirstAsync(b => b.Id == batch.Id);
        afterLock.Status.Should().Be("Approved");
        afterLock.IsLockedByPayroll.Should().BeFalse("a partially consumed batch is NOT locked");

        var refused = await Bonuses(db, tid).RejectBatch(batch.Id, new RejectRequest("changed our minds"), CancellationToken.None);
        var conflict = refused.Should().BeOfType<ConflictObjectResult>().Subject;
        Json(conflict.Value).Should().Contain("locked_by_payroll");
        db.ChangeTracker.Clear();

        (await db.BonusBatches.IgnoreQueryFilters().AsNoTracking().FirstAsync(b => b.Id == batch.Id))
            .Status.Should().Be("Approved", "the refusal must leave the batch untouched");
        (await Ledger(db, tid)).Should().NotContain(l => l.EventType == GlEventTypes.BonusAccrualReversal,
            "and post no contra — the accrual is still doing its job for the consumed slice");
    }

    /// <summary>
    /// RE-AUDIT #3 — the void is ATOMIC. Before this, four ExecuteUpdateAsync statements committed
    /// immediately while the GL contras / run status / audit row waited for the final SaveChanges: a
    /// throw there (audit hash-chain, concurrency, constraint) left the bonuses already re-opened and the
    /// batch already unlocked while the run was still Locked with its clearing lines LIVE — so the next
    /// run paid the same employee again and, because the un-reversed clearing was still visible to the
    /// sub-ledger, expensed it a second time straight to EARN:BONUS.
    ///
    /// <para>The failure is injected deterministically: a duplicate-PK row is tracked before the call, so
    /// the final SaveChanges throws. Everything the void did must be gone.</para>
    /// </summary>
    [Fact]
    public async Task I3_ReAudit3_WhenTheFinalSaveThrows_TheWholeVoidRollsBack_IncludingTheBonusReopen()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);
        var type = await BonusTypeOf(db, tid, "PERF", "Performance");
        var batch = await Batch(db, tid, type, "2026-06", "BON-1", "Approved", "Approved", (emp, 1_000m, 0m));
        await Accrual(db, tid, batch, 1_000m, "2026-06");
        await Lock(db, Payroll(db, tid), run.Id);

        var glBefore = (await Ledger(db, tid)).Count;

        // Poison the unit of work: an INSERT with an existing primary key fails at SaveChanges — i.e.
        // exactly where the run status, the GL contras and the audit row are written.
        db.BonusTypes.Add(new BonusType
        {
            Id = type.Id, TenantId = tid, Code = "DUP", NameEn = "Duplicate",
            TaxRegion = "GCC", IsActive = true,
        });

        var act = async () => await new PayrollVoidService(db).VoidAsync(run.Id, tid, null, "tester", "boom");
        await act.Should().ThrowAsync<Exception>();
        db.ChangeTracker.Clear();

        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id))
            .Status.Should().Be("Locked", "the run was never voided");
        var child = await db.EmployeeBonuses.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(b => b.BonusBatchId == batch.Id);
        child.Status.Should().Be("PaidInPayroll",
            "the bonus must NOT be re-openable while the run that paid it is still Locked — that is the " +
            "double-pay window");
        child.PayrollRunId.Should().Be(run.Id);
        (await db.BonusBatches.IgnoreQueryFilters().AsNoTracking().FirstAsync(b => b.Id == batch.Id))
            .IsLockedByPayroll.Should().BeTrue("and the batch must stay locked");
        (await db.PayrollSlips.IgnoreQueryFilters().AsNoTracking().Where(s => s.RunId == run.Id).ToListAsync())
            .Should().OnlyContain(s => s.Status != "Voided", "the payslip update rolls back with everything else");
        (await Ledger(db, tid)).Should().HaveCount(glBefore, "and no contra was posted");
    }

    /// <summary>
    /// RE-AUDIT #4 — the gl_unbalanced guard in MarkBatchPaid can now actually FIRE. Both the check it
    /// replaced and its first replacement were algebraic restatements of grossTotal. Comparing the DEBIT
    /// leg to the CREDIT leg is not, because they are only equal while net &gt;= 0: TaxWithheld &gt; gross
    /// drops the cash leg entirely and leaves the tax payable silently UNDER-credited while the lockstep
    /// walker still emits exactly grossTotal.
    /// </summary>
    [Fact]
    public async Task I4_ReAudit4_TaxWithheldExceedingGross_Fails422_AndPostsNothing()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var type = await BonusTypeOf(db, tid, "PERF", "Performance");
        // Gross 1,000 with 1,200 withheld — a mis-keyed tax, or a gross edited down after the tax was set.
        var batch = await Batch(db, tid, type, "2026-06", "BON-1", "Approved", "Approved", (emp, 1_000m, 1_200m));
        await Accrual(db, tid, batch, 1_000m, "2026-06");

        var result = await Bonuses(db, tid).MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), CancellationToken.None);
        var unprocessable = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        Json(unprocessable.Value).Should().Contain("gl_unbalanced").And.Contain("exceeds the gross");
        db.ChangeTracker.Clear();

        (await Ledger(db, tid)).Should().ContainSingle("nothing beyond the original accrual may be posted")
            .Which.EventType.Should().Be(GlEventTypes.BonusAccrualLegacy);
        (await db.BonusBatches.IgnoreQueryFilters().AsNoTracking().FirstAsync(b => b.Id == batch.Id))
            .Status.Should().Be("Approved", "and the batch must not be marked paid");
    }

    /// <summary>
    /// RE-AUDIT #5 — the review's exact counter-example. A FULLY accrued component (custom driver → 6110)
    /// alongside a NEVER accrued one (→ 6100): the remainder belongs entirely to the never-accrued
    /// component. Pro rata over gross put half of it on 6110 — overstating the Eid expense account by 500,
    /// understating the Perf one by 500, and expensing the Eid bonus twice at account level.
    /// </summary>
    [Fact]
    public async Task I5_ReAudit5_RemainderGoesOnlyToTheUnaccruedComponent_NotProRataAcrossFullyAccruedOnes()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);

        db.GlDrivers.Add(new GlDriver
        {
            TenantId = tid, CompanyId = null, Key = "EARN:BONUS_EID", Label = "Eid Bonus",
            Category = GlDriverCategories.Earning, PostingSide = "DR", AccountType = "Expense",
            DefaultCode = "6110", DefaultName = "Eid Bonus Expense",
            MatchSource = "Bonus", MatchMode = GlDriverMatchModes.Exact,
            MatchComponentCode = "BONUS_EID", IsSystem = false, IsActive = true, SortOrder = 10,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var eidType = await BonusTypeOf(db, tid, "EID", "Eid");
        var perfType = await BonusTypeOf(db, tid, "PERF", "Performance");
        var eidBatch = await Batch(db, tid, eidType, "2026-06", "BON-E", "Approved", "Approved", (emp, 1_000m, 0m));
        var perfBatch = await Batch(db, tid, perfType, "2026-06", "BON-P", "Approved", "Approved", (emp, 1_000m, 0m));
        await Accrual(db, tid, eidBatch, 1_000m, "2026-06");   // Eid accrued in full
        // Perf never accrued at all.

        await Lock(db, Payroll(db, tid), run.Id);

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "the journal must balance however the remainder is allocated");
        var payrollLines = gl.Where(l => l.SourceModule == "Payroll").ToList();

        Dr(payrollLines, "6110").Should().Be(0m,
            "the Eid bonus was accrued in full, so the run must re-expense NONE of it — pro rata over " +
            "gross put 500 here, expensing the Eid bonus twice at account level");
        payrollLines.Single(l => l.Description.EndsWith("(un-accrued)", StringComparison.Ordinal))
            .Should().Match<FinanceGlEntry>(l =>
                l.Amount == 1_000m && l.DebitAccount == "6100 - Employee Bonus Expense",
                "the whole remainder is the Perf bonus, which nothing ever accrued");
        Dr(payrollLines, "2300").Should().Be(1_000m, "and the Eid accrual is cleared, not re-expensed");
        Liability(gl, "2300").Should().Be(0m);
        Dr(gl, "6110").Should().Be(0m);
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();
    }

    /// <summary>
    /// RE-AUDIT #6 — an EMI is ApprovedAmount / Installments and is routinely 4dp; the DEBIT leg posts
    /// that stored amount verbatim. Rounding the receivable split to 2dp therefore made Σ CR ≠ Σ DR by up
    /// to ~0.01 per line — right at the caller's 422 tolerance, so the run either posted a journal off by
    /// a cent or refused a legitimate remittance. The split must return the EXACT residual.
    /// </summary>
    [Fact]
    public async Task I6_ReAudit6_FourDecimalEmi_RemitCreditsTieToTheDebitsExactly()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);

        // 4dp instalment, partially covered by a real disbursement so the split produces BOTH legs.
        await Loan(db, tid, co, emp, "LN-1", outstanding: 5_000m, installment: 333.3333m, disbursedToGl: 100m);

        await Lock(db, Payroll(db, tid), run.Id);
        var payroll = Payroll(db, tid);
        (await payroll.RemitStatutory(run.Id, new RemitStatutoryRequest("LOAN", "REF-1", new DateOnly(2026, 6, 30)), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var remit = (await Ledger(db, tid))
            .Where(l => l.EventType == GlEventTypes.Remit("LOAN")).ToList();
        remit.Should().NotBeEmpty();

        var dr = remit.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);
        var cr = remit.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount);
        cr.Should().Be(dr, "Σ(split) == the line amount EXACTLY — no rounding on the credit leg");
        dr.Should().Be(333.3333m, "the debit leg posts the stored 4dp accrual verbatim");
        Cr(remit, "1400").Should().Be(100m, "capped at what the receivable actually holds");
        Cr(remit, "1000").Should().Be(233.3333m, "and the uncovered excess is an exact cash payout");
        Asset(await Ledger(db, tid), "1400").Should().Be(0m);
    }

    /// <summary>
    /// RE-AUDIT #7 — the sign invariants are now detectable in PRODUCTION, on the tenant's OWN resolved
    /// accounts. Until this they existed only inside the test project: neither fingerprint was surfaced by
    /// any endpoint, so a live tenant could carry a broken control account indefinitely.
    /// </summary>
    [Fact]
    public async Task I7_ReAudit7_ControlAccountHealth_ReadsThePersistedLedger_AndFollowsAPerCompanyRemap()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");

        // This tenant remapped Bonus Payable to its own code for this entity.
        var account = new GlAccount
        {
            TenantId = tid, CompanyId = co.Id, Code = "2350", Name = "Bonus Payable (Co)",
            AccountType = "Liability", IsActive = true,
        };
        db.GlAccounts.Add(account);
        await db.SaveChangesAsync();
        db.GlAccountMappings.Add(new GlAccountMapping
        {
            TenantId = tid, CompanyId = co.Id, DriverKey = "BONUS_PAYABLE",
            AccountId = account.Id, IsActive = true,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await GlControlAccounts.CheckAsync(db, tid, co.Id, CancellationToken.None))
            .Should().OnlyContain(r => r.IsHealthy, "an empty ledger is clean");

        FinanceGlEntry Row(string dr, string cr, decimal amt) => new()
        {
            TenantId = tid, CompanyId = co.Id, SourceModule = "Bonus", SourceEntityId = Guid.NewGuid(),
            EventType = "Test", DebitAccount = dr, CreditAccount = cr, Amount = amt,
            Currency = "SAR", EntryDate = new DateOnly(2026, 6, 1), Period = "2026-06",
        };
        db.FinanceGlEntries.AddRange(
            // P0-1's fingerprint, on the REMAPPED account — the shipped 2300 default would miss this.
            Row("6100 - Employee Bonus Expense", "2350 - Bonus Payable (Co)", 1_000m),
            Row("2350 - Bonus Payable (Co)", "1000 - Cash/Bank", 1_200m),
            // P0-2's fingerprint, on the default receivable.
            Row("2107 - Loan & Advance Deductions Payable", "1400 - Employee Loans Receivable", 500m));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var health = await GlControlAccounts.CheckAsync(db, tid, co.Id, CancellationToken.None);
        health.Single(r => r.Driver == GlControlAccounts.BonusPayableDriver)
            .Should().Match<ControlAccountHealth>(r =>
                r.Account == "2350 - Bonus Payable (Co)" && !r.IsHealthy && r.TenantBalance == 200m);
        health.Single(r => r.Driver == GlControlAccounts.BonusPayableDriver).Violation
            .Should().Contain("DEBIT balance");
        health.Single(r => r.Driver == GlControlAccounts.LoanReceivableDriver).Violation
            .Should().Contain("CREDIT balance");
        health.Single(r => r.Driver == GlControlAccounts.AdvanceReceivableDriver).IsHealthy
            .Should().BeTrue("an untouched account is clean, not missing");

        // And the finance-facing endpoint reports it rather than 500-ing or hiding it.
        var http = Ctx(tid);
        var glController = new FinanceGlController(db) { ControllerContext = new ControllerContext { HttpContext = http } };
        var response = await glController.ControlAccountHealth(co.Id, CancellationToken.None);
        Json(response.Should().BeOfType<OkObjectResult>().Subject.Value)
            .Should().Contain("\"healthy\":false").And.Contain("2350 - Bonus Payable (Co)");
    }

    /// <summary>
    /// RE-AUDIT #8 — the P1-6 refusal must say WHICH problem it is. Every pre-company-layer EmployeeBonus
    /// row carries CompanyId = NULL and is visible to group scope only, so a company-scoped caller hits
    /// the same 403 as a genuinely cross-entity batch. The remedies differ (retry in group scope + let the
    /// boot backfill stamp the rows, vs. widen the scope), so the payload names the cause.
    /// </summary>
    [Fact]
    public async Task I8_ReAudit8_ScopeRefusal_DistinguishesUnattributedRowsFromACrossEntityBatch()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Sme Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var type = await BonusTypeOf(db, tid, "PERF", "Performance");
        var batch = await Batch(db, tid, type, "2026-06", "BON-1", "PendingApproval", "Draft", (emp, 1_000m, 0m));

        // The legacy shape: the bonus predates the company layer and was never stamped.
        await db.EmployeeBonuses.IgnoreQueryFilters().Where(b => b.BonusBatchId == batch.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.CompanyId, (Guid?)null));
        db.ChangeTracker.Clear();

        var scopedHttp = Ctx(tid, scopedToCompany: co.Id);
        var scopedDb = new ZayraDbContext(
            new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options,
            new _SmeHttp(scopedHttp));

        var refused = await Bonuses(scopedDb, tid, scopedHttp)
            .ApproveBatch(batch.Id, new BatchApproveRequest(null), CancellationToken.None);
        var payload = Json(refused.Should().BeOfType<ObjectResult>().Subject.Value);
        payload.Should().Contain("company_scope_partial")
            .And.Contain("\"unattributedEmployeeBonuses\":1")
            .And.Contain("not assigned to a legal entity yet",
                "the caller must be told the rows are unattributed, not that the batch spans entities");
        await scopedDb.DisposeAsync();

        (await Ledger(db, tid)).Should().BeEmpty("and nothing may be accrued from a partial view");
    }
}

// ── Test doubles (file-scoped) ─────────────────────────────────────────────────

file static class _SmeRules
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

file sealed class _SmeScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _SmeHttp : IHttpContextAccessor
{
    public _SmeHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _SmeNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _SmeKsaResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_SmeRules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _SmeLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _SmeDocs : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
