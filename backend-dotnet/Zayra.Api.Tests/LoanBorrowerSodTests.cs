using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>LON-04: a borrower who also holds the Finance role cannot move their own loan's cash records.</summary>
public class LoanBorrowerSodTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private sealed record Fx(ZayraDbContext Db, Guid TenantId, Guid BorrowerUser, Guid MakerUser, Guid CheckerUser, Employee Employee, Guid CompanyId);

    private static async Task<Fx> SeedAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var borrowerUser = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Loan Co", DefaultCurrency = "SAR", CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true };
        var employee = new Employee
        {
            TenantId = tenant, CompanyId = company.Id, EmployeeCode = "LOAN-1", FullName = "Borrower", EnglishName = "Borrower",
            Status = EmployeeStatuses.Active, BankName = "Test Bank", BankIban = "SA4420000001234567891234",
            JoiningDate = DateTime.UtcNow.AddYears(-2), UserAccountId = borrowerUser,
        };
        db.AddRange(company, employee);
        await db.SaveChangesAsync();
        return new Fx(db, tenant, borrowerUser, Guid.NewGuid(), Guid.NewGuid(), employee, company.Id);
    }

    private static LoansController As(Fx f, Guid user) => new(f.Db, new Scope())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", f.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, user.ToString()),
                    new Claim("permission", "loans.read"), new Claim(ClaimTypes.Name, "Tester"),
                    new Claim(ClaimTypes.Role, "Finance"), new Claim(ClaimTypes.Role, "HR Manager"),
                }, "test")),
            },
        },
    };

    private sealed class Scope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }

    /// <summary>A loan raised by Maker and approved by Checker through the real endpoints, awaiting disbursement.</summary>
    private static async Task<EmployeeLoan> ApprovedLoanAsync(Fx f)
    {
        var type = new LoanType
        {
            TenantId = f.TenantId, Code = "PERSONAL", NameEn = "Personal", MaxAmount = 10_000m,
            MaxInstallments = 12, RequiresApproval = true, IsActive = true,
        };
        f.Db.Add(type);
        await f.Db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await As(f, f.MakerUser).CreateLoan(new CreateLoanRequest(f.Employee.PublicId,
            f.Employee.FullName, type.Id, 100m, 3, null, f.Employee.Id), Ct));
        var loan = await f.Db.EmployeeLoans.SingleAsync();
        var step = await f.Db.LoanApprovals.SingleAsync(x => x.LoanId == loan.Id);
        Assert.IsType<OkObjectResult>(await As(f, f.CheckerUser).DecideApproval(loan.Id, step.Id,
            new ApprovalDecisionRequest("Approved", "ok", null, null, null), Ct));
        return loan;
    }

    private static async Task<Guid> ApprovedBatchAsync(Fx f, EmployeeLoan loan)
    {
        Assert.IsType<OkObjectResult>(await As(f, f.MakerUser).CreatePaymentBatch(new CreateLoanPaymentBatchRequest(new[] { loan.Id }), Ct));
        var batch = await f.Db.Set<LoanDisbursementBatch>().SingleAsync();
        Assert.IsType<OkObjectResult>(await As(f, f.CheckerUser).ApprovePaymentBatch(batch.Id, Ct));
        return batch.Id;
    }

    [Fact]
    public async Task RecordRepayment_BorrowerWithFinanceRoleIsRefused()
    {
        var f = await SeedAsync();
        await using var db = f.Db;
        var loan = await ApprovedLoanAsync(f);
        var batchId = await ApprovedBatchAsync(f, loan);
        Assert.IsType<OkObjectResult>(await As(f, f.CheckerUser).ConfirmPaymentBatchPaid(batchId, new ConfirmLoanPaymentRequest("BANK-1", Today), Ct));
        var glBefore = await db.FinanceGlEntries.CountAsync();

        var result = await As(f, f.BorrowerUser).RecordRepayment(loan.Id, new RecordLoanRepaymentRequest(10m, Today, "SELF-1"), Ct);

        Assert.IsType<BadRequestObjectResult>(result);
        var after = await db.EmployeeLoans.AsNoTracking().SingleAsync(x => x.Id == loan.Id);
        Assert.Equal(100m, after.OutstandingBalance);
        Assert.Equal(0m, after.TotalRepaid);
        Assert.Empty(await db.Set<LoanRepayment>().ToListAsync());
        Assert.Equal(glBefore, await db.FinanceGlEntries.CountAsync());
    }

    [Fact]
    public async Task RecordRepayment_ByAnIndependentFinanceUser_StillWorks()
    {
        var f = await SeedAsync();
        await using var db = f.Db;
        var loan = await ApprovedLoanAsync(f);
        var batchId = await ApprovedBatchAsync(f, loan);
        Assert.IsType<OkObjectResult>(await As(f, f.CheckerUser).ConfirmPaymentBatchPaid(batchId, new ConfirmLoanPaymentRequest("BANK-1", Today), Ct));

        Assert.IsType<OkObjectResult>(await As(f, f.CheckerUser).RecordRepayment(loan.Id, new RecordLoanRepaymentRequest(10m, Today, "R-1"), Ct));
    }

    [Fact]
    public async Task CreatePaymentBatch_BorrowerWithFinanceRoleIsRefused()
    {
        var f = await SeedAsync();
        await using var db = f.Db;
        var loan = await ApprovedLoanAsync(f);

        var result = await As(f, f.BorrowerUser).CreatePaymentBatch(new CreateLoanPaymentBatchRequest(new[] { loan.Id }), Ct);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("beneficiary", bad.Value?.ToString());
        Assert.Empty(await db.Set<LoanDisbursementBatch>().ToListAsync());
    }

    [Fact]
    public async Task ConfirmPaymentBatchPaid_BorrowerWithFinanceRoleIsRefused()
    {
        var f = await SeedAsync();
        await using var db = f.Db;
        var loan = await ApprovedLoanAsync(f);
        var batchId = await ApprovedBatchAsync(f, loan);

        var result = await As(f, f.BorrowerUser).ConfirmPaymentBatchPaid(batchId, new ConfirmLoanPaymentRequest("BANK-SELF", Today), Ct);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("beneficiary", bad.Value?.ToString());
        Assert.Equal("Approved", (await db.Set<LoanDisbursementBatch>().AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await db.FinanceGlEntries.ToListAsync());
        Assert.Equal("Approved", (await db.EmployeeLoans.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ConfirmPaymentBatchPaid_ByAnIndependentFinanceUser_StillWorks()
    {
        var f = await SeedAsync();
        await using var db = f.Db;
        var loan = await ApprovedLoanAsync(f);
        var batchId = await ApprovedBatchAsync(f, loan);

        Assert.IsType<OkObjectResult>(await As(f, f.CheckerUser).ConfirmPaymentBatchPaid(batchId, new ConfirmLoanPaymentRequest("BANK-OK", Today), Ct));
    }
}
