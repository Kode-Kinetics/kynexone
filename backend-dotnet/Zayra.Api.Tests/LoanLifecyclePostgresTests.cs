using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public class LoanLifecyclePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Reschedule_ReusesUnpaidInstallmentNumbers_WithoutUniqueViolation_AndPreservesPaidHistory()
    {
        var seed = await SeedLoan();
        Guid changeId;
        Guid paidInstallmentId;
        await using (var requestDb = fixture.CreateDb())
        {
            paidInstallmentId = (await requestDb.LoanInstallments.SingleAsync(x => x.LoanId == seed.LoanId && x.InstallmentNumber == 1)).Id;
            Assert.IsType<OkObjectResult>(await Controller(requestDb, seed.TenantId).RequestLoanChange(seed.LoanId,
                new LoanChangeRequestInput("Reschedule", "Extend remaining debt without changing principal", 3, Today.AddMonths(1)), default));
            changeId = (await requestDb.LoanChangeRequests.SingleAsync(x => x.LoanId == seed.LoanId)).Id;
        }
        await using (var approvalDb = fixture.CreateDb())
            Assert.IsType<OkObjectResult>(await Controller(approvalDb, seed.TenantId).DecideLoanChange(seed.LoanId, changeId,
                new LoanChangeDecisionRequest("Approved", "Independent approval of revised dates"), default));

        await using var verify = fixture.CreateDb();
        var loan = await verify.EmployeeLoans.SingleAsync(x => x.Id == seed.LoanId);
        var installments = await verify.LoanInstallments.Where(x => x.LoanId == seed.LoanId).OrderBy(x => x.InstallmentNumber).ToListAsync();
        Assert.Equal(4, installments.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, installments.Select(x => x.InstallmentNumber));
        Assert.Equal(paidInstallmentId, installments[0].Id);
        Assert.Equal(10m, installments[0].AmountDue);
        Assert.Equal(10m, installments[0].AmountPaid);
        Assert.Equal("Paid", installments[0].Status);
        Assert.All(installments.Skip(1), x => Assert.Equal(30m, x.AmountDue));
        Assert.Equal(100m, installments.Sum(x => x.AmountDue));
        Assert.Equal(10m, installments.Sum(x => x.AmountPaid));
        Assert.Equal(90m, loan.OutstandingBalance);
        Assert.Equal(100m, loan.ApprovedAmount);
        Assert.Equal(2, await verify.FinanceGlEntries.CountAsync(x => x.SourceEntityId == seed.LoanId || x.SourceEntityId == seed.ReceiptId));
        var audit = await verify.LoanAuditLogs.SingleAsync(x => x.LoanId == seed.LoanId && x.Action == "ScheduleRestructured");
        Assert.Contains("33.33", audit.OldValuesJson);
        Assert.Contains("33.34", audit.OldValuesJson);
    }

    [Fact]
    public async Task ConcurrentCorrectionApprovals_ReverseReceiptAndPostContraExactlyOnce()
    {
        var seed = await SeedLoan();
        Guid correctionId;
        await using (var requestDb = fixture.CreateDb())
        {
            Assert.IsType<OkObjectResult>(await Controller(requestDb, seed.TenantId).RequestLoanCorrection(seed.LoanId,
                new LoanCorrectionRequest("ReceiptReversal", Today, "REVERSE-RECEIPT", "Bank returned original funds", seed.ReceiptId), default));
            correctionId = (await requestDb.LoanChangeRequests.SingleAsync(x => x.LoanId == seed.LoanId)).Id;
        }
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ready = new SemaphoreSlim(0, 2);
        async Task<IActionResult> Approve()
        {
            await using var db = fixture.CreateDb();
            await db.Database.OpenConnectionAsync();
            ready.Release();
            await gate.Task;
            return await Controller(db, seed.TenantId).DecideLoanCorrection(seed.LoanId, correctionId,
                new LoanCorrectionDecision("Approved", "Independent bank return verification"), default);
        }
        var first = Approve();
        var second = Approve();
        await ready.WaitAsync();
        await ready.WaitAsync();
        gate.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.IsType<OkObjectResult>(result));

        await using var verify = fixture.CreateDb();
        var receipt = await verify.LoanRepayments.SingleAsync(x => x.Id == seed.ReceiptId);
        Assert.True(receipt.IsReversed);
        Assert.NotNull(receipt.ReversedBy);
        var loan = await verify.EmployeeLoans.SingleAsync(x => x.Id == seed.LoanId);
        Assert.Equal(0m, loan.TotalRepaid);
        Assert.Equal(100m, loan.OutstandingBalance);
        Assert.Equal(0m, await verify.LoanInstallments.Where(x => x.LoanId == seed.LoanId).SumAsync(x => x.AmountPaid));
        var reversal = Assert.Single(await verify.FinanceGlEntries.Where(x => x.SourceEntityId == seed.ReceiptId && x.EventType == "RepaymentReversal").ToListAsync());
        Assert.Equal(receipt.GlEntryId, reversal.ReversalOfEntryId);
        Assert.Equal(receipt.ReversalGlEntryId, reversal.Id);
        Assert.Single(await verify.LoanAuditLogs.Where(x => x.LoanId == seed.LoanId && x.Action == "CorrectionApproved").ToListAsync());
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private async Task<(Guid TenantId, Guid LoanId, Guid ReceiptId)> SeedLoan()
    {
        await using var db = fixture.CreateDb();
        var tid = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { TenantId = tid, LegalNameEn = "Loan Lifecycle PG", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
        var type = new LoanType { TenantId = tid, Code = "PERSONAL", NameEn = "Personal", MaxAmount = 10_000m, MaxInstallments = 12, IsActive = true };
        var employee = new Employee
        {
            TenantId = tid, CompanyId = company.Id, EmployeeCode = "PG-LOAN", FullName = "Loan Borrower",
            Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-3), BankName = "Test Bank", BankIban = "SA4420000001234567891234"
        };
        db.AddRange(company, type, employee);
        await db.SaveChangesAsync();
        var loan = new EmployeeLoan
        {
            TenantId = tid, CompanyId = company.Id, EmployeeIntId = employee.Id, EmployeeId = employee.PublicId,
            EmployeeName = employee.FullName, LoanTypeId = type.Id, LoanTypeName = type.NameEn, LoanNumber = "PG-LOAN-1",
            RequestedAmount = 100m, ApprovedAmount = 100m, RequestedInstallments = 3, ApprovedInstallments = 3,
            InstallmentAmount = 33.33m, TotalRepaid = 10m, OutstandingBalance = 90m, Status = "Active",
            DisbursementDate = Today.AddDays(-2), RepaymentStartDate = Today, RepaymentMethod = "BankTransfer", Currency = "SAR",
            CreatedBy = Guid.NewGuid(), PolicySnapshotJson = JsonSerializer.Serialize(new { AllowRescheduling = true, MaxInstallments = 12 })
        };
        db.EmployeeLoans.Add(loan);
        for (var i = 1; i <= 3; i++)
            db.LoanInstallments.Add(new LoanInstallment
            {
                TenantId = tid, LoanId = loan.Id, InstallmentNumber = i, DueDate = Today.AddMonths(i - 1),
                AmountDue = i == 3 ? 33.34m : 33.33m, AmountPaid = i == 1 ? 10m : 0m, PaidDate = i == 1 ? Today.AddDays(-1) : null
            });
        var receipt = new LoanRepayment { TenantId = tid, CompanyId = company.Id, LoanId = loan.Id, Amount = 10m, PaidDate = Today.AddDays(-1), Reference = "RECEIPT-1", PaymentMethod = "BankTransfer", CreatedBy = Guid.NewGuid() };
        db.LoanRepayments.Add(receipt);
        var receiptJournal = new FinanceGlEntry { TenantId = tid, CompanyId = company.Id, SourceModule = "Loan", SourceEntityId = receipt.Id, SourceEntityRef = loan.LoanNumber,
            EventType = "Repayment", Amount = 10m, Currency = "SAR", EntryDate = Today.AddDays(-1), Period = Today.AddDays(-1).ToString("yyyy-MM"), DebitAccount = "1000 - Cash/Bank", CreditAccount = "1400 - Employee Loans Receivable" };
        receipt.GlEntryId = receiptJournal.Id;
        db.FinanceGlEntries.AddRange(
            new FinanceGlEntry { TenantId = tid, CompanyId = company.Id, SourceModule = "Loan", SourceEntityId = loan.Id, SourceEntityRef = loan.LoanNumber,
                EventType = "Disbursement", Amount = 100m, Currency = "SAR", EntryDate = Today.AddDays(-2), Period = Today.AddDays(-2).ToString("yyyy-MM"), DebitAccount = "1400 - Employee Loans Receivable", CreditAccount = "1000 - Cash/Bank" },
            receiptJournal);
        await db.SaveChangesAsync();
        return (tid, loan.Id, receipt.Id);
    }

    private static LoansController Controller(ZayraDbContext db, Guid tenantId) => new(db, new Scope())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Name, "Finance PG Reviewer"), new Claim(ClaimTypes.Role, "Finance")
                }, "test"))
            }
        }
    };
    private sealed class Scope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
