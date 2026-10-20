using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Infrastructure.Recruitment;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferLifecycleServiceTests(PostgresFixture fixture)
{
    internal static IConfiguration Config(bool enabled = true) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Recruitment:ManagedOfferLifecycleEnabled"] = enabled.ToString() }).Build();

    [Fact]
    public async Task RevisionPreservesIssuedLegacyRootAndCreatesFreshScopedDraft()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        await using var db = fixture.CreateDb();
        var revision = await new OfferLifecycleService(db, Config()).ReviseAsync(seed.Actor, seed.OfferId, new(1, "New start date"));
        revision.Id.Should().NotBe(seed.OfferId);
        revision.SeriesId.Should().NotBeNull(); revision.RevisionNumber.Should().Be(1);
        revision.PreviousRevisionId.Should().Be(seed.OfferId); revision.Status.Should().Be("Draft");
        revision.CompanyId.Should().Be(seed.Actor.CompanyId); revision.RecipientEmail.Should().Be("candidate@example.invalid");
        (await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId)).Status.Should().Be("Sent");
        (await db.OfferApprovals.CountAsync(a => a.OfferLetterId == revision.Id)).Should().Be(0);
        (await db.OfferLifecycleEvents.CountAsync(e => e.OfferId == revision.Id && e.EventType == "RevisionCreated")).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentRevisionOfSameVersionHasOneWinnerAndOneConflict()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        async Task<string> Revise()
        {
            await using var db = fixture.CreateDb();
            try { await new OfferLifecycleService(db, Config()).ReviseAsync(seed.Actor, seed.OfferId, new(1, "Revised terms")); return "created"; }
            catch (OfferLifecycleException e) { return e.Code; }
        }
        (await Task.WhenAll(Revise(), Revise())).Should().BeEquivalentTo("created", "offer_version_conflict");
        await using var verify = fixture.CreateDb();
        (await verify.OfferLetters.CountAsync(o => o.ApplicationId == seed.ApplicationId)).Should().Be(2);
    }

    [Fact]
    public async Task ManagedMutationsDisabledByDefaultButScopeCheckedFirst()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        await using var db = fixture.CreateDb(); var service = new OfferLifecycleService(db, Config(false));
        var disabled = await FluentActions.Awaiting(() => service.ReviseAsync(seed.Actor, seed.OfferId, new(1, "Review")))
            .Should().ThrowAsync<OfferLifecycleException>();
        disabled.Which.Code.Should().Be("managed_offer_lifecycle_disabled");
        var forbidden = await FluentActions.Awaiting(() => service.ReviseAsync(seed.Actor with { CompanyId = Guid.NewGuid() }, seed.OfferId, new(1, "Review")))
            .Should().ThrowAsync<OfferLifecycleException>();
        forbidden.Which.Status.Should().Be(404);
    }

    [Fact]
    public async Task AcceptancePreventsRevisionAndReservationPreventsWithdrawal()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        await using var db = fixture.CreateDb();
        var app = await db.JobApplications.SingleAsync(a => a.Id == seed.ApplicationId);
        app.OfferReservationId = Guid.NewGuid(); app.OfferReservationOfferId = seed.OfferId; app.OfferReservationState = "Unknown";
        app.OfferReservationVersion = app.Version;
        await db.SaveChangesAsync();
        var blocked = await FluentActions.Awaiting(() => new OfferLifecycleService(db, Config()).WithdrawAsync(seed.Actor, seed.OfferId, new(1, "Withdraw")))
            .Should().ThrowAsync<OfferLifecycleException>();
        blocked.Which.Code.Should().Be("offer_issuance_in_flight");
        db.ChangeTracker.Clear(); app = await db.JobApplications.SingleAsync(a => a.Id == seed.ApplicationId);
        app.OfferReservationId = null; app.OfferReservationOfferId = null; app.OfferReservationState = null;
        app.OfferReservationVersion = null;
        await db.SaveChangesAsync();
        (await new RecruitmentService(db).AcceptOfferAsync(seed.Actor.TenantId, seed.OfferId, seed.Actor.UserId, seed.Actor.Name)).IsSuccess.Should().BeTrue();
        var accepted = await FluentActions.Awaiting(() => new OfferLifecycleService(db, Config()).ReviseAsync(seed.Actor, seed.OfferId, new(2, "Revision")))
            .Should().ThrowAsync<OfferLifecycleException>();
        accepted.Which.Code.Should().Be("application_not_active");
    }
}
