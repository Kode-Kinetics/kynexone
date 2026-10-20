using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Recruitment;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferImmutabilityTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("terms")]
    [InlineData("recipient")]
    [InlineData("artifact")]
    public async Task FrozenTermsIdentityAndArtifactKeyCannotBeRewritten(string target)
    {
        var seed = await OfferArtifactPostgresTests.ManagedDraftAsync(fixture); await using var db = fixture.CreateDb();
        await new OfferArtifactService(db, new OfferMemoryStorage(), OfferLifecycleServiceTests.Config()).FreezeAsync(seed.Actor, seed.OfferId, 2);
        var offer = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId);
        if (target == "terms") offer.GrossSalary++;
        else if (target == "recipient") offer.CandidateName = "Changed candidate";
        else (await db.OfferArtifacts.SingleAsync(a => a.Id == offer.ArtifactId)).StorageKey += ".replaced";
        var failure = await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Contain("offer_immutable");
    }
}
