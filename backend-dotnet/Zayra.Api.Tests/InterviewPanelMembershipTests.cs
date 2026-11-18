using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A panellist is on an interview by exact identity, never by a name that merely contains theirs: by the login ids the interview stored, else by their
/// exact normalised full name, and not at all when more than one employee or login of the company has that name (HR is told to fix the panel).
/// New interviews store the ids of the panel names that match one login. In-memory provider: no database server.
/// </summary>
public sealed class InterviewPanelMembershipTests
{
    private static readonly SubmitFeedbackRequest Scorecard = new(null, null, 4, 4, 4, 4, 4, 4, "s", "c", "n", "Proceed");

    private static ZayraDbContext NewDb() => new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"panel-{Guid.NewGuid():N}").Options);

    private static InterviewsController As(ZayraDbContext db, Guid tenant, Guid userId, string role, string name)
    {
        var controller = new InterviewsController(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim("name", name),
                    new Claim(ClaimTypes.Role, role), new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
                }, "Test")),
            },
        };
        return controller;
    }

    private static async Task<Guid> InterviewAsync(ZayraDbContext db, Guid tenant, string names, string? ids = null)
    {
        var interview = new InterviewSchedule { TenantId = tenant, ApplicationId = Guid.NewGuid(), InterviewerNames = names, InterviewerUserIds = ids };
        db.InterviewSchedules.Add(interview);
        await db.SaveChangesAsync();
        return interview.Id;
    }

    private static void Person(ZayraDbContext db, Guid tenant, string name, int count = 1)
    {
        for (var i = 0; i < count; i++)
            db.Employees.Add(new Employee { TenantId = tenant, EmployeeCode = $"E-{Guid.NewGuid():N}"[..12], FullName = name, EnglishName = name, Status = EmployeeStatuses.Active });
    }

    private static string CodeOf(IActionResult result) => JsonSerializer.Serialize(result.Should().BeOfType<ObjectResult>().Subject.Value);

    [Fact]
    public async Task ASimilarNameIsNotTheSamePerson()
    {
        await using var db = NewDb(); var tenant = Guid.NewGuid();
        Person(db, tenant, "Mohammed Ali"); await db.SaveChangesAsync();
        var interview = await InterviewAsync(db, tenant, "Mohammed Alias, Ivy Interviewer");

        (await As(db, tenant, Guid.NewGuid(), "Manager", "Mohammed Ali").SubmitFeedback(interview, Scorecard, CancellationToken.None)).Should().BeOfType<ForbidResult>();
        (await As(db, tenant, Guid.NewGuid(), "Manager", "Mohammed Ali").Get(interview, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await db.InterviewFeedbacks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TwoPeopleWithTheSameNameAreRefusedWithAClearMessage()
    {
        await using var db = NewDb(); var tenant = Guid.NewGuid();
        Person(db, tenant, "Mohammed Ali", count: 2); await db.SaveChangesAsync();
        var interview = await InterviewAsync(db, tenant, "Mohammed Ali");

        var refused = await As(db, tenant, Guid.NewGuid(), "Manager", "Mohammed Ali").SubmitFeedback(interview, Scorecard, CancellationToken.None);

        refused.Should().BeAssignableTo<IStatusCodeActionResult>().Which.StatusCode.Should().Be(403);
        CodeOf(refused).Should().Contain("interviewer_name_ambiguous").And.Contain("messageAr");
        (await db.InterviewFeedbacks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AnExactNameThatOnePersonHolds_IsOnThePanel_SpacingAndCaseIgnored()
    {
        await using var db = NewDb(); var tenant = Guid.NewGuid();
        Person(db, tenant, "Mohammed Ali"); await db.SaveChangesAsync();
        var interview = await InterviewAsync(db, tenant, "Ivy Interviewer;  mohammed   ALI");

        (await As(db, tenant, Guid.NewGuid(), "Manager", "Mohammed Ali").SubmitFeedback(interview, Scorecard, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await db.InterviewFeedbacks.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task WhenTheInterviewStoredItsPanelIds_OnlyThoseLoginsAreOnIt_WhateverTheirNames()
    {
        await using var db = NewDb(); var tenant = Guid.NewGuid();
        Person(db, tenant, "Mohammed Ali", count: 2); await db.SaveChangesAsync();
        var onPanel = Guid.NewGuid(); var other = Guid.NewGuid();
        var interview = await InterviewAsync(db, tenant, "Mohammed Ali", ids: onPanel.ToString());

        (await As(db, tenant, onPanel, "Manager", "Mohammed Ali").SubmitFeedback(interview, Scorecard, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>("the id settles it even though the name is shared");
        (await As(db, tenant, other, "Manager", "Mohammed Ali").SubmitFeedback(interview, Scorecard, CancellationToken.None))
            .Should().BeOfType<ForbidResult>("a login that is not on the stored panel is refused even with the panel's name");
    }

    [Fact]
    public async Task ANewInterviewStoresTheIdOfEachPanelNameThatMatchesExactlyOneLogin()
    {
        await using var db = NewDb(); var tenant = Guid.NewGuid();
        var mona = new User { TenantId = tenant, Email = "mona@example.test", NormalizedEmail = "MONA@EXAMPLE.TEST", FullName = "Mona Manager", PasswordHash = "x", IsActive = true };
        var sam1 = new User { TenantId = tenant, Email = "sam1@example.test", NormalizedEmail = "SAM1@EXAMPLE.TEST", FullName = "Sam Shared", PasswordHash = "x", IsActive = true };
        var sam2 = new User { TenantId = tenant, Email = "sam2@example.test", NormalizedEmail = "SAM2@EXAMPLE.TEST", FullName = "Sam Shared", PasswordHash = "x", IsActive = true };
        var app = new JobApplication { TenantId = tenant, JobOpeningId = Guid.NewGuid(), CandidateId = Guid.NewGuid(), CandidateName = "C", JobTitle = "J" };
        db.AddRange(mona, sam1, sam2, app); await db.SaveChangesAsync();

        var created = await As(db, tenant, Guid.NewGuid(), "HR Manager", "Hana HR").Schedule(
            new ScheduleInterviewRequest(app.Id, "Technical", "Mona Manager, Sam Shared, External Person", DateTime.UtcNow.AddDays(2), 30, "Video", null, null), CancellationToken.None);

        var stored = (await db.InterviewSchedules.SingleAsync()).InterviewerUserIds;
        created.Should().BeAssignableTo<IStatusCodeActionResult>();
        stored.Should().Be(mona.Id.ToString());
    }
}
