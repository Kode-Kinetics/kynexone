using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Scope;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public class OfferTemplatePersistenceTests(PostgresFixture fixture)
{
    private static HttpContextAccessor Context(Guid tenantId, Guid? companyId = null)
    {
        var claims = new List<Claim> { new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Role, "Admin"), new("is_group_scope", companyId is null ? "true" : "false") };
        if (companyId is { } cid) claims.Add(new("entity_access", JsonSerializer.Serialize(new { c = cid, r = "Admin" })));
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } };
        if (companyId is { } selected) accessor.HttpContext.Request.Headers[ZayraDbContext.CompanySelectionHeader] = selected.ToString();
        return accessor;
    }
    private static OfferTemplatesController Controller(ZayraDbContext db, HttpContextAccessor accessor) => new(db, new RequestEntityScopeResolver(accessor)) { ControllerContext = new ControllerContext { HttpContext = accessor.HttpContext! } };

    [Fact]
    public async Task TemplatePersistsVersionsConflictsAndArchivesWithoutDeletingIssuedSnapshot()
    {
        await using var seed = fixture.CreateDb(); var tid = await PostgresFixture.SeedMinimalTenant(seed);
        var accessor = Context(tid);
        await using var db = fixture.CreateDbWithAccessor(accessor); var controller = Controller(db, accessor);
        (await controller.Create(new("Executive offer", new OfferDesign()), default)).Should().BeOfType<CreatedAtActionResult>();
        var row = await db.OfferLetterTemplates.SingleAsync(t => t.TenantId == tid);
        var offer = new OfferLetter { TenantId = tid, CandidateName = "QA Candidate", OfferedJobTitle = "Manager", StartDate = new(2027, 1, 1), GrossSalary = 17000 };
        await OfferLetterDesign.ApplyTemplateAsync(db, offer, row.Id, default);
        var snapshot = offer.DesignSnapshotJson;
        var updated = new OfferDesign { AccentColor = "#992244" };
        (await controller.Update(row.Id, new("Revised offer", updated, 1), default)).Should().BeOfType<OkObjectResult>();
        (await controller.Update(row.Id, new("Stale editor", updated, 1), default)).Should().BeOfType<ConflictObjectResult>();
        (await controller.Archive(row.Id, new(2), default)).Should().BeOfType<NoContentResult>();
        offer.DesignSnapshotJson.Should().Be(snapshot);
        var useArchived = () => OfferLetterDesign.ApplyTemplateAsync(db, new OfferLetter { TenantId = tid }, row.Id, default);
        await useArchived.Should().ThrowAsync<ArgumentException>();
        (await db.RecruitmentAuditLogs.CountAsync(a => a.TenantId == tid && a.EntityType == "OfferTemplate")).Should().Be(3);
    }

    [Fact]
    public async Task CrossTenantReadAndTemplateApplicationAreRefused()
    {
        await using var seed = fixture.CreateDb(); var own = await PostgresFixture.SeedMinimalTenant(seed); var other = await PostgresFixture.SeedMinimalTenant(seed);
        var row = new OfferLetterTemplate { TenantId = other, DesignJson = JsonSerializer.Serialize(new OfferDesign(), OfferLetterDesign.Json) };
        seed.OfferLetterTemplates.Add(row); await seed.SaveChangesAsync();
        var accessor = Context(own); await using var db = fixture.CreateDbWithAccessor(accessor);
        (await Controller(db, accessor).Get(row.Id, default)).Should().BeOfType<NotFoundResult>();
        var useOther = () => OfferLetterDesign.ApplyTemplateAsync(db, new OfferLetter { TenantId = own }, row.Id, default);
        await useOther.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CompanyCannotMutateSharedTemplateOrUseAnotherCompanyTemplate()
    {
        await using var seed = fixture.CreateDb(); var tid = await PostgresFixture.SeedMinimalTenant(seed);
        var a = new Company { TenantId = tid, LegalNameEn = "Company A" }; var b = new Company { TenantId = tid, LegalNameEn = "Company B" };
        seed.Companies.AddRange(a, b);
        var shared = new OfferLetterTemplate { TenantId = tid, DesignJson = JsonSerializer.Serialize(new OfferDesign(), OfferLetterDesign.Json) };
        var privateTemplate = new OfferLetterTemplate { TenantId = tid, CompanyId = b.Id, DesignJson = shared.DesignJson };
        seed.OfferLetterTemplates.AddRange(shared, privateTemplate); await seed.SaveChangesAsync();
        var accessor = Context(tid, a.Id); await using var db = fixture.CreateDbWithAccessor(accessor); var controller = Controller(db, accessor);
        (await controller.Get(shared.Id, default)).Should().BeOfType<OkObjectResult>();
        (await controller.Update(shared.Id, new("Forbidden", new(), 1), default)).Should().BeOfType<ForbidResult>();
        (await controller.Get(privateTemplate.Id, default)).Should().BeOfType<NotFoundResult>();
        var act = () => OfferLetterDesign.ApplyTemplateAsync(db, new OfferLetter { TenantId = tid, CompanyId = a.Id }, privateTemplate.Id, default);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ActualOfferValuesAreFrozenUsingLegalCompanyName()
    {
        await using var db = fixture.CreateDb(); var tid = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company { TenantId = tid, LegalNameEn = "Actual legal entity" }; db.Companies.Add(company); await db.SaveChangesAsync();
        var offer = new OfferLetter { TenantId = tid, CompanyId = company.Id, CandidateName = "Actual candidate", GrossSalary = 14500, StartDate = new(2027, 2, 1) };
        var snapshot = await OfferLetterDesign.SnapshotAsync(db, offer, new(), null, null, default);
        snapshot.Values["company_name"].Should().Be(company.LegalNameEn);
        snapshot.Values["candidate_name"].Should().Be("Actual candidate");
        snapshot.Values["gross_salary"].Should().Be("14,500.00");
    }
}
