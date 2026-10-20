using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferHistoryQueryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task WorkspaceExposesSavedCompensationAndDesignSnapshotsNotLiveReconstruction()
    {
        var seed = await OfferArtifactPostgresTests.ManagedDraftAsync(fixture); await using var db = fixture.CreateDb();
        var workspace = await new OfferHistoryQueryService(db, new(db, OfferLifecycleServiceTests.Config(false))).WorkspaceAsync(seed.Actor, seed.OfferId);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(workspace, OfferCompensation.Json);
        json.TryGetProperty("compensationSnapshot", out var compensation).Should().BeTrue();
        compensation.GetProperty("monthlyCash").GetDecimal().Should().Be(1000);
        json.TryGetProperty("designSnapshot", out var design).Should().BeTrue(); design.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);
    }

    [Fact]
    public async Task HistoryFiltersAndTimestampTiesHaveStableScopedPages()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); await using var db = fixture.CreateDb();
        var original = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId);
        for (var i = 0; i < 3; i++) db.OfferLetters.Add(new() { TenantId = seed.Actor.TenantId,
            CompanyId = seed.Actor.CompanyId, ApplicationId = seed.ApplicationId, CandidateName = "Match Candidate",
            OfferedJobTitle = "Engineer", GeneratedAtUtc = original.GeneratedAtUtc, Status = "Sent", SentAtUtc = original.SentAtUtc });
        db.OfferLetters.Add(new() { TenantId = seed.Actor.TenantId, CompanyId = null, ApplicationId = seed.ApplicationId,
            CandidateName = "Match Secret", OfferedJobTitle = "Engineer", Status = "Sent" });
        await db.SaveChangesAsync();
        var service = new OfferHistoryQueryService(db, new(db, OfferLifecycleServiceTests.Config(false)));
        var filter = new OfferHistoryQuery(Search: "Match", ApplicationId: seed.ApplicationId, Position: "Engineer",
            Status: "Sent", CommunicationOutcome: "LegacyUnverified", IssuedFrom: DateTime.UtcNow.AddDays(-1), IssuedTo: DateTime.UtcNow.AddDays(1), PageSize: 2);
        var first = await service.ListAsync(seed.Actor, filter); var next = await service.ListAsync(seed.Actor, filter with { Page = 2 });
        first.Total.Should().Be(3); first.Items.Should().HaveCount(2); next.Items.Should().ContainSingle();
        var combined = first.Items.Concat(next.Items).Select(o => o.Id).ToArray();
        combined.Should().OnlyHaveUniqueItems(); combined.Should().Equal(combined.OrderDescending());
        first.Items.Should().OnlyContain(o => o.OriginalProvenance == "LegacyReconstructed" && o.CompanyId == seed.Actor.CompanyId);
    }

    [Fact]
    public async Task WorkspaceAndEventsAuthorizeParentAndNeverInventLegacyOriginal()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); await using var db = fixture.CreateDb();
        var service = new OfferHistoryQueryService(db, new(db, OfferLifecycleServiceTests.Config(false)));
        var workspace = await service.WorkspaceAsync(seed.Actor, seed.OfferId);
        workspace.Should().NotBeNull(); workspace!.OriginalProvenance.Should().Be("LegacyReconstructed"); workspace.Artifact.Should().BeNull();
        workspace.Capabilities["revise"].Allowed.Should().BeFalse();
        var hidden = await FluentActions.Awaiting(() => service.EventsAsync(seed.Actor with { CompanyId = Guid.NewGuid() }, seed.OfferId))
            .Should().ThrowAsync<OfferLifecycleException>();
        hidden.Which.Status.Should().Be(404);
    }
}
