using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeImportStructurePostgresTests
{
    private readonly PostgresFixture _fixture;
    public EmployeeImportStructurePostgresTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<(Guid Tenant, Company Company, Grade Grade)> Seed(ZayraDbContext db)
    {
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant, Plan = "Enterprise", Status = "Active", MaxEmployees = 300 });
        var company = new Company { TenantId = tenant, LegalNameEn = "Evostel PG A", CountryCode = "SA", Jurisdiction = "test", RegistrationNumber = "DEMO-PG-A", DefaultCurrency = "SAR", IsActive = true };
        var grade = new Grade { TenantId = tenant, Code = "G1", Name = "PG salary grade", Currency = "SAR", MinSalary = 1000, MidSalary = 7000, MaxSalary = 50000, IsActive = true };
        db.AddRange(company, grade);
        await db.SaveChangesAsync();
        db.GradePayScaleComponents.Add(new GradePayScaleComponent { TenantId = tenant, GradeId = grade.Id, ComponentCode = "BASIC", ComponentName = "Basic salary", ComponentType = "Earning", CalculationType = "Fixed", IsActive = true });
        await db.SaveChangesAsync();
        return (tenant, company, grade);
    }

    [Fact]
    public async Task TwoHundredFiftyRows_OneSharedStructure_RealUniqueIndexAndRepeatSafety()
    {
        await using var db = _fixture.CreateDb();
        var (tenant, company, _) = await Seed(db);
        var ctrl = HrmHierarchyTests.BuildImportControllerInternal(db, tenant);
        const string header = "EmployeeCode,FullName,CompanyLegalName,Grade,BasicSalary,HousingAllowance,TransportAllowance,Currency,JoiningDate,ManagerEmployeeCode,BankName\n";
        var csv = header + string.Join("\n", Enumerable.Range(1, 250).Select(i =>
            $"PG{i:D4},Synthetic Person {i},{company.LegalNameEn},G1,{5000+i},1000,500,SAR,2024-01-01,{(i == 1 ? "" : "PG0001")},Synthetic Bank"));
        var result = Assert.IsType<OkObjectResult>(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        Assert.Equal(250, JsonSerializer.SerializeToElement(result.Value).GetProperty("created").GetInt32());
        db.ChangeTracker.Clear();
        Assert.Equal(250, await db.Employees.CountAsync(e => e.TenantId == tenant));
        Assert.Equal(249, await db.Employees.CountAsync(e => e.TenantId == tenant && e.ManagerEmployeeId != null));
        var structure = Assert.Single(await db.SalaryStructures.Where(s => s.TenantId == tenant).ToListAsync());
        Assert.Equal(company.Id, structure.CompanyId);
        Assert.Equal(1, await db.SalaryComponents.CountAsync(c => c.TenantId == tenant && c.SalaryStructureId == structure.Id));
        var salaries = await db.EmployeeSalaryStructures.AsNoTracking().Where(s => s.TenantId == tenant).ToListAsync();
        Assert.Equal(250, salaries.Count);
        Assert.All(salaries, s => { Assert.Equal(structure.Id, s.SalaryStructureId); Assert.Equal("SAR", s.Currency); Assert.Equal(new DateOnly(2024, 1, 1), s.EffectiveDate); });
        Assert.Equal(Enumerable.Range(1, 250).Sum(i => 5000m + i), salaries.Sum(s => s.BasicSalary));
        Assert.Equal(250, await db.EmployeePayrollProfiles.CountAsync(p => p.TenantId == tenant));
        var retry = Assert.IsType<OkObjectResult>(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        Assert.Equal(0, JsonSerializer.SerializeToElement(retry.Value).GetProperty("created").GetInt32());
        Assert.Equal(250, await db.Employees.CountAsync(e => e.TenantId == tenant));
        Assert.Equal(250, await db.EmployeeSalaryStructures.CountAsync(s => s.TenantId == tenant));
        Assert.Equal(1, await db.SalaryStructures.CountAsync(s => s.TenantId == tenant));
    }

    [Fact]
    public async Task SameGradeAcrossCompanies_DoesNotCrossLinkSalaryStructures()
    {
        await using var db = _fixture.CreateDb();
        var (tenant, companyA, _) = await Seed(db);
        var companyB = new Company { TenantId = tenant, LegalNameEn = "Evostel PG B", CountryCode = "SA", Jurisdiction = "test", RegistrationNumber = "DEMO-PG-B", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.Add(companyB);
        await db.SaveChangesAsync();
        var ctrl = HrmHierarchyTests.BuildImportControllerInternal(db, tenant);
        var csv = "EmployeeCode,FullName,CompanyLegalName,Grade,BasicSalary,Currency,JoiningDate\n"
            + $"A1,Synthetic A1,{companyA.LegalNameEn},G1,5000,SAR,2024-01-01\n"
            + $"A2,Synthetic A2,{companyA.LegalNameEn},G1,5100,SAR,2024-01-01\n"
            + $"B1,Synthetic B1,{companyB.LegalNameEn},G1,5200,SAR,2024-01-01\n"
            + $"B2,Synthetic B2,{companyB.LegalNameEn},G1,5300,SAR,2024-01-01\n";
        var result = Assert.IsType<OkObjectResult>(await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        Assert.Equal(4, JsonSerializer.SerializeToElement(result.Value).GetProperty("created").GetInt32());
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.SalaryStructures.CountAsync(s => s.TenantId == tenant));
        var linked = await (from a in db.EmployeeSalaryStructures
            join e in db.Employees on a.EmployeeId equals e.Id
            join s in db.SalaryStructures on a.SalaryStructureId equals s.Id
            where a.TenantId == tenant
            select new { EmployeeCompany = e.CompanyId, StructureCompany = s.CompanyId }).ToListAsync();
        Assert.Equal(4, linked.Count);
        Assert.All(linked, x => Assert.Equal(x.EmployeeCompany, x.StructureCompany));
    }
}
