using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>A company-scoped HR user can read and change only their own company's payroll rules.</summary>
public class CompanyPayrollRulesControllerTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CompanyPayrollRulesController Controller(ZayraDbContext db, Guid tenant, string scope) => new(new Zayra.Api.Infrastructure.Payroll.CompanyPayrollRulesService(db, new AuditService(db)))
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            [new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new("tenant_id", tenant.ToString()), new(ClaimTypes.Role, "HR Manager"),
                new("permission", "payroll.write"), new("entity_scope", scope), new("entity_scope_strict", "true")], "Test")) } },
    };

    private static async Task<(Guid Tenant, Guid A, Guid B)> SeedAsync(ZayraDbContext db)
    {
        var tenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "A", CountryCode = "SAU", DefaultCurrency = "SAR" };
        var b = new Company { TenantId = tenant, LegalNameEn = "B", CountryCode = "SAU", DefaultCurrency = "SAR" };
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();
        return (tenant, a.Id, b.Id);
    }

    private static string OnlyCompany(Guid id) => $"{{\"v\":2,\"m\":\"companies\",\"c\":[\"{id}\"]}}";
    private const string Group = "{\"v\":2,\"m\":\"group\",\"c\":[]}";

    [Fact]
    public async Task ACompanyScopedUser_CannotReadAnotherCompanysRules()
    {
        await using var db = CreateDb();
        var (tenant, a, b) = await SeedAsync(db);
        (await Controller(db, tenant, OnlyCompany(a)).Get(b, default)).Should().BeOfType<NotFoundResult>();
        (await Controller(db, tenant, OnlyCompany(a)).Get(a, default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ACompanyScopedUser_CannotChangeAnotherCompanysRules_AndNothingIsWritten()
    {
        await using var db = CreateDb();
        var (tenant, a, b) = await SeedAsync(db);
        var request = new CompanyPayrollRulesController.SaveRulesRequest(b,
            [new PayrollRuleChange(PayrollRuleKeys.AbsenceDeductionBase, "Gross")]);

        (await Controller(db, tenant, OnlyCompany(a)).Save(request, default)).Should().BeOfType<NotFoundResult>();
        (await db.CompanyPayrollRules.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AGroupUser_CanChangeAnyCompanyInTheTenant_ButNotAnotherTenantsCompany()
    {
        await using var db = CreateDb();
        var (tenant, _, b) = await SeedAsync(db);
        var ok = await Controller(db, tenant, Group).Save(new CompanyPayrollRulesController.SaveRulesRequest(b,
            [new PayrollRuleChange(PayrollRuleKeys.MaxProbationDays, "60")]), default);
        ok.Should().BeOfType<OkObjectResult>();
        (await db.CompanyPayrollRules.SingleAsync()).CompanyId.Should().Be(b);

        var (otherTenant, _, otherCompany) = await SeedAsync(db);
        (await Controller(db, tenant, Group).Get(otherCompany, default)).Should().BeOfType<NotFoundResult>();
        _ = otherTenant;
    }

    [Fact]
    public async Task ResendingAnUnchangedValue_WritesNothingAndAuditsOnce()
    {
        await using var db = CreateDb();
        var (tenant, a, _) = await SeedAsync(db);
        var request = new CompanyPayrollRulesController.SaveRulesRequest(a, [new PayrollRuleChange(PayrollRuleKeys.MaxProbationDays, "60")]);
        await Controller(db, tenant, Group).Save(request, default);
        var first = await db.CompanyPayrollRules.AsNoTracking().SingleAsync();

        await Controller(db, tenant, Group).Save(request, default);

        (await db.CompanyPayrollRules.AsNoTracking().SingleAsync()).UpdatedAtUtc.Should().Be(first.UpdatedAtUtc);
        (await db.AuditLogs.CountAsync(x => x.Action == "payroll.rules.changed")).Should().Be(1);
    }
}
