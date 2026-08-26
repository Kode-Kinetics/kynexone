using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class FinanceLoanAdvanceMakerCheckerTests
{
    [Fact]
    public async Task LoanApproval_BlocksRequesterSelfApproval()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var loanType = new LoanType
        {
            TenantId = tenantId,
            Code = "EMER",
            NameEn = "Emergency Loan",
            MaxAmount = 20_000m,
            MaxInstallments = 12,
            RequiresApproval = true,
            IsActive = true,
        };
        var employee = MakeEmployee(tenantId, "EMP-MONA", "Mona Saleh");
        db.AddRange(loanType, employee);
        await db.SaveChangesAsync();

        var requester = MakeLoansController(db, tenantId, requesterId);
        var create = await requester.CreateLoan(
            new CreateLoanRequest(employee.PublicId, employee.FullName, loanType.Id, 6_000m, 3, null, employee.Id),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(create);

        var loan = await db.EmployeeLoans.SingleAsync(x => x.TenantId == tenantId);
        var approval = new LoanApproval
        {
            TenantId = tenantId,
            LoanId = loan.Id,
            StepOrder = 1,
            ApproverRole = "Finance",
        };
        db.LoanApprovals.Add(approval);
        await db.SaveChangesAsync();

        var result = await requester.DecideApproval(
            loan.Id,
            approval.Id,
            new ApprovalDecisionRequest("Approved", "self approve", 6_000m, 3, null),
            CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Maker-checker", bad.Value?.ToString());
        var unchangedLoan = await db.EmployeeLoans.SingleAsync(x => x.Id == loan.Id);
        Assert.Equal("Pending", unchangedLoan.Status);
        Assert.Equal(0m, unchangedLoan.ApprovedAmount);
        Assert.Empty(await db.LoanInstallments.Where(x => x.LoanId == loan.Id).ToListAsync());
        Assert.Empty(await db.FinanceGlEntries.Where(x => x.SourceEntityId == loan.Id).ToListAsync());
    }

    [Fact]
    public async Task LoanCreate_WhenTypeRequiresApproval_DoesNotAutoApproveOrPostGl()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var loanType = new LoanType
        {
            TenantId = tenantId,
            Code = "PERS",
            NameEn = "Personal Loan",
            MaxAmount = 50_000m,
            MaxInstallments = 24,
            RequiresApproval = true,
            IsActive = true,
        };
        var employee = MakeEmployee(tenantId, "EMP-OMAR", "Omar Nasser");
        db.AddRange(loanType, employee);
        await db.SaveChangesAsync();

        var result = await MakeLoansController(db, tenantId, Guid.NewGuid()).CreateLoan(
            new CreateLoanRequest(employee.PublicId, employee.FullName, loanType.Id, 10_000m, 5, null, employee.Id),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var loan = await db.EmployeeLoans.SingleAsync(x => x.TenantId == tenantId);
        Assert.Equal("Pending", loan.Status);
        Assert.Equal(0m, loan.ApprovedAmount);
        Assert.Empty(await db.LoanInstallments.Where(x => x.LoanId == loan.Id).ToListAsync());
        Assert.Empty(await db.FinanceGlEntries.Where(x => x.SourceEntityId == loan.Id).ToListAsync());
    }

    [Fact]
    public async Task AdvanceApproval_BlocksRequesterSelfApproval()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var employee = MakeEmployee(tenantId, "EMP-SARA", "Sara Ahmed");
        db.Add(new AdvancePolicy
        {
            TenantId = tenantId,
            PolicyName = "Standard",
            RequiresApproval = true,
            MaxAdvancesPerYear = 2,
            IsActive = true,
        });
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var requester = MakeAdvancesController(db, tenantId, requesterId);
        var create = await requester.Create(
            new CreateAdvanceRequest(employee.PublicId, employee.FullName, 2_500m, "Installments", 2, null, null, employee.Id),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(create);

        var advance = await db.SalaryAdvances.SingleAsync(x => x.TenantId == tenantId);
        var result = await requester.Approve(
            advance.Id,
            new AdvanceApproveRequest(2_500m, 2, DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1))),
            CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Maker-checker", bad.Value?.ToString());
        var unchangedAdvance = await db.SalaryAdvances.SingleAsync(x => x.Id == advance.Id);
        Assert.Equal("Pending", unchangedAdvance.Status);
        Assert.Equal(0m, unchangedAdvance.ApprovedAmount);
        Assert.Empty(await db.AdvanceInstallments.Where(x => x.AdvanceId == advance.Id).ToListAsync());
        Assert.Empty(await db.FinanceGlEntries.Where(x => x.SourceEntityId == advance.Id).ToListAsync());
    }

    [Fact]
    public async Task AdvanceCreate_WhenPolicyRequiresApproval_DoesNotAutoApproveOrPostGl()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var employee = MakeEmployee(tenantId, "EMP-KHALID", "Khalid Omar");
        db.Add(new AdvancePolicy
        {
            TenantId = tenantId,
            PolicyName = "Standard",
            RequiresApproval = true,
            MaxAdvancesPerYear = 2,
            IsActive = true,
        });
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        var result = await MakeAdvancesController(db, tenantId, Guid.NewGuid()).Create(
            new CreateAdvanceRequest(employee.PublicId, employee.FullName, 1_800m, "OneTime", 1, null, null, employee.Id),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var advance = await db.SalaryAdvances.SingleAsync(x => x.TenantId == tenantId);
        Assert.Equal("Pending", advance.Status);
        Assert.Equal(0m, advance.ApprovedAmount);
        Assert.Empty(await db.AdvanceInstallments.Where(x => x.AdvanceId == advance.Id).ToListAsync());
        Assert.Empty(await db.FinanceGlEntries.Where(x => x.SourceEntityId == advance.Id).ToListAsync());
    }

    private static ZayraDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ZayraDbContext(options);
    }

    private static Employee MakeEmployee(Guid tenantId, string code, string name) => new()
    {
        TenantId = tenantId,
        EmployeeCode = code,
        FullName = name,
        EnglishName = name,
        Status = EmployeeStatuses.Active,
    };

    private static LoansController MakeLoansController(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var ctrl = new LoansController(db, new UnrestrictedScopeService());
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = MakePrincipal(tenantId, userId) },
        };
        return ctrl;
    }

    private static AdvancesController MakeAdvancesController(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var ctrl = new AdvancesController(db, new UnrestrictedScopeService());
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = MakePrincipal(tenantId, userId) },
        };
        return ctrl;
    }

    private static ClaimsPrincipal MakePrincipal(Guid tenantId, Guid userId)
        => new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Finance Tester"),
            new Claim(ClaimTypes.Role, "Finance"),
        }, "Test"));

    private sealed class UnrestrictedScopeService : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
