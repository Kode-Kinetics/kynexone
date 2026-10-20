using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Infrastructure.Recruitment;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferResponsePostgresTests(PostgresFixture fixture)
{
    private static OfferResponseRequest Request(long version = 1, string decision = "Accepted") =>
        new(version, decision, "StaffRecorded", DateTime.UtcNow, "Candidate confirmed", "case/42");

    [Fact]
    public async Task StaleResponseRefusesWithoutPartialMutation()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); await using var db = fixture.CreateDb();
        var failure = await FluentActions.Awaiting(() => new OfferLifecycleService(db, OfferLifecycleServiceTests.Config())
            .RespondAsync(seed.Actor, seed.OfferId, Request(99))).Should().ThrowAsync<OfferLifecycleException>();
        failure.Which.Code.Should().Be("offer_version_conflict");
        (await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId)).Status.Should().Be("Sent");
        (await db.EmployeeDrafts.CountAsync(d => d.TenantId == seed.Actor.TenantId)).Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentExplicitAcceptanceReturnsOneDraftAndOneResponseEvent()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); var request = Request();
        async Task<OfferResponseDto> Accept()
        {
            await using var db = fixture.CreateDb();
            return await new OfferLifecycleService(db, OfferLifecycleServiceTests.Config()).RespondAsync(seed.Actor, seed.OfferId, request);
        }
        var outcomes = await Task.WhenAll(Accept(), Accept());
        outcomes.Should().OnlyContain(o => o.Status == "Accepted");
        outcomes.Select(o => o.OnboardingDraftId).Distinct().Should().ContainSingle().Which.Should().NotBeNull();
        outcomes.Count(o => o.AlreadyRecorded).Should().Be(1);
        await using var verify = fixture.CreateDb();
        (await verify.JobOpenings.SingleAsync(o => o.Id == seed.OpeningId)).FilledCount.Should().Be(1);
        (await verify.OfferLifecycleEvents.CountAsync(e => e.OfferId == seed.OfferId && e.EventType == "ResponseAccepted")).Should().Be(1);
    }

    [Theory]
    [InlineData("Queued", "Decline")]
    [InlineData("Sending", "Reject")]
    [InlineData("Unknown", "Advance")]
    public async Task ReservationBlocksEveryCompetingApplicationMutation(string state, string action)
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); await using var db = fixture.CreateDb();
        var app = await db.JobApplications.SingleAsync(a => a.Id == seed.ApplicationId);
        app.OfferReservationId = Guid.NewGuid(); app.OfferReservationOfferId = seed.OfferId; app.OfferReservationState = state;
        app.OfferReservationVersion = app.Version;
        await db.SaveChangesAsync(); var service = new OfferLifecycleService(db, OfferLifecycleServiceTests.Config());
        Func<Task> mutate = action == "Decline" ? async () => { await service.RespondAsync(seed.Actor, seed.OfferId, Request(1, "Declined")); }
            : async () => { await service.ChangeApplicationAsync(seed.Actor, seed.ApplicationId, 1, action, "HR review"); };
        var result = await FluentActions.Awaiting(mutate).Should().ThrowAsync<OfferLifecycleException>();
        result.Which.Code.Should().Be("offer_issuance_in_flight");
        (await db.JobApplications.AsNoTracking().SingleAsync(a => a.Id == seed.ApplicationId)).Status.Should().Be("Active");
    }

    [Fact]
    public async Task RejectionAndAcceptanceRaceHaveOnlyOneBusinessWinner()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        async Task<bool> Mutate(bool accept)
        {
            await using var db = fixture.CreateDb(); var service = new OfferLifecycleService(db, OfferLifecycleServiceTests.Config());
            try
            {
                if (accept) await service.RespondAsync(seed.Actor, seed.OfferId, Request());
                else await service.ChangeApplicationAsync(seed.Actor, seed.ApplicationId, 1, "Reject", "Not selected");
                return true;
            }
            catch (OfferLifecycleException) { return false; }
        }
        (await Task.WhenAll(Mutate(true), Mutate(false))).Count(x => x).Should().Be(1);
        await using var verify = fixture.CreateDb(); var app = await verify.JobApplications.SingleAsync(a => a.Id == seed.ApplicationId);
        var offer = await verify.OfferLetters.SingleAsync(o => o.Id == seed.OfferId);
        (app.Status == "Hired" ? offer.Status == "Accepted" : app.Status == "Rejected" && offer.Status == "Sent").Should().BeTrue();
    }

    [Fact]
    public async Task LegacyAcceptedMissingDraftRecoversWithoutSecondHeadcountOrInventedCompensation()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); await using var db = fixture.CreateDb();
        var offer = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId); offer.Status = "Accepted"; offer.AcceptedAtUtc = DateTime.UtcNow;
        var app = await db.JobApplications.SingleAsync(a => a.Id == seed.ApplicationId); app.Status = "Hired"; app.Stage = "Hired";
        (await db.JobOpenings.SingleAsync(o => o.Id == seed.OpeningId)).FilledCount = 1; await db.SaveChangesAsync();
        var service = new OfferLifecycleService(db, OfferLifecycleServiceTests.Config());
        var recovered = await service.RespondAsync(seed.Actor, seed.OfferId, Request());
        recovered.OnboardingDraftId.Should().NotBeNull(); recovered.AlreadyRecorded.Should().BeTrue();
        (await service.RespondAsync(seed.Actor, seed.OfferId, Request())).OnboardingDraftId.Should().Be(recovered.OnboardingDraftId);
        (await db.EmployeeDrafts.SingleAsync(d => d.Id == recovered.OnboardingDraftId)).Salary.Should().Be(1000);
        var legacyDraft = await db.EmployeeDrafts.SingleAsync(d => d.Id == recovered.OnboardingDraftId);
        legacyDraft.AcceptedOfferId.Should().Be(seed.OfferId);
        legacyDraft.AcceptedOfferCompensationJson.Should().BeNull("recovery must not invent historical compensation");
        (await db.JobOpenings.AsNoTracking().SingleAsync(o => o.Id == seed.OpeningId)).FilledCount.Should().Be(1);
    }
}
