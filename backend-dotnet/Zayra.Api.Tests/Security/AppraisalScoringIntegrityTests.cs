using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Performance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Performance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The numbers an appraisal is decided on: component scores are 0-100 percentages, refused outside that range
/// rather than clamped into a top rating; and when the employee has agreed KPI goals in the cycle, the KPI score
/// IS the weighted achievement of those goals (weights totalling 100), not a number typed beside them.
/// </summary>
public class AppraisalScoringIntegrityTests
{
    // ── Component scores are 0-100 ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(500, 50, 50, 50, 50, 50)]
    [InlineData(80, -1, 50, 50, 50, 50)]
    [InlineData(80, 50, 100.01, 50, 50, 50)]
    [InlineData(80, 50, 50, 50, 50, 1000)]
    public async Task ManagerReview_RefusesAComponentScoreOutside0To100(double kpi, double comp, double att, double prod, double fb, double disc)
    {
        var (db, tenantId) = await NewTenantAsync("score-range");
        var s = await SeedReviewAsync(db, tenantId, "SelfAssessmentSubmitted", "InReview");
        var hr = await CallerAsync(db, tenantId, "HR Director");

        var result = await Reviews(db, hr).SubmitManagerReview(s.ReviewId,
            new ManagerReviewRequest((decimal)kpi, (decimal)comp, (decimal)att, (decimal)prod, (decimal)fb, (decimal)disc, "notes", null, null, null), default);

        StatusOf(result).Should().Be(400);
        JsonSerializer.Serialize(((ObjectResult)result).Value).Should().Contain("score_out_of_range");
        var stored = await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == s.ReviewId);
        stored.Status.Should().Be("SelfAssessmentSubmitted", "a refused review must not half-apply");
        stored.FinalScore.Should().Be(0m);
    }

    [Fact]
    public async Task ManagerReview_AcceptsZero_AsARealScore()
    {
        var (db, tenantId) = await NewTenantAsync("score-zero");
        var s = await SeedReviewAsync(db, tenantId, "SelfAssessmentSubmitted", "InReview");
        var hr = await CallerAsync(db, tenantId, "HR Director");

        StatusOf(await Reviews(db, hr).SubmitManagerReview(s.ReviewId,
            new ManagerReviewRequest(0m, 0m, 0m, 0m, 0m, 0m, "Did not deliver.", null, null, null), default)).Should().Be(200);
    }

    [Fact]
    public async Task SelfAssessment_RefusesAScoreOutside0To100_AndAcceptsZero()
    {
        var (db, tenantId) = await NewTenantAsync("self-range");
        var s = await SeedReviewAsync(db, tenantId, "SelfAssessmentDue", "Active");
        var employee = await EmployeeCallerAsync(db, tenantId, s.EmployeeId);

        StatusOf(await Reviews(db, employee).SubmitSelfAssessment(s.ReviewId,
            new SelfAssessmentRequest("My year", 120m, 50m, 50m, null), default)).Should().Be(400);
        (await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == s.ReviewId)).Status.Should().Be("SelfAssessmentDue");

        StatusOf(await Reviews(db, employee).SubmitSelfAssessment(s.ReviewId,
            new SelfAssessmentRequest("My year", 0m, 50m, 50m, null), default)).Should().Be(200);
    }

    [Fact]
    public async Task OverrideScore_RefusesAScoreOutside0To100()
    {
        var (db, tenantId) = await NewTenantAsync("ovr-range");
        var s = await SeedReviewAsync(db, tenantId, "FinalApproval", "FinalApproval");
        var hr = await CallerAsync(db, tenantId, "HR Director");

        StatusOf(await Reviews(db, hr).OverrideScore(s.ReviewId,
            new ScoreOverrideRequest(150m, null, null, null, null, null, "Panel"), default)).Should().Be(400);
        (await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == s.ReviewId)).KpiScore.Should().Be(70m);
    }

    // ── KPI goals feed the KPI score ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ManagerReview_TakesTheKpiScoreFromTheAgreedGoals()
    {
        var (db, tenantId) = await NewTenantAsync("kpi-goals");
        var s = await SeedReviewAsync(db, tenantId, "SelfAssessmentSubmitted", "InReview");
        // 60% weight at 80% achieved + 40% weight at 100% achieved = 48 + 40 = 88.
        AddGoal(db, tenantId, s, weight: 60m, achievement: 80m);
        AddGoal(db, tenantId, s, weight: 40m, achievement: 100m, status: "Completed");
        // Not part of the scorecard: a draft nobody approved, a cancelled goal, and a goal of another cycle.
        AddGoal(db, tenantId, s, weight: 50m, achievement: 10m, approved: false, status: "Draft");
        AddGoal(db, tenantId, s, weight: 30m, achievement: 0m, status: "Cancelled");
        AddGoal(db, tenantId, s, weight: 100m, achievement: 5m, cycleId: Guid.NewGuid());
        await db.SaveChangesAsync();
        var hr = await CallerAsync(db, tenantId, "HR Director");

        var detail = Json(await Reviews(db, hr).Get(s.ReviewId, default));
        var goalKpi = detail.GetProperty("goalKpi");
        goalKpi.GetProperty("goalCount").GetInt32().Should().Be(2);
        goalKpi.GetProperty("totalWeight").GetDecimal().Should().Be(100m);
        goalKpi.GetProperty("kpiScore").GetDecimal().Should().Be(88m);

        StatusOf(await Reviews(db, hr).SubmitManagerReview(s.ReviewId,
            new ManagerReviewRequest(10m, 70m, 70m, 70m, 70m, 70m, "notes", null, null, null), default)).Should().Be(200);

        (await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == s.ReviewId)).KpiScore
            .Should().Be(88m, "the KPI score is the agreed goals' weighted achievement, not a typed number");
    }

    [Fact]
    public async Task ManagerReview_IsRefused_WhileTheAgreedGoalWeightsDoNotTotal100()
    {
        var (db, tenantId) = await NewTenantAsync("kpi-weights");
        var s = await SeedReviewAsync(db, tenantId, "SelfAssessmentSubmitted", "InReview");
        AddGoal(db, tenantId, s, weight: 60m, achievement: 80m);
        AddGoal(db, tenantId, s, weight: 30m, achievement: 100m);
        await db.SaveChangesAsync();
        var hr = await CallerAsync(db, tenantId, "HR Director");

        var result = await Reviews(db, hr).SubmitManagerReview(s.ReviewId,
            new ManagerReviewRequest(80m, 70m, 70m, 70m, 70m, 70m, "notes", null, null, null), default);

        StatusOf(result).Should().Be(409);
        JsonSerializer.Serialize(((ObjectResult)result).Value).Should().Contain("goal_weights_not_100");
        (await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == s.ReviewId)).Status.Should().Be("SelfAssessmentSubmitted");
    }

    [Fact]
    public async Task ManagerReview_WithNoGoalsInTheCycle_KeepsTheKpiScoreTheReviewerGives()
    {
        var (db, tenantId) = await NewTenantAsync("kpi-none");
        var s = await SeedReviewAsync(db, tenantId, "SelfAssessmentSubmitted", "InReview");
        var hr = await CallerAsync(db, tenantId, "HR Director");

        StatusOf(await Reviews(db, hr).SubmitManagerReview(s.ReviewId,
            new ManagerReviewRequest(64m, 70m, 70m, 70m, 70m, 70m, "notes", null, null, null), default)).Should().Be(200);
        (await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == s.ReviewId)).KpiScore.Should().Be(64m);
    }

    [Fact]
    public async Task ApprovingAGoal_ThatWouldTakeTheCyclesAgreedWeightsAbove100_IsRefused()
    {
        var (db, tenantId) = await NewTenantAsync("kpi-approve");
        var s = await SeedReviewAsync(db, tenantId, "SelfAssessmentDue", "Active");
        AddGoal(db, tenantId, s, weight: 70m, achievement: 0m);
        var draft = AddGoal(db, tenantId, s, weight: 40m, achievement: 0m, approved: false, status: "Draft");
        var fits = AddGoal(db, tenantId, s, weight: 30m, achievement: 0m, approved: false, status: "Draft");
        await db.SaveChangesAsync();
        var hr = await CallerAsync(db, tenantId, "HR Director");
        var goals = Bind(new GoalsController(db, new DataScopeService(db), new HrmHierarchyService(db, new AuditService(db))), hr);

        var refused = await goals.Approve(draft.Id, default);
        StatusOf(refused).Should().Be(409);
        JsonSerializer.Serialize(((ObjectResult)refused).Value).Should().Contain("goal_weights_exceed_100");
        (await db.EmployeeGoals.AsNoTracking().SingleAsync(g => g.Id == draft.Id)).Status.Should().Be("Draft");

        StatusOf(await goals.Approve(fits.Id, default)).Should().Be(200, "70 + 30 = 100 fits the scorecard");
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────

    internal sealed record Seed(Guid ReviewId, Guid CycleId, int EmployeeId);

    internal static EmployeeGoal AddGoal(ZayraDbContext db, Guid tenantId, Seed s, decimal weight, decimal achievement,
        bool approved = true, string status = "Active", Guid? cycleId = null)
    {
        var goal = new EmployeeGoal
        {
            TenantId = tenantId, CycleId = cycleId ?? s.CycleId, EmployeeId = s.EmployeeId, EmployeeName = "Review Subject",
            Title = $"Goal {weight}/{achievement}", Category = "KPI", KpiType = "Numeric", TargetValue = 100m, ActualValue = achievement,
            AchievementPct = achievement, Weight = weight, Status = status, ManagerApproved = approved,
        };
        db.EmployeeGoals.Add(goal);
        return goal;
    }

    internal static async Task<Seed> SeedReviewAsync(ZayraDbContext db, Guid tenantId, string reviewStatus, string cycleStatus,
        bool enableCalibration = false)
    {
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"SCI{Guid.NewGuid():N}"[..12], FullName = "Review Subject",
            Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddYears(-3),
        };
        db.Employees.Add(employee);
        var template = new PerformanceScorecardTemplate { TenantId = tenantId, Name = "Std", IsActive = true };
        db.PerformanceScorecardTemplates.Add(template);
        var cycle = new PerformanceCycle
        {
            TenantId = tenantId, Name = "FY26", Status = cycleStatus, DefaultScorecardTemplateId = template.Id, EnableCalibration = enableCalibration,
        };
        db.PerformanceCycles.Add(cycle);
        await db.SaveChangesAsync();
        var review = new AppraisalReview
        {
            TenantId = tenantId, CycleId = cycle.Id, CycleName = cycle.Name, ScorecardTemplateId = template.Id,
            EmployeeId = employee.Id, EmployeeName = employee.FullName, Status = reviewStatus, KpiScore = 70m,
        };
        db.AppraisalReviews.Add(review);
        db.PerformanceCycleEmployees.Add(new PerformanceCycleEmployee { TenantId = tenantId, CycleId = cycle.Id, EmployeeId = employee.Id, Status = "Active" });
        await db.SaveChangesAsync();
        return new Seed(review.Id, cycle.Id, employee.Id);
    }

    internal static async Task<ClaimsPrincipal> EmployeeCallerAsync(ZayraDbContext db, Guid tenantId, int employeeId)
    {
        var caller = await CallerAsync(db, tenantId, "Employee");
        var claims = caller.Claims.Where(c => c.Type != "is_group_scope").ToList();
        claims.Add(new Claim("employee_id", employeeId.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    internal static ReviewsController Reviews(ZayraDbContext db, ClaimsPrincipal caller) =>
        Bind(new ReviewsController(db, new PerformanceService(db), new DataScopeService(db),
            new HrmHierarchyService(db, new AuditService(db))), caller);

    internal static JsonElement Json(IActionResult result)
    {
        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value;
        return JsonDocument.Parse(JsonSerializer.Serialize(value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })).RootElement.Clone();
    }
}
