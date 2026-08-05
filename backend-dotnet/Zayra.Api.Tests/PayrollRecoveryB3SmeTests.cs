using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// POD-B3 — INDEPENDENT SME verification of "recover a bad payroll month".
///
/// <para>Written against the shipped endpoints only (no reaching into the void service internals), and
/// deliberately from angles the implementation suite does not take:</para>
/// <list type="bullet">
/// <item>the month starts with NON-ZERO control-account balances (a live bonus accrual on 2300, a loan
///   receivable on 1400, an advance receivable on 1410, cash already spent), so "flat" cannot pass by
///   accident on an empty ledger — the bar is <b>back to the OPENING balance</b>, penny for penny;</item>
/// <item>the whole tenant ledger is asserted to balance (Σ DR == Σ CR) after every phase — a trial
///   balance, not just a per-run one;</item>
/// <item>both funds dispositions are pinned, because "back where they started" means something DIFFERENT
///   for cash that came back and cash that did not;</item>
/// <item>the double-count question is asked of a run that carries TWO accrual journals (one reversed,
///   one live) — the shape the fixed Lock guard creates and the shape POD-A1's tie-out must survive.</item>
/// </list>
/// </summary>
public class PayrollRecoveryB3SmeTests
{
    // ══ Harness ═══════════════════════════════════════════════════════════════════════════════════

    private static readonly Guid Maker   = Guid.NewGuid();   // creates + processes
    private static readonly Guid Checker = Guid.NewGuid();   // approves, locks, overrides, voids

    private static (ZayraDbContext db, SqliteConnection conn) NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static DefaultHttpContext Ctx(Guid tenantId, Guid userId) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, userId == Maker ? "maker" : "checker"),
            new(ClaimTypes.Role, userId == Maker ? "Payroll Officer" : "Finance Controller"),
            new("permission", "payroll.write"),
            new("permission", "payroll.approve"),
            new("permission", "payroll.lock"),
            new("permission", "payroll.export"),
            new("permission", "finance.gl.manage"),
            new("permission", "finance.gl.read"),
        }, "test")),
    };

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var http = Ctx(tenantId, userId);
        return new PayrollController(
            db, new _SmeScope(), new _SmeHttp(http), new _SmeNotifications(),
            new _SmePackResolver(), _SmeRules.Rules, new _SmeLetters(), new _SmeStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static BonusesController Bonuses(ZayraDbContext db, Guid tenantId)
    {
        var http = Ctx(tenantId, Checker);
        return new BonusesController(db, new _SmeScope())
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static FinanceGlController Gl(ZayraDbContext db, Guid tenantId)
    {
        var http = Ctx(tenantId, Checker);
        return new FinanceGlController(db)
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    // ── The control accounts this pod is accountable for, by their PERSISTED label ────────────────
    private const string NetPayable   = "2100 - Salaries Payable";
    private const string GosiEe       = "2101 - Social Insurance Payable (Employee)";
    private const string GosiEr       = "2106 - Social Insurance Employer Payable";
    private const string TaxPayable   = "2102 - Income Tax Payable";
    private const string LoanPayable  = "2107 - Loan & Advance Deductions Payable";
    private const string BonusPayable = "2300 - Bonus Payable";
    private const string LoanRecv     = "1400 - Employee Loans Receivable";
    private const string AdvanceRecv  = "1410 - Employee Salary Advances";
    private const string CashBank     = "1000 - Cash/Bank";
    private const string EmpOverpaid  = "1420 - Employee Overpayment Receivable";
    private const string StatPrepaid  = "1430 - Prepaid Statutory Remittance";
    private const string BonusExpense = "6100 - Employee Bonus Expense";

    private static readonly string[] ControlAccounts =
        { NetPayable, GosiEe, GosiEr, TaxPayable, LoanPayable, BonusPayable, LoanRecv, AdvanceRecv, CashBank };

    private static Task<List<FinanceGlEntry>> Ledger(ZayraDbContext db, Guid tenantId) =>
        db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId).ToListAsync();

    /// <summary>Every control account's carrying balance (Σ DR − Σ CR), by exact label — the same
    /// semantics finance reads through <see cref="GlControlAccounts"/>. Exact labels, never substrings,
    /// so 2300 can never be satisfied by 12300.</summary>
    private static async Task<Dictionary<string, decimal>> Balances(ZayraDbContext db, Guid tenantId)
    {
        var rows = await Ledger(db, tenantId);
        return ControlAccounts.ToDictionary(a => a, a => GlControlAccounts.Balance(rows, a));
    }

    /// <summary>The trial balance: Σ debits == Σ credits over the WHOLE tenant ledger. A two-sided
    /// legacy-shaped row counts on both sides, exactly as a ledger reader would total it.</summary>
    private static async Task AssertLedgerBalances(ZayraDbContext db, Guid tenantId, string because)
    {
        var rows = await Ledger(db, tenantId);
        var dr = rows.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);
        var cr = rows.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount);
        dr.Should().Be(cr, because);
        GlControlAccounts.FindSignViolations(rows).Should().BeEmpty(
            "a recovery must never leave a liability in debit or a receivable in credit");
    }

    private static string Body(IActionResult r) =>
        JsonSerializer.Serialize(r is ObjectResult o ? o.Value : null);

    // ══ Fixture ═══════════════════════════════════════════════════════════════════════════════════

    private sealed record Fixture(
        Guid TenantId, Guid CompanyId, Employee Employee, PayrollRun Run,
        Guid LoanId, Guid AdvanceId);

    /// <summary>
    /// A company, one Saudi employee, an ACTIVE loan and advance whose disbursements are already on the
    /// books (so 1400/1410 carry real debit balances the remittance can amortise), and a Draft run.
    /// </summary>
    private static async Task<Fixture> Seed(
        ZayraDbContext db, Guid tenantId, int year = 2026, int month = 6, bool withIban = true)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "SME Recovery Co",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR",
        };
        db.Companies.Add(company);
        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = "SME", Name = "Base", Currency = "SAR",
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        };
        db.SalaryStructures.Add(structure);
        await db.SaveChangesAsync();

        var emp = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "S001", FullName = "Noura Al-Qahtani",
            Status = "Active", JoiningDate = new DateTime(2022, 3, 1),
            WorkEmail = "noura@sme.test", Nationality = "SAU", ContractType = "Indefinite",
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id,
            BasicSalary = 12_000m, HousingAllowance = 3_000m, TransportAllowance = 800m,
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        });
        if (withIban)
            db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
            {
                TenantId = tenantId, EmployeeId = emp.Id,
                Iban = "SA4420000001234567891234", MolId = "MOL-S001", SalaryCurrency = "SAR",
            });

        var loan = new EmployeeLoan
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
            LoanNumber = "LN-SME-1", Status = "Active", ApprovedAmount = 9_000m, OutstandingBalance = 9_000m,
            InstallmentAmount = 1_500m, TotalRepaid = 0m, ApprovedInstallments = 6,
        };
        var advance = new SalaryAdvance
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
            AdvanceNumber = "ADV-SME-1", Status = "Active", ApprovedAmount = 2_000m, OutstandingBalance = 2_000m,
            RepaymentType = "Installments", Installments = 4, InstallmentAmount = 500m, TotalRepaid = 0m,
        };
        db.EmployeeLoans.Add(loan);
        db.SalaryAdvances.Add(advance);
        await db.SaveChangesAsync();

        // The DISBURSEMENTS, on the books: DR receivable / CR cash. Without these 1400/1410 have no
        // debit balance, the withheld EMI would be routed to Cash/Bank by the clearing splitter, and the
        // receivable leg of the recovery would never be exercised at all.
        db.FinanceGlEntries.AddRange(
            OpeningLine(tenantId, company.Id, LoanRecv,    string.Empty, 9_000m, "Loan disbursement LN-SME-1"),
            OpeningLine(tenantId, company.Id, string.Empty, CashBank,    9_000m, "Loan disbursement LN-SME-1"),
            OpeningLine(tenantId, company.Id, AdvanceRecv, string.Empty, 2_000m, "Advance disbursement ADV-SME-1"),
            OpeningLine(tenantId, company.Id, string.Empty, CashBank,    2_000m, "Advance disbursement ADV-SME-1"));

        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id, Year = year, Month = month, Status = "Draft",
            CreatedByUserId = Maker,
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Fixture(tenantId, company.Id, emp, run, loan.Id, advance.Id);
    }

    private static FinanceGlEntry OpeningLine(
        Guid tenantId, Guid companyId, string debit, string credit, decimal amount, string description) => new()
    {
        TenantId = tenantId, CompanyId = companyId,
        SourceModule = "Loans", SourceEntityId = Guid.NewGuid(),
        DebitAccount = debit, CreditAccount = credit, Amount = amount, Currency = "SAR",
        EntryDate = new DateOnly(2026, 5, 20), Period = "2026-05",
        Description = description, PostedByName = "opening",
    };

    /// <summary>An approved bonus batch for the run's period: DR 6100 / CR 2300 at GROSS, posted by the
    /// REAL ApproveBatch endpoint, with tax withheld so 2102 and the TAX remittance group are live too.</summary>
    private static async Task<BonusBatch> ApprovedBonus(
        ZayraDbContext db, Guid tenantId, Employee emp, string period, decimal gross, decimal tax)
    {
        var type = new BonusType
        {
            TenantId = tenantId, Code = "PERF", NameEn = "Performance",
            IsIncludedInGosiBase = false, IsIncludedInWps = false, IsIncludedInEosb = false,
            TaxRegion = "GCC", IsActive = true,
        };
        db.BonusTypes.Add(type);
        await db.SaveChangesAsync();

        var batch = new BonusBatch
        {
            TenantId = tenantId, BonusTypeId = type.Id, BonusTypeName = type.NameEn,
            BatchNumber = "BB-SME-1", BatchName = "Perf 2026", PaymentPeriod = period,
            PaymentDate = new DateOnly(2026, 6, 25), Status = "PendingApproval",
            EmployeeCount = 1, TotalAmount = gross - tax,
        };
        db.BonusBatches.Add(batch);
        await db.SaveChangesAsync();
        db.EmployeeBonuses.Add(new EmployeeBonus
        {
            TenantId = tenantId, CompanyId = emp.CompanyId, BonusBatchId = batch.Id,
            EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id, EmployeeName = emp.FullName,
            BonusTypeId = type.Id, BonusTypeName = type.NameEn, BasicSalary = 12_000m,
            CalculationMethod = "Fixed", CalculationValue = gross,
            GrossBonusAmount = gross, TaxWithheld = tax, BonusAmount = gross - tax,
            PaymentPeriod = period, Status = "Draft", TaxRegion = "GCC",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await Bonuses(db, tenantId).ApproveBatch(batch.Id, new BatchApproveRequest("accrue"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        return batch;
    }

    /// <summary>Process (maker) → payslips → Approve (checker) → Lock (checker). The shipped order.</summary>
    private static async Task ProcessApproveLock(
        ZayraDbContext db, Guid tenantId, Guid runId, int? expectedOverriddenCount = null)
    {
        (await Payroll(db, tenantId, Maker).Process(runId, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tenantId, Maker).GeneratePayslips(runId, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tenantId, Checker).Approve(
            runId, new PayrollDecisionRequest("approved", null, expectedOverriddenCount), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tenantId, Checker).Lock(runId, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
    }

    /// <summary>Payment batch → Accepted → settle (net pay leaves the bank) → remit ALL statutory groups.</summary>
    private static async Task<PayrollPaymentBatch> PayAndRemit(
        ZayraDbContext db, Guid tenantId, Guid runId, DateOnly? cashDate = null)
    {
        var ctrl = Payroll(db, tenantId, Checker);
        (await ctrl.CreatePaymentBatch(runId, new PayrollPaymentBatchRequest("WPS", "SAR"), CancellationToken.None))
            .Should().BeOfType<CreatedResult>();
        db.ChangeTracker.Clear();
        var batch = await db.PayrollPaymentBatches.FirstAsync(b => b.PayrollRunId == runId && b.WpsStatus != WpsStatuses.Voided);
        batch.WpsStatus = WpsStatuses.Accepted;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await Payroll(db, tenantId, Checker).SettlePaymentBatch(
            batch.Id, new SettlePaymentBatchRequest("BANKREF", cashDate), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tenantId, Checker).RemitStatutory(
            runId, new RemitStatutoryRequest(RemitGroups.All, "GOSIREF", cashDate), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        return await db.PayrollPaymentBatches.AsNoTracking().FirstAsync(b => b.Id == batch.Id);
    }

    private static Task<IActionResult> Void(
        ZayraDbContext db, Guid tenantId, Guid runId, string reason,
        string? settle = PayrollVoidDispositions.FundsRecalled, string? settleRef = "RECALL-77",
        string? remit = PayrollVoidDispositions.FundsRecalled, string? remitRef = "REFUND-77",
        bool cascade = false, bool priorPeriodAdjustment = false, string? adjustmentPeriod = null) =>
        Payroll(db, tenantId, Checker).VoidRun(
            runId, new PayrollDecisionRequest(reason), CancellationToken.None,
            settlementDisposition: settle, settlementReference: settleRef,
            remittanceDisposition: remit, remittanceReference: remitRef,
            cascade: cascade, priorPeriodAdjustment: priorPeriodAdjustment, adjustmentPeriod: adjustmentPeriod);

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (a) A LOCKED + PAID + REMITTED month is voided and every control account returns to its OPENING
    //     balance — not to zero, to where it actually was.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Void_OfAFullyPaidAndRemittedMonth_RestoresEveryControlAccountToItsOpeningBalance()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        await ApprovedBonus(db, tid, f.Employee, "2026-06", gross: 5_000m, tax: 400m);

        // OPENING: taken AFTER the bonus accrual and the loan/advance disbursements, so 2300 is in credit,
        // 1400/1410 are in debit and cash is already down 11,000. Every one of these must come back.
        var opening = await Balances(db, tid);
        opening[BonusPayable].Should().Be(-5_000m, "the bonus is accrued and unpaid before the run");
        opening[LoanRecv].Should().Be(9_000m);
        opening[AdvanceRecv].Should().Be(2_000m);
        opening[CashBank].Should().Be(-11_000m);
        await AssertLedgerBalances(db, tid, "the opening ledger balances");

        await ProcessApproveLock(db, tid, f.Run.Id);

        // ACCRUED: every control account this pod is accountable for is genuinely LOADED before the money
        // moves. Asserted explicitly because "it ends at zero" is vacuous for an account that was never
        // touched — 2102 in particular only carries a balance because the bonus withheld tax.
        var run = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == f.Run.Id);
        var accrued = await Balances(db, tid);
        accrued[NetPayable].Should().Be(-run.TotalNetSalary, "the whole month's net pay is a liability");
        accrued[GosiEe].Should().BeLessThan(0m);
        accrued[GosiEr].Should().BeLessThan(0m);
        accrued[TaxPayable].Should().Be(-400m, "the bonus withholding accrued to 2102");
        accrued[LoanPayable].Should().Be(-2_000m, "the withheld 1,500 EMI + 500 advance installment");
        accrued[BonusPayable].Should().Be(0m, "the clearing retired the payable rather than re-expensing it");
        accrued[CashBank].Should().Be(opening[CashBank], "an accrual moves no cash");

        var batch = await PayAndRemit(db, tid, f.Run.Id, new DateOnly(2026, 7, 3));

        // The month really did move money and consume balances — otherwise the restore below is vacuous.
        var paid = await Balances(db, tid);
        paid[CashBank].Should().BeLessThan(opening[CashBank], "net pay and statutory remittance left the bank");
        paid[LoanRecv].Should().Be(7_500m, "one 1,500 EMI amortised the loan receivable");
        paid[AdvanceRecv].Should().Be(1_500m, "one 500 installment amortised the advance receivable");
        paid[BonusPayable].Should().Be(0m, "the payroll clearing retired the bonus payable");
        foreach (var acct in new[] { NetPayable, GosiEe, GosiEr, TaxPayable, LoanPayable })
            paid[acct].Should().Be(0m, $"{acct} was accrued and then fully settled/remitted");
        await AssertLedgerBalances(db, tid, "the paid + remitted ledger balances");

        // ── THE VOID: the funds were recalled, with references. ───────────────────────────────────
        var voided = await Void(db, tid, f.Run.Id, "June was run on stale attendance data");
        voided.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var after = await Balances(db, tid);
        foreach (var acct in ControlAccounts)
            after[acct].Should().Be(opening[acct],
                $"{acct} must return to its OPENING balance ({opening[acct]:0.00}) — a recovery that leaves any " +
                "control account moved is a manual journal entry waiting to happen");
        await AssertLedgerBalances(db, tid, "the ledger still balances after the unwind");

        // The recovery receivables are NOT used on a recall: the money genuinely came back.
        var rows = await Ledger(db, tid);
        GlControlAccounts.Balance(rows, EmpOverpaid).Should().Be(0m);
        GlControlAccounts.Balance(rows, StatPrepaid).Should().Be(0m);

        // Operational state came back with the ledger — a restored receivable whose sub-ledger still says
        // "repaid" charges the employee twice on the replacement run.
        var loan = await db.EmployeeLoans.AsNoTracking().FirstAsync(l => l.Id == f.LoanId);
        loan.OutstandingBalance.Should().Be(9_000m);
        loan.TotalRepaid.Should().Be(0m);
        loan.Status.Should().Be("Active");
        var adv = await db.SalaryAdvances.AsNoTracking().FirstAsync(a => a.Id == f.AdvanceId);
        adv.OutstandingBalance.Should().Be(2_000m);
        adv.TotalRepaid.Should().Be(0m);
        (await db.PayrollPaymentBatches.AsNoTracking().FirstAsync(b => b.Id == batch.Id)).WpsStatus
            .Should().Be(WpsStatuses.Voided);
        (await db.Payslips.AsNoTracking().Where(p => p.PayrollRunId == f.Run.Id).ToListAsync())
            .Should().OnlyContain(p => !p.IsPublishedToEss, "a voided month must leave ESS");
    }

    /// <summary>
    /// The other half of the doctrine, and the one an auditor will actually challenge: when the cash did
    /// NOT come back, "back where they started" must NOT be true of Cash/Bank. The control LIABILITIES
    /// still close to zero, the bank keeps its real outflow, and the difference is carried as a
    /// recoverable asset (1420 / 1430) instead of being invented back onto the cash line.
    /// </summary>
    [Fact]
    public async Task Void_WhenTheCashHasAlreadyLeft_KeepsTheOutflow_AndCarriesItAsARecoverable()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);

        var openingCash = (await Balances(db, tid))[CashBank];
        await ProcessApproveLock(db, tid, f.Run.Id);
        await PayAndRemit(db, tid, f.Run.Id);
        var cashAfterPay = (await Balances(db, tid))[CashBank];
        // Everything this run actually took out of the bank: net pay + the GOSI/TAX remittances. The LOAN
        // group withheld an EMI against a receivable and moved no cash at all, which is why it needs no
        // disposition and is reversed either way.
        var cashOutOnThisRun = openingCash - cashAfterPay;
        cashOutOnThisRun.Should().BeGreaterThan(0m);
        var run = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == f.Run.Id);

        // A void that touches disbursed cash must REFUSE until the operator states what happened. The
        // unwind runs money-out first, so the STATUTORY remittance is the first cash journal it reaches.
        var unstated = await Payroll(db, tid, Checker).VoidRun(
            f.Run.Id, new PayrollDecisionRequest("wrong month"), CancellationToken.None);
        unstated.Should().BeOfType<UnprocessableEntityObjectResult>();
        Body(unstated).Should().Contain("remittance_disposition_required");
        db.ChangeTracker.Clear();
        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == f.Run.Id)).Status
            .Should().Be("Locked", "a refused void writes nothing");

        // EACH cash journal needs its OWN election — one disposition is never inferred from the other.
        var onlyRemittanceStated = await Payroll(db, tid, Checker).VoidRun(
            f.Run.Id, new PayrollDecisionRequest("wrong month"), CancellationToken.None,
            remittanceDisposition: PayrollVoidDispositions.FundsDisbursed);
        onlyRemittanceStated.Should().BeOfType<UnprocessableEntityObjectResult>();
        Body(onlyRemittanceStated).Should().Contain("settlement_disposition_required");
        db.ChangeTracker.Clear();

        // A recall claim with NO reference is refused too — a reversal of cash is a claim that needs evidence.
        var noRef = await Void(db, tid, f.Run.Id, "wrong month", settleRef: null);
        noRef.Should().BeOfType<UnprocessableEntityObjectResult>();
        Body(noRef).Should().Contain("settlement_recall_reference_required");
        db.ChangeTracker.Clear();
        (await db.FinanceGlEntries.AsNoTracking().Where(x => x.SourceEntityId == f.Run.Id).ToListAsync())
            .Count(x => GlEventTypes.IsPayrollUnwindContra(x.EventType))
            .Should().Be(0, "three refusals in a row must have written nothing at all");

        var ok = await Void(db, tid, f.Run.Id, "salaries already wired; month is wrong",
            settle: PayrollVoidDispositions.FundsDisbursed, settleRef: null,
            remit: PayrollVoidDispositions.FundsDisbursed, remitRef: null);
        ok.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var after = await Balances(db, tid);
        after[CashBank].Should().Be(cashAfterPay,
            "the money left the bank and did not come back — crediting Cash/Bank here would put money on the " +
            "books the bank statement disproves, and the replacement would disburse a second time");
        foreach (var acct in new[] { NetPayable, GosiEe, GosiEr, LoanPayable })
            after[acct].Should().Be(0m, $"{acct} must still close to zero — the liability is discharged either way");

        var rows = await Ledger(db, tid);
        var employeeHolds  = GlControlAccounts.Balance(rows, EmpOverpaid);
        var authorityHolds = GlControlAccounts.Balance(rows, StatPrepaid);
        employeeHolds.Should().Be(run.TotalNetSalary, "the employee holds exactly the net pay that was wired");
        (employeeHolds + authorityHolds).Should().Be(cashOutOnThisRun,
            "every riyal that left the bank on this run is carried as recoverable — exactly once, and no more " +
            "than left");
        // Receivables restored by the LOAN unwind must NOT be re-recognised as recoverable: that money was
        // never a disbursement, it was a withholding.
        after[LoanRecv].Should().Be(9_000m, "the withheld EMI goes back to the loan receivable either way");
        after[AdvanceRecv].Should().Be(2_000m);
        await AssertLedgerBalances(db, tid, "a reclassification journal balances like any other");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (b) After the void, a re-lock posts FRESH BALANCED GL — and the OLD guard would have posted none.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The defect, stated as arithmetic. The pre-B3 probe was
    /// <c>SourceModule=="Payroll" &amp;&amp; SourceEntityId==id</c> — no EventType, no <c>!IsReversed</c> — and a
    /// void writes its contras with that SAME module + entity id and <c>IsReversed=false</c>. So the probe
    /// answers "already posted" over a ledger that holds NOTHING live, and the re-lock skips the journal
    /// while still marking slips Final, publishing payslips to ESS and setting ErpPostingStatus.
    ///
    /// <para>Both predicates are evaluated here against the SAME post-void ledger, so the test states the
    /// bug rather than describing it — then the shipped Lock is driven and the fresh journal asserted.</para>
    /// </summary>
    [Fact]
    public async Task AfterAVoid_TheOldIdempotencyProbeSaysAlreadyPosted_WhileNothingIsLive()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        await ProcessApproveLock(db, tid, f.Run.Id);
        await PayAndRemit(db, tid, f.Run.Id);
        var originalAccrual = await db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.SourceEntityId == f.Run.Id && x.EventType == GlEventTypes.Accrual).ToListAsync();
        originalAccrual.Should().NotBeEmpty();

        (await Void(db, tid, f.Run.Id, "re-run June")).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // THE OLD PREDICATE, verbatim.
        var oldProbe = await db.FinanceGlEntries.AnyAsync(
            x => x.TenantId == tid && x.SourceModule == "Payroll" && x.SourceEntityId == f.Run.Id);
        oldProbe.Should().BeTrue(
            "the void's own contras satisfy the pre-B3 guard, so Lock would have skipped the journal entirely");

        // THE SHIPPED PREDICATE.
        var newProbe = await db.FinanceGlEntries.AnyAsync(
            x => x.TenantId == tid && x.SourceModule == "Payroll" && x.SourceEntityId == f.Run.Id
              && x.EventType == GlEventTypes.Accrual && !x.IsReversed);
        newProbe.Should().BeFalse("nothing is LIVE: the accrual was contra'd, so the run has not accrued");

        // And the consequence the old guard would have produced is a genuine dead end, not a cosmetic one:
        // with no live accrual the money path itself refuses. (Proven against the shipped settle guard,
        // which uses the same LIVE-accrual predicate the fixed Lock now uses.)
        var tracked = await db.PayrollRuns.FirstAsync(r => r.Id == f.Run.Id);
        tracked.Status = "Locked";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var deadBatch = await db.PayrollPaymentBatches.FirstAsync(b => b.PayrollRunId == f.Run.Id);
        deadBatch.WpsStatus = WpsStatuses.Accepted;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var settleWithNoAccrual = await Payroll(db, tid, Checker).SettlePaymentBatch(
            deadBatch.Id, new SettlePaymentBatchRequest("REF"), CancellationToken.None);
        Body(settleWithNoAccrual).Should().Contain("gl_not_accrued",
            "a zero-GL lock leaves a month whose payslips assert a liability the ledger does not hold, " +
            "and which can never be paid");
    }

    /// <summary>
    /// The fix, end to end: the same run is re-locked after the void and posts a FRESH, BALANCED accrual
    /// of the same magnitude — and the ledger now legitimately holds two accrual journals for one run, of
    /// which exactly one is live. That shape is asserted directly because it is what every downstream
    /// "what did this run accrue?" query must survive.
    /// </summary>
    [Fact]
    public async Task ReLockAfterVoid_PostsAFreshBalancedAccrual_AndExactlyOneAccrualIsLive()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        await ProcessApproveLock(db, tid, f.Run.Id);
        var firstAccrual = await db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.SourceEntityId == f.Run.Id && x.EventType == GlEventTypes.Accrual).ToListAsync();

        (await Void(db, tid, f.Run.Id, "re-run June")).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var back = await db.PayrollRuns.FirstAsync(r => r.Id == f.Run.Id);
        back.Status = "Approved";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var relock = await Payroll(db, tid, Checker).Lock(f.Run.Id, CancellationToken.None);
        relock.Should().BeOfType<OkObjectResult>();
        Body(relock).Should().Contain("\"glPosted\":true",
            "whether a lock actually posted a journal must be part of the ANSWER, not buried in audit metadata");
        db.ChangeTracker.Clear();

        var accruals = await db.FinanceGlEntries.AsNoTracking()
            .Where(x => x.SourceEntityId == f.Run.Id && x.EventType == GlEventTypes.Accrual).ToListAsync();
        var live = accruals.Where(a => !a.IsReversed).ToList();
        live.Should().HaveCount(firstAccrual.Count, "a complete fresh journal, not a partial one");
        accruals.Count(a => a.IsReversed).Should().Be(firstAccrual.Count, "the original stands, flagged reversed");
        live.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount)
            .Should().Be(live.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount),
                "the fresh accrual balances on its own");
        live.Sum(l => l.Amount).Should().Be(firstAccrual.Sum(l => l.Amount),
            "the re-posted month is the same size as the one that was reversed");
        await AssertLedgerBalances(db, tid, "two accruals and one contra set still leave the ledger balanced");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (c) A bonus consumed by the voided month is re-usable; 2300 reaches zero; the expense is
    //     recognised EXACTLY ONCE across the whole recovery.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task VoidedMonthsBonus_IsReusable_And2300ClosesWithTheExpenseRecognisedExactlyOnce()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        var batch = await ApprovedBonus(db, tid, f.Employee, "2026-06", gross: 6_000m, tax: 500m);

        await ProcessApproveLock(db, tid, f.Run.Id);
        await PayAndRemit(db, tid, f.Run.Id);
        (await db.EmployeeBonuses.AsNoTracking().FirstAsync()).Status.Should().Be("PaidInPayroll");
        (await Balances(db, tid))[BonusPayable].Should().Be(0m, "the run's clearing retired the payable");

        (await Void(db, tid, f.Run.Id, "wrong bonus population")).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // The bonus is genuinely RE-USABLE, not merely un-cleared in the ledger: an un-restored operational
        // state means the re-opened payable can never be cleared again and the employee's bonus is lost.
        var bonus = await db.EmployeeBonuses.AsNoTracking().FirstAsync();
        bonus.Status.Should().Be("Approved");
        bonus.PayrollRunId.Should().BeNull();
        var reopenedBatch = await db.BonusBatches.AsNoTracking().FirstAsync(b => b.Id == batch.Id);
        reopenedBatch.IsLockedByPayroll.Should().BeFalse();
        reopenedBatch.Status.Should().Be("Approved");
        (await Balances(db, tid))[BonusPayable].Should().Be(-6_000m, "the payable is outstanding again, at GROSS");

        // ── The REPLACEMENT re-consumes it. ────────────────────────────────────────────────────────
        var created = await Payroll(db, tid, Maker).CreateRun(
            new CreatePayrollRunRequest(2026, 6, f.CompanyId, PayrollRunTypes.Replacement, f.Run.Id),
            CancellationToken.None);
        created.Should().BeOfType<CreatedResult>();
        var replacement = created.As<CreatedResult>().Value.As<PayrollRun>();
        db.ChangeTracker.Clear();

        await ProcessApproveLock(db, tid, replacement.Id);
        await PayAndRemit(db, tid, replacement.Id);

        (await db.EmployeeBonuses.AsNoTracking().FirstAsync()).PayrollRunId.Should().Be(replacement.Id,
            "the same bonus is paid by the corrected month");

        var rows = await Ledger(db, tid);
        GlControlAccounts.Balance(rows, BonusPayable).Should().Be(0m,
            "2300 must land exactly on zero across accrual → clearing → contra → re-clearing");
        GlControlAccounts.Balance(rows, BonusExpense).Should().Be(6_000m,
            "the bonus EXPENSE is recognised exactly ONCE, at accrual — the double-count POD-B1b closed must " +
            "not come back through the recovery path");
        rows.Count(l => l.EventType == GlEventTypes.BonusPayrollClearing && !l.IsReversed)
            .Should().Be(1, "exactly one live clearing: the replacement's");
        rows.Count(l => l.EventType == GlEventTypes.BonusAccrual).Should().Be(1,
            "the accrual is never re-posted — a re-consumed bonus clears the ORIGINAL payable");
        await AssertLedgerBalances(db, tid, "the recovered month + its bonus still balance");

        // The replacement paid a full month, and the voided month left nothing behind on the money side.
        GlControlAccounts.Balance(rows, LoanRecv).Should().Be(7_500m,
            "the loan is amortised once, by the surviving run only");
        foreach (var acct in new[] { NetPayable, GosiEe, GosiEr, TaxPayable, LoanPayable })
            GlControlAccounts.Balance(rows, acct).Should().Be(0m, $"{acct} is flat across the recovered month");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (d) POD-A1 GOSI reconciliation ties out for the SURVIVING run; reversed entries never double-count.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GosiTieOut_HoldsForTheSurvivingRun_AndTheVoidedMonthIsNotCountedTwice()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        var svc = new GosiReconciliationService(db, new _SmePackResolver());

        await ProcessApproveLock(db, tid, f.Run.Id);
        await PayAndRemit(db, tid, f.Run.Id);
        var beforeVoid = await svc.ReconcileAsync(
            tid, await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == f.Run.Id), CancellationToken.None);
        beforeVoid.GlEmployeeDelta.Should().Be(0m, "POD-A1: deducted == report == GL");
        beforeVoid.GlEmployerDelta.Should().Be(0m);
        var monthEe = beforeVoid.ActualEmployeeTotal;
        var monthEr = beforeVoid.ActualEmployerTotal;
        monthEe.Should().BeGreaterThan(0m);

        (await Void(db, tid, f.Run.Id, "re-run June")).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var created = await Payroll(db, tid, Maker).CreateRun(
            new CreatePayrollRunRequest(2026, 6, f.CompanyId, PayrollRunTypes.Replacement, f.Run.Id),
            CancellationToken.None);
        var replacement = created.As<CreatedResult>().Value.As<PayrollRun>();
        db.ChangeTracker.Clear();
        await ProcessApproveLock(db, tid, replacement.Id);
        await PayAndRemit(db, tid, replacement.Id);

        // 1. The SURVIVING run ties out on its own.
        var survivor = await svc.ReconcileAsync(
            tid, await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == replacement.Id), CancellationToken.None);
        survivor.GlPosted.Should().BeTrue();
        survivor.GlEmployeeDelta.Should().Be(0m, "the replacement is the filing source and must tie out");
        survivor.GlEmployerDelta.Should().Be(0m);
        survivor.IsVoided.Should().BeFalse();
        survivor.GlEmployeeLiability.Should().Be(monthEe,
            "the replacement's liability is ONE month's statutory, not two");

        // 2. The VOIDED run is not silently reported as a filable source, and names its successor.
        var dead = await svc.ReconcileAsync(
            tid, await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == f.Run.Id), CancellationToken.None);
        dead.IsVoided.Should().BeTrue();
        dead.GlPosted.Should().BeFalse("its accrual is reversed — there is no live liability behind it");
        dead.SupersededByRunId.Should().Be(replacement.Id);
        dead.FilingStatusNote.Should().NotBeNullOrWhiteSpace();

        // 3. The PERIOD tie-out — what actually gets filed — counts the month exactly once.
        var period = await svc.ReconcilePeriodAsync(tid, f.CompanyId, 2026, 6, CancellationToken.None);
        period.ActualEmployeeTotal.Should().Be(monthEe, "a voided run must not inflate the period's filing");
        period.ActualEmployerTotal.Should().Be(monthEr);
        period.VarianceCount.Should().Be(0);

        // 4. And the reversed rows are excluded even when BOTH journals hang off ONE run id — the shape the
        //    fixed Lock guard creates. Pre-B3 the tie-out read every Payroll row for the run with no
        //    EventType and no !IsReversed filter and survived only because a contra's Description is
        //    rewritten; a second live accrual on the same run doubles glEe with no string coincidence left.
        (await Void(db, tid, replacement.Id, "second correction", settleRef: "R2", remitRef: "R3"))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var reapproved = await db.PayrollRuns.FirstAsync(r => r.Id == replacement.Id);
        reapproved.Status = "Approved";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await Payroll(db, tid, Checker).Lock(replacement.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var twoJournals = await svc.ReconcileAsync(
            tid, await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == replacement.Id), CancellationToken.None);
        twoJournals.GlEmployeeLiability.Should().Be(monthEe,
            "one run now carries a reversed accrual AND a live one — the reversed one must not be counted");
        twoJournals.GlEmployerLiability.Should().Be(monthEr);
        twoJournals.GlEmployeeDelta.Should().Be(0m, "the tie-out still holds after a re-lock");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (e) A blocking validation error can be resolved WITH an audited reason — and never without one.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BlockingError_IsOnlyClearableWithAReason_ADifferentActor_AndAnAcknowledgedCount()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid, withIban: false);   // ⇒ MISSING_IBAN, Severity=Error

        (await Payroll(db, tid, Maker).Process(f.Run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var blocking = await db.PayrollValidationResults.AsNoTracking()
            .Where(x => x.PayrollRunId == f.Run.Id && x.Severity == "Error").ToListAsync();
        blocking.Should().ContainSingle(r => r.Code == "MISSING_IBAN");
        var target = blocking.First(r => r.Code == "MISSING_IBAN");

        // The dead end, restated: without a resolver, Approve is a wall and the only exit is a void.
        var blocked = await Payroll(db, tid, Checker).Approve(
            f.Run.Id, new PayrollDecisionRequest("go"), CancellationToken.None);
        blocked.Should().BeOfType<UnprocessableEntityObjectResult>();
        Body(blocked).Should().Contain("validation_errors");
        db.ChangeTracker.Clear();

        // ── The four gates. ───────────────────────────────────────────────────────────────────────
        // 1. NO reason.
        var noReason = await Payroll(db, tid, Checker).ResolveValidationResult(
            f.Run.Id, target.Id, new PayrollReasonRequest(null), CancellationToken.None);
        noReason.Should().BeOfType<BadRequestObjectResult>();
        Body(noReason).Should().Contain("reason_required");
        db.ChangeTracker.Clear();

        // 2. A token reason is not accountability.
        var thinReason = await Payroll(db, tid, Checker).ResolveValidationResult(
            f.Run.Id, target.Id, new PayrollReasonRequest("ok"), CancellationToken.None);
        thinReason.Should().BeOfType<BadRequestObjectResult>();
        Body(thinReason).Should().Contain("reason_required");
        db.ChangeTracker.Clear();

        // 3. The person who prepared the run cannot clear its own compliance errors.
        var selfClear = await Payroll(db, tid, Maker).ResolveValidationResult(
            f.Run.Id, target.Id, new PayrollReasonRequest("Paid by cheque, bank details on file in Finance."),
            CancellationToken.None);
        selfClear.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        Body(selfClear).Should().Contain("maker_checker_violation");
        db.ChangeTracker.Clear();

        // 4. A code that asserts an impossibility (or a cross-run double payment) is NEVER overridable —
        //    default-deny, and the refusal names the real exit rather than leaving the operator stuck.
        db.PayrollValidationResults.Add(new PayrollValidationResult
        {
            TenantId = tid, PayrollRunId = f.Run.Id, EmployeeId = f.Employee.Id,
            Severity = "Error", Code = "ALREADY_PAID_THIS_PERIOD",
            Message = "Employee already paid in this period by another run.",
        });
        await db.SaveChangesAsync();
        var crossRun = await db.PayrollValidationResults.AsNoTracking()
            .FirstAsync(x => x.Code == "ALREADY_PAID_THIS_PERIOD");
        db.ChangeTracker.Clear();
        var refused = await Payroll(db, tid, Checker).ResolveValidationResult(
            f.Run.Id, crossRun.Id, new PayrollReasonRequest("Finance accepts the risk of a second salary."),
            CancellationToken.None);
        refused.Should().BeOfType<UnprocessableEntityObjectResult>();
        Body(refused).Should().Contain("code_not_overridable");
        db.ChangeTracker.Clear();
        (await db.PayrollValidationResults.AsNoTracking().FirstAsync(x => x.Id == crossRun.Id)).IsResolved
            .Should().BeFalse("a refused override must not clear the block");
        db.PayrollValidationResults.Remove(await db.PayrollValidationResults.FirstAsync(x => x.Id == crossRun.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // ── The legitimate override. ──────────────────────────────────────────────────────────────
        const string reason = "Contractor paid by cheque this month; bank mandate pending (ticket FIN-3312).";
        var resolved = await Payroll(db, tid, Checker).ResolveValidationResult(
            f.Run.Id, target.Id, new PayrollReasonRequest(reason), CancellationToken.None);
        resolved.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var cleared = await db.PayrollValidationResults.AsNoTracking().FirstAsync(x => x.Id == target.Id);
        cleared.IsResolved.Should().BeTrue();
        cleared.ResolvedReason.Should().Be(reason);
        cleared.ResolvedByUserId.Should().Be(Checker, "the override carries WHO, not just THAT");
        cleared.ResolvedAtUtc.Should().NotBeNull();

        // Durability: /validate deletes and rebuilds every result row, so an override recorded only on the
        // result would silently evaporate and the run would re-stick.
        (await Payroll(db, tid, Maker).Validate(f.Run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await db.PayrollValidationResults.AsNoTracking()
            .Where(x => x.PayrollRunId == f.Run.Id && x.Severity == "Error" && !x.IsResolved).ToListAsync())
            .Should().BeEmpty("the override must survive a re-validate");

        // The approver must ACKNOWLEDGE the count — an override nobody counts is not a control.
        var unacknowledged = await Payroll(db, tid, Checker).Approve(
            f.Run.Id, new PayrollDecisionRequest("go"), CancellationToken.None);
        unacknowledged.Should().BeOfType<ConflictObjectResult>();
        Body(unacknowledged).Should().Contain("overridden_errors_not_acknowledged");
        db.ChangeTracker.Clear();

        (await Payroll(db, tid, Checker).Approve(
            f.Run.Id, new PayrollDecisionRequest("go", null, 1), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // …and the run PROCEEDS: the dead end is genuinely gone, and the lock restates the exception.
        var locked = await Payroll(db, tid, Checker).Lock(f.Run.Id, CancellationToken.None);
        locked.Should().BeOfType<OkObjectResult>();
        var lockBody = Body(locked);
        lockBody.Should().Contain("\"glPosted\":true");
        lockBody.Should().Contain("\"overriddenCount\":1",
            "the last irreversible step before payment must restate what was consciously overridden");
        db.ChangeTracker.Clear();

        // The override is on the tamper-evident chain, with the actor and the reason.
        var chain = await db.PayrollAuditLogs.AsNoTracking()
            .Where(x => x.Action == "payroll.validation.overridden").ToListAsync();
        chain.Should().ContainSingle();
        chain[0].UserId.Should().Be(Checker);
        chain[0].MetadataJson.Should().Contain(reason);
        chain[0].EntryHash.Should().NotBeNullOrEmpty();
    }

    /// <summary>An override can be TAKEN BACK, and the block returns. Without this the "audited exception"
    /// is one-way and a mistaken clearance is itself a dead end.</summary>
    [Fact]
    public async Task RevokingAnOverride_ReinstatesTheBlock()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid, withIban: false);
        (await Payroll(db, tid, Maker).Process(f.Run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var target = await db.PayrollValidationResults.AsNoTracking()
            .FirstAsync(x => x.PayrollRunId == f.Run.Id && x.Code == "MISSING_IBAN");
        (await Payroll(db, tid, Checker).ResolveValidationResult(
            f.Run.Id, target.Id, new PayrollReasonRequest("Cheque payment agreed with Finance for June."),
            CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var row = await db.PayrollValidationOverrides.AsNoTracking().FirstAsync();
        (await Payroll(db, tid, Checker).RevokeValidationOverride(f.Run.Id, row.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        (await db.PayrollValidationResults.AsNoTracking().FirstAsync(x => x.Id == target.Id)).IsResolved
            .Should().BeFalse();
        var blockedAgain = await Payroll(db, tid, Checker).Approve(
            f.Run.Id, new PayrollDecisionRequest("go"), CancellationToken.None);
        blockedAgain.Should().BeOfType<UnprocessableEntityObjectResult>();
        Body(blockedAgain).Should().Contain("validation_errors");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (f) Recovery inside a CLOSED period is refused — on BOTH sides of the recovery.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ClosedPeriod_RefusesTheVoid_LeavesTheMonthUntouched_AndAlsoRefusesTheReplacementsLock()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid, 2026, 6);
        await ProcessApproveLock(db, tid, f.Run.Id);
        var batch = await PayAndRemit(db, tid, f.Run.Id, new DateOnly(2026, 7, 5));

        (await Gl(db, tid).ClosePeriod("2026-06", f.CompanyId, new GlPeriodActionRequest("month-end"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await Gl(db, tid).ClosePeriod("2026-07", f.CompanyId, new GlPeriodActionRequest("month-end"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var snapshotGl   = (await Ledger(db, tid)).Count;
        var snapshotLoan = (await db.EmployeeLoans.AsNoTracking().FirstAsync(l => l.Id == f.LoanId)).OutstandingBalance;
        var voidRows     = await db.PayrollAuditLogs.AsNoTracking().CountAsync(x => x.Action == "payroll.run.voided");

        var refused = await Void(db, tid, f.Run.Id, "June is wrong");
        refused.Should().BeOfType<UnprocessableEntityObjectResult>();
        var body = Body(refused);
        body.Should().Contain("gl_period_closed");
        body.Should().Contain("2026-06");
        body.Should().Contain("2026-07",
            "ALL closed periods the unwind would write into must be listed — a settled+remitted run writes " +
            "into up to three, and reporting them one at a time turns a recovery into three round-trips");
        db.ChangeTracker.Clear();

        // "Refused ⇒ untouched" must be literally true, not merely true of the ledger.
        (await Ledger(db, tid)).Count.Should().Be(snapshotGl, "no contra was written");
        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == f.Run.Id)).Status.Should().Be("Locked");
        (await db.EmployeeLoans.AsNoTracking().FirstAsync(l => l.Id == f.LoanId)).OutstandingBalance
            .Should().Be(snapshotLoan, "the consumption unwind must not have run");
        (await db.PayrollPaymentBatches.AsNoTracking().FirstAsync(b => b.Id == batch.Id)).WpsStatus
            .Should().Be(WpsStatuses.Paid);
        (await db.Payslips.AsNoTracking().Where(p => p.PayrollRunId == f.Run.Id).ToListAsync())
            .Should().OnlyContain(p => p.IsPublishedToEss);
        (await db.PayrollAuditLogs.AsNoTracking().CountAsync(x => x.Action == "payroll.run.voided"))
            .Should().Be(voidRows, "a refused recovery must not claim on the audit chain that it happened");

        // An audited REOPEN is the stated exit; then the void proceeds.
        (await Gl(db, tid).ReopenPeriod("2026-06", f.CompanyId, new GlPeriodActionRequest("payroll correction"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await Gl(db, tid).ReopenPeriod("2026-07", f.CompanyId, new GlPeriodActionRequest("payroll correction"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Void(db, tid, f.Run.Id, "June is wrong")).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        // ── The OTHER side of the recovery: the replacement must not post into a closed month either. ──
        (await Gl(db, tid).ClosePeriod("2026-06", f.CompanyId, new GlPeriodActionRequest("re-closed"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var created = await Payroll(db, tid, Maker).CreateRun(
            new CreatePayrollRunRequest(2026, 6, f.CompanyId, PayrollRunTypes.Replacement, f.Run.Id),
            CancellationToken.None);
        created.Should().BeOfType<CreatedResult>();
        var replacement = created.As<CreatedResult>().Value.As<PayrollRun>();
        db.ChangeTracker.Clear();

        (await Payroll(db, tid, Maker).Process(replacement.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tid, Checker).Approve(replacement.Id, new PayrollDecisionRequest("ok"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var glBeforeLock = (await Ledger(db, tid)).Count;
        var lockRefused = await Payroll(db, tid, Checker).Lock(replacement.Id, CancellationToken.None);
        lockRefused.Should().BeOfType<UnprocessableEntityObjectResult>();
        Body(lockRefused).Should().Contain("gl_period_closed");
        db.ChangeTracker.Clear();
        (await Ledger(db, tid)).Count.Should().Be(glBeforeLock, "a refused lock posts nothing");
        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == replacement.Id)).Status
            .Should().Be("Approved", "…and does not advance the run");

        (await Gl(db, tid).ReopenPeriod("2026-06", f.CompanyId, new GlPeriodActionRequest("post the replacement"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tid, Checker).Lock(replacement.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        await AssertLedgerBalances(db, tid, "the recovered month balances once the period is legitimately open");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // (g) Every recovery action is on the tamper-evident chain — and the chain is genuinely checked.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EveryRecoveryActionIsOnTheHashChain_WithActorAndReason_AndTheChainStillVerifies()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);

        await ProcessApproveLock(db, tid, f.Run.Id);
        await PayAndRemit(db, tid, f.Run.Id);
        const string voidReason = "June processed against the wrong attendance import (INC-4471).";
        (await Void(db, tid, f.Run.Id, voidReason)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var created = await Payroll(db, tid, Maker).CreateRun(
            new CreatePayrollRunRequest(2026, 6, f.CompanyId, PayrollRunTypes.Replacement, f.Run.Id),
            CancellationToken.None);
        var replacement = created.As<CreatedResult>().Value.As<PayrollRun>();
        db.ChangeTracker.Clear();
        await ProcessApproveLock(db, tid, replacement.Id);
        await PayAndRemit(db, tid, replacement.Id);

        var integrity = await Payroll(db, tid, Checker).AuditIntegrity(CancellationToken.None);
        var report = integrity.As<OkObjectResult>().Value.As<AuditIntegrityReport>();
        report.IsValid.Should().BeTrue("every recovery write must leave the chain verifiable");
        report.Failures.Should().BeEmpty();
        report.CheckedEntries.Should().BeGreaterThan(0);

        var logs = await db.PayrollAuditLogs.AsNoTracking().OrderBy(x => x.Seq).ToListAsync();
        logs.Select(l => l.Seq).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        logs.Should().OnlyContain(l => !string.IsNullOrEmpty(l.EntryHash), "an unsealed row is a hole in the chain");

        // The recovery is narratable end to end from the chain alone.
        foreach (var action in new[]
                 {
                     "payroll.run.locked", "payroll.batch.settled", "payroll.statutory.remitted",
                     "payroll.run.voided", "payroll.run.created",
                 })
            logs.Should().Contain(l => l.Action == action, $"'{action}' must be on the chain");

        var voidLog = logs.Single(l => l.Action == "payroll.run.voided");
        voidLog.UserId.Should().Be(Checker, "an unattributed void is not an audit trail");
        voidLog.EntityId.Should().Be(f.Run.Id.ToString());
        voidLog.MetadataJson.Should().Contain(voidReason, "the REASON is part of the sealed record");
        voidLog.MetadataJson.Should().Contain("FundsRecalled", "…and so is the funds disposition claimed");
        voidLog.MetadataJson.Should().Contain("RECALL-77", "…and its evidence reference");

        var replacementLock = logs.Last(l => l.Action == "payroll.run.locked");
        replacementLock.MetadataJson.Should().Contain("Replacement");
        replacementLock.MetadataJson.Should().Contain(f.Run.Id.ToString(),
            "the replacement's lock names the run it replaces, so the pair is legible without a join");

        // ── The chain is a CONTROL, not a decoration: tamper with the sealed void record and the
        //    verification must fail and NAME it. (ExecuteUpdate bypasses the ChangeTracker append-only
        //    guard, which is exactly the attack the hash chain exists to catch.)
        await db.PayrollAuditLogs.Where(x => x.Id == voidLog.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.MetadataJson,
                voidLog.MetadataJson.Replace(voidReason, "routine month-end adjustment")), CancellationToken.None);
        db.ChangeTracker.Clear();

        var tampered = (await Payroll(db, tid, Checker).AuditIntegrity(CancellationToken.None))
            .As<OkObjectResult>().Value.As<AuditIntegrityReport>();
        tampered.IsValid.Should().BeFalse("a rewritten void reason must be detectable");
        tampered.Failures.Should().Contain(x => x.AuditLogId == voidLog.Id && x.Reason == "entry_hash_mismatch");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // Dead ends: the recovery must not create new ones.
    // ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A run that has NOT posted GL is recovered by <c>reopen</c>, and the reopened run must be genuinely
    /// re-runnable: consumption released, ESS payslips gone (not merely un-published — a stale Payslip row
    /// makes the regenerate a silent no-op carrying pre-correction figures), and the selector unfrozen.
    /// </summary>
    [Fact]
    public async Task ReopenOfAnUnpostedRun_ReleasesConsumption_AndTheRerunPaysTheCorrectedFigures()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);

        (await Payroll(db, tid, Maker).Process(f.Run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tid, Maker).GeneratePayslips(f.Run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var firstNet = (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == f.Run.Id)).TotalNetSalary;
        (await db.EmployeeLoans.AsNoTracking().FirstAsync(l => l.Id == f.LoanId)).OutstandingBalance.Should().Be(7_500m);

        var noReason = await Payroll(db, tid, Checker).ReopenRun(
            f.Run.Id, new PayrollReasonRequest(null), CancellationToken.None);
        noReason.Should().BeOfType<BadRequestObjectResult>();
        Body(noReason).Should().Contain("reason_required");
        db.ChangeTracker.Clear();

        (await Payroll(db, tid, Checker).ReopenRun(
            f.Run.Id, new PayrollReasonRequest("Housing allowance was keyed wrong"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var reopened = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == f.Run.Id);
        reopened.Status.Should().Be("Draft");
        reopened.TotalNetSalary.Should().Be(0m);
        (await db.EmployeeLoans.AsNoTracking().FirstAsync(l => l.Id == f.LoanId)).OutstandingBalance
            .Should().Be(9_000m, "the EMI is released — otherwise the re-run collects a second installment");
        (await db.Payslips.AsNoTracking().Where(p => p.PayrollRunId == f.Run.Id).ToListAsync())
            .Should().BeEmpty("a stale ESS payslip would survive the re-run carrying the pre-correction figures");
        (await db.PayrollRunConsumptions.AsNoTracking().Where(c => c.PayrollRunId == f.Run.Id).ToListAsync())
            .Should().BeEmpty("the witness is replayed and cleared, ready for a fresh one");

        // Correct the input and re-run: the corrected figures are what the month now pays.
        var salary = await db.EmployeeSalaryStructures.FirstAsync(s => s.EmployeeId == f.Employee.Id);
        salary.HousingAllowance = 4_500m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await ProcessApproveLock(db, tid, f.Run.Id);
        var corrected = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == f.Run.Id);
        corrected.TotalNetSalary.Should().BeGreaterThan(firstNet, "the correction is what got paid");
        (await db.Payslips.AsNoTracking().Where(p => p.PayrollRunId == f.Run.Id).ToListAsync())
            .Should().ContainSingle("exactly one payslip for the month");
        await AssertLedgerBalances(db, tid, "the re-run's journal balances");
    }

    /// <summary>
    /// The seam POD-B2 left open: a live Correction hanging off a run being voided. It must be cascaded or
    /// refused — never orphaned, because a child counting against a month that no longer exists
    /// double-counts against the Replacement.
    /// </summary>
    [Fact]
    public async Task VoidingARunWithLiveChildren_IsRefusedUntilTheCascadeIsStated()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        await ProcessApproveLock(db, tid, f.Run.Id);

        var child = await Payroll(db, tid, Maker).CreateRun(
            new CreatePayrollRunRequest(2026, 6, f.CompanyId, PayrollRunTypes.Correction, f.Run.Id),
            CancellationToken.None);
        child.Should().BeOfType<CreatedResult>();
        var correction = child.As<CreatedResult>().Value.As<PayrollRun>();
        db.ChangeTracker.Clear();

        var refused = await Void(db, tid, f.Run.Id, "June is wrong");
        refused.Should().BeOfType<UnprocessableEntityObjectResult>();
        Body(refused).Should().Contain("child_runs_live");
        db.ChangeTracker.Clear();
        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == f.Run.Id)).Status.Should().Be("Locked");

        var cascaded = await Void(db, tid, f.Run.Id, "June is wrong", cascade: true);
        cascaded.Should().BeOfType<OkObjectResult>();
        Body(cascaded).Should().Contain(correction.Id.ToString());
        db.ChangeTracker.Clear();
        (await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == correction.Id)).Status
            .Should().Be("Voided", "the amending run is voided with its parent, not left dangling");
        await AssertLedgerBalances(db, tid, "the cascade leaves the ledger balanced");
    }
}

// ── Test doubles (file-scoped) ──────────────────────────────────────────────────

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

file sealed class _SmeScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(
        ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        {
            Level = Zayra.Api.Application.Common.DataScopeLevel.Organization,
            AllowedEmployeeIds = null,
        });
}

file sealed class _SmeHttp : IHttpContextAccessor
{
    public _SmeHttp(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _SmeNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct)
        => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct)
        => Task.CompletedTask;
}

file sealed class _SmePackResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_SmeRules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new KsaWageProtectionExporter();
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

file sealed class _SmeStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
