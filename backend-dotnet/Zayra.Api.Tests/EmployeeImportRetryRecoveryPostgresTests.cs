using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class EmployeeImportRetryRecoveryPostgresTests
{
    private readonly PostgresFixture _fixture;
    public EmployeeImportRetryRecoveryPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task RetryExistingCodes_RepairsOnlyMissingPayrollAndHierarchy_WithoutDuplicatingPeople()
    {
        await using var db = _fixture.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        db.TenantSubscriptions.Add(new TenantSubscription
        {
            TenantId = tenant, Plan = "Enterprise", Status = "Active", MaxEmployees = 300
        });
        var company = new Company
        {
            TenantId = tenant, LegalNameEn = "Retry Repair Co", CountryCode = "SA",
            Jurisdiction = "test", RegistrationNumber = "RETRY-REPAIR", DefaultCurrency = "SAR", IsActive = true
        };
        var grade = new Grade
        {
            TenantId = tenant, Code = "G1", Name = "Repair Grade", Currency = "SAR",
            MinSalary = 1, MaxSalary = 50000, IsActive = true
        };
        db.AddRange(company, grade);
        await db.SaveChangesAsync();
        db.GradePayScaleComponents.Add(new GradePayScaleComponent
        {
            TenantId = tenant, GradeId = grade.Id, ComponentCode = "BASIC",
            ComponentName = "Basic salary", ComponentType = "Earning",
            CalculationType = "Fixed", IsActive = true
        });
        var manager = new Employee
        {
            TenantId = tenant, EmployeeCode = "RR001", FullName = "Retry Manager",
            CompanyId = company.Id, GradeId = grade.Id, Grade = "G1",
            CountryCode = "SA", Nationality = "Saudi", Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var worker = new Employee
        {
            TenantId = tenant, EmployeeCode = "RR002", FullName = "Retry Worker",
            CompanyId = company.Id, GradeId = grade.Id, Grade = "G1",
            CountryCode = "SA", Nationality = "Saudi", Status = "Active",
            JoiningDate = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        db.AddRange(manager, worker);
        await db.SaveChangesAsync();

        var csv =
            "EmployeeCode,FullName,CompanyLegalName,Grade,BasicSalary,HousingAllowance,Currency,JoiningDate,ManagerEmployeeCode,BankName,IBAN,MolId\n"
            + "RR001,Retry Manager,Retry Repair Co,G1,7000,1750,SAR,2024-01-01,,Synthetic Bank,SA0380000000608010167519,MOL-RR001\n"
            + "RR002,Retry Worker,Retry Repair Co,G1,6000,1500,SAR,2024-02-01,RR001,Synthetic Bank,SA0380000000608010167519,MOL-RR002\n";

        var ctrl = HrmHierarchyTests.BuildImportControllerInternal(db, tenant);
        var preview = Assert.IsType<OkObjectResult>(
            await ctrl.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        var previewJson = JsonSerializer.SerializeToElement(preview.Value);
        Assert.Equal(2, previewJson.GetProperty("wouldRepair").GetInt32());
        Assert.Equal(0, previewJson.GetProperty("wouldSkip").GetInt32());
        Assert.All(previewJson.GetProperty("rows").EnumerateArray(),
            row => Assert.Equal("WillRepair", row.GetProperty("status").GetString()));

        var result = Assert.IsType<OkObjectResult>(
            await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        var json = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal(0, json.GetProperty("created").GetInt32());
        Assert.Equal(2, json.GetProperty("repaired").GetInt32());
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Employees.CountAsync(e => e.TenantId == tenant));
        Assert.Equal(2, await db.EmployeePayrollProfiles.CountAsync(p => p.TenantId == tenant));
        Assert.Equal(2, await db.EmployeeSalaryStructures.CountAsync(s => s.TenantId == tenant && s.IsActive));
        Assert.Equal(1, await db.SalaryStructures.CountAsync(s => s.TenantId == tenant));

        var managerReloaded = await db.Employees.SingleAsync(e => e.TenantId == tenant && e.EmployeeCode == "RR001");
        var workerReloaded = await db.Employees.SingleAsync(e => e.TenantId == tenant && e.EmployeeCode == "RR002");
        Assert.Equal(managerReloaded.Id, workerReloaded.ManagerEmployeeId);

        var secondPreview = Assert.IsType<OkObjectResult>(
            await ctrl.ImportPreview(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        var secondPreviewJson = JsonSerializer.SerializeToElement(secondPreview.Value);
        Assert.Equal(0, secondPreviewJson.GetProperty("wouldRepair").GetInt32());
        Assert.Equal(2, secondPreviewJson.GetProperty("wouldSkip").GetInt32());

        var second = Assert.IsType<OkObjectResult>(
            await ctrl.Import(new EmployeesController.ImportEmployeesRequest(csv), CancellationToken.None));
        var secondJson = JsonSerializer.SerializeToElement(second.Value);
        Assert.Equal(0, secondJson.GetProperty("created").GetInt32());
        Assert.Equal(0, secondJson.GetProperty("repaired").GetInt32());

        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Employees.CountAsync(e => e.TenantId == tenant));
        Assert.Equal(2, await db.EmployeePayrollProfiles.CountAsync(p => p.TenantId == tenant));
        Assert.Equal(2, await db.EmployeeSalaryStructures.CountAsync(s => s.TenantId == tenant));
        Assert.Equal(1, await db.SalaryStructures.CountAsync(s => s.TenantId == tenant));
    }
}
