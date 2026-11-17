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

/// <summary>A blacklisted candidate cannot apply, be interviewed, be moved forward, be given an offer, or be hired from a sent offer. Real Postgres.</summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class HiringBlacklistPostgresTests(PostgresFixture fixture) : HiringPostgresTestBase(fixture)
{
    // ── A blacklisted candidate is never hired ─────────────────────────────────────────────────────────────────────────

    private static ClaimsPrincipal Hr(Guid tenant) => new(new ClaimsIdentity(new[]
    {
        new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        new Claim(ClaimTypes.Name, "HR Manager"), new Claim(ClaimTypes.Role, "HR Manager"),
        new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
    }, "Test"));

    private static T With<T>(T controller, Guid tenant) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Hr(tenant) } };
        return controller;
    }

    private static void AssertRefused(IActionResult result)
    {
        var status = result.Should().BeAssignableTo<IStatusCodeActionResult>().Subject.StatusCode;
        status.Should().Be(StatusCodes.Status409Conflict);
        JsonSerializer.Serialize((result as ObjectResult)!.Value).Should().Contain("candidate_blacklisted");
    }

    private Task<Seeded> SeedBlacklistedAsync(string stage = "Offer", Action<OfferLetter>? offer = null) =>
        SeedAsync(candidate: c => c.Status = "Blacklisted", offer: offer, opening: o => o.Status = "Open");

    [Fact]
    public async Task ABlacklistedCandidate_CannotApplyForAnotherOpening()
    {
        var s = await SeedBlacklistedAsync();
        Guid other;
        await using (var db = Fixture.CreateDb())
        {
            var opening = new JobOpening { TenantId = s.TenantId, JobCode = $"JOB-{Guid.NewGuid():N}", Title = "Another role", Status = "Open", HeadCount = 1 };
            db.JobOpenings.Add(opening);
            await db.SaveChangesAsync();
            other = opening.Id;
        }
        await using var run = Fixture.CreateDb();
        var result = await With(new ApplicationsController(run, new RecruitmentService(run), new RecordingNotifications()), s.TenantId)
            .Apply(new ApplyRequest(other, s.CandidateId, null), CancellationToken.None);
        AssertRefused(result);
        (await run.JobApplications.CountAsync(a => a.CandidateId == s.CandidateId)).Should().Be(1, "no second application was created");
    }

    [Fact]
    public async Task ABlacklistedCandidate_CannotBeScheduledForAnInterview_ByEitherRoute()
    {
        var s = await SeedBlacklistedAsync();
        var when = DateTime.UtcNow.AddDays(3);
        await using (var db = Fixture.CreateDb())
            AssertRefused(await With(new ApplicationsController(db, new RecruitmentService(db), new RecordingNotifications()), s.TenantId)
                .ScheduleInterview(s.ApplicationId, new ScheduleInterviewRequest(s.ApplicationId, "Technical", "Ivy", when, 45, "Video", null, null), CancellationToken.None));
        await using (var db = Fixture.CreateDb())
            AssertRefused(await With(new InterviewsController(db), s.TenantId)
                .Schedule(new ScheduleInterviewRequest(s.ApplicationId, "Technical", "Ivy", when, 45, "Video", null, null), CancellationToken.None));
        await using var verify = Fixture.CreateDb();
        (await verify.InterviewSchedules.CountAsync(i => i.ApplicationId == s.ApplicationId)).Should().Be(0);
    }

    [Fact]
    public async Task ABlacklistedCandidate_CannotBeAdvancedOrGivenAnOffer()
    {
        var s = await SeedBlacklistedAsync();
        long version;
        await using (var db = Fixture.CreateDb())
            version = (await db.JobApplications.AsNoTracking().SingleAsync(a => a.Id == s.ApplicationId)).Version;
        await using (var db = Fixture.CreateDb())
            AssertRefused(await With(new ApplicationsController(db, new RecruitmentService(db), new RecordingNotifications()), s.TenantId)
                .Advance(s.ApplicationId, new AdvanceRequest("next", null, version), CancellationToken.None));
        await using (var db = Fixture.CreateDb())
            AssertRefused(await With(new ApplicationsController(db, new RecruitmentService(db), new RecordingNotifications()), s.TenantId)
                .GenerateOffer(s.ApplicationId, new GenerateOfferRequest("People", new DateOnly(2027, 3, 1), 20_000, 5_000, 1_500, 0, 3), CancellationToken.None));
    }

    [Fact]
    public async Task ABlacklistedCandidate_CannotBeHired_EvenWithASentOffer()
    {
        var s = await SeedBlacklistedAsync();
        var result = await AcceptAsync(s);

        result.Outcome.Should().NotBe(OfferAcceptanceOutcome.Accepted);
        result.OnboardingDraftId.Should().BeNull();
        await using var verify = Fixture.CreateDb();
        (await verify.JobOpenings.AsNoTracking().SingleAsync(o => o.Id == s.OpeningId)).FilledCount.Should().Be(0);
        (await verify.EmployeeDrafts.CountAsync(d => d.TenantId == s.TenantId)).Should().Be(0);
        (await verify.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == s.OfferId)).Status.Should().Be("Sent");
    }
}
