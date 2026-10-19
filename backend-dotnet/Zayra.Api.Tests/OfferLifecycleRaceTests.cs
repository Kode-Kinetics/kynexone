using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferLifecycleRaceTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("Withdraw")]
    [InlineData("Decline")]
    public async Task AcceptanceAndConflictingResponseHaveOnlyOneDurableWinner(string other)
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        async Task<string> Run(bool accept)
        {
            await using var db = fixture.CreateDb(); var service = new OfferLifecycleService(db, OfferLifecycleServiceTests.Config());
            try
            {
                if (!accept && other == "Withdraw") await service.WithdrawAsync(seed.Actor, seed.OfferId, new(1, "Withdrawn by HR"));
                else await service.RespondAsync(seed.Actor, seed.OfferId, new(1, accept ? "Accepted" : "Declined", "StaffRecorded", DateTime.UtcNow, "Confirmed response"));
                return accept ? "Accepted" : other;
            }
            catch (OfferLifecycleException e) { return e.Code; }
        }
        var outcomes = await Task.WhenAll(Run(true), Run(false));
        outcomes.Count(x => x is "Accepted" or "Withdraw" or "Decline").Should().Be(1);
        outcomes.Any(x => x is "offer_version_conflict" or "invalid_offer_state" or "application_not_active" or "offer_response_blocked").Should().BeTrue();
        await using var verify = fixture.CreateDb(); var offer = await verify.OfferLetters.SingleAsync(o => o.Id == seed.OfferId);
        var filled = (await verify.JobOpenings.SingleAsync(o => o.Id == seed.OpeningId)).FilledCount;
        filled.Should().Be(offer.Status == "Accepted" ? 1 : 0);
        (await verify.EmployeeDrafts.CountAsync(d => d.TenantId == seed.Actor.TenantId)).Should().Be(filled);
    }

    [Fact]
    public async Task DraftedReplacementDoesNotInvalidateLegacyIssuedOffer()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); await using var db = fixture.CreateDb();
        var service = new OfferLifecycleService(db, OfferLifecycleServiceTests.Config());
        var revision = await service.ReviseAsync(seed.Actor, seed.OfferId, new(1, "Proposed alternative"));
        (await service.RespondAsync(seed.Actor, seed.OfferId, new(2, "Accepted", "StaffRecorded", DateTime.UtcNow, "Accepted existing offer"))).Status.Should().Be("Accepted");
        (await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == revision.Id)).Status.Should().Be("Draft");
        (await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId)).SupersededAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptanceRacingReplacementIssueOrReservationCannotLeaveTwoValidOutcomes(bool reserveOnly)
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); var storage = new OfferMemoryStorage(); Guid revisionId;
        await using (var setup = fixture.CreateDb())
        {
            setup.SystemSettings.Add(new() { TenantId = seed.Actor.TenantId, Category = OfferRules.PolicyCategory, SettingKey = OfferRules.PolicyKey, SettingValue = "false" });
            setup.Departments.Add(new() { TenantId = seed.Actor.TenantId, NameEn = "Engineering", Code = "ENG" });
            setup.Designations.Add(new() { TenantId = seed.Actor.TenantId, TitleEn = "Engineer", Code = "ENG" }); await setup.SaveChangesAsync();
            var artifacts = new OfferArtifactService(setup, storage, OfferLifecycleServiceTests.Config());
            var service = new OfferLifecycleService(setup, OfferLifecycleServiceTests.Config(), artifacts);
            var revision = await service.ReviseAsync(seed.Actor, seed.OfferId, new(1, "Reviewed replacement")); revisionId = revision.Id;
            await service.SaveDraftAsync(seed.Actor, revision.Id, new(1, 0, new(2027, 1, 1), DateTime.UtcNow.AddDays(7), 3,
                "candidate@example.invalid", "Engineer", "Engineering", BasicSalary: 1000));
            await artifacts.FreezeAsync(seed.Actor, revision.Id, 2);
        }
        async Task<string> Run(bool accept)
        {
            await using var db = fixture.CreateDb(); var artifacts = new OfferArtifactService(db, storage, OfferLifecycleServiceTests.Config());
            var service = new OfferLifecycleService(db, OfferLifecycleServiceTests.Config(), artifacts);
            try
            {
                if (accept) await service.RespondAsync(seed.Actor, seed.OfferId, new(2, "Accepted", "StaffRecorded", DateTime.UtcNow, "Confirmed old offer"));
                else if (reserveOnly) await service.ReserveIssuanceAsync(seed.Actor, revisionId, 4, Guid.NewGuid());
                else await service.RecordExternalSendAsync(seed.Actor, revisionId, new(4, "replacement", "Email", DateTime.UtcNow, "outbox/1", "Sent replacement"));
                return accept ? "Accepted" : "Replacement";
            }
            catch (OfferLifecycleException e) { return e.Code; }
        }
        var outcomes = await Task.WhenAll(Run(true), Run(false));
        outcomes.Count(x => x is "Accepted" or "Replacement").Should().Be(1);
        outcomes.Any(x => x is "offer_issuance_in_flight" or "application_not_active" or "offer_version_conflict" or "offer_not_current" or "offer_response_blocked").Should().BeTrue();
        await using var verify = fixture.CreateDb();
        var app = await verify.JobApplications.SingleAsync(a => a.Id == seed.ApplicationId);
        var old = await verify.OfferLetters.SingleAsync(a => a.Id == seed.OfferId);
        var replacement = await verify.OfferLetters.SingleAsync(a => a.Id == revisionId);
        if (outcomes.Contains("Accepted"))
        {
            app.Status.Should().Be("Hired"); old.Status.Should().Be("Accepted"); replacement.Status.Should().Be("Draft"); app.OfferReservationId.Should().BeNull();
        }
        else
        {
            app.Status.Should().Be("Active"); old.Status.Should().Be("Sent");
            if (reserveOnly) { app.OfferReservationOfferId.Should().Be(revisionId); old.SupersededAtUtc.Should().BeNull(); }
            else { app.CurrentIssuedOfferId.Should().Be(revisionId); old.SupersededAtUtc.Should().NotBeNull(); replacement.Status.Should().Be("Sent"); }
        }
    }

    [Fact]
    public async Task ConcurrentRevisionsFromDifferentParentsAllocateDistinctSeriesNumbers()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture); Guid first;
        await using (var db = fixture.CreateDb()) first = (await new OfferLifecycleService(db, OfferLifecycleServiceTests.Config())
            .ReviseAsync(seed.Actor, seed.OfferId, new(1, "Initial revision"))).Id;
        async Task<OfferLetter> Revise(Guid id, long version)
        {
            await using var db = fixture.CreateDb(); return await new OfferLifecycleService(db, OfferLifecycleServiceTests.Config())
                .ReviseAsync(seed.Actor, id, new(version, "Concurrent alternative"));
        }
        (await Task.WhenAll(Revise(seed.OfferId, 2), Revise(first, 1))).Select(o => o.RevisionNumber).Should().BeEquivalentTo(new int?[] { 2, 3 });
    }
}
