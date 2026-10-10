using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// P0: the bulk organization-structure import wrote companies, branches, cost centres, departments, grades and
/// designations straight to the database. It skipped every gate the Setup forms apply — the plan's company limit,
/// the single-company account type, platform-controlled and draft-approval company creation, registration-number
/// uniqueness, the ISO country check, the code normalisation, the branch-stays-in-its-company rule — and wrote one
/// bulk audit row, after its transaction, instead of one per entity. On real PostgreSQL; asserts on persisted rows.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class OrganizationStructureImportGateTests
{
    private readonly PostgresFixture _fx;
    public OrganizationStructureImportGateTests(PostgresFixture fx) => _fx = fx;

    private const string CompaniesHeader = "LegalNameEn,CountryCode,RegistrationNumber,DefaultCurrency\n";

    private async Task<(Guid Tenant, Guid Existing)> SeedAsync(
        string accountType = TenantAccountTypes.Group, string mode = CompanyCreationModes.GroupSelfServiceWithinLimit, int maxCompanies = 10)
    {
        await using var db = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var t = await db.Tenants.SingleAsync(x => x.Id == tenant);
        t.AccountType = accountType;
        t.CompanyCreationMode = mode;
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant, Plan = "Enterprise", Status = "Active", MaxCompanies = maxCompanies, MaxEmployees = 100 });
        var existing = new Company { TenantId = tenant, LegalNameEn = "Existing Co", CountryCode = "SA", Jurisdiction = "SA", RegistrationNumber = "REG-EXISTING", DefaultCurrency = "SAR", IsActive = true };
        db.Companies.Add(existing);
        await db.SaveChangesAsync();
        return (tenant, existing.Id);
    }

    private static OrganizationStructureImportController Controller(ZayraDbContext db, Guid tenant) => new(db, new AuditService(db))
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenant.ToString()), new Claim("sub", Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Admin"),
                    new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
                }, "Test")),
            },
        },
    };

    private static OrganizationStructureImportRequest Companies(string rows, string? branches = null) => new(
        CompaniesCsv: CompaniesHeader + rows, BranchesCsv: branches, CostCentersCsv: null, DepartmentsCsv: null,
        GradesCsv: null, GradePayComponentsCsv: null, DesignationsCsv: null);

    private async Task<int> CompanyCount(Guid tenant)
    {
        await using var verify = _fx.CreateDb();
        return await verify.Companies.IgnoreQueryFilters().CountAsync(c => c.TenantId == tenant);
    }

    private static IReadOnlyList<string> Errors(ActionResult<OrganizationStructureImportResult> result)
    {
        var refused = Assert.IsType<UnprocessableEntityObjectResult>(result.Result);
        var body = Assert.IsType<OrganizationStructureImportResult>(refused.Value);
        return body.Rows.Where(r => r.Status == ImportRowStatus.Error).SelectMany(r => r.Errors).ToList();
    }

    [Fact]
    public async Task ASingleCompanyAccount_CannotAddASecondCompany_ByImport()
    {
        var (tenant, _) = await SeedAsync(accountType: TenantAccountTypes.SingleCompany);
        await using var db = _fx.CreateDb();
        var result = await Controller(db, tenant).Commit(Companies("Second Co,SA,REG-2,SAR\n"), CancellationToken.None);

        Assert.Equal(1, await CompanyCount(tenant));
        Assert.Contains(Errors(result), e => e.Contains("single-company account"));
    }

    [Fact]
    public async Task ThePlansCompanyLimit_CountsEveryNewCompanyInTheFile_AndRefusesTheWholeFile()
    {
        var (tenant, _) = await SeedAsync(maxCompanies: 2);
        await using var db = _fx.CreateDb();
        var result = await Controller(db, tenant).Commit(
            Companies("Second Co,SA,REG-2,SAR\nThird Co,SA,REG-3,SAR\n"), CancellationToken.None);

        Assert.Equal(1, await CompanyCount(tenant)); // all or nothing: not even the one that fit
        Assert.Contains(Errors(result), e => e.Contains("Your plan allows up to 2"));
    }

    [Fact]
    public async Task APlatformControlledTenant_CannotCreateCompanies_ByImport()
    {
        var (tenant, _) = await SeedAsync(mode: CompanyCreationModes.PlatformControlled);
        await using var db = _fx.CreateDb();
        var result = await Controller(db, tenant).Commit(Companies("Second Co,SA,REG-2,SAR\n"), CancellationToken.None);

        Assert.Equal(1, await CompanyCount(tenant));
        Assert.Contains(Errors(result), e => e.Contains("managed by the platform"));
    }

    [Fact]
    public async Task DraftApprovalMode_ImportsANewCompanyAsAnInactiveDraft_LikeTheForm()
    {
        var (tenant, _) = await SeedAsync(mode: CompanyCreationModes.GroupDraftPlatformApproval);
        await using var db = _fx.CreateDb();
        Assert.IsType<OkObjectResult>((await Controller(db, tenant).Commit(Companies("Second Co,SA,REG-2,SAR\n"), CancellationToken.None)).Result);

        await using var verify = _fx.CreateDb();
        var created = await verify.Companies.IgnoreQueryFilters().SingleAsync(c => c.TenantId == tenant && c.LegalNameEn == "Second Co");
        Assert.False(created.IsActive);
        Assert.Equal(CompanyApprovalStatuses.Draft, created.ApprovalStatus);
    }

    [Fact]
    public async Task ADuplicateRegistrationNumber_OrAnUnrecognisedCountry_IsRefused_AndNothingIsWritten()
    {
        var (tenant, _) = await SeedAsync();
        await using var db = _fx.CreateDb();
        var result = await Controller(db, tenant).Commit(
            Companies("Copy Co,SA,REG-EXISTING,SAR\nFree Text Co,Saudi,REG-9,SAR\nFine Co,SA,REG-10,SAR\n"), CancellationToken.None);

        Assert.Equal(1, await CompanyCount(tenant));
        var errors = Errors(result);
        Assert.Contains(errors, e => e.Contains("registration number already exists"));
        Assert.Contains(errors, e => e.Contains("Unrecognized country code 'Saudi'"));
    }

    [Fact]
    public async Task EachCompanyCanUseTheSameBranchCode_ByImport()
    {
        var (tenant, existing) = await SeedAsync();
        await using (var seed = _fx.CreateDb())
        {
            seed.Companies.Add(new Company { TenantId = tenant, LegalNameEn = "Other Co", CountryCode = "SA", Jurisdiction = "SA", RegistrationNumber = "REG-OTHER", DefaultCurrency = "SAR", IsActive = true });
            seed.Branches.Add(new Branch { TenantId = tenant, CompanyId = existing, Code = "HQ", NameEn = "Head Office" });
            await seed.SaveChangesAsync();
        }
        await using var db = _fx.CreateDb();
        var result = await Controller(db, tenant).Commit(new OrganizationStructureImportRequest(
            CompaniesCsv: null, BranchesCsv: "CompanyLegalName,Code,NameEn\nOther Co,HQ,Head Office\n", CostCentersCsv: null,
            DepartmentsCsv: null, GradesCsv: null, GradePayComponentsCsv: null, DesignationsCsv: null), CancellationToken.None);

        await using var verify = _fx.CreateDb();
        Assert.IsType<OkObjectResult>(result.Result);
        var branches = await verify.Branches.IgnoreQueryFilters().Where(b => b.TenantId == tenant && b.Code == "HQ").ToListAsync();
        Assert.Equal(2, branches.Count);
        Assert.Contains(branches, branch => branch.CompanyId == existing);
        Assert.Contains(branches, branch => branch.CompanyId != existing);
    }

    [Fact]
    public async Task ImportedCodesAreNormalisedLikeTheForm_AndEveryEntityGetsItsOwnAuditRow_InTheSameTransaction()
    {
        var (tenant, _) = await SeedAsync();
        await using var db = _fx.CreateDb();
        var result = await Controller(db, tenant).Commit(new OrganizationStructureImportRequest(
            CompaniesCsv: CompaniesHeader + "New Co,SA,REG-NEW,SAR\n",
            BranchesCsv: "CompanyLegalName,Code,NameEn\nNew Co,hq-1,Head Office\n",
            CostCentersCsv: "CompanyLegalName,Code,Name\nNew Co,cc-ops,Operations\n",
            DepartmentsCsv: "CompanyLegalName,Code,NameEn\nNew Co,ops,Operations\n",
            GradesCsv: "Code,Name,MinSalary,MidSalary,MaxSalary,Currency\ng1,Grade 1,5000,7500,10000,SAR\n",
            GradePayComponentsCsv: null,
            DesignationsCsv: "Code,TitleEn,DepartmentCode,GradeCode\nops-off,Operations Officer,ops,g1\n"), CancellationToken.None);
        Assert.IsType<OkObjectResult>(result.Result);

        await using var verify = _fx.CreateDb();
        var branch = await verify.Branches.IgnoreQueryFilters().SingleAsync(b => b.TenantId == tenant);
        var cc = await verify.CostCenters.IgnoreQueryFilters().SingleAsync(b => b.TenantId == tenant);
        var dept = await verify.Departments.IgnoreQueryFilters().SingleAsync(b => b.TenantId == tenant);
        var grade = await verify.Grades.IgnoreQueryFilters().SingleAsync(b => b.TenantId == tenant);
        var desig = await verify.Designations.IgnoreQueryFilters().SingleAsync(b => b.TenantId == tenant);
        var company = await verify.Companies.IgnoreQueryFilters().SingleAsync(c => c.TenantId == tenant && c.LegalNameEn == "New Co");
        Assert.Equal(new[] { "HQ-1", "CC-OPS", "OPS", "G1", "OPS-OFF" }, new[] { branch.Code, cc.Code, dept.Code, grade.Code, desig.Code });

        var audits = await verify.AuditLogs.IgnoreQueryFilters().Where(a => a.TenantId == tenant).ToListAsync();
        foreach (var (action, id) in new[]
                 {
                     ("organization.company_created", company.Id), ("organization.branch_created", branch.Id),
                     ("organization.cost_center_created", cc.Id), ("organization.department_created", dept.Id),
                     ("organization.grade_created", grade.Id), ("organization.designation_created", desig.Id),
                 })
            Assert.Contains(audits, a => a.Action == action && a.EntityId == id.ToString() && a.Metadata!.Contains("organization_structure_import"));
    }

    [Fact]
    public async Task ACompanyCreatedInTheSameFile_CanUseAnExistingBranchCode()
    {
        var (tenant, existing) = await SeedAsync();
        await using (var seed = _fx.CreateDb())
        {
            seed.Branches.Add(new Branch { TenantId = tenant, CompanyId = existing, Code = "HQ", NameEn = "Head Office" });
            await seed.SaveChangesAsync();
        }
        var request = new OrganizationStructureImportRequest(
            CompaniesCsv: CompaniesHeader + "New Co,SA,REG-NEW,SAR\n", BranchesCsv: "CompanyLegalName,Code,NameEn\nNew Co,HQ,Head Office\n",
            CostCentersCsv: null, DepartmentsCsv: null, GradesCsv: null, GradePayComponentsCsv: null, DesignationsCsv: null);

        await using (var db = _fx.CreateDb())
        {
            var preview = Assert.IsType<OkObjectResult>((await Controller(db, tenant).Preview(request, CancellationToken.None)).Result);
            var result = Assert.IsType<OrganizationStructureImportResult>(preview.Value);
            Assert.False(result.HasBlockingErrors);
        }
        await using (var db = _fx.CreateDb())
            Assert.IsType<OkObjectResult>((await Controller(db, tenant).Commit(request, CancellationToken.None)).Result);
        Assert.Equal(2, await CompanyCount(tenant));
        await using var verify = _fx.CreateDb();
        Assert.Equal(2, await verify.Branches.IgnoreQueryFilters().CountAsync(branch => branch.TenantId == tenant && branch.Code == "HQ"));
    }
}
