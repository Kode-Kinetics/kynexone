using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class LoanFinancialCorrectionTests
{
    [Fact]
    public async Task PartialBatch_FailedLineCanRetry_WithoutRepostingPaidLoan_AndEvidenceIsIdempotent()
    {
        await using var h = await Harness.Create(2);
        Assert.IsType<OkObjectResult>(await h.Pay(0, "BANK-FIRST"));
        Assert.IsType<OkObjectResult>(await h.Controller().RecordPaymentLineOutcome(h.Batch.Id, h.Lines[1].Id,
            new LoanPaymentOutcomeRequest("Failed", Reason: "Bank rejected beneficiary"), default));
        Assert.Equal("PartiallyPaid", h.Batch.Status);
        Assert.Equal("Failed", h.Lines[1].Status);
        Assert.Equal("Approved", h.Loans[1].Status);
        Assert.Single(await h.Db.FinanceGlEntries.ToListAsync());
        Assert.IsType<ConflictObjectResult>(await h.Controller().ConfirmPaymentBatchPaid(h.Batch.Id,
            new ConfirmLoanPaymentRequest("WHOLE-BATCH", h.Today), default));
        Assert.IsType<ConflictObjectResult>(await h.Pay(1, "BANK-FIRST"));
        Assert.IsType<OkObjectResult>(await h.Pay(1, "BANK-RETRY"));
        Assert.Equal("Paid", h.Batch.Status);
        Assert.Equal(2, await h.Db.FinanceGlEntries.CountAsync(x => x.EventType == "Disbursement"));
        Assert.Equal(4, await h.Db.LoanInstallments.CountAsync());
        Assert.IsType<OkObjectResult>(await h.Pay(0, "BANK-FIRST"));
        Assert.IsType<ConflictObjectResult>(await h.Pay(0, "CHANGED-EVIDENCE"));
        Assert.Equal(2, await h.Db.FinanceGlEntries.CountAsync(x => x.EventType == "Disbursement"));
        Assert.All(h.Loans, loan => Assert.Equal(100m, loan.OutstandingBalance));
    }

    [Fact]
    public async Task ReceiptReversal_IsMakerChecked_RetainsReceipt_AndReconcilesScheduleAndContraJournalOnce()
    {
        await using var h = await Harness.Create();
        Assert.IsType<OkObjectResult>(await h.Pay(0, "PAYOUT"));
        var loan = h.Loans[0];
        Assert.IsType<OkObjectResult>(await h.Controller().RecordRepayment(loan.Id,
            new RecordLoanRepaymentRequest(20m, h.Today, "RECEIPT-ORIGINAL"), default));
        var receipt = await h.Db.LoanRepayments.SingleAsync();
        var makerId = Guid.NewGuid();
        var maker = h.Controller(userId: makerId);
        var request = new LoanCorrectionRequest("ReceiptReversal", h.Today, "CORRECT-RECEIPT", "Bank returned the receipt", receipt.Id);
        Assert.IsType<OkObjectResult>(await maker.RequestLoanCorrection(loan.Id, request, default));
        Assert.IsType<OkObjectResult>(await maker.RequestLoanCorrection(loan.Id, request, default));
        Assert.IsType<ConflictObjectResult>(await maker.RequestLoanCorrection(loan.Id, request with { Reason = "Different correction" }, default));
        var correction = await h.Db.LoanChangeRequests.SingleAsync();
        var decision = new LoanCorrectionDecision("Approved", "Independent bank evidence checked");
        Assert.IsType<BadRequestObjectResult>(await maker.DecideLoanCorrection(loan.Id, correction.Id, decision, default));
        Assert.False(receipt.IsReversed);
        Assert.IsType<OkObjectResult>(await h.Controller().DecideLoanCorrection(loan.Id, correction.Id, decision, default));
        Assert.True(receipt.IsReversed);
        Assert.NotNull(receipt.ReversedAtUtc);
        Assert.NotNull(receipt.ReversedBy);
        Assert.Equal("RECEIPT-ORIGINAL", receipt.Reference);
        Assert.Equal(20m, receipt.Amount);
        Assert.Equal(h.Today, receipt.PaidDate);
        Assert.Equal(100m, loan.OutstandingBalance);
        Assert.Equal(0m, loan.TotalRepaid);
        Assert.All(await h.Db.LoanInstallments.ToListAsync(), x => Assert.Equal(0m, x.AmountPaid));
        var repayment = await h.Db.FinanceGlEntries.SingleAsync(x => x.EventType == "Repayment");
        var reversal = await h.Db.FinanceGlEntries.SingleAsync(x => x.EventType == "RepaymentReversal");
        Assert.Equal(repayment.Amount, reversal.Amount);
        Assert.Equal(repayment.DebitAccount, reversal.CreditAccount);
        Assert.Equal(repayment.CreditAccount, reversal.DebitAccount);
        Assert.IsType<OkObjectResult>(await h.Controller().DecideLoanCorrection(loan.Id, correction.Id, decision, default));
        Assert.Single(await h.Db.FinanceGlEntries.Where(x => x.EventType == "RepaymentReversal").ToListAsync());
        Assert.IsType<ConflictObjectResult>(await h.Controller().RecordRepayment(loan.Id,
            new RecordLoanRepaymentRequest(20m, h.Today, "RECEIPT-ORIGINAL"), default));
    }

    [Fact]
    public async Task PayoutReversal_RequiresReceiptsReversed_RetainsSchedulesAndJournals_AndCancelsLoan()
    {
        await using var h = await Harness.Create();
        Assert.IsType<OkObjectResult>(await h.Pay(0, "PAYOUT"));
        var loan = h.Loans[0];
        var scheduleIds = await h.Db.LoanInstallments.Select(x => x.Id).ToListAsync();
        Assert.IsType<OkObjectResult>(await h.Controller().RecordRepayment(loan.Id,
            new RecordLoanRepaymentRequest(10m, h.Today, "RECEIPT"), default));
        Assert.IsType<ConflictObjectResult>(await h.Controller().RequestLoanCorrection(loan.Id,
            new LoanCorrectionRequest("DisbursementReversal", h.Today, "REVERSE-PAYOUT", "Bank payout recalled"), default));
        var receipt = await h.Db.LoanRepayments.SingleAsync();
        await h.Correct(loan, "ReceiptReversal", "REVERSE-RECEIPT", receipt.Id);
        await h.Correct(loan, "DisbursementReversal", "REVERSE-PAYOUT");
        Assert.Equal("Cancelled", loan.Status);
        Assert.Null(loan.DisbursementDate);
        Assert.Equal(0m, loan.OutstandingBalance);
        Assert.Equal(100m, loan.ApprovedAmount);
        Assert.Equal("Reversed", h.Lines[0].Status);
        Assert.True(h.Lines[0].IsCancelled);
        var schedule = await h.Db.LoanInstallments.ToListAsync();
        Assert.Equal(scheduleIds.OrderBy(x => x), schedule.Select(x => x.Id).OrderBy(x => x));
        Assert.All(schedule, x => Assert.Equal("Cancelled", x.Status));
        Assert.Single(await h.Db.FinanceGlEntries.Where(x => x.EventType == "Disbursement").ToListAsync());
        Assert.Equal(100m, (await h.Db.FinanceGlEntries.SingleAsync(x => x.EventType == "DisbursementReversal")).Amount);
        Assert.Single(await h.Db.LoanRepayments.ToListAsync());
        Assert.IsType<ConflictObjectResult>(await h.Pay(0, "NEW-PAYOUT"));
    }

    [Fact]
    public async Task ReversingPaidLineOfPartialBatch_LeavesUnpaidInstructionOperable()
    {
        await using var h = await Harness.Create(2);
        Assert.IsType<OkObjectResult>(await h.Pay(0, "FIRST"));
        await h.Correct(h.Loans[0], "DisbursementReversal", "RECALL-FIRST");
        Assert.Equal("Approved", h.Batch.Status);
        Assert.Equal("Reversed", h.Lines[0].Status);
        Assert.IsType<OkObjectResult>(await h.Pay(1, "SECOND"));
        Assert.Equal("Completed", h.Batch.Status);
        Assert.Equal("Active", h.Loans[1].Status);
        Assert.Equal(100m, h.Loans[1].OutstandingBalance);
        Assert.Equal("Cancelled", h.Loans[0].Status);
        Assert.Equal(2, await h.Db.FinanceGlEntries.CountAsync(x => x.EventType == "Disbursement"));
        Assert.Single(await h.Db.FinanceGlEntries.Where(x => x.EventType == "DisbursementReversal").ToListAsync());
    }

    [Fact]
    public async Task CancellingRemainingInstructions_DoesNotOverwriteReversedInstructionHistory()
    {
        await using var h = await Harness.Create(2);
        Assert.IsType<OkObjectResult>(await h.Pay(0, "FIRST"));
        await h.Correct(h.Loans[0], "DisbursementReversal", "RECALL-FIRST");
        Assert.IsType<OkObjectResult>(await h.Controller().CancelPaymentBatch(h.Batch.Id, default));
        Assert.Equal("Reversed", h.Lines[0].Status);
        Assert.Equal("Cancelled", h.Lines[1].Status);
        Assert.Equal("Approved", h.Loans[1].Status);
        Assert.Empty(await h.Db.FinanceGlEntries.Where(x => x.SourceEntityId == h.Loans[1].Id).ToListAsync());
    }

    [Fact]
    public async Task CorrectionApproval_RefusesChangedBalance_AndClosedPeriod()
    {
        await using var h = await Harness.Create();
        Assert.IsType<OkObjectResult>(await h.Pay(0, "PAYOUT"));
        var loan = h.Loans[0];
        Assert.IsType<OkObjectResult>(await h.Controller().RecordRepayment(loan.Id, new RecordLoanRepaymentRequest(10m, h.Today, "RECEIPT-1"), default));
        var receipt = await h.Db.LoanRepayments.SingleAsync();
        Assert.IsType<OkObjectResult>(await h.Controller().RequestLoanCorrection(loan.Id,
            new LoanCorrectionRequest("ReceiptReversal", h.Today, "STALE-CORRECTION", "Returned first receipt", receipt.Id), default));
        var correction = await h.Db.LoanChangeRequests.SingleAsync();
        Assert.IsType<OkObjectResult>(await h.Controller().RecordRepayment(loan.Id, new RecordLoanRepaymentRequest(5m, h.Today, "RECEIPT-2"), default));
        Assert.IsType<ConflictObjectResult>(await h.Controller().DecideLoanCorrection(loan.Id, correction.Id, new LoanCorrectionDecision("Approved", "Attempt stale approval"), default));
        Assert.Equal("Pending", correction.Status);
        Assert.Equal(85m, loan.OutstandingBalance);
        h.Db.GlPeriodCloses.Add(new GlPeriodClose { TenantId = h.Tid, CompanyId = loan.CompanyId, Period = h.Today.ToString("yyyy-MM"), Status = GlPeriodStatuses.Closed });
        await h.Db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await h.Controller().RequestLoanCorrection(loan.Id,
            new LoanCorrectionRequest("ReceiptReversal", h.Today, "CLOSED-CORRECTION", "Accounting period closed", receipt.Id), default));
        Assert.False(receipt.IsReversed);
        Assert.Empty(await h.Db.FinanceGlEntries.Where(x => x.EventType == "RepaymentReversal").ToListAsync());
    }

    [Fact]
    public async Task OutcomesAndCorrections_EnforceScopeAndFinanceRole_WithoutMovingCash()
    {
        await using var h = await Harness.Create();
        var line = h.Lines[0];
        var payout = new LoanPaymentOutcomeRequest("Paid", "UNAUTHORIZED", h.Today);
        Assert.IsType<ForbidResult>(await h.Controller(role: "HR Manager").RecordPaymentLineOutcome(h.Batch.Id, line.Id, payout, default));
        Assert.IsType<ForbidResult>(await h.Controller(restricted: true).RecordPaymentLineOutcome(h.Batch.Id, line.Id, payout, default));
        Assert.IsType<NotFoundResult>(await h.Controller(tenantId: Guid.NewGuid()).RecordPaymentLineOutcome(h.Batch.Id, line.Id, payout, default));
        Assert.Empty(await h.Db.FinanceGlEntries.ToListAsync());
        Assert.IsType<OkObjectResult>(await h.Pay(0, "VALID"));
        var request = new LoanCorrectionRequest("DisbursementReversal", h.Today, "UNAUTHORIZED-CORRECTION", "Unauthorized bank reversal");
        Assert.IsType<ForbidResult>(await h.Controller(role: "HR Director").RequestLoanCorrection(h.Loans[0].Id, request, default));
        Assert.IsType<ForbidResult>(await h.Controller(restricted: true).RequestLoanCorrection(h.Loans[0].Id, request, default));
        Assert.IsType<NotFoundResult>(await h.Controller(tenantId: Guid.NewGuid()).RequestLoanCorrection(h.Loans[0].Id, request, default));
        Assert.Empty(await h.Db.LoanChangeRequests.ToListAsync());
        Assert.Equal(100m, h.Loans[0].OutstandingBalance);
    }

    [Fact]
    public async Task IndividualPayout_RefusesClosedPeriod_WithoutPartialFinancialWrites()
    {
        await using var h = await Harness.Create();
        h.Db.GlPeriodCloses.Add(new GlPeriodClose { TenantId = h.Tid, CompanyId = h.Loans[0].CompanyId, Period = h.Today.ToString("yyyy-MM"), Status = GlPeriodStatuses.Closed });
        await h.Db.SaveChangesAsync();
        Assert.IsType<UnprocessableEntityObjectResult>(await h.Pay(0, "CLOSED-PAYOUT"));
        Assert.Equal("Approved", h.Loans[0].Status);
        Assert.Equal(0m, h.Loans[0].OutstandingBalance);
        Assert.Empty(await h.Db.LoanInstallments.ToListAsync());
        Assert.Empty(await h.Db.FinanceGlEntries.ToListAsync());
    }

    [Fact]
    public async Task CorrectionApproval_RechecksAccountingPeriodAfterRequest()
    {
        await using var h = await Harness.Create();
        Assert.IsType<OkObjectResult>(await h.Pay(0, "PAYOUT"));
        var loan = h.Loans[0];
        Assert.IsType<OkObjectResult>(await h.Controller().RequestLoanCorrection(loan.Id,
            new LoanCorrectionRequest("DisbursementReversal", h.Today, "PENDING-RECALL", "Bank returned payment"), default));
        var correction = await h.Db.LoanChangeRequests.SingleAsync();
        h.Db.GlPeriodCloses.Add(new GlPeriodClose { TenantId = h.Tid, CompanyId = loan.CompanyId, Period = h.Today.ToString("yyyy-MM"), Status = GlPeriodStatuses.Closed });
        await h.Db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await h.Controller().DecideLoanCorrection(loan.Id, correction.Id,
            new LoanCorrectionDecision("Approved", "Period closed after submission"), default));
        Assert.Equal("Pending", correction.Status);
        Assert.Equal("Active", loan.Status);
        Assert.Equal(100m, loan.OutstandingBalance);
        Assert.Equal("Paid", h.Lines[0].Status);
        Assert.Single(await h.Db.FinanceGlEntries.ToListAsync());
    }

    private sealed class Harness : IAsyncDisposable
    {
        public ZayraDbContext Db { get; } = new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Tid { get; } = Guid.NewGuid();
        public List<EmployeeLoan> Loans { get; } = [];
        public LoanDisbursementBatch Batch { get; private set; } = null!;
        public List<LoanDisbursementLine> Lines { get; private set; } = [];
        public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
        public static async Task<Harness> Create(int count = 1)
        {
            var h = new Harness();
            var company = new Company { TenantId = h.Tid, LegalNameEn = "Correction Lender", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
            var type = new LoanType { TenantId = h.Tid, Code = "PERSONAL", NameEn = "Personal", IsActive = true, MaxAmount = 10_000m, MaxInstallments = 24 };
            h.Db.AddRange(company, type);
            for (var i = 0; i < count; i++)
            {
                var employee = new Employee
                {
                    TenantId = h.Tid, CompanyId = company.Id, EmployeeCode = $"CORRECTION-{i}", FullName = $"Borrower {i}",
                    Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3), BankName = "Test Bank", BankIban = "SA4420000001234567891234"
                };
                h.Db.Employees.Add(employee);
                await h.Db.SaveChangesAsync();
                var loan = new EmployeeLoan
                {
                    TenantId = h.Tid, CompanyId = company.Id, EmployeeId = employee.PublicId, EmployeeIntId = employee.Id,
                    EmployeeName = employee.FullName, LoanTypeId = type.Id, LoanTypeName = type.NameEn, LoanNumber = $"LN-CORRECTION-{i}",
                    RequestedAmount = 100m, ApprovedAmount = 100m, RequestedInstallments = 2, ApprovedInstallments = 2,
                    InstallmentAmount = 50m, Status = "Approved", RepaymentMethod = "BankTransfer", Currency = "SAR", CreatedBy = Guid.NewGuid()
                };
                h.Db.EmployeeLoans.Add(loan); h.Loans.Add(loan);
            }
            await h.Db.SaveChangesAsync();
            Assert.IsType<OkObjectResult>(await h.Controller().CreatePaymentBatch(new CreateLoanPaymentBatchRequest(h.Loans.Select(x => x.Id).ToArray()), default));
            h.Batch = await h.Db.LoanDisbursementBatches.SingleAsync();
            h.Lines = (await h.Db.LoanDisbursementLines.ToListAsync()).OrderBy(x => h.Loans.FindIndex(loan => loan.Id == x.LoanId)).ToList();
            Assert.IsType<OkObjectResult>(await h.Controller().ApprovePaymentBatch(h.Batch.Id, default));
            return h;
        }
        public Task<IActionResult> Pay(int index, string reference) => Controller().RecordPaymentLineOutcome(Batch.Id, Lines[index].Id,
            new LoanPaymentOutcomeRequest("Paid", reference, Today), default);
        public async Task Correct(EmployeeLoan loan, string kind, string reference, Guid? repaymentId = null)
        {
            Assert.IsType<OkObjectResult>(await Controller().RequestLoanCorrection(loan.Id,
                new LoanCorrectionRequest(kind, Today, reference, "Bank evidence reviewed", repaymentId), default));
            var change = await Db.LoanChangeRequests.SingleAsync(x => x.Reference == reference);
            Assert.IsType<OkObjectResult>(await Controller().DecideLoanCorrection(loan.Id, change.Id, new LoanCorrectionDecision("Approved", "Independent Finance approval"), default));
        }
        public LoansController Controller(string role = "Finance", Guid? userId = null, bool restricted = false, Guid? tenantId = null) => new(Db, new Scope(restricted))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", (tenantId ?? Tid).ToString()), new Claim(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
                        new Claim(ClaimTypes.Name, "Finance Reviewer"), new Claim(ClaimTypes.Role, role)
                    }, "test"))
                }
            }
        };
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
    private sealed class Scope(bool restricted) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(restricted
            ? new DataScope { Level = DataScopeLevel.Own, AllowedEmployeeIds = new HashSet<int>() }
            : new DataScope { Level = DataScopeLevel.Organization });
    }
}
