using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Common.Import;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The branch list is scoped to the caller's companies the same way the company list is, and each
/// branch carries its company's legal name so the Setup screen never has to print a CompanyId.
/// </summary>
public class BranchCompanyScopeTests
{
    private static ZayraDbContext Db() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record World(Guid TenantId, Company A, Company B, Branch BranchA, Branch BranchB, Branch OtherTenantBranch);

    private static async Task<World> Seed(ZayraDbContext db)
    {
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var a = new Company { TenantId = tenant, LegalNameEn = "Alpha Trading", RegistrationNumber = "A-1", IsActive = true };
        var b = new Company { TenantId = tenant, LegalNameEn = "Beta Contracting", RegistrationNumber = "B-1", IsActive = true };
        var foreign = new Company { TenantId = otherTenant, LegalNameEn = "Foreign Co", RegistrationNumber = "F-1", IsActive = true };
        var branchA = new Branch { TenantId = tenant, CompanyId = a.Id, Code = "A1", NameEn = "Alpha HQ" };
        var branchB = new Branch { TenantId = tenant, CompanyId = b.Id, Code = "B1", NameEn = "Beta HQ" };
        var foreignBranch = new Branch { TenantId = otherTenant, CompanyId = foreign.Id, Code = "F1", NameEn = "Foreign HQ" };
        db.AddRange(a, b, foreign, branchA, branchB, foreignBranch);
        await db.SaveChangesAsync();
        return new World(tenant, a, b, branchA, branchB, foreignBranch);
    }

    private static DefaultHttpContext Caller(Guid tenantId, Guid? selectedCompany = null, params Guid[] companyScope)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, "HR Manager"),
            new("permission", "organization.read"),
        };
        if (companyScope.Length == 0) claims.Add(new Claim("is_group_scope", "true"));
        else claims.AddRange(companyScope.Select(c => new Claim("entity_access", JsonSerializer.Serialize(new { c, r = "HR Manager" }))));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        if (selectedCompany is not null) http.Request.Headers[ZayraDbContext.CompanySelectionHeader] = selectedCompany.ToString();
        return http;
    }

    private static BranchesController Branches(ZayraDbContext db, DefaultHttpContext http) =>
        new(new OrganizationSetupService(db, new AuditService(db)), db) { ControllerContext = new ControllerContext { HttpContext = http } };

    private static async Task<PagedResult<BranchDto>> List(BranchesController c, Guid? companyId = null) =>
        (PagedResult<BranchDto>)((OkObjectResult)(await c.Search(companyId, 1, 100, CancellationToken.None)).Result!).Value!;

    [Fact]
    public async Task CompanyScopedCaller_ListsOnlyTheirCompaniesBranches()
    {
        await using var db = Db();
        var w = await Seed(db);

        var page = await List(Branches(db, Caller(w.TenantId, null, w.A.Id)));
        page.Items.Select(x => x.Id).Should().BeEquivalentTo(new[] { w.BranchA.Id });
        page.Total.Should().Be(1, "the total must count only the branches the caller may see");

        (await List(Branches(db, Caller(w.TenantId, null, w.A.Id)), w.B.Id)).Items
            .Should().BeEmpty("filtering by a company the caller does not hold returns nothing");

        (await Branches(db, Caller(w.TenantId, null, w.A.Id)).Get(w.BranchB.Id, CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>();
        (await Branches(db, Caller(w.TenantId, null, w.A.Id)).Get(w.BranchA.Id, CancellationToken.None)).Result
            .Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GroupCaller_ListsEveryBranchOfTheTenant_WithCompanyNames()
    {
        await using var db = Db();
        var w = await Seed(db);

        var page = await List(Branches(db, Caller(w.TenantId)));
        page.Items.Select(x => (x.Id, x.CompanyName)).Should().BeEquivalentTo(new[]
        {
            (w.BranchA.Id, "Alpha Trading"),
            (w.BranchB.Id, "Beta Contracting"),
        });
    }

    [Fact]
    public async Task GroupCaller_WithCompanySelected_ListsThatCompanysBranches_LikeTheCompanyList()
    {
        await using var db = Db();
        var w = await Seed(db);

        var companies = (PagedResult<CompanyDto>)((OkObjectResult)(await new CompaniesController(
                new OrganizationSetupService(db, new AuditService(db)), db)
            { ControllerContext = new ControllerContext { HttpContext = Caller(w.TenantId, w.A.Id) } }
            .Search(1, 100, CancellationToken.None)).Result!).Value!;
        var branches = await List(Branches(db, Caller(w.TenantId, w.A.Id)));

        branches.Items.Select(x => x.CompanyId).Should().OnlyContain(id => companies.Items.Select(c => c.Id).Contains(id),
            "every listed branch's company is one the company list also returns, so none renders without a name");
        branches.Items.Single().CompanyName.Should().Be("Alpha Trading");
    }

    [Fact]
    public async Task AnotherTenantsBranches_NeverAppear()
    {
        await using var db = Db();
        var w = await Seed(db);

        (await List(Branches(db, Caller(w.TenantId)))).Items.Select(x => x.Id).Should().NotContain(w.OtherTenantBranch.Id);
        (await List(Branches(db, Caller(w.TenantId, null, w.OtherTenantBranch.CompanyId)))).Items
            .Should().BeEmpty("a company grant naming another tenant's company does not reach across tenants");
        (await Branches(db, Caller(w.TenantId)).Get(w.OtherTenantBranch.Id, CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task OrganizationBranchesEndpoint_AppliesTheSameCompanyScope()
    {
        await using var db = Db();
        var w = await Seed(db);
        var c = new OrganizationController(new OrganizationSetupService(db, new AuditService(db)), db)
        { ControllerContext = new ControllerContext { HttpContext = Caller(w.TenantId, null, w.B.Id) } };

        var page = (PagedResult<BranchDto>)((OkObjectResult)(await c.Branches(null, 1, 100, CancellationToken.None)).Result!).Value!;
        page.Items.Select(x => x.Id).Should().BeEquivalentTo(new[] { w.BranchB.Id });
        page.Items.Single().CompanyName.Should().Be("Beta Contracting");
    }

    [Fact]
    public async Task Export_ContainsOnlyTheCallersCompaniesBranches()
    {
        await using var db = Db();
        var w = await Seed(db);

        var file = (FileContentResult)await Branches(db, Caller(w.TenantId, null, w.A.Id)).Export(CancellationToken.None);
        var csv = Encoding.UTF8.GetString(file.FileContents);
        csv.Should().Contain("Alpha HQ").And.NotContain("Beta HQ").And.NotContain("Foreign HQ");
    }

    // ── Branch writes: scoped to the caller's companies ───────────────────────────────────────

    private static BranchRequest Req(Guid companyId, string code, string name = "Branch") =>
        new(companyId, code, name, null, "SA", "Riyadh", null, null, "Asia/Riyadh", null);

    private static async Task<int> LiveBranches(ZayraDbContext db, Guid tenantId) =>
        await db.Branches.CountAsync(b => b.TenantId == tenantId && !b.IsDeleted);

    [Fact]
    public async Task CompanyScopedCaller_CannotCreateUpdateOrDeleteAnotherCompanysBranch()
    {
        await using var db = Db();
        var w = await Seed(db);
        DefaultHttpContext ScopedA(Guid? selected = null) => Caller(w.TenantId, selected, w.A.Id);

        (await Branches(db, ScopedA()).Create(Req(w.B.Id, "B2"), CancellationToken.None)).Result
            .Should().BeOfType<ForbidResult>("creating under a company outside the grant");
        (await Branches(db, ScopedA()).Update(w.BranchA.Id, Req(w.B.Id, "A1", "Alpha HQ"), CancellationToken.None)).Result
            .Should().BeOfType<ForbidResult>("moving an own branch INTO another company");
        (await Branches(db, ScopedA()).Update(w.BranchB.Id, Req(w.A.Id, "B1", "Beta HQ"), CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>("moving another company's branch OUT of it");
        (await Branches(db, ScopedA()).Update(w.BranchB.Id, Req(w.B.Id, "B1", "Renamed"), CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>("editing another company's branch in place");
        (await Branches(db, ScopedA(w.A.Id)).Delete(w.BranchB.Id, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();

        db.ChangeTracker.Clear();
        (await db.Branches.SingleAsync(b => b.Id == w.BranchA.Id)).CompanyId.Should().Be(w.A.Id);
        var beta = await db.Branches.SingleAsync(b => b.Id == w.BranchB.Id);
        beta.CompanyId.Should().Be(w.B.Id);
        beta.NameEn.Should().Be("Beta HQ");
        beta.IsDeleted.Should().BeFalse();
        (await LiveBranches(db, w.TenantId)).Should().Be(2);
    }

    [Fact]
    public async Task CompanyScopedCaller_CanMaintainOwnCompanysBranches()
    {
        await using var db = Db();
        var w = await Seed(db);

        (await Branches(db, Caller(w.TenantId, null, w.A.Id)).Create(Req(w.A.Id, "A2", "Alpha East"), CancellationToken.None)).Result
            .Should().BeOfType<CreatedAtActionResult>();
        (await Branches(db, Caller(w.TenantId, null, w.A.Id)).Update(w.BranchA.Id, Req(w.A.Id, "A1", "Alpha Head Office"), CancellationToken.None)).Result
            .Should().BeOfType<OkObjectResult>();
        (await Branches(db, Caller(w.TenantId, null, w.A.Id)).Delete(w.BranchA.Id, CancellationToken.None))
            .Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task GroupCaller_WritesAnyCompanysBranches_WithOrWithoutCompanySelected()
    {
        await using var db = Db();
        var w = await Seed(db);

        foreach (var (selected, code) in new[] { ((Guid?)null, "B2"), (w.A.Id, "B3") })
        {
            (await Branches(db, Caller(w.TenantId, selected)).Create(Req(w.B.Id, code), CancellationToken.None)).Result
                .Should().BeOfType<CreatedAtActionResult>();
        }
        (await Branches(db, Caller(w.TenantId, w.A.Id)).Update(w.BranchB.Id, Req(w.B.Id, "B1", "Beta Head Office"), CancellationToken.None)).Result
            .Should().BeOfType<OkObjectResult>("the switcher narrows reads, not a group grant's writes");
        (await Branches(db, Caller(w.TenantId, w.A.Id)).Delete(w.BranchB.Id, CancellationToken.None))
            .Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task AnotherTenantsBranch_IsNotFound_ForWrites()
    {
        await using var db = Db();
        var w = await Seed(db);

        (await Branches(db, Caller(w.TenantId)).Update(w.OtherTenantBranch.Id, Req(w.A.Id, "F1"), CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>();
        (await Branches(db, Caller(w.TenantId)).Delete(w.OtherTenantBranch.Id, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
        db.ChangeTracker.Clear();
        var foreign = await db.Branches.SingleAsync(b => b.Id == w.OtherTenantBranch.Id);
        foreign.IsDeleted.Should().BeFalse();
        foreign.CompanyId.Should().NotBe(w.A.Id);
    }

    [Fact]
    public async Task CompanyScopedImport_RefusesRowsForOtherCompanies_AndWritesNothingForThem()
    {
        await using var db = Db();
        var w = await Seed(db);
        var csv = string.Join("\n",
            "CompanyLegalName,Code,NameEn,NameAr,CountryCode,City,AddressLine1,AddressLine2,TimeZoneId,LaborOfficeCode,IsHeadOffice,IsActive",
            "Alpha Trading,A2,Alpha East,,SA,Riyadh,,,Asia/Riyadh,,false,true",
            "Beta Contracting,B2,Beta East,,SA,Jeddah,,,Asia/Riyadh,,false,true",
            "Beta Contracting,B1,Beta Renamed,,SA,Jeddah,,,Asia/Riyadh,,false,true");

        var preview = (ImportPreviewResult)((OkObjectResult)await Branches(db, Caller(w.TenantId, null, w.A.Id))
            .ImportPreview(new BranchImportRequest(csv), CancellationToken.None)).Value!;
        preview.Rows.Where(r => r.Status == ImportRowStatus.Error).Select(r => r.EntityCode).Should().BeEquivalentTo(new[] { "B2", "B1" });

        var result = (ImportCommitResult)((OkObjectResult)await Branches(db, Caller(w.TenantId, null, w.A.Id))
            .Import(new BranchImportRequest(csv), CancellationToken.None)).Value!;
        result.Created.Should().Be(1);
        result.Updated.Should().Be(0);
        result.Rows.Where(r => r.Status == ImportRowStatus.Error)
            .Should().HaveCount(2).And.OnlyContain(r => r.Errors.Any(e => e.Contains("not one of the companies you can manage")));

        db.ChangeTracker.Clear();
        (await db.Branches.AnyAsync(b => b.TenantId == w.TenantId && b.Code == "B2")).Should().BeFalse();
        (await db.Branches.SingleAsync(b => b.Id == w.BranchB.Id)).NameEn.Should().Be("Beta HQ");
        (await db.Branches.AnyAsync(b => b.TenantId == w.TenantId && b.Code == "A2" && b.CompanyId == w.A.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task CompanyScopedImport_DoesNotNameTheCompanyThatOwnsACollidingCode()
    {
        await using var db = Db();
        var w = await Seed(db);
        var csv = "CompanyLegalName,Code,NameEn,CountryCode,City,TimeZoneId\nAlpha Trading,B1,Clash,SA,Riyadh,Asia/Riyadh";

        var result = (ImportCommitResult)((OkObjectResult)await Branches(db, Caller(w.TenantId, null, w.A.Id))
            .Import(new BranchImportRequest(csv), CancellationToken.None)).Value!;
        var row = result.Rows.Single();
        row.Status.Should().Be(ImportRowStatus.Error);
        string.Join(" ", row.Errors).Should().Contain("another company").And.NotContain("Beta Contracting");
    }

    [Fact]
    public async Task GroupImport_WithCompanySelected_StillImportsIntoEveryCompany()
    {
        await using var db = Db();
        var w = await Seed(db);
        var csv = "CompanyLegalName,Code,NameEn,CountryCode,City,TimeZoneId\nAlpha Trading,A2,Alpha East,SA,Riyadh,Asia/Riyadh\nBeta Contracting,B2,Beta East,SA,Jeddah,Asia/Riyadh";

        var result = (ImportCommitResult)((OkObjectResult)await Branches(db, Caller(w.TenantId, w.A.Id))
            .Import(new BranchImportRequest(csv), CancellationToken.None)).Value!;
        result.Created.Should().Be(2);
        result.Skipped.Should().Be(0);
    }

    // ── /api/organization/companies follows the same scope as /api/companies ─────────────────

    private static async Task<PagedResult<CompanyDto>> OrgCompanies(ZayraDbContext db, DefaultHttpContext http) =>
        (PagedResult<CompanyDto>)((OkObjectResult)(await new OrganizationController(new OrganizationSetupService(db, new AuditService(db)), db)
            { ControllerContext = new ControllerContext { HttpContext = http } }
            .Companies(1, 100, CancellationToken.None)).Result!).Value!;

    [Fact]
    public async Task OrganizationCompanies_CompanyScopedCaller_ListsOnlyTheirCompanies()
    {
        await using var db = Db();
        var w = await Seed(db);
        var page = await OrgCompanies(db, Caller(w.TenantId, null, w.B.Id));
        page.Items.Select(c => c.Id).Should().BeEquivalentTo(new[] { w.B.Id });
        page.Total.Should().Be(1);
    }

    [Fact]
    public async Task OrganizationCompanies_GroupCaller_ListsTheTenantsCompanies_AndNoOtherTenants()
    {
        await using var db = Db();
        var w = await Seed(db);
        (await OrgCompanies(db, Caller(w.TenantId))).Items.Select(c => c.Id).Should().BeEquivalentTo(new[] { w.A.Id, w.B.Id });
        (await OrgCompanies(db, Caller(w.TenantId, w.A.Id))).Items.Select(c => c.Id)
            .Should().BeEquivalentTo(new[] { w.A.Id }, "with a company selected it matches /api/companies");
        (await OrgCompanies(db, Caller(w.TenantId, null, w.OtherTenantBranch.CompanyId))).Items.Should().BeEmpty();
    }

    // ── Single-company read, create and update: one rule for both company endpoints ──────────

    private static CompaniesController Companies(ZayraDbContext db, DefaultHttpContext http) =>
        new(new OrganizationSetupService(db, new AuditService(db)), db) { ControllerContext = new ControllerContext { HttpContext = http } };

    private static OrganizationController Organization(ZayraDbContext db, DefaultHttpContext http) =>
        new(new OrganizationSetupService(db, new AuditService(db)), db) { ControllerContext = new ControllerContext { HttpContext = http } };

    private static CompanyRequest CompanyEdit(Company c, string name) => new(
        name, null, null, c.CountryCode is { Length: > 0 } cc ? cc : "SA", null, c.RegistrationNumber,
        null, null, null, null, "SAR", IsActive: c.IsActive);

    private static async Task<ActionResult<CompanyDto>[]> ReadBoth(ZayraDbContext db, DefaultHttpContext http, Guid id) => new[]
    {
        await Companies(db, http).Get(id, CancellationToken.None),
        await Organization(db, http).Company(id, CancellationToken.None),
    };

    [Fact]
    public async Task CompanyRead_CompanyScopedCaller_GetsOwnAndNotFoundForOthers()
    {
        await using var db = Db();
        var w = await Seed(db);
        foreach (var r in await ReadBoth(db, Caller(w.TenantId, null, w.A.Id), w.A.Id)) r.Result.Should().BeOfType<OkObjectResult>();
        foreach (var r in await ReadBoth(db, Caller(w.TenantId, null, w.A.Id), w.B.Id)) r.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task CompanyRead_GroupCaller_ReadsAll_OrTheSelectedCompany_LikeTheList()
    {
        await using var db = Db();
        var w = await Seed(db);
        foreach (var id in new[] { w.A.Id, w.B.Id })
            foreach (var r in await ReadBoth(db, Caller(w.TenantId), id)) r.Result.Should().BeOfType<OkObjectResult>();
        foreach (var r in await ReadBoth(db, Caller(w.TenantId, w.A.Id), w.A.Id)) r.Result.Should().BeOfType<OkObjectResult>();
        foreach (var r in await ReadBoth(db, Caller(w.TenantId, w.A.Id), w.B.Id))
            r.Result.Should().BeOfType<NotFoundResult>("with Company A selected the list shows only A, and so does the single read");
    }

    [Fact]
    public async Task CompanyRead_AnotherTenantsCompany_IsNotFound()
    {
        await using var db = Db();
        var w = await Seed(db);
        var foreignCompany = w.OtherTenantBranch.CompanyId;
        foreach (var r in await ReadBoth(db, Caller(w.TenantId), foreignCompany)) r.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task CompanyUpdate_CompanyScopedCaller_RefusedForOthers_OnBothEndpoints_AllowedForOwn()
    {
        await using var db = Db();
        var w = await Seed(db);
        // A real tenant row, so an unguarded update would succeed rather than miss the tenant lock.
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = w.TenantId, Name = "Group", Slug = $"g-{w.TenantId:N}", AccountType = Zayra.Api.Domain.Entities.TenantAccountTypes.Group });
        await db.SaveChangesAsync();

        (await Companies(db, Caller(w.TenantId, null, w.A.Id)).Update(w.B.Id, CompanyEdit(w.B, "Taken Over"), CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>();
        (await Organization(db, Caller(w.TenantId, null, w.A.Id)).UpdateCompany(w.B.Id, CompanyEdit(w.B, "Taken Over"), CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>();
        (await Companies(db, Caller(w.TenantId, w.A.Id, w.A.Id)).Delete(w.B.Id, CancellationToken.None))
            .Should().BeOfType<ForbidResult>("deleting a legal entity needs a group grant");
        db.ChangeTracker.Clear();
        var beta = await db.Companies.SingleAsync(c => c.Id == w.B.Id);
        beta.LegalNameEn.Should().Be("Beta Contracting");
        beta.IsDeleted.Should().BeFalse();

        (await Organization(db, Caller(w.TenantId, null, w.A.Id)).UpdateCompany(w.A.Id, CompanyEdit(w.A, "Alpha Renamed"), CancellationToken.None)).Result
            .Should().BeOfType<OkObjectResult>("own company stays editable");
    }

    [Fact]
    public async Task CompanyCreate_OrganizationEndpoint_RequiresAGroupGrant_LikeCompanies()
    {
        await using var db = Db();
        var w = await Seed(db);
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = w.TenantId, Name = "Group", Slug = $"g-{w.TenantId:N}", AccountType = Zayra.Api.Domain.Entities.TenantAccountTypes.Group });
        await db.SaveChangesAsync();
        var request = new CompanyRequest("Gamma Holding", null, null, "SA", null, "G-1", null, null, null, null, "SAR");

        foreach (var refused in new[]
                 {
                     (await Companies(db, Caller(w.TenantId, null, w.A.Id)).Create(request, CancellationToken.None)).Result,
                     (await Organization(db, Caller(w.TenantId, null, w.A.Id)).CreateCompany(request, CancellationToken.None)).Result,
                 })
        {
            var obj = refused.Should().BeOfType<ObjectResult>().Subject;
            obj.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
            JsonSerializer.Serialize(obj.Value).Should().Contain("group_scope_required");
        }
        (await db.Companies.AnyAsync(c => c.TenantId == w.TenantId && c.LegalNameEn == "Gamma Holding")).Should().BeFalse();

        (await Organization(db, Caller(w.TenantId, w.A.Id)).CreateCompany(request, CancellationToken.None)).Result
            .Should().BeOfType<CreatedResult>("a group admin creates with or without a company selected");
    }

    [Fact]
    public async Task CompanyCreate_OrganizationEndpoint_AppliesTheCreationGate()
    {
        await using var db = Db();
        var w = await Seed(db);
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = w.TenantId, Name = "Single", Slug = $"s-{w.TenantId:N}", AccountType = Zayra.Api.Domain.Entities.TenantAccountTypes.SingleCompany });
        await db.SaveChangesAsync();
        var request = new CompanyRequest("Gamma Holding", null, null, "SA", null, "G-1", null, null, null, null, "SAR");

        (await Organization(db, Caller(w.TenantId)).CreateCompany(request, CancellationToken.None)).Result
            .Should().BeOfType<ConflictObjectResult>("a single-company account cannot add a second company through either endpoint");
    }

    [Fact]
    public async Task CompanyUpdate_GroupCaller_WithOrWithoutCompanySelected_UpdatesAnyCompany()
    {
        await using var db = Db();
        var w = await Seed(db);
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = w.TenantId, Name = "Group", Slug = $"g-{w.TenantId:N}", AccountType = Zayra.Api.Domain.Entities.TenantAccountTypes.Group });
        await db.SaveChangesAsync();
        foreach (var (selected, name) in new[] { ((Guid?)null, "Beta Renamed"), (w.A.Id, "Beta Renamed Again") })
        {
            (await Organization(db, Caller(w.TenantId, selected)).UpdateCompany(w.B.Id, CompanyEdit(w.B, name), CancellationToken.None)).Result
                .Should().BeOfType<OkObjectResult>();
            (await Companies(db, Caller(w.TenantId, selected)).Update(w.B.Id, CompanyEdit(w.B, name + "!"), CancellationToken.None)).Result
                .Should().BeOfType<OkObjectResult>();
        }
    }

    [Fact]
    public async Task OrganizationCreateBranch_AppliesTheSameCompanyRule()
    {
        await using var db = Db();
        var w = await Seed(db);
        (await Organization(db, Caller(w.TenantId, null, w.A.Id)).CreateBranch(Req(w.B.Id, "B9"), CancellationToken.None)).Result
            .Should().BeOfType<ForbidResult>();
        (await Organization(db, Caller(w.TenantId, null, w.A.Id)).CreateBranch(Req(w.A.Id, "A9"), CancellationToken.None)).Result
            .Should().BeOfType<CreatedResult>();
        (await Organization(db, Caller(w.TenantId, w.A.Id)).CreateBranch(Req(w.B.Id, "B9"), CancellationToken.None)).Result
            .Should().BeOfType<CreatedResult>("a group grant with a company selected still writes any company");
        (await db.Branches.CountAsync(b => b.TenantId == w.TenantId && b.Code == "B9")).Should().Be(1);
    }

    [Fact]
    public async Task CompanyDelete_RequiresAGroupGrant_EvenForTheCallersOwnCompany()
    {
        await using var db = Db();
        var w = await Seed(db);
        foreach (var selected in new Guid?[] { null, w.A.Id })
            (await Companies(db, Caller(w.TenantId, selected, w.A.Id)).Delete(w.A.Id, CancellationToken.None))
                .Should().BeOfType<ForbidResult>();
        db.ChangeTracker.Clear();
        (await db.Companies.SingleAsync(c => c.Id == w.A.Id)).IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task CompanyExport_ContainsOnlyTheCallersCompanies()
    {
        await using var db = Db();
        var w = await Seed(db);
        var scoped = Encoding.UTF8.GetString(((FileContentResult)await Companies(db, Caller(w.TenantId, null, w.A.Id)).Export(CancellationToken.None)).FileContents);
        scoped.Should().Contain("Alpha Trading").And.NotContain("Beta Contracting").And.NotContain("Foreign Co");
        var group = Encoding.UTF8.GetString(((FileContentResult)await Companies(db, Caller(w.TenantId)).Export(CancellationToken.None)).FileContents);
        group.Should().Contain("Alpha Trading").And.Contain("Beta Contracting").And.NotContain("Foreign Co");
    }

    // ── /api/organization department/designation/grade writes match their dedicated controllers ──

    [Fact]
    public async Task OrganizationDoor_DepartmentDesignationGradeCreates_NeedAGroupGrant()
    {
        await using var db = Db();
        var w = await Seed(db);
        // Addendum B: each names its company, as the Setup form does.
        var dept = new DepartmentRequest(null, null, null, "OPS", "Operations", null, null, CompanyId: w.A.Id);
        var desig = new DesignationRequest(null, "MGR", "Manager", null, null, null, null, null, CompanyId: w.A.Id);
        var grade = new GradeRequest("G1", "Grade 1", null, 1, CompanyId: w.A.Id);

        var scoped = Organization(db, Caller(w.TenantId, null, w.A.Id));
        (await scoped.CreateDepartment(dept, CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await scoped.CreateDesignation(desig, CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await scoped.CreateGrade(grade, CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await db.Departments.AnyAsync(d => d.TenantId == w.TenantId)).Should().BeFalse();
        (await db.Designations.AnyAsync(d => d.TenantId == w.TenantId)).Should().BeFalse();
        (await db.Grades.AnyAsync(g => g.TenantId == w.TenantId)).Should().BeFalse();

        var group = Organization(db, Caller(w.TenantId, w.A.Id));
        (await group.CreateDepartment(dept, CancellationToken.None)).Result.Should().BeOfType<CreatedResult>();
        (await group.CreateDesignation(desig, CancellationToken.None)).Result.Should().BeOfType<CreatedResult>();
        (await group.CreateGrade(grade, CancellationToken.None)).Result.Should().BeOfType<CreatedResult>();
    }

    // ── Cost centres: company-scoped writes on both doors ─────────────────────────────────────

    private static CostCentersController CostCenters(ZayraDbContext db, DefaultHttpContext http) =>
        new(new OrganizationSetupService(db, new AuditService(db)), db) { ControllerContext = new ControllerContext { HttpContext = http } };

    private static async Task<(CostCenter A, CostCenter B, CostCenter Shared, CostCenter Foreign)> SeedCostCenters(ZayraDbContext db, World w)
    {
        var a = new CostCenter { TenantId = w.TenantId, CompanyId = w.A.Id, Code = "CC-A", Name = "Alpha ops" };
        var b = new CostCenter { TenantId = w.TenantId, CompanyId = w.B.Id, Code = "CC-B", Name = "Beta ops" };
        var shared = new CostCenter { TenantId = w.TenantId, CompanyId = null, Code = "CC-G", Name = "Group" };
        var foreign = new CostCenter { TenantId = Guid.NewGuid(), CompanyId = w.OtherTenantBranch.CompanyId, Code = "CC-F", Name = "Foreign" };
        db.AddRange(a, b, shared, foreign);
        await db.SaveChangesAsync();
        return (a, b, shared, foreign);
    }

    [Fact]
    public async Task CostCenter_CompanyScopedCaller_RefusedOutsideTheirCompanies_OnBothDoors()
    {
        await using var db = Db();
        var w = await Seed(db);
        var cc = await SeedCostCenters(db, w);
        DefaultHttpContext ScopedA() => Caller(w.TenantId, null, w.A.Id);

        (await CostCenters(db, ScopedA()).Create(new CostCenterRequest(w.B.Id, "CC-B2", "x"), CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await Organization(db, ScopedA()).CreateCostCenter(new CostCenterRequest(w.B.Id, "CC-B3", "x"), CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await CostCenters(db, ScopedA()).Create(new CostCenterRequest(null, "CC-G2", "x"), CancellationToken.None)).Result
            .Should().BeOfType<ForbidResult>("a cost centre without a company is tenant-wide");
        (await CostCenters(db, ScopedA()).Update(cc.A.Id, new CostCenterRequest(w.B.Id, "CC-A", "moved"), CancellationToken.None)).Result
            .Should().BeOfType<ForbidResult>("moving an own cost centre into another company");
        (await CostCenters(db, ScopedA()).Update(cc.B.Id, new CostCenterRequest(w.A.Id, "CC-B", "moved"), CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>("moving another company's cost centre out of it");
        (await CostCenters(db, ScopedA()).Update(cc.Shared.Id, new CostCenterRequest(null, "CC-G", "renamed"), CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>();
        (await CostCenters(db, ScopedA()).Delete(cc.B.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();

        db.ChangeTracker.Clear();
        (await db.CostCenters.CountAsync(c => c.TenantId == w.TenantId)).Should().Be(3);
        (await db.CostCenters.SingleAsync(c => c.Id == cc.A.Id)).CompanyId.Should().Be(w.A.Id);
        (await db.CostCenters.SingleAsync(c => c.Id == cc.B.Id)).Name.Should().Be("Beta ops");
        (await db.CostCenters.SingleAsync(c => c.Id == cc.B.Id)).IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task CostCenter_CompanyScopedCaller_MaintainsOwn_GroupCallerAny()
    {
        await using var db = Db();
        var w = await Seed(db);
        var cc = await SeedCostCenters(db, w);

        (await CostCenters(db, Caller(w.TenantId, null, w.A.Id)).Create(new CostCenterRequest(w.A.Id, "CC-A2", "Alpha east"), CancellationToken.None)).Result
            .Should().BeOfType<CreatedAtActionResult>();
        (await CostCenters(db, Caller(w.TenantId, null, w.A.Id)).Update(cc.A.Id, new CostCenterRequest(w.A.Id, "CC-A", "Alpha renamed"), CancellationToken.None)).Result
            .Should().BeOfType<OkObjectResult>();

        foreach (var selected in new Guid?[] { null, w.A.Id })
        {
            (await CostCenters(db, Caller(w.TenantId, selected)).Update(cc.B.Id, new CostCenterRequest(w.B.Id, "CC-B", "Beta renamed"), CancellationToken.None)).Result
                .Should().BeOfType<OkObjectResult>();
            (await Organization(db, Caller(w.TenantId, selected)).CreateCostCenter(new CostCenterRequest(null, $"CC-G{(selected is null ? 2 : 3)}", "Group 2"), CancellationToken.None)).Result
                .Should().BeOfType<CreatedResult>();
        }
        (await CostCenters(db, Caller(w.TenantId, w.A.Id)).Delete(cc.B.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task CostCenter_AnotherTenants_IsNotFound()
    {
        await using var db = Db();
        var w = await Seed(db);
        var cc = await SeedCostCenters(db, w);
        (await CostCenters(db, Caller(w.TenantId)).Update(cc.Foreign.Id, new CostCenterRequest(null, "CC-F", "x"), CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>();
        (await CostCenters(db, Caller(w.TenantId)).Delete(cc.Foreign.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task CostCenter_CompanyScopedImport_RefusesTenantWideAndOtherCompanyRows()
    {
        await using var db = Db();
        var w = await Seed(db);
        var cc = await SeedCostCenters(db, w);
        var csv = "Code,Name,NameAr,DepartmentCode,IsActive\nCC-A,Alpha renamed,,,true\nCC-B,Beta renamed,,,true\nCC-NEW,Brand new,,,true";

        var result = (ImportCommitResult)((OkObjectResult)await CostCenters(db, Caller(w.TenantId, null, w.A.Id))
            .Import(new CostCenterImportRequest(csv), CancellationToken.None)).Value!;
        // Cost-centre codes are per company (owner decision 2026-10-23): a company-scoped caller imports into their
        // own company. CC-A is A's and is updated; CC-B and CC-NEW become A's own cost centres; B's CC-B is untouched.
        result.Updated.Should().Be(1);
        result.Created.Should().Be(2);
        result.Rows.Where(r => r.Status == ImportRowStatus.Error).Should().BeEmpty();
        db.ChangeTracker.Clear();
        (await db.CostCenters.SingleAsync(c => c.Id == cc.B.Id)).Name.Should().Be("Beta ops");
        (await db.CostCenters.Where(c => c.Code == "CC-B").Select(c => c.CompanyId).ToListAsync()).Should().BeEquivalentTo(new Guid?[] { w.B.Id, w.A.Id });
        (await db.CostCenters.SingleAsync(c => c.Code == "CC-NEW")).CompanyId.Should().Be(w.A.Id);
    }

    // ── Refusals do not confirm which sibling companies exist ─────────────────────────────────

    [Fact]
    public async Task CompanyScopedImport_SameWordsForMissingAndOutOfScopeCompanies()
    {
        await using var db = Db();
        var w = await Seed(db);
        var csv = "CompanyLegalName,Code,NameEn,CountryCode,City,TimeZoneId\nBeta Contracting,B7,x,SA,Riyadh,Asia/Riyadh\nNo Such Co,N7,x,SA,Riyadh,Asia/Riyadh";
        var result = (ImportCommitResult)((OkObjectResult)await Branches(db, Caller(w.TenantId, null, w.A.Id))
            .Import(new BranchImportRequest(csv), CancellationToken.None)).Value!;
        result.Rows.Select(r => string.Join(" ", r.Errors)).Should().Equal(
            "Company 'Beta Contracting' is not one of the companies you can manage.",
            "Company 'No Such Co' is not one of the companies you can manage.");
    }

    [Fact]
    public async Task DuplicateRegistrationNumber_HeldOutsideTheCallersCompanies_GetsAGenericMessage()
    {
        await using var db = Db();
        var w = await Seed(db);
        db.Tenants.Add(new Zayra.Api.Domain.Entities.Tenant { Id = w.TenantId, Name = "Group", Slug = $"g-{w.TenantId:N}", AccountType = Zayra.Api.Domain.Entities.TenantAccountTypes.Group });
        await db.SaveChangesAsync();
        var takeBetasNumber = CompanyEdit(w.A, "Alpha Trading") with { RegistrationNumber = w.B.RegistrationNumber };

        var scoped = (BadRequestObjectResult)(await Companies(db, Caller(w.TenantId, null, w.A.Id)).Update(w.A.Id, takeBetasNumber, CancellationToken.None)).Result!;
        var scopedBody = JsonSerializer.SerializeToElement(scoped.Value);
        scopedBody.GetProperty("error").GetString().Should().Be("registration_number_taken");
        scopedBody.GetProperty("message").GetString().Should().Be("This registration number can't be used.");

        var group = (BadRequestObjectResult)(await Companies(db, Caller(w.TenantId)).Update(w.A.Id, takeBetasNumber, CancellationToken.None)).Result!;
        JsonSerializer.SerializeToElement(group.Value).GetProperty("message").GetString().Should().Contain("already exists in this tenant");
    }
}
