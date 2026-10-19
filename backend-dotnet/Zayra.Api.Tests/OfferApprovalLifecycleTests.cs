using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Tests;

[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class OfferApprovalLifecycleTests(PostgresFixture fixture)
{
    [Fact]
    public async Task NamedIndependentCheckerApprovesFrozenVersionAndRejectRemainsImmutable()
    {
        var seed = await OfferArtifactPostgresTests.ManagedDraftAsync(fixture);
        await using var db = fixture.CreateDb();
        var role = new Role { TenantId = seed.Actor.TenantId, Name = "HR Manager", NormalizedName = "HR MANAGER" };
        var checker = new User { TenantId = seed.Actor.TenantId, Email = $"checker-{Guid.NewGuid():N}@example.invalid",
            NormalizedEmail = Guid.NewGuid().ToString(), FullName = "Independent checker" };
        checker.UserRoles.Add(new() { UserId = checker.Id, RoleId = role.Id });
        db.Roles.Add(role); db.Users.Add(checker);
        db.RecruitmentAuditLogs.Add(OfferRules.AuditRow(seed.Actor.TenantId, seed.OfferId, OfferRules.CreatedAction, seed.Actor.UserId, seed.Actor.Name));
        await db.SaveChangesAsync();
        var storage = new OfferMemoryStorage();
        var artifact = new OfferArtifactService(db, storage, OfferLifecycleServiceTests.Config());
        await artifact.FreezeAsync(seed.Actor, seed.OfferId, 2);
        var service = new OfferLifecycleService(db, OfferLifecycleServiceTests.Config(), artifact);
        var approval = await service.AddApprovalAsync(seed.Actor, seed.OfferId, 4, checker.Id, checker.FullName, "HR Manager");
        (await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId)).Status.Should().Be("PendingApproval");
        var wrong = await FluentActions.Awaiting(() => service.DecideApprovalAsync(seed.Actor, seed.OfferId, approval.Id, 5, "Approved", "Reviewed"))
            .Should().ThrowAsync<OfferLifecycleException>();
        wrong.Which.Status.Should().Be(403);
        await service.DecideApprovalAsync(seed.Actor with { UserId = checker.Id }, seed.OfferId, approval.Id, 5, "Rejected", "Revise terms");
        var rejected = await db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == seed.OfferId);
        rejected.Status.Should().Be("Draft"); rejected.FrozenAtUtc.Should().NotBeNull();
        var repeat = await FluentActions.Awaiting(() => service.AddApprovalAsync(seed.Actor, seed.OfferId, 6, checker.Id, checker.FullName, "HR Manager"))
            .Should().ThrowAsync<OfferLifecycleException>();
        repeat.Which.Code.Should().Be("offer_approval_rejected");
        (await db.OfferLifecycleEvents.CountAsync(e => e.OfferId == seed.OfferId && e.EventType == "ApprovalDecided")).Should().Be(1);
    }

    [Fact]
    public async Task ApprovalCannotEndorseUnfinishedOriginal()
    {
        var seed = await OfferArtifactPostgresTests.ManagedDraftAsync(fixture);
        await using var db = fixture.CreateDb();
        var failure = await FluentActions.Awaiting(() => new OfferLifecycleService(db, OfferLifecycleServiceTests.Config())
            .AddApprovalAsync(seed.Actor, seed.OfferId, 2, Guid.NewGuid(), "Checker", "Admin"))
            .Should().ThrowAsync<OfferLifecycleException>();
        failure.Which.Code.Should().Be("offer_artifact_pending");
        (await db.OfferApprovals.CountAsync(a => a.OfferLetterId == seed.OfferId)).Should().Be(0);
    }

    [Fact]
    public async Task LegacySubmittedTermsRequireNewRevisionBeforeApproval()
    {
        var seed = await OfferLifecycleTestData.SeedAsync(fixture);
        await using var db = fixture.CreateDb();
        var offer = await db.OfferLetters.SingleAsync(o => o.Id == seed.OfferId); offer.Status = "PendingApproval";
        await db.SaveChangesAsync();
        var failure = await FluentActions.Awaiting(() => new OfferLifecycleService(db, OfferLifecycleServiceTests.Config())
            .AddApprovalAsync(seed.Actor, seed.OfferId, 1, Guid.NewGuid(), "Checker", "Admin"))
            .Should().ThrowAsync<OfferLifecycleException>();
        failure.Which.Code.Should().Be("offer_revision_required");
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Unknown")]
    public async Task ReservationBlocksApprovalDecision(string reservationState)
    {
        var seed = await OfferArtifactPostgresTests.ManagedDraftAsync(fixture);
        await using var db = fixture.CreateDb();
        var app = await db.JobApplications.SingleAsync(a => a.Id == seed.ApplicationId);
        app.OfferReservationId = Guid.NewGuid(); app.OfferReservationOfferId = seed.OfferId; app.OfferReservationState = reservationState;
        app.OfferReservationVersion = app.Version;
        await db.SaveChangesAsync();
        var failure = await FluentActions.Awaiting(() => new OfferLifecycleService(db, OfferLifecycleServiceTests.Config())
            .DecideApprovalAsync(seed.Actor, seed.OfferId, Guid.NewGuid(), 2, "Approved", "Reviewed"))
            .Should().ThrowAsync<OfferLifecycleException>();
        failure.Which.Code.Should().Be("offer_issuance_in_flight");
    }
}
