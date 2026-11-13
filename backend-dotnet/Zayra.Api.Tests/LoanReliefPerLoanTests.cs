using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Loan relief is capped at the LOAN, not at the account. The 1400 receivable is one account shared by every loan, so an
/// account-level cap lets a loan that was never booked (no disbursement journal) take its instalment out of ANOTHER loan's debit.
/// When that other loan is later settled in cash the account lands in credit.
///
/// Fixture: loan Y was booked (Dr 1400 2,000); legacy loan Z has no disbursement journal. Both have a 500 instalment in the run.
/// </summary>
public class LoanReliefPerLoanTests
{
    private const string LoanRecv = "1400 - Employee Loans Receivable";
    private const string LoanClearing = "2107 - Loan & Advance Deductions Payable";
    private const string CashBank = "1000 - Cash/Bank";

    private static decimal Credited(IEnumerable<FinanceGlEntry> rows, string account) =>
        rows.Where(l => l.CreditAccount == account && string.IsNullOrEmpty(l.DebitAccount)).Sum(l => l.Amount);

    private static EmployeeLoan NewLoan(GlPostingKit k, int employee, string number) => new()
    {
        TenantId = k.TenantId, CompanyId = k.CompanyId, EmployeeId = Guid.NewGuid(), EmployeeIntId = employee, LoanNumber = number,
        Status = "Active", ApprovedAmount = 2_000m, OutstandingBalance = 2_000m, InstallmentAmount = 500m, ApprovedInstallments = 4,
        Currency = "SAR", RepaymentMethod = "PayrollDeduction",
    };

    private static FinanceGlEntry Disbursement(GlPostingKit k, EmployeeLoan loan) => new()
    {
        TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = "Loan", SourceEntityId = loan.Id, SourceEntityRef = loan.LoanNumber,
        EventType = "Disbursement", DebitAccount = LoanRecv, CreditAccount = CashBank, Amount = loan.ApprovedAmount, Currency = "SAR",
        EntryDate = new DateOnly(2026, 8, 1), Period = "2026-08", Description = $"Loan Disbursement: {loan.LoanNumber}",
    };

    /// <summary>The loan's journal-backed 2,000 is on 1400; a second loan has none. A run takes 500 from each.</summary>
    private static async Task<(PayrollRun Run, EmployeeLoan Booked, EmployeeLoan Legacy)> SeedRunAsync(GlPostingKit k, bool bookedLegacy = false)
    {
        var y = NewLoan(k, 1, "LN-Y");
        var z = NewLoan(k, 2, "LN-Z");
        k.Db.EmployeeLoans.AddRange(y, z);
        k.Db.FinanceGlEntries.Add(Disbursement(k, y));
        if (bookedLegacy) k.Db.FinanceGlEntries.Add(Disbursement(k, z));
        await k.Db.SaveChangesAsync();

        var run = await k.SeedApprovedRunAsync(
            new[] { (1, "BASIC", "Salary", 5_000m), (2, "BASIC", "Salary", 5_000m) },
            new[] { (1, "LOAN_EMI", "Loan", 500m), (2, "LOAN_EMI", "Loan", 500m) },
            net: 9_000m);
        foreach (var (loan, emp) in new[] { (y, 1), (z, 2) })
            k.Db.PayrollRunConsumptions.Add(new PayrollRunConsumption
            {
                TenantId = k.TenantId, CompanyId = k.CompanyId, PayrollRunId = run.Id, ArtifactType = PayrollConsumptionArtifacts.Loan,
                ArtifactId = loan.Id, EmployeeId = emp, Amount = 500m, PriorStatus = "Active", PriorOutstandingBalance = 2_000m, PriorTotalRepaid = 0m,
            });
        await k.Db.SaveChangesAsync();
        return (run, y, z);
    }

    [Fact]
    public async Task Lock_RelievesOnlyTheLoansThatWereBooked_AndSendsTheOthersInstalmentTo2107()
    {
        await using var k = await GlPostingKit.CreateAsync();
        var (run, _, _) = await SeedRunAsync(k);

        (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<OkObjectResult>();

        var gl = await k.RunGlAsync(run.Id);
        Credited(gl, LoanRecv).Should().Be(500m, "only loan Y has a disbursement journal on 1400 to relieve");
        Credited(gl, LoanClearing).Should().Be(500m, "loan Z was never booked, so its instalment waits on the clearing liability");
        GlPostingKit.Debits(gl).Should().Be(GlPostingKit.Credits(gl));
    }

    [Fact]
    public async Task Lock_WhenBothLoansAreBooked_RelievesBoth_AndNothingGoesTo2107()
    {
        await using var k = await GlPostingKit.CreateAsync();
        var (run, _, _) = await SeedRunAsync(k, bookedLegacy: true);

        (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<OkObjectResult>();

        var gl = await k.RunGlAsync(run.Id);
        Credited(gl, LoanRecv).Should().Be(1_000m);
        Credited(gl, LoanClearing).Should().Be(0m);
    }

    [Fact]
    public async Task Lock_NeverRelievesMoreThanTheLoansOwnBalance()
    {
        await using var k = await GlPostingKit.CreateAsync();
        var (run, y, _) = await SeedRunAsync(k);
        // Loan Y had only 300 left before this run, but the run took 500 from it (legacy data): the account holds Y's 2,000, yet 300 is all that is Y's.
        var witness = await k.Db.PayrollRunConsumptions.SingleAsync(c => c.ArtifactId == y.Id);
        witness.PriorOutstandingBalance = 300m;
        await k.Db.SaveChangesAsync();

        (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<OkObjectResult>();

        var gl = await k.RunGlAsync(run.Id);
        Credited(gl, LoanRecv).Should().Be(300m);
        Credited(gl, LoanClearing).Should().Be(700m);
    }

    // ── loan remittance (clearing 2107) ────────────────────────────────────────────────────────────

    private static Task<IActionResult> RemitLoansAsync(GlPostingKit k, Guid runId)
    {
        k.Db.ChangeTracker.Clear();
        return k.Payroll(null, null, null, "payroll.export").RemitStatutory(runId, new RemitStatutoryRequest(RemitGroups.Loan, "LOANREF", null), default);
    }

    private static async Task<List<FinanceGlEntry>> LedgerAsync(GlPostingKit k) =>
        await k.Db.FinanceGlEntries.AsNoTracking().Where(x => x.TenantId == k.TenantId).ToListAsync();

    [Fact]
    public async Task Remit_OfAnUnbookedLoansInstalment_IsRefused_AndNeverCreditsTheReceivableWithAnotherLoansDebit()
    {
        await using var k = await GlPostingKit.CreateAsync();
        var (run, _, _) = await SeedRunAsync(k);
        (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<OkObjectResult>();
        var before = await LedgerAsync(k);
        GlControlAccounts.Balance(before, LoanRecv).Should().Be(1_500m, "loan Y's 2,000 less the 500 Lock relieved; loan Z never put anything there");

        var result = await RemitLoansAsync(k, run.Id);

        var refused = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        var body = System.Text.Json.JsonSerializer.Serialize(refused.Value);
        body.Should().Contain("loan_receivable_shortfall").And.Contain("\"shortfall\":500");
        var after = await LedgerAsync(k);
        after.Should().HaveCount(before.Count, "nothing is posted when part of the loan group has no journal-backed receivable");
        GlControlAccounts.Balance(after, LoanRecv).Should().Be(1_500m, "loan Y's debit is not spent on loan Z");
        GlControlAccounts.Balance(after, LoanClearing).Should().Be(-500m);
        GlPostingKit.Debits(after).Should().Be(GlPostingKit.Credits(after));
    }

    [Fact]
    public async Task Remit_AfterTheUnbookedLoanIsBooked_ClearsItsInstalmentAgainstItsOwnJournal()
    {
        await using var k = await GlPostingKit.CreateAsync();
        var (run, _, z) = await SeedRunAsync(k);
        (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<OkObjectResult>();
        k.Db.FinanceGlEntries.Add(Disbursement(k, z));
        await k.Db.SaveChangesAsync();

        (await RemitLoansAsync(k, run.Id)).Should().BeOfType<OkObjectResult>();

        var ledger = await LedgerAsync(k);
        var remit = ledger.Where(x => x.SourceEntityId == run.Id && x.EventType == GlEventTypes.Remit(RemitGroups.Loan)).ToList();
        Credited(remit, LoanRecv).Should().Be(500m);
        remit.Where(l => l.DebitAccount == LoanClearing).Sum(l => l.Amount).Should().Be(500m);
        GlControlAccounts.Balance(ledger, LoanClearing).Should().Be(0m);
        GlControlAccounts.Balance(ledger, LoanRecv).Should().Be(3_000m, "4,000 booked less 500 at Lock and 500 at remittance");
        GlPostingKit.Debits(remit).Should().Be(GlPostingKit.Credits(remit));
    }

    [Fact]
    public async Task Remit_OfBookedLoansThatLockCouldNotRelieve_RelievesTheReceivableExactlyAsBefore()
    {
        await using var k = await GlPostingKit.CreateAsync();
        // Neither loan is on the books at Lock, so both instalments wait on 2107.
        var y = NewLoan(k, 1, "LN-Y");
        var z = NewLoan(k, 2, "LN-Z");
        k.Db.EmployeeLoans.AddRange(y, z);
        await k.Db.SaveChangesAsync();
        var run = await k.SeedApprovedRunAsync(
            new[] { (1, "BASIC", "Salary", 5_000m), (2, "BASIC", "Salary", 5_000m) },
            new[] { (1, "LOAN_EMI", "Loan", 500m), (2, "LOAN_EMI", "Loan", 500m) },
            net: 9_000m);
        foreach (var (loan, emp) in new[] { (y, 1), (z, 2) })
            k.Db.PayrollRunConsumptions.Add(new PayrollRunConsumption
            {
                TenantId = k.TenantId, CompanyId = k.CompanyId, PayrollRunId = run.Id, ArtifactType = PayrollConsumptionArtifacts.Loan,
                ArtifactId = loan.Id, EmployeeId = emp, Amount = 500m, PriorStatus = "Active", PriorOutstandingBalance = 2_000m, PriorTotalRepaid = 0m,
            });
        await k.Db.SaveChangesAsync();
        (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<OkObjectResult>();
        Credited(await k.RunGlAsync(run.Id), LoanClearing).Should().Be(1_000m);
        // Finance then books both disbursements.
        k.Db.FinanceGlEntries.AddRange(Disbursement(k, y), Disbursement(k, z));
        await k.Db.SaveChangesAsync();

        (await RemitLoansAsync(k, run.Id)).Should().BeOfType<OkObjectResult>();

        var ledger = await LedgerAsync(k);
        var remit = ledger.Where(x => x.SourceEntityId == run.Id && x.EventType == GlEventTypes.Remit(RemitGroups.Loan)).ToList();
        Credited(remit, LoanRecv).Should().Be(1_000m);
        Credited(remit, CashBank).Should().Be(0m);
        GlControlAccounts.Balance(ledger, LoanClearing).Should().Be(0m);
        GlControlAccounts.Balance(ledger, LoanRecv).Should().Be(3_000m);
        GlControlAccounts.FindSignViolations(ledger).Should().BeEmpty();
        GlPostingKit.Debits(ledger).Should().Be(GlPostingKit.Credits(ledger));
    }

    // ── cash settlement ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordRepayment_IsRefused_WhenTheReceivableAccountDoesNotCarryTheAmount()
    {
        await using var h = await Harness.Create();
        await h.Pay();
        // Something else (a legacy loan's instalment) has already relieved 60 of the 100 this loan put on 1400.
        h.Db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = h.Tid, CompanyId = h.Loan.CompanyId, SourceModule = "Payroll", SourceEntityId = Guid.NewGuid(), EventType = "Accrual",
            DebitAccount = string.Empty, CreditAccount = LoanRecv, Amount = 60m, Currency = "SAR",
            EntryDate = h.Today, Period = h.Today.ToString("yyyy-MM"), Description = "Payroll loan recovery: LOAN_EMI",
        });
        await h.Db.SaveChangesAsync();

        var result = await h.Controller().RecordRepayment(h.Loan.Id, new RecordLoanRepaymentRequest(100m, h.Today, "SETTLE-1"), default);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value.Should().BeOfType<string>().Which.Should().Contain("reconcile");
        (await h.Db.LoanRepayments.CountAsync()).Should().Be(0);
        (await h.Db.EmployeeLoans.AsNoTracking().SingleAsync()).OutstandingBalance.Should().Be(100m);
    }

    [Fact]
    public async Task RecordRepayment_StillSettlesALoanWhoseReceivableIsFullyCarried()
    {
        await using var h = await Harness.Create();
        await h.Pay();

        var result = await h.Controller().RecordRepayment(h.Loan.Id, new RecordLoanRepaymentRequest(100m, h.Today, "SETTLE-2"), default);

        result.Should().BeOfType<OkObjectResult>();
        var ledger = await h.Db.FinanceGlEntries.AsNoTracking().ToListAsync();
        ledger.Where(x => x.DebitAccount == LoanRecv).Sum(x => x.Amount).Should().Be(100m);
        ledger.Where(x => x.CreditAccount == LoanRecv).Sum(x => x.Amount).Should().Be(100m, "the account lands on zero, never below");
    }

    private sealed class Harness : IAsyncDisposable
    {
        public ZayraDbContext Db { get; } = new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Tid { get; } = Guid.NewGuid();
        public EmployeeLoan Loan { get; private set; } = null!;
        public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

        public static async Task<Harness> Create()
        {
            var h = new Harness();
            var company = new Company { TenantId = h.Tid, LegalNameEn = "Lender", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
            var type = new LoanType { TenantId = h.Tid, Code = "PERSONAL", NameEn = "Personal", IsActive = true, MaxAmount = 10000m, MaxInstallments = 24 };
            var employee = new Employee
            {
                TenantId = h.Tid, CompanyId = company.Id, UserAccountId = Guid.NewGuid(), EmployeeCode = "BORROWER", FullName = "Borrower",
                Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3), BankName = "Bank", BankIban = "SA4420000001234567891234",
            };
            h.Db.AddRange(company, type, employee);
            await h.Db.SaveChangesAsync();
            h.Loan = new EmployeeLoan
            {
                TenantId = h.Tid, CompanyId = company.Id, EmployeeId = employee.PublicId, EmployeeIntId = employee.Id,
                EmployeeName = employee.FullName, LoanTypeId = type.Id, LoanTypeName = type.NameEn, LoanNumber = "LN-RELIEF",
                RequestedAmount = 100m, ApprovedAmount = 100m, RequestedInstallments = 2, ApprovedInstallments = 2,
                InstallmentAmount = 50m, Status = "Approved", RepaymentMethod = "BankTransfer", Currency = "SAR", CreatedBy = Guid.NewGuid(),
            };
            h.Db.Add(h.Loan);
            await h.Db.SaveChangesAsync();
            Assert.IsType<OkObjectResult>(await h.Controller().CreatePaymentBatch(new CreateLoanPaymentBatchRequest([h.Loan.Id]), default));
            return h;
        }

        public async Task Pay()
        {
            var batch = await Db.LoanDisbursementBatches.SingleAsync();
            Assert.IsType<OkObjectResult>(await Controller().ApprovePaymentBatch(batch.Id, default));
            Assert.IsType<OkObjectResult>(await Controller().ConfirmPaymentBatchPaid(batch.Id, new ConfirmLoanPaymentRequest("PAYOUT", Today), default));
        }

        public LoansController Controller() => new(Db, new Scope())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim("tenant_id", Tid.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim("permission", "loans.read"), new Claim(ClaimTypes.Name, "Finance"), new Claim(ClaimTypes.Role, "Finance")], "test")),
                },
            },
        };

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class Scope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
