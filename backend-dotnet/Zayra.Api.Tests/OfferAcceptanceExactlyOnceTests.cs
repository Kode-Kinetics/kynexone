using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class OfferAcceptanceExactlyOnceTests
{
    private readonly PostgresFixture _fixture;

    public OfferAcceptanceExactlyOnceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task BothAcceptanceEndpoints_SequentialReplay_ReturnsExistingDraftWithoutRepeatingEffects()
    {
        var seeded = await SeedOfferAsync();
        var actorId = Guid.NewGuid();

        await using (var firstDb = _fixture.CreateDb())
        {
            var applications = new ApplicationsController(
                firstDb, new RecruitmentService(firstDb), new AcceptanceNullNotifications());
            SetPrincipal(applications, seeded.TenantId, actorId);

            var first = await applications.AcceptOffer(seeded.OfferId, CancellationToken.None);
            first.Should().BeOfType<OkObjectResult>();
        }

        await using (var replayDb = _fixture.CreateDb())
        {
            var offers = new OffersController(
                replayDb, new AcceptanceNullLetters(), new RecruitmentService(replayDb));
            SetPrincipal(offers, seeded.TenantId, actorId);

            var replay = await offers.Accept(seeded.OfferId, CancellationToken.None);
            replay.Should().BeOfType<OkObjectResult>("acceptance replay is an idempotent success");
        }

        await AssertExactlyOnceAsync(seeded);
    }

    [Fact]
    public async Task ConcurrentAcceptance_TwoDbContexts_HasOneDurableWinnerAndOneIdempotentReplay()
    {
        var seeded = await SeedOfferAsync();
        await using var dbA = _fixture.CreateDb();
        await using var dbB = _fixture.CreateDb();
        var serviceA = new RecruitmentService(dbA);
        var serviceB = new RecruitmentService(dbB);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<OfferAcceptanceResult> AcceptAsync(RecruitmentService service, string actor)
        {
            await gate.Task;
            return await service.AcceptOfferAsync(
                seeded.TenantId, seeded.OfferId, Guid.NewGuid(), actor, CancellationToken.None);
        }

        var attemptA = AcceptAsync(serviceA, "Concurrent A");
        var attemptB = AcceptAsync(serviceB, "Concurrent B");
        gate.SetResult();
        var results = await Task.WhenAll(attemptA, attemptB);

        results.Should().ContainSingle(x => x.Outcome == OfferAcceptanceOutcome.Accepted);
        results.Should().ContainSingle(x => x.Outcome == OfferAcceptanceOutcome.AlreadyAccepted);
        results.Select(x => x.OnboardingDraftId).Distinct().Should().ContainSingle();
        await AssertExactlyOnceAsync(seeded);
    }

    [Fact]
    public async Task SecondSentOfferForSameApplication_CannotCreateAnotherDraftOrConsumeAnotherSeat()
    {
        var seeded = await SeedOfferAsync();
        Guid secondOfferId;
        await using (var seedDb = _fixture.CreateDb())
        {
            var second = NewOffer(seeded.TenantId, seeded.ApplicationId);
            seedDb.OfferLetters.Add(second);
            await seedDb.SaveChangesAsync();
            secondOfferId = second.Id;
        }

        await using (var firstDb = _fixture.CreateDb())
        {
            var accepted = await new RecruitmentService(firstDb).AcceptOfferAsync(
                seeded.TenantId, seeded.OfferId, Guid.NewGuid(), "First", CancellationToken.None);
            accepted.Outcome.Should().Be(OfferAcceptanceOutcome.Accepted);
        }

        await using (var secondDb = _fixture.CreateDb())
        {
            var rejected = await new RecruitmentService(secondDb).AcceptOfferAsync(
                seeded.TenantId, secondOfferId, Guid.NewGuid(), "Second", CancellationToken.None);
            rejected.Outcome.Should().Be(OfferAcceptanceOutcome.InvalidApplicationState);
        }

        await AssertExactlyOnceAsync(seeded);
        await using var verify = _fixture.CreateDb();
        (await verify.OfferLetters.AsNoTracking().SingleAsync(x => x.Id == secondOfferId))
            .Status.Should().Be("Sent", "a losing offer must not be partially mutated");
    }

    [Fact]
    public async Task CrossTenantAcceptance_FailsClosedWithoutAnyMutation()
    {
        var seeded = await SeedOfferAsync();
        await using var db = _fixture.CreateDb();

        var result = await new RecruitmentService(db).AcceptOfferAsync(
            Guid.NewGuid(), seeded.OfferId, Guid.NewGuid(), "Other tenant", CancellationToken.None);

        result.Outcome.Should().Be(OfferAcceptanceOutcome.NotFound);
        await AssertExactlyOnceAsync(seeded, expectedEffects: 0);
    }

    [Fact]
    public async Task Decline_AfterAcceptance_IsRejectedWithoutUndoingOnboardingOrSeatConsumption()
    {
        var seeded = await SeedOfferAsync();
        await using (var acceptDb = _fixture.CreateDb())
        {
            var accepted = await new RecruitmentService(acceptDb).AcceptOfferAsync(
                seeded.TenantId, seeded.OfferId, Guid.NewGuid(), "Acceptor", CancellationToken.None);
            accepted.Outcome.Should().Be(OfferAcceptanceOutcome.Accepted);
        }

        await using (var declineDb = _fixture.CreateDb())
        {
            var controller = new OffersController(
                declineDb, new AcceptanceNullLetters(), new RecruitmentService(declineDb));
            SetPrincipal(controller, seeded.TenantId, Guid.NewGuid());

            var result = await controller.Decline(
                seeded.OfferId, new DeclineOfferRequest("changed mind"), CancellationToken.None);

            result.Should().BeOfType<ConflictObjectResult>();
        }

        await AssertExactlyOnceAsync(seeded);
    }

    [Fact]
    public async Task DecideApproval_InvalidDecision_IsRejectedAndLeavesPendingRowsUnchanged()
    {
        var seeded = await SeedOfferAsync();
        Guid approvalId;
        await using (var seedDb = _fixture.CreateDb())
        {
            var offer = await seedDb.OfferLetters.SingleAsync(x => x.Id == seeded.OfferId);
            offer.Status = "PendingApproval";
            var approval = new OfferApproval
            {
                TenantId = seeded.TenantId,
                OfferLetterId = seeded.OfferId,
                ApplicationId = seeded.ApplicationId,
                StepOrder = 1,
                ApproverName = "HR Manager",
                ApproverRole = "HR Manager",
            };
            seedDb.OfferApprovals.Add(approval);
            await seedDb.SaveChangesAsync();
            approvalId = approval.Id;
        }

        await using (var decideDb = _fixture.CreateDb())
        {
            var controller = new OffersController(
                decideDb, new AcceptanceNullLetters(), new RecruitmentService(decideDb));
            SetPrincipal(controller, seeded.TenantId, Guid.NewGuid());

            var result = await controller.DecideApproval(
                seeded.OfferId, approvalId, new DecideApprovalRequest("Escalated", null), CancellationToken.None);

            result.Should().BeOfType<BadRequestObjectResult>();
        }

        await using var verify = _fixture.CreateDb();
        (await verify.OfferApprovals.AsNoTracking().SingleAsync(x => x.Id == approvalId)).Status.Should().Be("Pending");
        (await verify.OfferLetters.AsNoTracking().SingleAsync(x => x.Id == seeded.OfferId)).Status.Should().Be("PendingApproval");
    }

    private async Task AssertExactlyOnceAsync(SeededOffer seeded, int expectedEffects = 1)
    {
        await using var verify = _fixture.CreateDb();
        var offer = await verify.OfferLetters.AsNoTracking().SingleAsync(x => x.Id == seeded.OfferId);
        var application = await verify.JobApplications.AsNoTracking().SingleAsync(x => x.Id == seeded.ApplicationId);
        var opening = await verify.JobOpenings.AsNoTracking().SingleAsync(x => x.Id == seeded.OpeningId);

        offer.Status.Should().Be(expectedEffects == 1 ? "Accepted" : "Sent");
        application.OnboardingDraftId.HasValue.Should().Be(expectedEffects == 1);
        application.Status.Should().Be(expectedEffects == 1 ? "Hired" : "Active");
        opening.FilledCount.Should().Be(expectedEffects);
        (await verify.EmployeeDrafts.CountAsync(x => x.TenantId == seeded.TenantId))
            .Should().Be(expectedEffects);
        (await verify.ApplicationEvents.CountAsync(x =>
                x.TenantId == seeded.TenantId
                && x.ApplicationId == seeded.ApplicationId
                && x.EventType == "OfferAccepted"))
            .Should().Be(expectedEffects);
        (await verify.RecruitmentAuditLogs.CountAsync(x =>
                x.TenantId == seeded.TenantId
                && x.EntityType == "Offer"
                && x.EntityId == seeded.OfferId.ToString()
                && x.Action == "Accepted"))
            .Should().Be(expectedEffects);
    }

    private async Task<SeededOffer> SeedOfferAsync()
    {
        await using var db = _fixture.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var candidate = new Candidate
        {
            TenantId = tenantId,
            FirstName = "Noura",
            LastName = "Al Mansoori",
            Email = $"noura-{Guid.NewGuid():N}@example.test",
            Phone = "+971500000001",
            Nationality = "AE",
            Status = "Active",
        };
        var opening = new JobOpening
        {
            TenantId = tenantId,
            JobCode = $"JOB-{Guid.NewGuid():N}",
            Title = "Enterprise HR Lead",
            DepartmentName = "People",
            HeadCount = 3,
            Status = "InProgress",
        };
        var application = new JobApplication
        {
            TenantId = tenantId,
            JobOpeningId = opening.Id,
            JobTitle = opening.Title,
            CandidateId = candidate.Id,
            CandidateName = "Noura Al Mansoori",
            CandidateEmail = candidate.Email,
            Stage = "Offer",
            StageOrder = 5,
            Status = "Active",
        };
        var offer = NewOffer(tenantId, application.Id);
        db.Candidates.Add(candidate);
        db.JobOpenings.Add(opening);
        db.JobApplications.Add(application);
        db.OfferLetters.Add(offer);
        await db.SaveChangesAsync();
        return new SeededOffer(tenantId, opening.Id, application.Id, offer.Id);
    }

    private static OfferLetter NewOffer(Guid tenantId, Guid applicationId) => new()
    {
        TenantId = tenantId,
        ApplicationId = applicationId,
        CandidateName = "Noura Al Mansoori",
        OfferedJobTitle = "Enterprise HR Lead",
        OfferedDepartment = "People",
        StartDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)),
        BasicSalary = 20_000,
        HousingAllowance = 5_000,
        TransportAllowance = 1_500,
        GrossSalary = 26_500,
        ContentHtml = "<p>Offer</p>",
        Status = "Sent",
        SentAtUtc = DateTime.UtcNow,
    };

    private static void SetPrincipal(ControllerBase controller, Guid tenantId, Guid userId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Test HR Manager"),
            new Claim(ClaimTypes.Role, "HR Manager"),
        }, "Test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal },
        };
    }

    private sealed record SeededOffer(Guid TenantId, Guid OpeningId, Guid ApplicationId, Guid OfferId);
}

file sealed class AcceptanceNullNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName,
        string? entityId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName,
        Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
}

file sealed class AcceptanceNullLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData data, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData data, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> GenerateExperienceLetterAsync(LetterData data, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());

    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData data, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());
}
