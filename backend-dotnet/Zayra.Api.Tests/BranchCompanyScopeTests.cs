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
        var c = new OrganizationController(new OrganizationSetupService(db, new AuditService(db)))
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
}
