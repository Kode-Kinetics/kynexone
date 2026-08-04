using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
/// POD-B1b-FIX — regression lock-down for the seven MUST-FIX findings of the independent GL review.
/// Every test here FAILS on the pre-fix code and passes after. Account matching is on the CODE token
/// ("2300 "), never a substring, so 12300 / 23000 can never satisfy a 2300 assertion.
///
///   P0-1  MarkBatchPaid over-cleared a shared unattributed accrual across companies (2300 → debit).
///   P0-2  The LOAN remit credited a receivable that was never debited (1400/1410 → credit).
///   P0-3  The gl_unbalanced guard in MarkBatchPaid was algebraically tautological.
///   P1-4  Void left 2300 re-opened with no path to zero and the bonus operationally lost.
///   P1-5  RejectBatch was unguarded and never cancelled its children.
///   P1-6  MarkBatchPaid / ApproveBatch locked a whole batch from a partial, scope-filtered view.
///   P1-7  The un-accrued bonus remainder bypassed per-component driver routing.
///   P2-3  Trial-balance sign invariants (2300 never debit; 1400/1410 never credit).
/// </summary>
public class BonusGlRemediationTests
{
    // ── Ledger helpers (code-token matching, contras included: IsReversed is an audit link) ─────

    private static bool IsAcct(string? label, string code) =>
        !string.IsNullOrEmpty(label) && label.StartsWith(code + " ", StringComparison.Ordinal);

    private static decimal Dr(IEnumerable<FinanceGlEntry> gl, string code) =>
        gl.Where(l => IsAcct(l.DebitAccount, code)).Sum(l => l.Amount);

    private static decimal Cr(IEnumerable<FinanceGlEntry> gl, string code) =>
        gl.Where(l => IsAcct(l.CreditAccount, code)).Sum(l => l.Amount);

    /// <summary>Liability carrying balance: Σ CR − Σ DR. Negative ⇒ the liability is in DEBIT (illegal).</summary>
    private static decimal Liability(IEnumerable<FinanceGlEntry> gl, string code) => Cr(gl, code) - Dr(gl, code);

    /// <summary>Asset carrying balance: Σ DR − Σ CR. Negative ⇒ the asset is in CREDIT (illegal).</summary>
    private static decimal Asset(IEnumerable<FinanceGlEntry> gl, string code) => Dr(gl, code) - Cr(gl, code);

    private static void AssertBalanced(IEnumerable<FinanceGlEntry> gl, string because)
    {
        var list = gl.ToList();
        list.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount)
            .Should().Be(list.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount), because);
    }

    private static Task<List<FinanceGlEntry>> Ledger(ZayraDbContext db, Guid tid) =>
        db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == tid).ToListAsync();

    // ── Harness ─────────────────────────────────────────────────────────────────

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static DefaultHttpContext Ctx(Guid tid, Guid? scopedToCompany = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tid.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "fix-user"),
            new("permission", "payroll.lock"),
            new("permission", "payroll.export"),
            new("permission", "payroll.write"),
            new("permission", "payroll.approve"),
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

    private static PayrollController Payroll(ZayraDbContext db, Guid tid)
    {
        var http = Ctx(tid);
        return new PayrollController(
            db, new _FixScope(), new _FixHttp(http), new _FixNotifications(),
            new _FixKsaResolver(), _FixRules.Rules, new _FixLetters(), new _FixDocs(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static BonusesController Bonuses(ZayraDbContext db, Guid tid, HttpContext? http = null)
    {
        http ??= Ctx(tid);
        return new BonusesController(db, new _FixScope())
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
            WorkEmail = $"{code}@fix.test", Nationality = "SAU", ContractType = "Indefinite",
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
        return r;
    }

    /// <summary>Batch in the given status whose children carry (gross, tax) and the given child status.</summary>
    private static async Task<BonusBatch> Batch(
        ZayraDbContext db, Guid tid, string period, string number, string batchStatus, string childStatus,
        params (Employee Emp, decimal Gross, decimal Tax)[] children)
    {
        var type = await db.BonusTypes.FirstOrDefaultAsync(t => t.TenantId == tid);
        if (type is null)
        {
            type = new BonusType
            {
                TenantId = tid, Code = "PERF", NameEn = "Performance",
                IsIncludedInGosiBase = false, IsIncludedInWps = false, IsIncludedInEosb = false,
                TaxRegion = "GCC", IsActive = true,
            };
            db.BonusTypes.Add(type);
            await db.SaveChangesAsync();
        }
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

    /// <summary>A pre-POD-B1b accrual: EventType "BonusApproval", CompanyId NULL — the shape every one of
    /// the ~55 live tenants' existing bonus accruals has on disk.</summary>
    private static async Task LegacyAccrual(
        ZayraDbContext db, Guid tid, BonusBatch batch, decimal amount, string period)
    {
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tid, CompanyId = null,
            SourceModule = BonusGlLedger.SourceModule, SourceEntityId = batch.Id,
            SourceEntityRef = batch.BatchNumber, EventType = GlEventTypes.BonusAccrualLegacy,
            DebitAccount = "6100 - Bonus Expense", CreditAccount = "2300 - Bonus Payable",
            Amount = amount, Currency = "SAR",
            EntryDate = new DateOnly(2026, 6, 1), Period = period,
            Description = "Bonus accrual (pre-B1b)",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
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
    // P0-1 — one shared unattributed accrual, two companies: NEVER over-clear
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P0_1_MarkBatchPaid_SharedLegacyAccrualAcrossCompanies_NeverOverClearsBonusPayable()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var coA = await Co(db, tid, "Co A");
        var coB = await Co(db, tid, "Co B");
        var empA = await Emp(db, tid, coA.Id, "A001");
        var empB = await Emp(db, tid, coB.Id, "B001");

        // The review's exact failure case: ONE pre-B1b accrual of 1,000 (CompanyId NULL) behind a batch
        // that spans two companies at 600 gross each.
        var batch = await Batch(db, tid, "2026-06", "BON-X", "Approved", "Approved",
            (empA, 600m, 0m), (empB, 600m, 0m));
        await LegacyAccrual(db, tid, batch, 1_000m, "2026-06");

        (await Bonuses(db, tid).MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "the payment journal must balance in every company");

        Dr(gl, "2300").Should().Be(1_000m,
            "the shared accrual may be debited at most ONCE — before the fix group A cleared 600 and " +
            "group B cleared the SAME position again for 600, debiting 1,200 against a 1,000 accrual");
        Liability(gl, "2300").Should().Be(0m, "Bonus Payable must land exactly on zero");
        Liability(gl, "2300").Should().BeGreaterOrEqualTo(0m,
            "a liability must never carry a debit balance");

        // The 200 the accrual never covered is real expense and must be recognised, not lost.
        Dr(gl, "6100").Should().Be(1_200m,
            "1,000 accrued at approval + 200 un-accrued expensed at payment = the full 1,200 gross paid");
        Cr(gl, "1000").Should().Be(1_200m, "the full net leaves the bank");

        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();
    }

    [Fact]
    public void P0_1_Cursor_MetersConsumptionAcrossCompanies()
    {
        var batchId = Guid.NewGuid();
        var legacy = new BonusAccrualPosition(batchId, null, "2300 - Bonus Payable", "SAR", 1_000m, 0m);
        var cursor = new BonusAccrualCursor(new[] { legacy });

        var (p1, taken1) = cursor.Take(batchId, Guid.NewGuid(), 600m);
        var (p2, taken2) = cursor.Take(batchId, Guid.NewGuid(), 600m);

        p1.Should().BeSameAs(legacy);
        taken1.Should().Be(600m);
        p2.Should().BeSameAs(legacy, "the fallback still resolves the same legacy accrual…");
        taken2.Should().Be(400m, "…but only 400 of it is LEFT — the un-metered code handed out 600 twice");
        (taken1 + taken2).Should().Be(legacy.Accrued);
        cursor.RemainingOn(legacy).Should().Be(0m);
        cursor.Take(batchId, Guid.NewGuid(), 100m).Taken.Should().Be(0m, "an exhausted accrual funds nothing");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // P0-2 — cap the receivable credit at its real balance, route the excess to cash
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P0_2_Splitter_CapsAtBalance_RoutesExcessToCash_AndMetersAcrossLines()
    {
        const string loan = "1400 - Employee Loans Receivable";
        const string cash = "1000 - Cash/Bank";
        var splitter = new ReceivableClearingSplitter(cash, new[]
        {
            new KeyValuePair<string, decimal>(loan, 700m),
        });

        // Line 1: fully covered by the receivable.
        splitter.Split(loan, 500m).Should().BeEquivalentTo(new[] { (loan, 500m) });
        // Line 2: only 200 of the receivable is left — the rest is a genuine cash payout.
        splitter.Split(loan, 500m).Should().BeEquivalentTo(new[] { (loan, 200m), (cash, 300m) });
        // Line 3: the receivable is exhausted; 100% cash (the pre-B1b behaviour).
        splitter.Split(loan, 400m).Should().BeEquivalentTo(new[] { (cash, 400m) });
        // An unrecognised Loan-source component (garnishment) is cash in full — dispatch unchanged.
        splitter.Split(null, 250m).Should().BeEquivalentTo(new[] { (cash, 250m) });

        splitter.AppliedToReceivable[loan].Should().Be(700m, "never more than the account actually held");
        splitter.AppliedToCash.Should().Be(950m, "300 + 400 + 250");
    }

    [Fact]
    public void P0_2_Splitter_NeverChangesTheAmountItSplits()
    {
        var splitter = new ReceivableClearingSplitter("1000 - Cash/Bank", new[]
        {
            new KeyValuePair<string, decimal>("1400 - X", 333.33m),
        });
        foreach (var amount in new[] { 100.01m, 233.33m, 0.01m, 999.99m })
            splitter.Split("1400 - X", amount).Sum(s => s.Amount).Should().Be(amount,
                "Σ(split) == amount is what keeps BuildLiabilityClearingGl balanced by construction");
    }

    [Fact]
    public async Task P0_2_LoanRemit_WithNoDisbursementGl_CreditsCashNotAPhantomReceivable()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Fix Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);

        // The AuthSeeder.cs:1155 shape and the pre-72d3983 shape: an ACTIVE advance with a real
        // OutstandingBalance and NO disbursement GL behind it at all.
        db.SalaryAdvances.Add(new SalaryAdvance
        {
            TenantId = tid, CompanyId = co.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
            EmployeeName = emp.FullName, AdvanceNumber = "ADV-NOGL", RequestedAmount = 1_200m,
            ApprovedAmount = 1_200m, InstallmentAmount = 1_200m, Installments = 1,
            OutstandingBalance = 1_200m, Status = "Active", RepaymentType = "Installments",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var payroll = Payroll(db, tid);
        await Lock(db, payroll, run.Id);
        (await payroll.RemitStatutory(run.Id, new RemitStatutoryRequest("LOAN", "EMI"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "accrual + remittance must balance");
        Asset(gl, "1410").Should().Be(0m,
            "there is no receivable to amortise — crediting 1410 anyway drove an ASSET to a CREDIT balance");
        Cr(gl, "1410").Should().Be(0m, "nothing may be credited to a receivable that was never debited");
        Liability(gl, "2107").Should().Be(0m, "the loan-deduction control account still clears");
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();
    }

    [Fact]
    public async Task P0_2_LoanRemit_WithPartialDisbursementGl_SplitsReceivableAndCash()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Fix Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);

        // 1,200 withheld this month, but only 500 of it was ever booked as a receivable — the
        // "ApprovedAmount raised past the post-once probe" / partially-migrated shape.
        db.SalaryAdvances.Add(new SalaryAdvance
        {
            TenantId = tid, CompanyId = co.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
            EmployeeName = emp.FullName, AdvanceNumber = "ADV-PART", RequestedAmount = 1_200m,
            ApprovedAmount = 1_200m, InstallmentAmount = 1_200m, Installments = 1,
            OutstandingBalance = 1_200m, Status = "Active", RepaymentType = "Installments",
        });
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tid, CompanyId = co.Id, SourceModule = "Advance", SourceEntityId = Guid.NewGuid(),
            SourceEntityRef = "ADV-PART", EventType = "Disbursement",
            DebitAccount = "1410 - Employee Salary Advances", CreditAccount = "1000 - Cash/Bank",
            Amount = 500m, Currency = "SAR", EntryDate = new DateOnly(2026, 5, 1), Period = "2026-05",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var payroll = Payroll(db, tid);
        await Lock(db, payroll, run.Id);
        var remit = (await payroll.RemitStatutory(run.Id, new RemitStatutoryRequest("LOAN", "EMI"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject;
        db.ChangeTracker.Clear();

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "disbursement + accrual + remittance must balance");
        Cr(gl, "1410").Should().Be(500m, "the receivable is credited only up to what it actually held");
        Asset(gl, "1410").Should().Be(0m, "…which amortises it to exactly zero, never below");
        Liability(gl, "2107").Should().Be(0m, "the whole 1,200 withholding still clears the control account");
        GlControlAccounts.FindSignViolations(gl).Should().BeEmpty();

        // The split is surfaced on the response so Finance can reconcile it (review: "make it testable").
        JsonSerializer.Serialize(remit.Value).Should().Contain("credits");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // P0-3 — the MarkBatchPaid guard must be able to fire
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P0_3_MarkBatchPaid_EmittedRowsAlwaysTieToTheGrossBeingPaid()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Fix Co");
        var emp = await Emp(db, tid, co.Id, "E001");

        // Taxed bonus with only a PARTIAL accrual behind it: exercises every leg of the walker
        // (DR payable + DR un-accrued expense against CR tax + CR cash) in one journal.
        var batch = await Batch(db, tid, "2026-06", "BON-G", "Approved", "Approved", (emp, 4_000m, 880m));
        await LegacyAccrual(db, tid, batch, 2_500m, "2026-06");

        (await Bonuses(db, tid).MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var payment = (await Ledger(db, tid)).Where(l => l.EventType == GlEventTypes.BonusPayment).ToList();
        payment.Sum(l => l.Amount).Should().Be(4_000m,
            "the guard now asserts the EMITTED rows sum to the gross; the old check compared " +
            "clearable+unaccrued with tax+net, which are both grossTotal by definition — 0 == 0");
        Dr(payment, "2300").Should().Be(2_500m, "capped at what was actually accrued");
        Dr(payment, "6100").Should().Be(1_500m, "the un-accrued remainder is expensed here");
        Cr(payment, "2102").Should().Be(880m);
        Cr(payment, "1000").Should().Be(3_120m);
        GlControlAccounts.FindSignViolations(await Ledger(db, tid)).Should().BeEmpty();
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // P1-4 — void restores the operational state so the re-opened payable can clear
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P1_4_VoidingARunThatPaidABonus_ReopensTheBonusAndUnlocksTheBatch()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Fix Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);
        var batch = await Batch(db, tid, "2026-06", "BON-V", "PendingApproval", "Draft", (emp, 5_000m, 0m));

        var bonuses = Bonuses(db, tid);
        (await bonuses.ApproveBatch(batch.Id, new BatchApproveRequest("ok"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var payroll = Payroll(db, tid);
        await Lock(db, payroll, run.Id);
        (await db.BonusBatches.AsNoTracking().FirstAsync(b => b.Id == batch.Id))
            .IsLockedByPayroll.Should().BeTrue("the run consumed the whole batch");

        (await payroll.VoidRun(run.Id, new PayrollDecisionRequest("wrong run"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // The contra re-opened the payable…
        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "accrual + clearing + contra must balance");
        Liability(gl, "2300").Should().Be(5_000m, "the bonus was NOT paid — the payable is outstanding again");

        // …and the operational state now matches, so it can actually be cleared.
        var reopened = await db.EmployeeBonuses.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.BonusBatchId == batch.Id).ToListAsync();
        reopened.Should().OnlyContain(b => b.Status == "Approved" && b.PayrollRunId == null,
            "Process only picks up Approved + PayrollRunId==null; leaving them PaidInPayroll lost the bonus");
        var reloaded = await db.BonusBatches.AsNoTracking().FirstAsync(b => b.Id == batch.Id);
        reloaded.IsLockedByPayroll.Should().BeFalse("MarkBatchPaid 409s on IsLockedByPayroll");
        reloaded.Status.Should().Be("Approved");

        // Prove the recovery path actually works end to end: pay it manually, payable back to zero.
        (await Bonuses(db, tid).MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var after = await Ledger(db, tid);
        AssertBalanced(after, "the recovery payment keeps the ledger balanced");
        Liability(after, "2300").Should().Be(0m, "the re-opened payable finally clears");
        Dr(after, "6100").Should().Be(5_000m, "expense STILL exactly once across void + re-pay");
        GlControlAccounts.FindSignViolations(after).Should().BeEmpty();
    }

    [Fact]
    public async Task P1_4_Void_DoesNotReopenABonusAlreadyPaidInCash()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Fix Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);
        var batch = await Batch(db, tid, "2026-06", "BON-C", "PendingApproval", "Draft", (emp, 5_000m, 0m));

        var bonuses = Bonuses(db, tid);
        (await bonuses.ApproveBatch(batch.Id, new BatchApproveRequest("ok"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        // Paid OUTSIDE payroll, stamped against this run id — cash has already left the bank.
        (await Bonuses(db, tid).MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(run.Id), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await Payroll(db, tid).VoidRun(run.Id, new PayrollDecisionRequest("unrelated"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await db.EmployeeBonuses.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.BonusBatchId == batch.Id).ToListAsync())
            .Should().OnlyContain(b => b.Status == "PaidInPayroll",
                "a void must never re-open a bonus whose cash already left — that would double-pay");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // P1-5 — RejectBatch is guarded and cancels its children
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P1_5_RejectBatch_CancelsChildren_SoTheNextPayrollRunCannotPayAContradBatch()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Fix Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);
        var batch = await Batch(db, tid, "2026-06", "BON-R", "PendingApproval", "Draft", (emp, 5_000m, 0m));

        var bonuses = Bonuses(db, tid);
        (await bonuses.ApproveBatch(batch.Id, new BatchApproveRequest("ok"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Bonuses(db, tid).RejectBatch(batch.Id, new RejectRequest("budget pulled"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await db.EmployeeBonuses.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.BonusBatchId == batch.Id).ToListAsync())
            .Should().OnlyContain(b => b.Status == "Cancelled",
                "PayrollController's pendingBonuses query keys on the BONUS status and ignores the batch, " +
                "so leaving children Approved let the next run PAY a batch whose accrual was contra'd");

        var afterCancel = await Ledger(db, tid);
        Liability(afterCancel, "2300").Should().Be(0m, "the contra returns the payable to zero");
        Asset(afterCancel, "6100").Should().Be(0m, "…and the net expense with it (DR 5,000 − contra CR 5,000)");

        // The definitive proof: a payroll run for the same period now picks up nothing.
        await Lock(db, Payroll(db, tid), run.Id);
        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "accrual + contra + payroll must balance");
        gl.Any(l => l.EventType == GlEventTypes.BonusPayrollClearing)
            .Should().BeFalse("a cancelled batch must not be paid by the next run");
        (await db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == run.Id && e.Source == "Bonus").ToListAsync())
            .Should().BeEmpty();
    }

    [Fact]
    public async Task P1_5_RejectBatch_RefusesAPaidOrPayrollLockedBatch()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Fix Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var batch = await Batch(db, tid, "2026-06", "BON-P", "PendingApproval", "Draft", (emp, 5_000m, 0m));

        var bonuses = Bonuses(db, tid);
        (await bonuses.ApproveBatch(batch.Id, new BatchApproveRequest("ok"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Bonuses(db, tid).MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var before = await Ledger(db, tid);
        (await Bonuses(db, tid).RejectBatch(batch.Id, new RejectRequest("oops"), CancellationToken.None))
            .Should().BeOfType<ConflictObjectResult>(
                "a batch already paid + locked must not be rewritten to Cancelled");
        db.ChangeTracker.Clear();

        (await Ledger(db, tid)).Count.Should().Be(before.Count, "and nothing may be contra'd");
        (await db.BonusBatches.AsNoTracking().FirstAsync(b => b.Id == batch.Id)).Status.Should().Be("Paid");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // P1-6 — never lock a whole batch from a partial, company-filtered view
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P1_6_MarkBatchPaid_FromACompanyScopedCaller_RefusesInsteadOfHalfPaying()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using var _ = conn;
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options;

        await using var seedDb = new ZayraDbContext(options);   // no HttpContext ⇒ group scope
        seedDb.Database.EnsureCreated();
        var tid = Guid.NewGuid();
        var coA = await Co(seedDb, tid, "Co A");
        var coB = await Co(seedDb, tid, "Co B");
        var empA = await Emp(seedDb, tid, coA.Id, "A001");
        var empB = await Emp(seedDb, tid, coB.Id, "B001");
        var batch = await Batch(seedDb, tid, "2026-06", "BON-S", "Approved", "Approved",
            (empA, 600m, 0m), (empB, 600m, 0m));
        await LegacyAccrual(seedDb, tid, batch, 1_200m, "2026-06");

        // A caller whose token only grants Company A.
        var scopedHttp = Ctx(tid, coA.Id);
        await using var scopedDb = new ZayraDbContext(options, new _FixHttp(scopedHttp));
        scopedDb.EmployeeBonuses.Count(b => b.BonusBatchId == batch.Id)
            .Should().Be(1, "sanity: the company filter really does hide Company B's child");

        var result = await Bonuses(scopedDb, tid, scopedHttp)
            .MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), CancellationToken.None);
        result.Should().BeOfType<ObjectResult>();
        ((ObjectResult)result).StatusCode.Should().Be(403,
            "paying half a batch and then permanently locking it strands the other company forever");

        var after = await Ledger(seedDb, tid);
        after.Should().OnlyContain(l => l.EventType == GlEventTypes.BonusAccrualLegacy,
            "nothing may be posted when the caller cannot see the whole batch");
        (await seedDb.BonusBatches.AsNoTracking().FirstAsync(b => b.Id == batch.Id))
            .IsLockedByPayroll.Should().BeFalse("and the batch must NOT be locked");
    }

    [Fact]
    public async Task P1_6_ApproveBatch_FromACompanyScopedCaller_RefusesInsteadOfAccruingHalf()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using var _ = conn;
        var options = new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options;

        await using var seedDb = new ZayraDbContext(options);
        seedDb.Database.EnsureCreated();
        var tid = Guid.NewGuid();
        var coA = await Co(seedDb, tid, "Co A");
        var coB = await Co(seedDb, tid, "Co B");
        var empA = await Emp(seedDb, tid, coA.Id, "A001");
        var empB = await Emp(seedDb, tid, coB.Id, "B001");
        var batch = await Batch(seedDb, tid, "2026-06", "BON-Q", "PendingApproval", "Draft",
            (empA, 600m, 0m), (empB, 600m, 0m));

        var scopedHttp = Ctx(tid, coA.Id);
        await using var scopedDb = new ZayraDbContext(options, new _FixHttp(scopedHttp));

        var result = await Bonuses(scopedDb, tid, scopedHttp)
            .ApproveBatch(batch.Id, new BatchApproveRequest("half"), CancellationToken.None);
        ((ObjectResult)result).StatusCode.Should().Be(403);

        (await Ledger(seedDb, tid)).Should().BeEmpty("no half-accrual may be posted");
        (await seedDb.BonusBatches.AsNoTracking().FirstAsync(b => b.Id == batch.Id))
            .Status.Should().Be("PendingApproval", "and the batch must not advance");
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // P1-7 — the un-accrued remainder keeps per-component driver routing
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P1_7_UnaccruedBonusRemainder_RoutesThroughTheTenantsCustomComponentDriver()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var co = await Co(db, tid, "Fix Co");
        var emp = await Emp(db, tid, co.Id, "E001");
        var run = await Run(db, tid, co.Id, 2026, 6);

        // A shipped Phase-2 feature: a custom Earning driver matched to this bonus component.
        // Bonus earnings are coded BONUS_<TYPE NAME UPPER> (PayrollController.cs:955).
        db.GlDrivers.Add(new GlDriver
        {
            TenantId = tid, CompanyId = null, Key = "EARN:BONUS_PERF", Label = "Performance Bonus",
            Category = GlDriverCategories.Earning, PostingSide = "DR", AccountType = "Expense",
            DefaultCode = "6150", DefaultName = "Performance Bonus Expense",
            MatchSource = "Bonus", MatchMode = GlDriverMatchModes.Exact,
            MatchComponentCode = "BONUS_PERFORMANCE", IsSystem = false, IsActive = true, SortOrder = 10,
        });
        await db.SaveChangesAsync();

        // 5,000 gross paid through payroll, but only 3,000 was ever accrued ⇒ 2,000 un-accrued remainder.
        var batch = await Batch(db, tid, "2026-06", "BON-D", "Approved", "Approved", (emp, 5_000m, 0m));
        await LegacyAccrual(db, tid, batch, 3_000m, "2026-06");

        await Lock(db, Payroll(db, tid), run.Id);

        var gl = await Ledger(db, tid);
        AssertBalanced(gl, "clearing + remainder must equal the bonus earning total");
        Dr(gl, "2300").Should().Be(3_000m, "the accrued slice clears the payable");
        Liability(gl, "2300").Should().Be(0m);
        Dr(gl, "6150").Should().Be(2_000m,
            "the un-accrued remainder must honour the tenant's custom driver — it used to be dumped, " +
            "unconditionally, on the hard-coded EARN:BONUS account");
        Dr(gl, "6100").Should().Be(3_000m, "6100 carries only the original accrual, not the remainder");
        gl.Should().Contain(l => l.Description == "Payroll earning: BONUS_PERFORMANCE (un-accrued)",
            "per-component detail must survive instead of collapsing into one anonymous BONUS line");
    }

    // NOTE: the draft-preview half of P1-7 (PayrollController.GlJournal :1601-1655) is fixed in lockstep
    // with the posting builder above and is deliberately NOT covered here — GlJournal's draft branch calls
    // SumAsync over a decimal (PayrollController.cs:1586), which SQLite cannot translate, so the whole
    // preview endpoint is untestable on this in-memory harness for reasons that predate this pod.

    // ══════════════════════════════════════════════════════════════════════════════
    // P2-3 — trial-balance sign assertions
    // ══════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P2_3_SignAssertion_CatchesADebitPayableAndACreditReceivable()
    {
        FinanceGlEntry Row(string dr, string cr, decimal amt) => new()
        {
            TenantId = Guid.NewGuid(), DebitAccount = dr, CreditAccount = cr, Amount = amt,
        };

        // Clean: accrue 1,000 then clear 1,000.
        GlControlAccounts.FindSignViolations(new[]
        {
            Row("6100 - Bonus Expense", "2300 - Bonus Payable", 1_000m),
            Row("2300 - Bonus Payable", "1000 - Cash/Bank", 1_000m),
        }).Should().BeEmpty();

        // P0-1's fingerprint: the payable cleared for 1,200 against a 1,000 accrual.
        GlControlAccounts.FindSignViolations(new[]
        {
            Row("6100 - Bonus Expense", "2300 - Bonus Payable", 1_000m),
            Row("2300 - Bonus Payable", "1000 - Cash/Bank", 1_200m),
        }).Should().ContainSingle().Which.Should().Contain("DEBIT balance");

        // P0-2's fingerprint: a receivable credited that was never debited.
        GlControlAccounts.FindSignViolations(new[]
        {
            Row("2107 - Loan Deductions Payable", "1400 - Employee Loans Receivable", 1_200m),
        }).Should().ContainSingle().Which.Should().Contain("CREDIT balance");
    }
}

// ── Test doubles (file-scoped) ─────────────────────────────────────────────────

file static class _FixRules
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

file sealed class _FixScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _FixHttp : IHttpContextAccessor
{
    public _FixHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _FixNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _FixKsaResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_FixRules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _FixLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _FixDocs : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
