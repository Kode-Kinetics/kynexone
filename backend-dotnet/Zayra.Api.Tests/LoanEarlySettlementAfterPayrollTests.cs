using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A loan that payroll has already taken an instalment from can still be settled early by cash: the receipt is
/// allocated to the REMAINING (unpaid) instalments only, never to one a payroll run consumed, and the journal is the
/// ordinary Dr Cash/Bank / Cr receivable. A payroll-collected loan is only ever settled IN FULL (a part payment would
/// race the next payroll deduction), and never while an unfinished payroll run holds one of its instalments.
/// </summary>
public class LoanEarlySettlementAfterPayrollTests
{
    [Theory]
    [InlineData("BankTransfer")]
    [InlineData("PayrollDeduction")]
    public async Task FullEarlySettlement_AfterAPayrollInstalment_IsAccepted_AndOnlyTheRemainingInstalmentsAreSettled(string method)
    {
        await using var h = await Harness.Create();
        await h.Pay();
        var runId = await h.PayrollTakesFirstInstalment(method);

        var result = await h.Controller().RecordRepayment(h.Loan.Id, new RecordLoanRepaymentRequest(50m, h.Today, "EARLY-1"), default);

        Assert.IsType<OkObjectResult>(result);
        var loan = await h.Db.EmployeeLoans.AsNoTracking().SingleAsync();
        Assert.Equal("Settled", loan.Status);
        Assert.Equal(0m, loan.OutstandingBalance);
        Assert.Equal(100m, loan.TotalRepaid);

        var installments = await h.Db.LoanInstallments.AsNoTracking().OrderBy(x => x.InstallmentNumber).ToListAsync();
        Assert.Equal(runId, installments[0].PayrollRunId);
        Assert.Equal(50m, installments[0].AmountPaid);
        Assert.Null(installments[1].PayrollRunId);
        Assert.Equal(50m, installments[1].AmountPaid);
        Assert.Equal("Paid", installments[1].Status);

        var receipt = await h.Db.FinanceGlEntries.SingleAsync(x => x.EventType == "Repayment");
        Assert.Equal(50m, receipt.Amount);
        Assert.Equal("1000 - Cash/Bank", receipt.DebitAccount);
        Assert.Equal("1400 - Employee Loans Receivable", receipt.CreditAccount);
    }

    [Fact]
    public async Task PartPayment_OfAPayrollCollectedLoan_IsRefused_BecauseTheNextPayrollWouldCollectTheSameInstalment()
    {
        await using var h = await Harness.Create();
        await h.Pay();
        await h.PayrollTakesFirstInstalment("PayrollDeduction");

        var result = await h.Controller().RecordRepayment(h.Loan.Id, new RecordLoanRepaymentRequest(20m, h.Today, "PART-1"), default);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(50m, (await h.Db.EmployeeLoans.AsNoTracking().SingleAsync()).OutstandingBalance);
        Assert.Empty(await h.Db.LoanRepayments.ToListAsync());
    }

    [Fact]
    public async Task EarlySettlement_IsRefused_WhileAnUnfinishedPayrollRunHoldsAnInstalment()
    {
        await using var h = await Harness.Create();
        await h.Pay();
        var runId = await h.PayrollTakesFirstInstalment("PayrollDeduction", runStatus: "Processed");

        var result = await h.Controller().RecordRepayment(h.Loan.Id, new RecordLoanRepaymentRequest(50m, h.Today, "EARLY-2"), default);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(50m, (await h.Db.EmployeeLoans.AsNoTracking().SingleAsync()).OutstandingBalance);
        Assert.NotEqual(Guid.Empty, runId);
    }

    [Fact]
    public async Task PayrollDeductionLoan_WithNoPayrollInstalmentYet_StillCannotReceiveACashRepayment_ExceptAsAFullSettlement()
    {
        await using var h = await Harness.Create();
        await h.Pay();
        var loan = await h.Db.EmployeeLoans.SingleAsync();
        loan.RepaymentMethod = "PayrollDeduction";
        await h.Db.SaveChangesAsync();

        var part = await h.Controller().RecordRepayment(h.Loan.Id, new RecordLoanRepaymentRequest(20m, h.Today, "PART-2"), default);
        Assert.IsType<ConflictObjectResult>(part);

        var full = await h.Controller().RecordRepayment(h.Loan.Id, new RecordLoanRepaymentRequest(100m, h.Today, "FULL-1"), default);
        Assert.IsType<OkObjectResult>(full);
        Assert.Equal("Settled", (await h.Db.EmployeeLoans.AsNoTracking().SingleAsync()).Status);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public ZayraDbContext Db { get; } = new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Tid { get; } = Guid.NewGuid();
        public Guid BorrowerId { get; } = Guid.NewGuid();
        public EmployeeLoan Loan { get; private set; } = null!;
        public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

        public static async Task<Harness> Create()
        {
            var h = new Harness();
            var company = new Company { TenantId = h.Tid, LegalNameEn = "Lender", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
            var type = new LoanType { TenantId = h.Tid, Code = "PERSONAL", NameEn = "Personal", IsActive = true, MaxAmount = 10000m, MaxInstallments = 24 };
            var employee = new Employee
            {
                TenantId = h.Tid, CompanyId = company.Id, UserAccountId = h.BorrowerId, EmployeeCode = "BORROWER", FullName = "Borrower",
                Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3), BankName = "Bank", BankIban = "SA4420000001234567891234",
            };
            h.Db.AddRange(company, type, employee);
            await h.Db.SaveChangesAsync();
            h.Loan = new EmployeeLoan
            {
                TenantId = h.Tid, CompanyId = company.Id, EmployeeId = employee.PublicId, EmployeeIntId = employee.Id,
                EmployeeName = employee.FullName, LoanTypeId = type.Id, LoanTypeName = type.NameEn, LoanNumber = "LN-EARLY",
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

        /// <summary>The state payroll leaves behind after Process + Lock took the first of two instalments: the sub-ledger
        /// moved, instalment 1 stamped Paid with the run id, a witness written, and the receivable credited at Lock.</summary>
        public async Task<Guid> PayrollTakesFirstInstalment(string method, string runStatus = "Locked")
        {
            var run = new PayrollRun { TenantId = Tid, CompanyId = Loan.CompanyId, Year = Today.Year, Month = Today.Month, Status = runStatus };
            Db.PayrollRuns.Add(run);
            var loan = await Db.EmployeeLoans.SingleAsync();
            loan.RepaymentMethod = method;
            Db.PayrollRunConsumptions.Add(new PayrollRunConsumption
            {
                TenantId = Tid, CompanyId = loan.CompanyId, PayrollRunId = run.Id, ArtifactType = PayrollConsumptionArtifacts.Loan,
                ArtifactId = loan.Id, EmployeeId = loan.EmployeeIntId ?? 0, Amount = 50m, PriorStatus = "Active",
                PriorOutstandingBalance = 100m, PriorTotalRepaid = 0m,
            });
            loan.OutstandingBalance = 50m;
            loan.TotalRepaid = 50m;
            var first = await Db.LoanInstallments.OrderBy(x => x.InstallmentNumber).FirstAsync();
            first.Status = "Paid"; first.AmountPaid = 50m; first.PaidDate = Today; first.PayrollRunId = run.Id;
            Db.FinanceGlEntries.Add(new FinanceGlEntry
            {
                TenantId = Tid, CompanyId = loan.CompanyId, SourceModule = "Payroll", SourceEntityId = run.Id, SourceEntityRef = "payroll",
                EventType = "Accrual", DebitAccount = string.Empty, CreditAccount = "1400 - Employee Loans Receivable", Amount = 50m,
                Currency = "SAR", EntryDate = Today, Period = Today.ToString("yyyy-MM"), Description = "Payroll loan recovery: LOAN_EMI",
            });
            await Db.SaveChangesAsync();
            return run.Id;
        }

        public LoansController Controller(Guid? userId = null) => new(Db, new Scope())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim("tenant_id", Tid.ToString()), new Claim(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
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
