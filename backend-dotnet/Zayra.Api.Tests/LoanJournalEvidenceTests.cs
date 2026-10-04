using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class LoanJournalEvidenceTests
{
    [Fact]
    public async Task LoanDetailAndAudit_IncludeReceiptCorrelatedJournals()
    {
        await using var h = await Harness.Create();
        await h.Pay();
        await h.Receive("VISIBLE-RECEIPT");
        var detail = Assert.IsType<OkObjectResult>(await h.Controller().GetLoan(h.Loan.Id, default));
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(detail.Value));
        Assert.Equal(2, json.RootElement.GetProperty("glEntries").GetArrayLength());
        var audit = Assert.IsType<OkObjectResult>(await h.Controller().AuditReport(null, null, default));
        using var report = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(audit.Value));
        Assert.Equal(2, report.RootElement.GetProperty("GlEntriesCount").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemappingAccounts_PreservesOriginalReceivable_AndExactlyReversesEachReceipt(bool perLine)
    {
        await using var h = await Harness.Create();
        await h.Map("LOAN_RECEIVABLE", "1401", "Original receivable");
        await h.Map("CASH_BANK", "1001", "Original bank");
        await h.Pay(perLine);
        var disbursement = await h.Db.FinanceGlEntries.SingleAsync();
        Assert.Equal(disbursement.Id, h.Line.GlEntryId);
        await h.Map("LOAN_RECEIVABLE", "1499", "Replacement receivable");
        await h.Map("CASH_BANK", "1099", "Second bank");
        var first = await h.Receive("FIRST");
        var firstGl = await h.Db.FinanceGlEntries.SingleAsync(x => x.Id == first.GlEntryId);
        Assert.Equal(disbursement.DebitAccount, firstGl.CreditAccount);
        Assert.Equal("1099 - Second bank", firstGl.DebitAccount);
        await h.Map("CASH_BANK", "1098", "Third bank");
        var second = await h.Receive("SECOND");
        var secondGl = await h.Db.FinanceGlEntries.SingleAsync(x => x.Id == second.GlEntryId);
        Assert.NotEqual(firstGl.Id, secondGl.Id);
        Assert.Equal(first.Id, firstGl.SourceEntityId);
        Assert.Equal(second.Id, secondGl.SourceEntityId);
        await h.Map("CASH_BANK", "1097", "Fourth bank");
        await h.Correct("ReceiptReversal", first.Id);
        var reversal = await h.Db.FinanceGlEntries.SingleAsync(x => x.Id == first.ReversalGlEntryId);
        AssertInverse(firstGl, reversal);
        Assert.False(second.IsReversed);
        Assert.Equal(80m, h.Loan.OutstandingBalance);
        await h.Correct("ReceiptReversal", second.Id);
        await h.Correct("DisbursementReversal");
        AssertInverse(disbursement, await h.Db.FinanceGlEntries.SingleAsync(x => x.EventType == "DisbursementReversal"));
        Assert.Equal(0m, h.Loan.OutstandingBalance);
        var journals = await h.Db.FinanceGlEntries.ToListAsync();
        var balances = journals.SelectMany(x => new[] { (Account: x.DebitAccount, Amount: x.Amount), (Account: x.CreditAccount, Amount: -x.Amount) })
            .GroupBy(x => x.Account).Select(x => x.Sum(y => y.Amount));
        Assert.All(balances, balance => Assert.Equal(0m, balance));
    }

    [Fact]
    public async Task LegacyReceiptWithoutJournalLink_FailsClosedEvenWhenAmountsAndDatesMatch()
    {
        await using var h = await Harness.Create();
        await h.Pay();
        var first = await h.Receive("FIRST");
        var second = await h.Receive("SECOND");
        foreach (var receipt in new[] { first, second })
        {
            var journal = await h.Db.FinanceGlEntries.SingleAsync(x => x.Id == receipt.GlEntryId);
            journal.SourceEntityId = h.Loan.Id;
            receipt.GlEntryId = null;
        }
        await h.Db.SaveChangesAsync();
        var result = Assert.IsType<ConflictObjectResult>(await h.Controller().RequestLoanCorrection(h.Loan.Id,
            new LoanCorrectionRequest("ReceiptReversal", h.Today, "LEGACY", "Bank return", first.Id), default));
        Assert.Contains("reconcil", Assert.IsType<string>(result.Value), StringComparison.OrdinalIgnoreCase);
        Assert.False(first.IsReversed);
        Assert.Equal(60m, h.Loan.OutstandingBalance);
        Assert.Empty(await h.Db.LoanChangeRequests.ToListAsync());
        Assert.Empty(await h.Db.FinanceGlEntries.Where(x => x.ReversalOfEntryId != null).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrAmbiguousOriginalDisbursement_RefusesRecoveryWithoutMutations(bool ambiguous)
    {
        await using var h = await Harness.Create();
        await h.Pay();
        var original = await h.Db.FinanceGlEntries.SingleAsync();
        h.Line.GlEntryId = null;
        if (ambiguous)
            h.Db.FinanceGlEntries.Add(new FinanceGlEntry { TenantId = h.Tid, CompanyId = h.Loan.CompanyId,
                SourceModule = "Loan", SourceEntityId = h.Loan.Id, EventType = "Disbursement",
                Amount = original.Amount, Currency = original.Currency, DebitAccount = original.DebitAccount,
                CreditAccount = original.CreditAccount, EntryDate = original.EntryDate, Period = original.Period });
        else h.Db.FinanceGlEntries.Remove(original);
        await h.Db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await h.Controller().RecordRepayment(h.Loan.Id,
            new RecordLoanRepaymentRequest(20m, h.Today, "NO-EVIDENCE"), default));
        Assert.Equal(100m, h.Loan.OutstandingBalance);
        Assert.Empty(await h.Db.LoanRepayments.ToListAsync());
        Assert.All(await h.Db.LoanInstallments.ToListAsync(), x => Assert.Equal(0m, x.AmountPaid));
    }

    [Fact]
    public async Task LinkedJournalFromAnotherLoan_CannotAuthorizeReceiptReversal()
    {
        await using var h = await Harness.Create();
        await h.Pay();
        var receipt = await h.Receive("RECEIPT");
        var journal = await h.Db.FinanceGlEntries.SingleAsync(x => x.Id == receipt.GlEntryId);
        journal.SourceEntityId = Guid.NewGuid();
        await h.Db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await h.Controller().RequestLoanCorrection(h.Loan.Id,
            new LoanCorrectionRequest("ReceiptReversal", h.Today, "WRONG-LINK", "Bank return", receipt.Id), default));
        Assert.False(receipt.IsReversed);
    }

    [Theory]
    [InlineData("Amount")]
    [InlineData("Currency")]
    [InlineData("Company")]
    public async Task CorrectionApproval_RevalidatesOriginalJournalEvidence(string field)
    {
        await using var h = await Harness.Create();
        await h.Pay();
        var receipt = await h.Receive("RECEIPT");
        Assert.IsType<OkObjectResult>(await h.Controller().RequestLoanCorrection(h.Loan.Id,
            new LoanCorrectionRequest("ReceiptReversal", h.Today, "RETURN", "Bank return", receipt.Id), default));
        var change = await h.Db.LoanChangeRequests.SingleAsync();
        var journal = await h.Db.FinanceGlEntries.SingleAsync(x => x.Id == receipt.GlEntryId);
        if (field == "Amount") journal.Amount += 1m;
        if (field == "Currency") journal.Currency = "USD";
        if (field == "Company") journal.CompanyId = Guid.NewGuid();
        await h.Db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await h.Controller().DecideLoanCorrection(h.Loan.Id,
            change.Id, new LoanCorrectionDecision("Approved", "Review evidence"), default));
        Assert.Equal("Pending", change.Status);
        Assert.False(receipt.IsReversed);
        Assert.Equal(80m, h.Loan.OutstandingBalance);
        Assert.Empty(await h.Db.FinanceGlEntries.Where(x => x.ReversalOfEntryId != null).ToListAsync());
    }

    [Fact]
    public async Task BeneficiaryCannotApproveBatchOrCorrectionCreatedBySomeoneElse()
    {
        await using var h = await Harness.Create();
        Assert.IsType<BadRequestObjectResult>(await h.Controller(h.BorrowerId).ApprovePaymentBatch(h.Batch.Id, default));
        Assert.Equal("Draft", h.Batch.Status);
        await h.Pay();
        Assert.IsType<OkObjectResult>(await h.Controller().RequestLoanCorrection(h.Loan.Id,
            new LoanCorrectionRequest("DisbursementReversal", h.Today, "RECALL", "Bank return"), default));
        var change = await h.Db.LoanChangeRequests.SingleAsync();
        Assert.IsType<BadRequestObjectResult>(await h.Controller(h.BorrowerId).DecideLoanCorrection(h.Loan.Id,
            change.Id, new LoanCorrectionDecision("Approved", "Self approval"), default));
        Assert.Equal("Pending", change.Status);
        Assert.Equal(100m, h.Loan.OutstandingBalance);
    }

    private static void AssertInverse(FinanceGlEntry original, FinanceGlEntry reversal)
    {
        Assert.Equal(original.Id, reversal.ReversalOfEntryId);
        Assert.Equal(original.CreditAccount, reversal.DebitAccount);
        Assert.Equal(original.DebitAccount, reversal.CreditAccount);
        Assert.Equal(original.CompanyId, reversal.CompanyId);
        Assert.Equal(original.Currency, reversal.Currency);
        Assert.Equal(original.Amount, reversal.Amount);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public ZayraDbContext Db { get; } = new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Tid { get; } = Guid.NewGuid();
        public Guid BorrowerId { get; } = Guid.NewGuid();
        public EmployeeLoan Loan { get; private set; } = null!;
        public LoanDisbursementBatch Batch { get; private set; } = null!;
        public LoanDisbursementLine Line { get; private set; } = null!;
        public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
        public static async Task<Harness> Create()
        {
            var h = new Harness();
            var company = new Company { TenantId = h.Tid, LegalNameEn = "Lender", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
            var type = new LoanType { TenantId = h.Tid, Code = "PERSONAL", NameEn = "Personal", IsActive = true, MaxAmount = 10000m, MaxInstallments = 24 };
            var employee = new Employee { TenantId = h.Tid, CompanyId = company.Id, UserAccountId = h.BorrowerId,
                EmployeeCode = "BORROWER", FullName = "Borrower", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3),
                BankName = "Bank", BankIban = "SA4420000001234567891234" };
            h.Db.AddRange(company, type, employee);
            await h.Db.SaveChangesAsync();
            h.Loan = new EmployeeLoan { TenantId = h.Tid, CompanyId = company.Id, EmployeeId = employee.PublicId,
                EmployeeIntId = employee.Id, EmployeeName = employee.FullName, LoanTypeId = type.Id, LoanTypeName = type.NameEn,
                LoanNumber = "LN-JOURNAL", RequestedAmount = 100m, ApprovedAmount = 100m, RequestedInstallments = 2,
                ApprovedInstallments = 2, InstallmentAmount = 50m, Status = "Approved", RepaymentMethod = "BankTransfer", Currency = "SAR", CreatedBy = Guid.NewGuid() };
            h.Db.Add(h.Loan);
            await h.Db.SaveChangesAsync();
            Assert.IsType<OkObjectResult>(await h.Controller().CreatePaymentBatch(new CreateLoanPaymentBatchRequest([h.Loan.Id]), default));
            h.Batch = await h.Db.LoanDisbursementBatches.SingleAsync();
            h.Line = await h.Db.LoanDisbursementLines.SingleAsync();
            return h;
        }
        public async Task Pay(bool perLine = false)
        {
            Assert.IsType<OkObjectResult>(await Controller().ApprovePaymentBatch(Batch.Id, default));
            var result = perLine
                ? await Controller().RecordPaymentLineOutcome(Batch.Id, Line.Id, new LoanPaymentOutcomeRequest("Paid", "PAYOUT", Today), default)
                : await Controller().ConfirmPaymentBatchPaid(Batch.Id, new ConfirmLoanPaymentRequest("PAYOUT", Today), default);
            Assert.IsType<OkObjectResult>(result);
        }
        public async Task<LoanRepayment> Receive(string reference)
        {
            Assert.IsType<OkObjectResult>(await Controller().RecordRepayment(Loan.Id, new RecordLoanRepaymentRequest(20m, Today, reference), default));
            return await Db.LoanRepayments.SingleAsync(x => x.Reference == reference);
        }
        public async Task Correct(string kind, Guid? receipt = null)
        {
            var reference = Guid.NewGuid().ToString();
            Assert.IsType<OkObjectResult>(await Controller().RequestLoanCorrection(Loan.Id, new LoanCorrectionRequest(kind, Today, reference, "Bank evidence", receipt), default));
            var change = await Db.LoanChangeRequests.SingleAsync(x => x.Reference == reference);
            Assert.IsType<OkObjectResult>(await Controller().DecideLoanCorrection(Loan.Id, change.Id, new LoanCorrectionDecision("Approved", "Independent review"), default));
        }
        public async Task Map(string driver, string code, string name)
        {
            var account = new GlAccount { TenantId = Tid, CompanyId = Loan.CompanyId, Code = code, Name = name, AccountType = "Asset" };
            Db.Add(account);
            var mapping = await Db.GlAccountMappings.SingleOrDefaultAsync(x => x.DriverKey == driver);
            if (mapping == null) Db.Add(new GlAccountMapping { TenantId = Tid, CompanyId = Loan.CompanyId, DriverKey = driver, AccountId = account.Id });
            else mapping.AccountId = account.Id;
            await Db.SaveChangesAsync();
        }
        public LoansController Controller(Guid? userId = null) => new(Db, new Scope())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("tenant_id", Tid.ToString()), new Claim(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
                new Claim("permission", "loans.read"),
                new Claim(ClaimTypes.Name, "Finance"), new Claim(ClaimTypes.Role, "Finance")], "test")) } }
        };
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
    private sealed class Scope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
