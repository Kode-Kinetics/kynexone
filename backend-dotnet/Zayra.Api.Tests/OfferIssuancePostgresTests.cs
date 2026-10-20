using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;
using Zayra.Api.Data;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferIssuancePostgresTests(PostgresFixture fixture)
{
    private readonly Dictionary<Guid, OfferMemoryStorage> stores = [];
    private OfferLifecycleService Service(ZayraDbContext db, OfferLifecycleSeed seed) => new(db,
        OfferLifecycleServiceTests.Config(), new OfferArtifactService(db, stores[seed.OfferId], OfferLifecycleServiceTests.Config()));
    private async Task<OfferLifecycleSeed> FrozenAsync(bool approvalOff = true)
    {
        var seed = await OfferArtifactPostgresTests.ManagedDraftAsync(fixture);
        await using var db = fixture.CreateDb();
        if (approvalOff) db.SystemSettings.Add(new() { TenantId = seed.Actor.TenantId,
            Category = OfferRules.PolicyCategory, SettingKey = OfferRules.PolicyKey, SettingValue = "false", DataType = "bool" });
        await db.SaveChangesAsync();
        stores[seed.OfferId] = new OfferMemoryStorage();
        await new OfferArtifactService(db, stores[seed.OfferId], OfferLifecycleServiceTests.Config()).FreezeAsync(seed.Actor, seed.OfferId, 2);
        return seed;
    }

    [Fact]
    public async Task ExternalIssueIsIdempotentPreservesDeadlineAndRecordsFrozenRecipient()
    {
        var seed = await FrozenAsync(); await using var db = fixture.CreateDb();
        var service = Service(db, seed);
        var before = await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId);
        var request = new OfferExternalSendRequest(4, "external-1", "Email", DateTime.UtcNow, "outbox/42", "Confirmed sending");
        (await service.RecordExternalSendAsync(seed.Actor, seed.OfferId, request)).Status.Should().Be("Sent");
        (await service.RecordExternalSendAsync(seed.Actor, seed.OfferId, request)).Status.Should().Be("Sent");
        var after = await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId);
        after.ResponseDeadline.Should().Be(before.ResponseDeadline);
        (await db.JobApplications.AsNoTracking().SingleAsync(a => a.Id == seed.ApplicationId)).CurrentIssuedOfferId.Should().Be(seed.OfferId);
        var events = await db.OfferLifecycleEvents.Where(e => e.OfferId == seed.OfferId && e.EventType == "ExternalSent").ToListAsync();
        events.Should().ContainSingle(); events[0].DataJson.Should().Contain("candidate@example.invalid").And.Contain("Test Candidate");
        var conflict = await FluentActions.Awaiting(() => service.RecordExternalSendAsync(seed.Actor, seed.OfferId, request with { Reference = "changed" }))
            .Should().ThrowAsync<OfferLifecycleException>();
        conflict.Which.Code.Should().Be("offer_idempotency_conflict");
    }

    [Fact]
    public async Task ReservedIssueBlocksWithdrawalAndAcceptanceUntilOwnerReleasesIt()
    {
        var seed = await FrozenAsync(); await using var db = fixture.CreateDb();
        var service = Service(db, seed);
        var reservation = await service.ReserveIssuanceAsync(seed.Actor, seed.OfferId, 4, Guid.NewGuid());
        reservation.State.Should().Be("Queued"); reservation.OfferId.Should().Be(seed.OfferId);
        var blocked = await FluentActions.Awaiting(() => service.WithdrawAsync(seed.Actor, seed.OfferId, new(reservation.OfferVersion, "Withdraw")))
            .Should().ThrowAsync<OfferLifecycleException>();
        blocked.Which.Code.Should().Be("offer_issuance_in_flight");
        (await db.JobApplications.AsNoTracking().SingleAsync(a => a.Id == seed.ApplicationId)).OfferReservationId.Should().Be(reservation.OperationId);
    }

    [Fact]
    public async Task ExternalIssueCannotBypassApprovalOrMakerChecker()
    {
        var seed = await FrozenAsync(false); await using var db = fixture.CreateDb();
        var service = Service(db, seed);
        var request = new OfferExternalSendRequest(4, "approval-test", "InPerson", DateTime.UtcNow, "signed-handover/1", "Handed to candidate");
        var missing = await FluentActions.Awaiting(() => service.RecordExternalSendAsync(seed.Actor, seed.OfferId, request))
            .Should().ThrowAsync<OfferLifecycleException>();
        missing.Which.Code.Should().Be("offer_approval_required");
        var offer = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId); offer.Status = "Approved";
        db.OfferApprovals.Add(new() { TenantId = seed.Actor.TenantId, OfferLetterId = offer.Id,
            ApplicationId = seed.ApplicationId, StepOrder = 1, Status = "Approved", ApproverUserId = seed.Actor.UserId });
        await db.SaveChangesAsync();
        var maker = await FluentActions.Awaiting(() => service.RecordExternalSendAsync(seed.Actor, seed.OfferId, request))
            .Should().ThrowAsync<OfferLifecycleException>();
        maker.Which.Code.Should().Be("offer_maker_checker");
    }

    [Theory]
    [InlineData("Other", null, 0)]
    [InlineData("Email", "ref", -10)]
    [InlineData("Email", "ref", 10)]
    public async Task ExternalEvidenceCannotInventChannelIdentityOrBackdateBeforeFreeze(string channel, string? reference, int days)
    {
        var seed = await FrozenAsync(); await using var db = fixture.CreateDb();
        var failure = await FluentActions.Awaiting(() => Service(db, seed)
            .RecordExternalSendAsync(seed.Actor, seed.OfferId, new(4, "invalid-evidence", channel, DateTime.UtcNow.AddDays(days), reference, "Recorded")))
            .Should().ThrowAsync<OfferLifecycleException>();
        failure.Which.Code.Should().Be("offer_external_evidence_invalid");
        (await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId)).Status.Should().Be("Draft");
    }
}
