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

/// <summary>
/// A loan identifies its borrower the way an advance does: from the login-to-employee link (the employee_id claim issued from
/// employee_user_accounts, CallerEmployeeResolver), with the legacy Employees.UserAccountId column as a fallback. A login linked
/// only through the link table (UserAccountId empty, as NoLogin access and offboarding leave it) used to be refused its own loan
/// request, could not see its own loans, and was not recognised as the borrower, so the bar on deciding its own loan did not fire.
/// </summary>
public class LoanBorrowerLinkIdentityTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed record Fx(ZayraDbContext Db, Guid TenantId, Guid BorrowerUser, Employee Employee, LoanType Type);

    private static async Task<Fx> SeedAsync(bool legacyColumnToo = false)
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        var borrowerUser = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Loan Co", DefaultCurrency = "SAR", CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true };
        var employee = new Employee
        {
            TenantId = tenant, CompanyId = company.Id, EmployeeCode = "LINK-1", FullName = "Linked Borrower", EnglishName = "Linked Borrower",
            Status = EmployeeStatuses.Active, BankName = "Test Bank", BankIban = "SA4420000001234567891234",
            JoiningDate = DateTime.UtcNow.AddYears(-2), UserAccountId = legacyColumnToo ? borrowerUser : null,
        };
        var type = new LoanType
        {
            TenantId = tenant, Code = "PERSONAL", NameEn = "Personal", MaxAmount = 10_000m,
            MaxInstallments = 12, RequiresApproval = true, IsActive = true,
        };
        db.AddRange(company, employee, type);
        await db.SaveChangesAsync();
        db.EmployeeUserAccounts.Add(new EmployeeUserAccount { TenantId = tenant, EmployeeId = employee.Id, UserId = borrowerUser, Status = "Active" });
        await db.SaveChangesAsync();
        return new Fx(db, tenant, borrowerUser, employee, type);
    }

    /// <summary>The borrower's own session: the employee_id claim is what the token service issues from the link row.</summary>
    private static LoansController Borrower(Fx f, params string[] roles) => Controller(f, f.BorrowerUser, f.Employee.Id,
        roles.Length == 0 ? ["Employee"] : roles, roles.Length == 0 ? ["loans.self"] : ["loans.self", "loans.read", "loans.write"]);

    private static LoansController Hr(Fx f) => Controller(f, Guid.NewGuid(), null, ["HR Manager"], ["loans.read", "loans.write"]);

    private static LoansController Controller(Fx f, Guid user, int? employeeId, string[] roles, string[] permissions) => new(f.Db, new Scope())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("tenant_id", f.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, user.ToString()), new Claim(ClaimTypes.Name, "Tester") }
                        .Concat(employeeId is int id ? new[] { new Claim("employee_id", id.ToString()) } : Array.Empty<Claim>())
                        .Concat(roles.Select(r => new Claim(ClaimTypes.Role, r)))
                        .Concat(permissions.Select(p => new Claim("permission", p))), "test")),
            },
        },
    };

    private sealed class Scope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }

    private static CreateLoanRequest SelfRequest(Fx f) => new(Guid.Empty, string.Empty, f.Type.Id, 100m, 3, null);

    [Fact]
    public async Task LinkOnlyLogin_CanRequestItsOwnLoan()
    {
        var f = await SeedAsync();
        await using var db = f.Db;

        Assert.IsType<OkObjectResult>(await Borrower(f).CreateLoan(SelfRequest(f), Ct));

        var loan = await db.EmployeeLoans.SingleAsync();
        Assert.Equal(f.Employee.Id, loan.EmployeeIntId);
        Assert.Equal(f.BorrowerUser, loan.CreatedBy);
    }

    [Fact]
    public async Task LinkOnlyLogin_SeesItsOwnLoan_InTheListAndTheDetail()
    {
        var f = await SeedAsync();
        await using var db = f.Db;
        Assert.IsType<OkObjectResult>(await Hr(f).CreateLoan(new CreateLoanRequest(f.Employee.PublicId, f.Employee.FullName, f.Type.Id, 100m, 3, null, f.Employee.Id), Ct));
        var loan = await db.EmployeeLoans.SingleAsync();

        var list = Json(Assert.IsType<OkObjectResult>(await Borrower(f).ListLoans(null, null, mine: true)).Value);
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.IsType<OkObjectResult>(await Borrower(f).GetLoan(loan.Id, Ct));
    }

    [Fact]
    public async Task LinkOnlyLogin_GetsItsOwnEligibility_WithoutNamingAnEmployee()
    {
        var f = await SeedAsync();
        await using var db = f.Db;

        Assert.IsType<OkObjectResult>(await Borrower(f).GetLoanEligibility(f.Type.Id, null, null, ct: Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedBorrowerWithAnHrRole_CannotApproveTheirOwnLoan(bool legacyColumnToo)
    {
        var f = await SeedAsync(legacyColumnToo);
        await using var db = f.Db;
        Assert.IsType<OkObjectResult>(await Hr(f).CreateLoan(new CreateLoanRequest(f.Employee.PublicId, f.Employee.FullName, f.Type.Id, 100m, 3, null, f.Employee.Id), Ct));
        var loan = await db.EmployeeLoans.SingleAsync();
        var step = await db.LoanApprovals.SingleAsync(x => x.LoanId == loan.Id);

        var result = await Borrower(f, "HR Manager").DecideApproval(loan.Id, step.Id, new ApprovalDecisionRequest("Approved", "self", null, null, null), Ct);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("borrower cannot approve", bad.Value?.ToString());
        Assert.Equal("Pending", (await db.EmployeeLoans.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task LinkedBorrowerWithTheFinanceRole_CannotBatchTheirOwnLoan()
    {
        var f = await SeedAsync();
        await using var db = f.Db;
        Assert.IsType<OkObjectResult>(await Hr(f).CreateLoan(new CreateLoanRequest(f.Employee.PublicId, f.Employee.FullName, f.Type.Id, 100m, 3, null, f.Employee.Id), Ct));
        var loan = await db.EmployeeLoans.SingleAsync();
        var step = await db.LoanApprovals.SingleAsync(x => x.LoanId == loan.Id);
        Assert.IsType<OkObjectResult>(await Hr(f).DecideApproval(loan.Id, step.Id, new ApprovalDecisionRequest("Approved", "ok", null, null, null), Ct));

        var result = await Borrower(f, "Finance").CreatePaymentBatch(new CreateLoanPaymentBatchRequest(new[] { loan.Id }), Ct);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("beneficiary", bad.Value?.ToString());
        Assert.Empty(await db.Set<LoanDisbursementBatch>().ToListAsync());
    }

    [Fact]
    public async Task AnotherEmployeesLogin_StillCannotRequestALoanForThisEmployee()
    {
        var f = await SeedAsync();
        await using var db = f.Db;
        var stranger = Controller(f, Guid.NewGuid(), null, ["Employee"], ["loans.self"]);

        Assert.IsType<ForbidResult>(await stranger.CreateLoan(new CreateLoanRequest(f.Employee.PublicId, f.Employee.FullName, f.Type.Id, 100m, 3, null, f.Employee.Id), Ct));
        Assert.Empty(await db.EmployeeLoans.ToListAsync());
    }

    private static JsonElement Json(object? value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return doc.RootElement.Clone();
    }
}
