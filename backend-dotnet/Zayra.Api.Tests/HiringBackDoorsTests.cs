using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Scope;
using Zayra.Api.Models;
using Zayra.Api.Application.Recruitment;

namespace Zayra.Api.Tests;

/// <summary>
/// Two recruitment writers used to side-step rules their sibling enforces. The legacy Recruitment > Onboarding tab wrote any status
/// to the same task rows the worklist guards (evidence before completion, a reason to skip a task that blocks activation, a fixed list of
/// statuses). The second interview-feedback endpoint let any Manager-role login overwrite any interview. Each door now applies the
/// rule its sibling applies. In-memory provider: no database server.
/// </summary>
public sealed class HiringBackDoorsTests
{
    private static ZayraDbContext NewDb() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseInMemoryDatabase($"hiring-doors-{Guid.NewGuid():N}").Options);

    private static ClaimsPrincipal Principal(Guid tenantId, string role, string name) => new(new ClaimsIdentity(new[]
    {
        new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        new Claim(ClaimTypes.Name, name), new Claim("name", name), new Claim(ClaimTypes.Role, role),
        new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
    }, "Test"));

    private static T With<T>(T controller, ClaimsPrincipal principal) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return controller;
    }

    // ── The legacy onboarding tab ──────────────────────────────────────────────────────────────────────────────────────

    private static async Task<(ZayraDbContext Db, Guid Tenant, OnboardingTask Task)> SeedTaskAsync(Action<OnboardingTask> shape)
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var task = new OnboardingTask { TenantId = tenant, ApplicationId = Guid.NewGuid(), TaskTitle = "Collect documents" };
        shape(task);
        db.OnboardingTasks.Add(task);
        await db.SaveChangesAsync();
        return (db, tenant, task);
    }

    private static string CodeOf(IActionResult result)
    {
        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(422);
        return JsonSerializer.Serialize(obj.Value);
    }

    [Fact]
    public async Task LegacyTab_CannotCompleteATaskThatNeedsEvidenceUntilEvidenceIsAttached()
    {
        var (db, tenant, task) = await SeedTaskAsync(t => t.EvidenceRequired = true);
        await using var _ = db;
        var controller = With(new OnboardingController(db), Principal(tenant, "HR Manager", "Hana HR"));

        var refused = await controller.UpdateTaskStatus(task.Id, new UpdateTaskStatusRequest("Completed", null), CancellationToken.None);

        CodeOf(refused).Should().Contain("task_evidence_required");
        (await db.OnboardingTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id)).Status.Should().Be("Pending");
    }

    [Fact]
    public async Task LegacyTab_CannotSkipATaskThatBlocksActivationWithoutAReason()
    {
        var (db, tenant, task) = await SeedTaskAsync(t => t.BlocksActivation = true);
        await using var _ = db;
        var controller = With(new OnboardingController(db), Principal(tenant, "HR Manager", "Hana HR"));

        CodeOf(await controller.UpdateTaskStatus(task.Id, new UpdateTaskStatusRequest("Skipped", null), CancellationToken.None))
            .Should().Contain("skip_reason_required");
        CodeOf(await controller.UpdateTaskStatus(task.Id, new UpdateTaskStatusRequest("Skipped", "n/a"), CancellationToken.None))
            .Should().Contain("skip_reason_required");
        (await db.OnboardingTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id)).Status.Should().Be("Pending");

        (await controller.UpdateTaskStatus(task.Id, new UpdateTaskStatusRequest("Skipped", "Candidate already holds a valid iqama"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await db.OnboardingTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id)).Status.Should().Be("Skipped");
    }

    [Fact]
    public async Task LegacyTab_RefusesAStatusOutsideTheFixedList()
    {
        var (db, tenant, task) = await SeedTaskAsync(_ => { });
        await using var _ = db;
        var controller = With(new OnboardingController(db), Principal(tenant, "HR Manager", "Hana HR"));

        CodeOf(await controller.UpdateTaskStatus(task.Id, new UpdateTaskStatusRequest("Done-ish", null), CancellationToken.None))
            .Should().Contain("validation_failed");
        (await db.OnboardingTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id)).Status.Should().Be("Pending");
    }

    [Fact]
    public async Task LegacyTab_StillCompletesAnOrdinaryTask()
    {
        var (db, tenant, task) = await SeedTaskAsync(_ => { });
        await using var _ = db;
        var controller = With(new OnboardingController(db), Principal(tenant, "HR Manager", "Hana HR"));

        (await controller.UpdateTaskStatus(task.Id, new UpdateTaskStatusRequest("Completed", "done"), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        var saved = await db.OnboardingTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        saved.Status.Should().Be("Completed");
        saved.CompletedDate.Should().NotBeNull();
    }

    // ── The second interview-feedback writer ───────────────────────────────────────────────────────────────────────────

    private static async Task<(ZayraDbContext Db, Guid Tenant, Guid Interview)> SeedInterviewAsync()
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var interview = new InterviewSchedule { TenantId = tenant, ApplicationId = Guid.NewGuid(), InterviewerNames = "Ivy Interviewer, Mona Manager" };
        db.InterviewSchedules.Add(interview);
        await db.SaveChangesAsync();
        return (db, tenant, interview.Id);
    }

    private static ApplicationsController Applications(ZayraDbContext db, Guid tenant, string role, string name) =>
        With(new ApplicationsController(db, new Zayra.Api.Infrastructure.Recruitment.RecruitmentService(db), new NoOpNotifications()), Principal(tenant, role, name));

    private static readonly InterviewFeedbackRequest Feedback = new(4, "Proceed", "Strong on fundamentals");

    [Fact]
    public async Task FeedbackThroughTheApplicationsRoute_IsRefusedForAManagerWhoIsNotOnThePanel()
    {
        var (db, tenant, interviewId) = await SeedInterviewAsync();
        await using var _ = db;

        var result = await Applications(db, tenant, "Manager", "Someone Else").RecordFeedback(interviewId, Feedback, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        var saved = await db.InterviewSchedules.AsNoTracking().SingleAsync(i => i.Id == interviewId);
        saved.Status.Should().Be("Scheduled");
        saved.FeedbackNotes.Should().BeEmpty();
    }

    [Fact]
    public async Task FeedbackThroughTheApplicationsRoute_IsAcceptedFromAManagerOnThePanel_AndFromHr()
    {
        var (db, tenant, interviewId) = await SeedInterviewAsync();
        await using var _ = db;

        (await Applications(db, tenant, "Manager", "Mona Manager").RecordFeedback(interviewId, Feedback, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await Applications(db, tenant, "HR Manager", "Hana HR").RecordFeedback(interviewId, Feedback, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
    }
}

internal sealed class NoOpNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName,
        string? entityId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName,
        Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
}
