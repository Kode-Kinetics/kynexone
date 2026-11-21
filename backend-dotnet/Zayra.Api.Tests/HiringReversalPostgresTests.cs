using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>A rejected or withdrawn hire draft releases its seat and returns the candidate to the Offer stage; the acceptance stays on the record, and a second reject changes nothing. Real Postgres.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class HiringReversalPostgresTests(PostgresFixture fixture) : HiringPostgresTestBase(fixture)
{
    // ── Reversal: the hire draft is rejected or withdrawn ──────────────────────────────────────────────────────────────

    private async Task AssertSeatReleasedAsync(Seeded s, Guid draftId, string draftStatus, int auditRows = 1)
    {
        await using var verify = Fixture.CreateDb();
        var app = await verify.JobApplications.AsNoTracking().SingleAsync(a => a.Id == s.ApplicationId);
        app.Status.Should().Be("Active");
        app.Stage.Should().Be("Offer", "the candidate returns to the stage before Hired");
        app.StageOrder.Should().Be(5);
        app.OnboardingDraftId.Should().BeNull();
        app.HiredAtUtc.Should().BeNull();
        var opening = await verify.JobOpenings.AsNoTracking().SingleAsync(o => o.Id == s.OpeningId);
        opening.FilledCount.Should().Be(0, "the seat is released exactly once");
        opening.Status.Should().NotBe("Closed", "a filled opening reopens when its seat is released");
        (await verify.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == s.OfferId)).Status.Should().Be("Sent", "the offer can be accepted again");
        (await verify.EmployeeDrafts.AsNoTracking().SingleAsync(d => d.Id == draftId)).Status.Should().Be(draftStatus);
        (await verify.ApplicationEvents.CountAsync(e => e.TenantId == s.TenantId && e.ApplicationId == s.ApplicationId && e.EventType == "HireReleased")).Should().Be(auditRows);
        (await verify.RecruitmentAuditLogs.CountAsync(a => a.TenantId == s.TenantId && a.EntityId == s.OfferId.ToString() && a.Action == "HireReleased")).Should().Be(auditRows);
        (await verify.ApplicationEvents.CountAsync(e => e.TenantId == s.TenantId && e.ApplicationId == s.ApplicationId && e.EventType == "OfferAccepted"))
            .Should().Be(1, "the audit trail of the acceptance is kept");
    }

    [Fact]
    public async Task RejectingTheHireDraft_ReleasesTheSeat_AndReturnsTheCandidateToTheOfferStage()
    {
        var s = await SeedAsync();
        var draftId = (await AcceptAsync(s)).OnboardingDraftId!.Value;

        await using (var db = Fixture.CreateDb())
            (await EmployeeDraftLifecyclePostgresTests.Employees(db, s.TenantId, Guid.NewGuid())
                .RejectDraft(draftId, new EmployeeDraftDecisionRequest("Reference check failed"), CancellationToken.None))
                .Should().BeOfType<NoContentResult>();

        await AssertSeatReleasedAsync(s, draftId, "Rejected");
    }

    [Fact]
    public async Task WithdrawingTheHireDraft_ReleasesTheSeatToo()
    {
        var s = await SeedAsync();
        var maker = Guid.NewGuid();
        var draftId = (await AcceptAsync(s, maker)).OnboardingDraftId!.Value;

        await using (var db = Fixture.CreateDb())
            (await EmployeeDraftLifecyclePostgresTests.Employees(db, s.TenantId, maker)
                .CancelDraft(draftId, new EmployeeDraftDecisionRequest("Candidate withdrew"), CancellationToken.None))
                .Should().BeOfType<NoContentResult>();

        await AssertSeatReleasedAsync(s, draftId, "Cancelled");
    }

    [Fact]
    public async Task ASecondRejectIsAConflict_AndNeverReleasesTheSeatTwice()
    {
        var s = await SeedAsync(headCount: 3);
        var other = await SeedAsync(headCount: 3);   // another tenant's opening must be untouched
        var draftId = (await AcceptAsync(s)).OnboardingDraftId!.Value;
        await AcceptAsync(other);

        for (var attempt = 0; attempt < 2; attempt++)
            await using (var db = Fixture.CreateDb())
            {
                var result = await EmployeeDraftLifecyclePostgresTests.Employees(db, s.TenantId, Guid.NewGuid())
                    .RejectDraft(draftId, new EmployeeDraftDecisionRequest("Reference check failed"), CancellationToken.None);
                if (attempt == 0) result.Should().BeOfType<NoContentResult>();
                else ((IStatusCodeActionResult)result).StatusCode.Should().Be(StatusCodes.Status409Conflict);
            }

        await using var verify = Fixture.CreateDb();
        (await verify.JobOpenings.AsNoTracking().SingleAsync(o => o.Id == s.OpeningId)).FilledCount.Should().Be(0, "released once, never below zero");
        (await verify.JobOpenings.AsNoTracking().SingleAsync(o => o.Id == other.OpeningId)).FilledCount.Should().Be(1);
        (await verify.ApplicationEvents.CountAsync(e => e.ApplicationId == s.ApplicationId && e.EventType == "HireReleased")).Should().Be(1);
    }

    [Fact]
    public async Task AfterARejectedHire_TheCandidateCanBeHiredAgain_WithAFreshDraftAndOneSeat()
    {
        var s = await SeedAsync();
        var first = (await AcceptAsync(s)).OnboardingDraftId!.Value;
        await using (var db = Fixture.CreateDb())
            await EmployeeDraftLifecyclePostgresTests.Employees(db, s.TenantId, Guid.NewGuid())
                .RejectDraft(first, new EmployeeDraftDecisionRequest("Salary above the approved band"), CancellationToken.None);

        var again = await AcceptAsync(s);

        again.Outcome.Should().Be(OfferAcceptanceOutcome.Accepted, again.Message);
        again.OnboardingDraftId.Should().NotBe(first);
        await using var verify = Fixture.CreateDb();
        (await verify.JobOpenings.AsNoTracking().SingleAsync(o => o.Id == s.OpeningId)).FilledCount.Should().Be(1);
        (await verify.JobApplications.AsNoTracking().SingleAsync(a => a.Id == s.ApplicationId)).Status.Should().Be("Hired");
        (await verify.EmployeeDrafts.CountAsync(d => d.TenantId == s.TenantId)).Should().Be(2);
    }

    [Fact]
    public async Task AHireThatAlreadyCreatedAnEmployeeIsNeverReversedHere()
    {
        var s = await SeedAsync();
        var draftId = (await AcceptAsync(s)).OnboardingDraftId!.Value;
        await using (var db = Fixture.CreateDb())
        {
            var draft = await db.EmployeeDrafts.SingleAsync(d => d.Id == draftId);
            draft.Status = EmployeeDraftStatuses.Activated;
            await db.SaveChangesAsync();
        }

        HireReleaseResult result;
        await using (var db = Fixture.CreateDb())
        {
            result = await OfferHireReversal.ReleaseAsync(db, s.TenantId, draftId, Guid.NewGuid(), "Checker", "Reversal attempt", CancellationToken.None);
            await db.SaveChangesAsync();
        }

        result.Outcome.Should().Be(HireReleaseOutcome.EmployeeAlreadyCreated);
        result.Message.Should().Contain("employee");
        await using var verify = Fixture.CreateDb();
        (await verify.JobApplications.AsNoTracking().SingleAsync(a => a.Id == s.ApplicationId)).Status.Should().Be("Hired");
        (await verify.JobOpenings.AsNoTracking().SingleAsync(o => o.Id == s.OpeningId)).FilledCount.Should().Be(1);
    }
}
