using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Performance;
using Zayra.Api.Controllers.Performance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// A person never decides on their own record. The employee's own login cannot change the status of their improvement plan, record its
/// check-ins, rewrite its content, or adjust the calibrated score of their own appraisal; another HR user still can. The employee may
/// still add their own comments to the plan.
/// </summary>
public sealed class OwnRecordDecisionBarTests
{
    private sealed record World(ZayraDbContext Db, Guid Tenant, Employee Subject, Guid SubjectUser, Guid OtherUser);

    private static async Task<World> WorldAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "Own Record", Slug = $"own-{tenant:N}" });
        var subjectUser = Guid.NewGuid();
        var subject = new Employee
        {
            TenantId = tenant, EmployeeCode = "SUBJ-1", FullName = "Subject Person", Status = "Active",
            JoiningDate = DateTime.UtcNow.AddYears(-2), UserAccountId = subjectUser,
        };
        db.Employees.Add(subject);
        await db.SaveChangesAsync();
        return new World(db, tenant, subject, subjectUser, Guid.NewGuid());
    }

    private static ControllerContext Hr(Guid tenant, Guid userId) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", tenant.ToString()),
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, "HR Manager"),
                new Claim("permission", "employees.read"),
                new Claim("permission", "employees.write"),
                new Claim("permission", "performance.approve"),
                new Claim("FullName", "HR Person"),
            }, "test")),
        },
    };

    private static PIPController Pip(World w, Guid userId) =>
        new(w.Db, new DataScopeService(w.Db)) { ControllerContext = Hr(w.Tenant, userId) };

    private static async Task<PerformanceImprovementPlan> AddPlanAsync(World w)
    {
        var plan = new PerformanceImprovementPlan
        {
            TenantId = w.Tenant, EmployeeId = w.Subject.Id, EmployeeName = w.Subject.FullName, Status = "Active",
            StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2026, 12, 1), PerformanceGaps = "Original gaps",
        };
        w.Db.PerformanceImprovementPlans.Add(plan);
        await w.Db.SaveChangesAsync();
        return plan;
    }

    private static string ErrorOf(IActionResult result)
    {
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var body = JsonSerializer.SerializeToElement(bad.Value);
        body.GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
        return body.GetProperty("error").GetString()!;
    }

    // ── Improvement plan ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PipStatus_CannotBeChangedByTheEmployeeTheyAreAbout()
    {
        var w = await WorldAsync();
        await using var _ = w.Db;
        var plan = await AddPlanAsync(w);

        var result = await Pip(w, w.SubjectUser).UpdateStatus(plan.Id, new PIPStatusRequest("Improved", "I did well"), default);

        ErrorOf(result).Should().Be(SubjectDecisionBar.ErrorCode);
        (await w.Db.PerformanceImprovementPlans.AsNoTracking().SingleAsync(p => p.Id == plan.Id)).Status.Should().Be("Active");
    }

    [Fact]
    public async Task PipStatus_CanStillBeChangedByAnotherHrUser()
    {
        var w = await WorldAsync();
        await using var _ = w.Db;
        var plan = await AddPlanAsync(w);

        var result = await Pip(w, w.OtherUser).UpdateStatus(plan.Id, new PIPStatusRequest("Improved", "Met the goals"), default);

        result.Should().BeOfType<OkObjectResult>();
        (await w.Db.PerformanceImprovementPlans.AsNoTracking().SingleAsync(p => p.Id == plan.Id)).Status.Should().Be("Improved");
    }

    [Fact]
    public async Task PipCheckIn_CannotBeRecordedByTheEmployeeTheyAreAbout_ButCanByAnotherUser()
    {
        var w = await WorldAsync();
        await using var _ = w.Db;
        var plan = await AddPlanAsync(w);
        var request = new CheckInRequest(new DateOnly(2026, 10, 1), "Weekly check", "OnTrack");

        ErrorOf(await Pip(w, w.SubjectUser).AddCheckIn(plan.Id, request, default)).Should().Be(SubjectDecisionBar.ErrorCode);
        (await w.Db.PIPCheckIns.CountAsync()).Should().Be(0);

        (await Pip(w, w.OtherUser).AddCheckIn(plan.Id, request, default)).Should().BeOfType<OkResult>();
        (await w.Db.PIPCheckIns.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task PipContent_CannotBeRewrittenByTheEmployeeTheyAreAbout_ButTheirOwnCommentsCanBeAdded()
    {
        var w = await WorldAsync();
        await using var _ = w.Db;
        var plan = await AddPlanAsync(w);

        var rewrite = new PIPUpdateRequest("Different gaps", null, null, null, null, null, null);
        ErrorOf(await Pip(w, w.SubjectUser).Update(plan.Id, rewrite, default)).Should().Be(SubjectDecisionBar.ErrorCode);
        (await w.Db.PerformanceImprovementPlans.AsNoTracking().SingleAsync(p => p.Id == plan.Id)).PerformanceGaps.Should().Be("Original gaps");

        var comment = new PIPUpdateRequest(null, null, null, null, null, null, "My own response");
        (await Pip(w, w.SubjectUser).Update(plan.Id, comment, default)).Should().BeOfType<OkObjectResult>();
        (await w.Db.PerformanceImprovementPlans.AsNoTracking().SingleAsync(p => p.Id == plan.Id)).EmployeeComments.Should().Be("My own response");
    }

    // ── Calibration ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CalibrationAdjustment_CannotBeMadeByTheEmployeeTheReviewIsAbout()
    {
        var w = await WorldAsync();
        await using var _ = w.Db;
        var template = new PerformanceScorecardTemplate { TenantId = w.Tenant, Name = "Template" };
        var cycle = new PerformanceCycle { TenantId = w.Tenant, Name = "Cycle", Status = "Calibration" };
        var review = new AppraisalReview
        {
            TenantId = w.Tenant, CycleId = cycle.Id, CycleName = cycle.Name, EmployeeId = w.Subject.Id, EmployeeName = w.Subject.FullName,
            ScorecardTemplateId = template.Id, Status = "ManagerReviewComplete", FinalScore = 70,
        };
        w.Db.AddRange(template, cycle, review);
        await w.Db.SaveChangesAsync();
        var controller = new CalibrationController(w.Db, new PerformanceService(w.Db), new DataScopeService(w.Db)) { ControllerContext = Hr(w.Tenant, w.SubjectUser) };

        var result = await controller.AdjustScore(cycle.Id, new CalibrationAdjustRequest(review.Id, 10, "Raise my own score"), default);

        ErrorOf(result).Should().Be(SubjectDecisionBar.ErrorCode);
        (await w.Db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == review.Id)).CalibrationAdjustment.Should().Be(0);
        (await w.Db.AppraisalCalibrations.CountAsync()).Should().Be(0);
    }
}
