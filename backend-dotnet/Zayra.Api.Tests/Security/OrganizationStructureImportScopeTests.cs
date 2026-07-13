using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

[Trait("Category", "Integration")]
[Collection("Integration")]
public class OrganizationStructureImportScopeTests
{
    private readonly PostgresFixture _fx;
    public OrganizationStructureImportScopeTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task ScopedHr_CannotCommitGroupMasterDataPackage()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { TenantId = tenantId, LegalNameEn = "Alpha Legal", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var controller = CreateController(db, ScopedHr(tenantId, company.Id));

        var result = await controller.Commit(new OrganizationStructureImportRequest(
            CompaniesCsv: "LegalNameEn,TradeName,CountryCode,Jurisdiction,RegistrationNumber,TaxNumber,DefaultCurrency,IsActive\nBeta Legal,Beta,SA,SA,CR-B,TAX-B,SAR,true",
            BranchesCsv: null,
            CostCentersCsv: null,
            DepartmentsCsv: null,
            GradesCsv: "Code,Name,Band,Level,MinSalary,MidSalary,MaxSalary,Currency,IsActive\nG5,Grade 5,Management,5,20000,25000,30000,SAR,true",
            GradePayComponentsCsv: null,
            DesignationsCsv: null), CancellationToken.None);

        var blocked = result.Result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        var payload = blocked.Value.Should().BeOfType<OrganizationStructureImportResult>().Subject;
        payload.HasBlockingErrors.Should().BeTrue();
        payload.Rows.SelectMany(x => x.Errors).Should().Contain(e => e.Contains("Only group-scope"));
    }

    [Fact]
    public async Task ScopedHr_CannotCommitSiblingCompanyBranch()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var alpha = new Company { TenantId = tenantId, LegalNameEn = "Alpha Legal", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        var beta = new Company { TenantId = tenantId, LegalNameEn = "Beta Legal", CountryCode = "SA", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.AddRange(alpha, beta);
        await db.SaveChangesAsync();
        var controller = CreateController(db, ScopedHr(tenantId, alpha.Id));

        var result = await controller.Commit(new OrganizationStructureImportRequest(
            CompaniesCsv: null,
            BranchesCsv: "CompanyLegalName,Code,NameEn,CountryCode,City,IsHeadOffice,IsActive\nBeta Legal,BETA-HQ,Beta HQ,SA,Jeddah,true,true",
            CostCentersCsv: null,
            DepartmentsCsv: null,
            GradesCsv: null,
            GradePayComponentsCsv: null,
            DesignationsCsv: null), CancellationToken.None);

        var blocked = result.Result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        var payload = blocked.Value.Should().BeOfType<OrganizationStructureImportResult>().Subject;
        payload.HasBlockingErrors.Should().BeTrue();
        payload.Rows.SelectMany(x => x.Errors).Should().Contain(e => e.Contains("outside the caller's entity scope"));
    }

    [Fact]
    public async Task GroupHr_CommitWritesAuditLog()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var controller = CreateController(db, GroupHr(tenantId));

        var result = await controller.Commit(new OrganizationStructureImportRequest(
            CompaniesCsv: "LegalNameEn,TradeName,CountryCode,Jurisdiction,RegistrationNumber,TaxNumber,DefaultCurrency,IsActive\nGamma Legal,Gamma,SA,SA,CR-G,TAX-G,SAR,true",
            BranchesCsv: null,
            CostCentersCsv: null,
            DepartmentsCsv: null,
            GradesCsv: null,
            GradePayComponentsCsv: null,
            DesignationsCsv: null), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        db.AuditLogs.Should().Contain(x =>
            x.TenantId == tenantId &&
            x.Action == "setup.organization_structure_import_committed" &&
            x.EntityName == "OrganizationStructureImport");
    }

    private static OrganizationStructureImportController CreateController(ZayraDbContext db, ClaimsPrincipal principal)
    {
        var controller = new OrganizationStructureImportController(db, new AuditService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    private static ClaimsPrincipal ScopedHr(Guid tenantId, Guid companyId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "HR Manager"),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { companyId } })),
        }, "Test"));

    private static ClaimsPrincipal GroupHr(Guid tenantId) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        }, "Test"));
}
