using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Loan and advance recovery at payroll Lock credits the RECEIVABLE (1400 loans, 1410 advances) directly, capped at what
/// that receivable carries; only an uncovered excess credits the 2107 clearing liability, which the manual loan remittance
/// still clears. The journal still balances to the halala and the employee's net pay is unchanged.
///
/// Fixture: one Saudi employee, a loan of 9,000 (instalment 1,500) and an advance of 2,000 (instalment 500). The
/// disbursements are on the books unless a test says otherwise, so 1400 / 1410 carry real debit balances.
/// </summary>
public class LoanRecoveryDirectCreditTests
{
    private static readonly Guid Maker = Guid.NewGuid();
    private static readonly Guid Checker = Guid.NewGuid();

    private const string NetPayable = "2100 - Salaries Payable";
    private const string LoanClearing = "2107 - Loan & Advance Deductions Payable";
    private const string LoanRecv = "1400 - Employee Loans Receivable";
    private const string AdvanceRecv = "1410 - Employee Salary Advances";
    private const string CashBank = "1000 - Cash/Bank";

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
            db, new _Scope(), new _Http(http), new _Notifications(),
            new _PackResolver(), _Rules.Rules, new _Letters(), new _Storage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private sealed record Fixture(Guid TenantId, Guid CompanyId, Employee Employee, PayrollRun Run, Guid LoanId, Guid AdvanceId);

    /// <param name="loanOnBooks">What the loan's disbursement put on 1400 (0 = the loan was never booked).</param>
    /// <param name="advanceOnBooks">What the advance's disbursement put on 1410.</param>
    private static async Task<Fixture> Seed(
        ZayraDbContext db, Guid tenantId, decimal loanOnBooks = 9_000m, decimal advanceOnBooks = 2_000m)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Recovery Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            IsActive = true, DefaultCurrency = "SAR",
        };
        db.Companies.Add(company);
        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = "STD", Name = "Base", Currency = "SAR",
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        };
        db.SalaryStructures.Add(structure);
        await db.SaveChangesAsync();

        var emp = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "L001", FullName = "Noura Al-Qahtani",
            Status = "Active", JoiningDate = new DateTime(2022, 3, 1), WorkEmail = "noura@loan.test",
            Nationality = "SAU", ContractType = "Indefinite",
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id,
            BasicSalary = 12_000m, HousingAllowance = 3_000m, TransportAllowance = 800m,
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id, Iban = "SA4420000001234567891234", MolId = "MOL-L001", SalaryCurrency = "SAR",
        });

        var loan = new EmployeeLoan
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
            LoanNumber = "LN-1", Status = "Active", ApprovedAmount = 9_000m, OutstandingBalance = 9_000m,
            InstallmentAmount = 1_500m, TotalRepaid = 0m, ApprovedInstallments = 6,
        };
        var advance = new SalaryAdvance
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
            AdvanceNumber = "ADV-1", Status = "Active", ApprovedAmount = 2_000m, OutstandingBalance = 2_000m,
            RepaymentType = "Installments", Installments = 4, InstallmentAmount = 500m, TotalRepaid = 0m,
        };
        db.EmployeeLoans.Add(loan);
        db.SalaryAdvances.Add(advance);
        await db.SaveChangesAsync();

        // The loan's own disbursement journal (Loan / Disbursement, keyed on the loan): a loan relieves 1400 only up to what ITS journal put there.
        if (loanOnBooks > 0m) db.FinanceGlEntries.AddRange(Opening(tenantId, company.Id, LoanRecv, loanOnBooks, "Loan disbursement LN-1", loan.Id));
        if (advanceOnBooks > 0m) db.FinanceGlEntries.AddRange(Opening(tenantId, company.Id, AdvanceRecv, advanceOnBooks, "Advance disbursement ADV-1"));

        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 6, Status = "Draft", CreatedByUserId = Maker,
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Fixture(tenantId, company.Id, emp, run, loan.Id, advance.Id);
    }

    private static FinanceGlEntry[] Opening(Guid tenantId, Guid companyId, string receivable, decimal amount, string description, Guid? loanId = null)
    {
        var debit = Row(tenantId, companyId, receivable, string.Empty, amount, description);
        if (loanId is Guid id) { debit.SourceModule = "Loan"; debit.SourceEntityId = id; debit.EventType = "Disbursement"; }
        return new[] { debit, Row(tenantId, companyId, string.Empty, CashBank, amount, description) };
    }

    private static FinanceGlEntry Row(Guid tenantId, Guid companyId, string debit, string credit, decimal amount, string description) => new()
    {
        TenantId = tenantId, CompanyId = companyId, SourceModule = "Loans", SourceEntityId = Guid.NewGuid(),
        DebitAccount = debit, CreditAccount = credit, Amount = amount, Currency = "SAR",
        EntryDate = new DateOnly(2026, 5, 20), Period = "2026-05", Description = description, PostedByName = "opening",
    };

    private static async Task ProcessApproveLock(ZayraDbContext db, Guid tenantId, Guid runId)
    {
        (await Payroll(db, tenantId, Maker).Process(runId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tenantId, Maker).GeneratePayslips(runId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        await ReconTestSupport.SignOffAsync(db, runId);
        (await Payroll(db, tenantId, Checker).Approve(runId, new PayrollDecisionRequest("approved", null, null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        (await Payroll(db, tenantId, Checker).Lock(runId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
    }

    private static Task<List<FinanceGlEntry>> Ledger(ZayraDbContext db, Guid tenantId) =>
        db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync();

    private static decimal Balance(IEnumerable<FinanceGlEntry> rows, string account) => GlControlAccounts.Balance(rows, account);

    private static void AssertBalanced(IReadOnlyCollection<FinanceGlEntry> rows, string because)
        => rows.Where(l => !string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount)
            .Should().Be(rows.Where(l => !string.IsNullOrEmpty(l.CreditAccount)).Sum(l => l.Amount), because);

    private static void AssertNoReceivableInCredit(IReadOnlyCollection<FinanceGlEntry> ledger)
        => GlControlAccounts.FindSignViolations(ledger).Should().BeEmpty("no receivable may be driven into credit");

    private static decimal Credited(IEnumerable<FinanceGlEntry> accrual, string account) =>
        accrual.Where(l => l.CreditAccount == account && string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);

    // ═══ T3.1 — a loan the books carry is recovered straight off the receivable ═══════════════════════

    [Fact]
    public async Task Lock_CreditsTheLoanAndAdvanceReceivablesDirectly_AndNothingGoesToTheClearingLiability()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);

        await ProcessApproveLock(db, tid, f.Run.Id);

        var rows = await Ledger(db, tid);
        var accrual = rows.Where(x => x.SourceEntityId == f.Run.Id && x.EventType == GlEventTypes.Accrual).ToList();
        Credited(accrual, LoanRecv).Should().Be(1_500m, "the loan instalment relieves the loan receivable at Lock");
        Credited(accrual, AdvanceRecv).Should().Be(500m, "the advance instalment relieves the advance receivable at Lock");
        accrual.Where(l => l.CreditAccount == LoanClearing || l.DebitAccount == LoanClearing).Should().BeEmpty(
            "a covered instalment never touches the 2107 clearing liability, so no manual remittance step is needed");
        Balance(rows, LoanClearing).Should().Be(0m);
        Balance(rows, LoanRecv).Should().Be(7_500m, "9,000 disbursed less the 1,500 recovered");
        Balance(rows, AdvanceRecv).Should().Be(1_500m, "2,000 disbursed less the 500 recovered");

        var run = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == f.Run.Id);
        accrual.Where(l => l.CreditAccount == NetPayable).Sum(l => l.Amount).Should().Be(run.TotalNetSalary,
            "moving the credit from 2107 to the receivable never changes what the employee is paid");
        AssertBalanced(accrual, "the accrual journal still balances");
        AssertBalanced(rows, "the ledger still balances");
        AssertNoReceivableInCredit(rows);

        // The sub-ledger moved at Process by the same amounts, so loan balance report and 1400 tie.
        (await db.EmployeeLoans.AsNoTracking().FirstAsync(l => l.Id == f.LoanId)).OutstandingBalance.Should().Be(7_500m);
        (await db.SalaryAdvances.AsNoTracking().FirstAsync(a => a.Id == f.AdvanceId)).OutstandingBalance.Should().Be(1_500m);
    }

    // ═══ T3.2 — only what the receivable carries is credited directly; the excess keeps the old path ═══

    [Fact]
    public async Task Lock_CapsTheDirectCreditAtWhatTheReceivableCarries_AndSendsOnlyTheExcessTo2107()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        // A loan that predates the disbursement journal: 1400 holds only 300 for it, 1410 holds nothing.
        var f = await Seed(db, tid, loanOnBooks: 300m, advanceOnBooks: 0m);

        await ProcessApproveLock(db, tid, f.Run.Id);

        var rows = await Ledger(db, tid);
        var accrual = rows.Where(x => x.SourceEntityId == f.Run.Id && x.EventType == GlEventTypes.Accrual).ToList();
        Credited(accrual, LoanRecv).Should().Be(300m, "1400 carries 300, so only 300 can be relieved");
        Credited(accrual, AdvanceRecv).Should().Be(0m, "an advance that was never booked has nothing to relieve");
        Credited(accrual, LoanClearing).Should().Be(1_200m + 500m,
            "the uncovered 1,200 of the loan instalment and the whole 500 advance instalment keep the clearing path");
        Balance(rows, LoanRecv).Should().Be(0m, "the receivable lands on zero, never below");
        Balance(rows, AdvanceRecv).Should().Be(0m);
        Balance(rows, LoanClearing).Should().Be(-1_700m);
        AssertBalanced(rows, "an excess does not unbalance the journal");
        AssertNoReceivableInCredit(rows);
    }

    [Fact]
    public async Task Remit_StillClearsA2107BalanceThatAReceivableCanNowCover()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid, loanOnBooks: 300m, advanceOnBooks: 0m);
        await ProcessApproveLock(db, tid, f.Run.Id);
        Balance(await Ledger(db, tid), LoanClearing).Should().Be(-1_700m, "legacy 2107 balance is left by the capped Lock");

        // Finance books the missing disbursements; the manual remittance then clears 2107 against them exactly as before.
        // The loan's late booking is its own disbursement journal: a remittance credits 1400 only up to what the loan's journals back.
        db.FinanceGlEntries.AddRange(Opening(tid, f.CompanyId, LoanRecv, 1_200m, "Late disbursement booking LN-1", f.LoanId));
        db.FinanceGlEntries.AddRange(Opening(tid, f.CompanyId, AdvanceRecv, 500m, "Late disbursement booking ADV-1"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await Payroll(db, tid, Checker).RemitStatutory(
            f.Run.Id, new RemitStatutoryRequest(RemitGroups.Loan, "LOANREF", null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var rows = await Ledger(db, tid);
        Balance(rows, LoanClearing).Should().Be(0m, "the remittance still clears the clearing liability");
        Balance(rows, LoanRecv).Should().Be(0m);
        Balance(rows, AdvanceRecv).Should().Be(0m);
        AssertBalanced(rows, "the remittance balances");
    }

    [Fact]
    public async Task Remit_OfARunWhoseInstalmentsWereAllCovered_HasNothingToClear_AndPostsNothing()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        await ProcessApproveLock(db, tid, f.Run.Id);

        var before = (await Ledger(db, tid)).Count;
        var result = await Payroll(db, tid, Checker).RemitStatutory(
            f.Run.Id, new RemitStatutoryRequest(RemitGroups.Loan, "LOANREF", null), CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>("there is no 2107 line left, so the loan group reports nothing_remitted");
        db.ChangeTracker.Clear();
        (await Ledger(db, tid)).Should().HaveCount(before, "a remittance with nothing to clear must not post a journal");
    }

    // ═══ T3.3 — a void mirrors the direct credit ═════════════════════════════════════════════════════

    [Fact]
    public async Task Void_ReversesTheDirectReceivableCredit_AndRestoresTheSubLedger()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        await ProcessApproveLock(db, tid, f.Run.Id);
        Balance(await Ledger(db, tid), LoanRecv).Should().Be(7_500m);

        (await Payroll(db, tid, Checker).VoidRun(f.Run.Id, new PayrollDecisionRequest("wrong month"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var rows = await Ledger(db, tid);
        Balance(rows, LoanRecv).Should().Be(9_000m, "the void puts the instalment back on the loan receivable");
        Balance(rows, AdvanceRecv).Should().Be(2_000m);
        Balance(rows, LoanClearing).Should().Be(0m);
        Balance(rows, NetPayable).Should().Be(0m);
        AssertBalanced(rows, "the unwind balances");

        var loan = await db.EmployeeLoans.AsNoTracking().FirstAsync(l => l.Id == f.LoanId);
        loan.OutstandingBalance.Should().Be(9_000m, "the sub-ledger ties to 1400 again");
        loan.TotalRepaid.Should().Be(0m);
        (await db.SalaryAdvances.AsNoTracking().FirstAsync(a => a.Id == f.AdvanceId)).OutstandingBalance.Should().Be(2_000m);
        var contraLines = rows.Where(r => r.SourceEntityId == f.Run.Id && r.DebitAccount == LoanRecv).ToList();
        contraLines.Should().ContainSingle().Which.ReversalOfEntryId.Should().NotBeNull("the contra links to the line it reverses");
    }

    // ═══ N1 — a void after a cash settlement must not forgive or double-collect ═══════════════════════

    [Fact]
    public async Task Void_AfterACashSettlementOfTheRemainder_RestoresOnlyWhatTheRunTook()
    {
        var (db, conn) = NewDb();
        await using var _ = conn; await using var __ = db;
        var tid = Guid.NewGuid();
        var f = await Seed(db, tid);
        await ProcessApproveLock(db, tid, f.Run.Id);

        // Finance settles the remaining 7,500 by cash after the run took its 1,500 instalment.
        var loan = await db.EmployeeLoans.FirstAsync(l => l.Id == f.LoanId);
        loan.OutstandingBalance = 0m; loan.TotalRepaid = 9_000m; loan.Status = "Settled";
        db.LoanRepayments.Add(new LoanRepayment
        {
            TenantId = tid, CompanyId = f.CompanyId, LoanId = f.LoanId, Amount = 7_500m, PaidDate = new DateOnly(2026, 6, 20),
            Reference = "EARLY", PaymentMethod = "BankTransfer", CreatedAtUtc = DateTime.UtcNow.AddMinutes(1),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await Payroll(db, tid, Checker).VoidRun(f.Run.Id, new PayrollDecisionRequest("wrong month"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();

        var after = await db.EmployeeLoans.AsNoTracking().FirstAsync(l => l.Id == f.LoanId);
        after.OutstandingBalance.Should().Be(1_500m, "the voided month's instalment is owed again; the cash settlement stands");
        after.TotalRepaid.Should().Be(7_500m, "only the cash receipt counts as repaid now");
        after.Status.Should().Be("Active");
    }
}

// ── Test doubles (file-scoped) ──────────────────────────────────────────────────

file static class _Rules
{
    internal static readonly StubRuleReader Rules = new StubRuleReader()
        .Set("gosi.saudi_employee_rate", 0.09m)
        .Set("gosi.saudi_employer_rate", 0.09m)
        .Set("gosi.saned_rate", 0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar", 45_000m)
        .Set("ot.standard_multiplier", 1.5m)
        .Set("ot.standard_monthly_hours", 240m)
        .Set("lop.monthly_day_divisor", 30m)
        .Set("lop.standard_work_minutes_per_day", 480m);
}

file sealed class _Scope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope
        {
            Level = Zayra.Api.Application.Common.DataScopeLevel.Organization,
            AllowedEmployeeIds = null,
        });
}

file sealed class _Http : IHttpContextAccessor
{
    public _Http(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _Notifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct)
        => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct)
        => Task.CompletedTask;
}

file sealed class _PackResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_Rules.Rules);
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc is "SAU" or "SA" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new KsaWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _Letters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _Storage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
