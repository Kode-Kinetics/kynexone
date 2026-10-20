using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Infrastructure.Scope;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferCandidatePreviewTests(PostgresFixture fixture)
{
    private static OfferComponentInput[] Components() => [
        new("BASIC", "Basic salary", null, "Cash", 1000, null, "Monthly", true, null, null, 0),
        new("CUSTOM", "Custom monthly", null, "Cash", 123, null, "Monthly", true, null, null, 1),
        new("ANNUAL", "Annual travel", null, "Cash", 2400, null, "Annual", true, null, null, 2),
        new("ONCE", "Signing bonus", null, "Cash", 500, null, "OneTime", true, null, null, 3)];
    private static string Text(byte[] bytes) { using var pdf = PdfDocument.Open(bytes); return string.Join(" ", pdf.GetPages().Select(p => p.Text)); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedCreateOrSaveWithNoDesignFreezesAllAuthoritativeComponents(bool save)
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); await using var db = fixture.CreateDb();
        db.Departments.Add(new() { TenantId = seed.Actor.TenantId, NameEn = "Engineering", Code = "ENG" });
        db.Designations.Add(new() { TenantId = seed.Actor.TenantId, TitleEn = "Engineer", Code = "ENG" });
        await db.SaveChangesAsync();
        var lifecycle = new OfferLifecycleService(db, OfferLifecycleServiceTests.Config());
        var offer = await lifecycle.CreateAsync(seed.Actor, seed.ApplicationId, new(new(2027, 2, 1), 3, "Engineer", "Engineering", ResponseDeadline: DateTime.UtcNow.AddDays(7), Components: Components(), ExpectedApplicationVersion: 1));
        if (save)
        {
            offer.DesignSnapshotJson = null; await db.SaveChangesAsync();
            offer = await lifecycle.SaveDraftAsync(seed.Actor, offer.Id, new(offer.Version, 0, offer.StartDate, offer.ResponseDeadline, 3, offer.RecipientEmail!, "Engineer", "Engineering", Components: Components()));
        }
        var storage = new OfferMemoryStorage(); var artifacts = new OfferArtifactService(db, storage, OfferLifecycleServiceTests.Config());
        await artifacts.FreezeAsync(seed.Actor, offer.Id, offer.Version);
        var original = await artifacts.OriginalAsync(seed.Actor, offer.Id);
        Text(original).Should().Contain("Custom monthly").And.Contain("Annual travel").And.Contain("Signing bonus").And.Contain("1,123.00").And.Contain("15,876.00").And.Contain("500.00");
        var persisted = await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == offer.Id);
        var snapshot = JsonSerializer.Deserialize<OfferDesignSnapshot>(persisted.DesignSnapshotJson!, OfferLetterDesign.Json)!;
        snapshot.Design.SchemaVersion.Should().Be(2);
        snapshot.Compensation!.MonthlyCash.Should().Be(1123);
        snapshot.Compensation.AnnualRecurringCash.Should().Be(15876);
        snapshot.Compensation.OneTimeCash.Should().Be(500);
        original.Should().Equal(storage.Bytes.Values.Single());
    }

    private HttpContextAccessor Context(OfferLifecycleSeed seed, Guid company, string permission = "recruitment.read")
    {
        var claims = new[] { new Claim("tenant_id", seed.Actor.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, seed.Actor.UserId.ToString()), new Claim(ClaimTypes.Role, "Admin"), new Claim("permissions", permission), new Claim("permission", permission), new Claim("is_group_scope", "false"), new Claim("entity_access", JsonSerializer.Serialize(new { c = company, r = "Admin" })) };
        var result = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } };
        result.HttpContext.Request.Headers[ZayraDbContext.CompanySelectionHeader] = company.ToString(); return result;
    }

    [Fact]
    public async Task CandidatePreviewUsesSavedCandidateTermsRequiresVersionAndRejectsFrozenAndCompanyGuess()
    {
        var seed = await OfferArtifactPostgresTests.ManagedDraftAsync(fixture);
        var context = Context(seed, seed.Actor.CompanyId!.Value); await using var db = fixture.CreateDbWithAccessor(context);
        var controller = new OfferTemplatesController(db, new RequestEntityScopeResolver(context)) { ControllerContext = new() { HttpContext = context.HttpContext! } };
        var design = OfferLetterDesign.DynamicDefault();
        var result = await controller.Preview(new(design, seed.OfferId, 2), default);
        result.Should().BeOfType<FileContentResult>();
        Text(((FileContentResult)result).FileContents).Should().Contain("Test Candidate").And.Contain("1,000.00").And.NotContain("Alex Morgan");
        (await controller.Preview(new(design, seed.OfferId), default)).Should().BeOfType<ConflictObjectResult>();
        (await controller.Preview(new(design, seed.OfferId, 1), default)).Should().BeOfType<ConflictObjectResult>();
        var ownHttpContext = context.HttpContext;
        var wrongContext = Context(seed, Guid.NewGuid()); await using var wrongDb = fixture.CreateDbWithAccessor(wrongContext);
        var wrong = new OfferTemplatesController(wrongDb, new RequestEntityScopeResolver(wrongContext)) { ControllerContext = new() { HttpContext = wrongContext.HttpContext! } };
        (await wrong.Preview(new(design, seed.OfferId, 2), default)).Should().BeOfType<NotFoundResult>();
        context.HttpContext = ownHttpContext;
        await new OfferArtifactService(db, new OfferMemoryStorage(), OfferLifecycleServiceTests.Config()).FreezeAsync(seed.Actor, seed.OfferId, 2);
        var version = await db.OfferLetters.AsNoTracking().Where(o => o.Id == seed.OfferId).Select(o => o.Version).SingleAsync();
        (await controller.Preview(new(design, seed.OfferId, version), default)).Should().BeOfType<ConflictObjectResult>();
    }
}
