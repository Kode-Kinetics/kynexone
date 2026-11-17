using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The overtime company settings through the real Setup > Payroll rules endpoint (CompanyPayrollRulesController + service): who
/// has to agree, and what the server refuses outright.
/// </summary>
public class OvertimeCompanySettingsServiceTests
{
    private const string Group = "{\"v\":2,\"m\":\"group\",\"c\":[]}";
    private static readonly Guid Maker = Guid.NewGuid();
    private static readonly Guid Checker = Guid.NewGuid();

    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CompanyPayrollRulesController As(ZayraDbContext db, Guid tenant, Guid user) => new(new CompanyPayrollRulesService(db, new AuditService(db)))
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            [new(ClaimTypes.NameIdentifier, user.ToString()), new("tenant_id", tenant.ToString()),
                new("entity_scope", Group), new("entity_scope_strict", "true")], "Test")) } },
    };

    private static async Task<(Guid Tenant, Guid Company)> SeedAsync(ZayraDbContext db, bool withBudget = false)
    {
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "A", CountryCode = "SAU", DefaultCurrency = "SAR" };
        db.Companies.Add(company);
        var permission = new Permission { Key = "payroll.approve", Module = "Payroll" };
        var role = new Role { TenantId = tenant, Name = "Finance Approver", NormalizedName = "FINANCE APPROVER" };
        db.Permissions.Add(permission);
        db.Roles.Add(role);
        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
        db.Users.Add(new User { Id = Checker, TenantId = tenant, Email = "f@x.test", NormalizedEmail = "F@X.TEST", FullName = "Finance", IsGroupScope = true });
        db.UserRoles.Add(new UserRole { UserId = Checker, RoleId = role.Id });
        // Another company's budget never counts as this company's.
        var other = new Company { TenantId = tenant, LegalNameEn = "B", CountryCode = "SAU", DefaultCurrency = "SAR" };
        db.Companies.Add(other);
        var otherDept = new Department { TenantId = tenant, CompanyId = other.Id, Code = "X", NameEn = "X", NameAr = "س" };
        db.Departments.Add(otherDept);
        db.OvertimeBudgets.Add(new OvertimeBudget { TenantId = tenant, DepartmentId = otherDept.Id, Year = 2026, Month = 11, BudgetAmount = 1000m, Currency = "SAR" });
        if (withBudget)
        {
            var dept = new Department { TenantId = tenant, CompanyId = company.Id, Code = "OPS", NameEn = "Operations", NameAr = "العمليات" };
            db.Departments.Add(dept);
            db.OvertimeBudgets.Add(new OvertimeBudget { TenantId = tenant, DepartmentId = dept.Id, Year = 2026, Month = 11, BudgetAmount = 1000m, Currency = "SAR" });
        }
        await db.SaveChangesAsync();
        return (tenant, company.Id);
    }

    private static CompanyPayrollRulesController.SaveRulesRequest Save(Guid company, string key, string value, bool acknowledge = false, params string[] warnings) =>
        new(company, [new PayrollRuleChange(key, value)], acknowledge, "Agreed with the owner", null, warnings.ToList());

    [Fact]
    public async Task TurningAutoApproveOn_Returns202_AndTheMakerCannotApproveIt()
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db);

        var saved = await As(db, tenant, Maker).Save(Save(company, PayrollRuleKeys.OvertimeAutoApprove, "true", true, "overtime_auto_approve:OVERTIME_AUTO_APPROVE_ON"), default);

        var accepted = saved.Should().BeOfType<AcceptedResult>().Subject;
        var requestId = JsonSerializer.SerializeToElement(accepted.Value).GetProperty("requestId").GetGuid();
        (await CompanyPayrollRules.ResolveAsync(db, tenant, company, default)).OvertimeAutoApprove.Should().BeFalse("nothing applies until a second person approves");

        (await As(db, tenant, Maker).Approve(requestId, null, default)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        (await As(db, tenant, Checker).Approve(requestId, null, default)).Should().BeOfType<OkObjectResult>();
        (await CompanyPayrollRules.ResolveAsync(db, tenant, company, default)).OvertimeAutoApprove.Should().BeTrue();
    }

    [Fact]
    public async Task TurningTheManagerStepOff_Returns202()
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db);

        var saved = await As(db, tenant, Maker).Save(Save(company, PayrollRuleKeys.OvertimeManagerStepRequired, "false", true, "overtime_manager_step_required:OVERTIME_MANAGER_STEP_OFF"), default);

        saved.Should().BeOfType<AcceptedResult>();
        (await CompanyPayrollRules.ResolveAsync(db, tenant, company, default)).OvertimeManagerStepRequired.Should().BeTrue();
    }

    [Theory]
    [InlineData("Warn")]
    [InlineData("Block")]
    public async Task BudgetWarnOrBlock_IsRefused_WhileTheCompanyHasNoBudget(string mode)
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db, withBudget: false);

        var saved = await As(db, tenant, Maker).Save(Save(company, PayrollRuleKeys.OvertimeBudgetMode, mode), default);

        var refused = saved.Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = JsonSerializer.SerializeToElement(refused.Value);
        body.GetProperty("code").GetString().Should().Be("overtime_budget_none");
        body.GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
        (await db.PayrollRuleChangeRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task BudgetBlock_WithABudgetDefined_GoesToASecondPerson()
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db, withBudget: true);

        (await As(db, tenant, Maker).Save(Save(company, PayrollRuleKeys.OvertimeBudgetMode, "Block"), default)).Should().BeOfType<AcceptedResult>();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task TheView_SaysWhetherTheCompanyHasABudget(bool withBudget, bool expected)
    {
        await using var db = CreateDb();
        var (tenant, company) = await SeedAsync(db, withBudget);

        var view = (await As(db, tenant, Maker).Get(company, default)).Should().BeOfType<OkObjectResult>().Subject;

        JsonSerializer.SerializeToElement(view.Value).GetProperty("overtimeBudgetsDefined").GetBoolean().Should().Be(expected);
    }
}
