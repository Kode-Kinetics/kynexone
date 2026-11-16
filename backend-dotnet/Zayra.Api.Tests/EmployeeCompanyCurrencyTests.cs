using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Money written for an employee takes the currency of the company that employs them, and is refused when that is unknown, never a
/// hard-coded dirham: the shared resolver, and the two import sections that used to default to "AED" when the column was left out.
/// </summary>
public sealed class EmployeeCompanyCurrencyTests
{
    private static ZayraDbContext NewDb() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(ZayraDbContext Db, Guid Tenant, Employee InCompany, Employee NoCompany)> SeedAsync(string currency = "SAR")
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = new Company { TenantId = tenant, LegalNameEn = "Alpha", CountryCode = "SA", DefaultCurrency = currency, IsActive = true };
        db.Tenants.Add(new Tenant { Id = tenant, Name = "T", Slug = $"t-{tenant:N}" });
        db.Companies.Add(company);
        var inCompany = new Employee { TenantId = tenant, CompanyId = company.Id, EmployeeCode = "EMP-001", FullName = "In Company", Status = EmployeeStatuses.Active };
        var noCompany = new Employee { TenantId = tenant, EmployeeCode = "EMP-002", FullName = "No Company", Status = EmployeeStatuses.Active };
        db.Employees.AddRange(inCompany, noCompany);
        await db.SaveChangesAsync();
        return (db, tenant, inCompany, noCompany);
    }

    [Fact]
    public async Task Resolver_ReturnsTheEmployeesCompanyCurrency_UpperCased()
    {
        var (db, _, inCompany, _) = await SeedAsync(" sar ");
        (await EmployeeCompanyCurrency.ResolveAsync(db, inCompany, default)).Should().Be("SAR");
    }

    [Fact]
    public async Task Resolver_RefusesAnEmployeeWithNoCompany_InsteadOfInventingDirhams()
    {
        var (db, _, _, noCompany) = await SeedAsync();
        var act = () => EmployeeCompanyCurrency.ResolveAsync(db, noCompany, default);
        await act.Should().ThrowAsync<CompanyCurrencyUnknownException>();
    }

    [Fact]
    public async Task Resolver_RefusesACompanyWithABlankCurrency()
    {
        var (db, _, inCompany, _) = await SeedAsync("  ");
        var act = () => EmployeeCompanyCurrency.ResolveAsync(db, inCompany, default);
        await act.Should().ThrowAsync<CompanyCurrencyUnknownException>();
    }

    private static MigrationImportController Controller(ZayraDbContext db, Guid tenant)
    {
        var controller = new MigrationImportController(db, new Zayra.Api.Infrastructure.Auth.Pbkdf2PasswordHasher(1_000), new Zayra.Api.Infrastructure.Audit.AuditService(db));
        var claims = new[]
        {
            new System.Security.Claims.Claim("tenant_id", tenant.ToString()),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin"),
            new System.Security.Claims.Claim("permission", "security.manage"),
        };
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "test")),
            },
        };
        return controller;
    }

    private static async Task<MigrationReconciliationDto> CommitAsync(ZayraDbContext db, Guid tenant, string section, string csv)
    {
        var result = await Controller(db, tenant).Commit(new MigrationPackageRequest($"cur-{Guid.NewGuid():N}", new Dictionary<string, string> { [section] = csv }, false), default);
        return (MigrationReconciliationDto)((Microsoft.AspNetCore.Mvc.OkObjectResult)result.Result!).Value!;
    }

    [Fact]
    public async Task BenefitsEnrollmentImport_WithoutACurrencyColumn_UsesTheEmployeesCompanyCurrency()
    {
        var (db, tenant, _, _) = await SeedAsync("SAR");
        await CommitAsync(db, tenant, "benefitsEnrollments",
            "EmployeeCode,PlanCode,PlanName,CoverageTier,EffectiveDate,EndDate,EmployeeContribution,EmployerContribution,Status,SourceSystem,SourceRecordId\n"
            + "EMP-001,MED-GOLD,Medical Gold,Family,2026-01-01,,500,1500,Active,Oracle,BEN-001\n");
        (await db.BenefitPlans.Where(p => p.TenantId == tenant && p.Code == "MED-GOLD").Select(p => p.Currency).SingleAsync()).Should().Be("SAR");
    }

    [Fact]
    public async Task BenefitsEnrollmentImport_ForAnEmployeeWithNoCompany_AndNoCurrency_IsRefusedNotWrittenInDirhams()
    {
        var (db, tenant, _, _) = await SeedAsync("SAR");
        await CommitAsync(db, tenant, "benefitsEnrollments",
            "EmployeeCode,PlanCode,PlanName,CoverageTier,EffectiveDate,EndDate,EmployeeContribution,EmployerContribution,Status,SourceSystem,SourceRecordId\n"
            + "EMP-002,MED-GOLD,Medical Gold,Family,2026-01-01,,500,1500,Active,Oracle,BEN-002\n");
        (await db.BenefitPlans.Where(p => p.TenantId == tenant && p.Code == "MED-GOLD").ToListAsync()).Should().BeEmpty("no currency is known, so no plan is written");
    }

    [Fact]
    public async Task ContractImport_WithoutACurrencyColumn_UsesTheEmployeesCompanyCurrency()
    {
        var (db, tenant, _, _) = await SeedAsync("SAR");
        await CommitAsync(db, tenant, "contracts",
            "EmployeeCode,ContractNumber,ContractType,Status,StartDate,EndDate,BasicSalary,FileUrl,Version,SourceSystem,SourceRecordId\n"
            + "EMP-001,LEG-CON-001,Employment,Active,2026-01-01,,25000,s3://legacy/c.pdf,1,Oracle,CON-001\n");
        (await db.EmployeeContracts.Where(c => c.TenantId == tenant && c.ContractNumber == "LEG-CON-001").Select(c => c.CurrencyCode).SingleAsync()).Should().Be("SAR");
    }

    [Fact]
    public async Task ContractImport_ForAnEmployeeWithNoCompany_AndNoCurrency_IsRefused()
    {
        var (db, tenant, _, _) = await SeedAsync("SAR");
        await CommitAsync(db, tenant, "contracts",
            "EmployeeCode,ContractNumber,ContractType,Status,StartDate,EndDate,BasicSalary,FileUrl,Version,SourceSystem,SourceRecordId\n"
            + "EMP-002,LEG-CON-002,Employment,Active,2026-01-01,,25000,s3://legacy/c.pdf,1,Oracle,CON-002\n");
        (await db.EmployeeContracts.Where(c => c.TenantId == tenant && c.ContractNumber == "LEG-CON-002").ToListAsync()).Should().BeEmpty();
    }
}
