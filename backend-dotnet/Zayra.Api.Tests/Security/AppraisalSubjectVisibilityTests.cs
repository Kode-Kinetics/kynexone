using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
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
/// What the employee a review is about may read of it, and when.
///
/// <para>Until HR publishes, the score, rating, component scores, calibration and the reasons in the
/// review's audit trail are the reviewers' working material: the employee sees their own
/// self-assessment and the stage, nothing else. After publication they see the result and its
/// breakdown. The manager's and HR's narrative and HR's calibration reasoning stay with the
/// rating-approver tier at every stage (unchanged).</para>
/// </summary>
public class AppraisalSubjectVisibilityTests
{
    private const string ManagerNotes = "Missed two migration milestones.";
    private const string HrNotes = "Moved down one band at the panel.";
    private const string CalibrationReason = "Panel moderation: leniency in the team.";

    [Theory]
    [InlineData("ManagerReview")]
    [InlineData("ManagerReviewComplete")]
    [InlineData("Calibration")]
    [InlineData("FinalApproval")]
    public async Task Employee_CannotReadTheirOwnUnpublishedResult(string status)
    {
        var (db, tenantId) = await NewTenantAsync("subj-pre");
        var seed = await SeedAsync(db, tenantId, status);
        var employee = await EmployeeCallerAsync(db, tenantId, seed.EmployeeId);

        var body = Json(await Reviews(db, employee).Get(seed.ReviewId, default));
        var review = body.GetProperty("review");
        review.GetProperty("finalScore").GetDecimal().Should().Be(0m, "the score is not the employee's until it is published");
        review.GetProperty("finalRating").GetString().Should().BeEmpty();
        foreach (var component in new[] { "kpiScore", "competencyScore", "attendanceScore", "productivityScore", "feedbackScore", "disciplineScore", "calibrationAdjustment" })
            review.GetProperty(component).GetDecimal().Should().Be(0m, $"{component} is the reviewer's working score before publication");
        review.GetProperty("calibrationNotes").GetString().Should().BeEmpty();
        review.GetProperty("managerNotes").GetString().Should().BeEmpty();
        review.GetProperty("hrNotes").GetString().Should().BeEmpty();
        review.GetProperty("selfAssessmentNotes").GetString().Should().Be("My year", "their own self-assessment stays theirs to read");
        body.GetProperty("breakdown").EnumerateArray().Should().BeEmpty();
        body.GetProperty("calibration").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("feedback360").EnumerateArray().Should().BeEmpty("360 input is reviewer material until publication");
        var competency = body.GetProperty("competencies").EnumerateArray().Single();
        competency.GetProperty("managerRating").GetDecimal().Should().Be(0m);
        competency.GetProperty("managerComments").GetString().Should().BeEmpty();
        competency.GetProperty("selfRating").GetDecimal().Should().Be(4m);
        AssertNoAuditReason(body);
        body.GetRawText().Should().NotContain(ManagerNotes).And.NotContain(HrNotes).And.NotContain(CalibrationReason).And.NotContain("Score:81");

        // My Reviews (the list) must not carry it either.
        var list = Json(await Reviews(db, employee).List(null, null, null, null, ReviewsController.MineView, 1, 50, default));
        var row = list.GetProperty("items").EnumerateArray().Single();
        row.GetProperty("finalScore").GetDecimal().Should().Be(0m);
        row.GetProperty("finalRating").GetString().Should().BeEmpty();
        row.GetProperty("kpiScore").GetDecimal().Should().Be(0m);
        row.GetProperty("calibrationNotes").GetString().Should().BeEmpty();
    }

    [Theory]
    [InlineData("Published")]
    [InlineData("Acknowledged")]
    [InlineData("Appealed")]
    public async Task Employee_ReadsThePublishedResultAndBreakdown_ButNotTheReviewersNotesOrReasons(string status)
    {
        var (db, tenantId) = await NewTenantAsync("subj-post");
        var seed = await SeedAsync(db, tenantId, status);
        var employee = await EmployeeCallerAsync(db, tenantId, seed.EmployeeId);

        var body = Json(await Reviews(db, employee).Get(seed.ReviewId, default));
        var review = body.GetProperty("review");
        review.GetProperty("finalScore").GetDecimal().Should().Be(81m);
        review.GetProperty("finalRating").GetString().Should().Be("Exceeds Expectations");
        review.GetProperty("kpiScore").GetDecimal().Should().Be(85m);
        body.GetProperty("breakdown").EnumerateArray().Should().NotBeEmpty("the published breakdown is the employee's to read");
        body.GetProperty("competencies").EnumerateArray().Single().GetProperty("managerRating").GetDecimal().Should().Be(3m);

        review.GetProperty("managerNotes").GetString().Should().BeEmpty();
        review.GetProperty("hrNotes").GetString().Should().BeEmpty();
        review.GetProperty("calibrationNotes").GetString().Should().BeEmpty("HR's calibration reasoning stays with the approver tier");
        body.GetProperty("calibration").ValueKind.Should().Be(JsonValueKind.Null);
        AssertNoAuditReason(body);
        body.GetRawText().Should().NotContain(ManagerNotes).And.NotContain(HrNotes).And.NotContain(CalibrationReason);
    }

    [Fact]
    public async Task TheApproverTier_StillReadsEverythingBeforePublication()
    {
        var (db, tenantId) = await NewTenantAsync("subj-hr");
        var seed = await SeedAsync(db, tenantId, "FinalApproval");
        var hr = await CallerAsync(db, tenantId, "HR Director");

        var body = Json(await Reviews(db, hr).Get(seed.ReviewId, default));
        body.GetProperty("review").GetProperty("finalScore").GetDecimal().Should().Be(81m);
        body.GetProperty("review").GetProperty("calibrationNotes").GetString().Should().Be(CalibrationReason);
        body.GetProperty("calibration").GetProperty("adjustmentReason").GetString().Should().Be(CalibrationReason);
        body.GetProperty("auditLog").EnumerateArray().Select(a => a.GetProperty("reason").GetString())
            .Should().Contain(ManagerNotes);
    }

    [Fact]
    public async Task AnotherNonApprover_SeesTheScores_ButNotTheReasonsInTheAuditTrail()
    {
        // Payroll Manager: organisation-wide read, not the approver tier. The manager's notes are blanked on the
        // review for this caller already; the audit trail carried the same text as its Reason.
        var (db, tenantId) = await NewTenantAsync("subj-other");
        var seed = await SeedAsync(db, tenantId, "FinalApproval");
        var other = await CallerAsync(db, tenantId, "Payroll Manager");

        var body = Json(await Reviews(db, other).Get(seed.ReviewId, default));
        body.GetProperty("review").GetProperty("finalScore").GetDecimal().Should().Be(81m);
        body.GetProperty("auditLog").EnumerateArray().Should().NotBeEmpty("the trail itself (who did what, when) stays readable");
        AssertNoAuditReason(body);
        body.GetRawText().Should().NotContain(ManagerNotes).And.NotContain(HrNotes);
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────

    private static void AssertNoAuditReason(JsonElement body)
    {
        foreach (var entry in body.GetProperty("auditLog").EnumerateArray())
            entry.GetProperty("reason").GetString().Should().BeEmpty("the audit Reason carries the reviewers' notes");
    }

    private sealed record Seed(Guid ReviewId, int EmployeeId);

    private static async Task<Seed> SeedAsync(ZayraDbContext db, Guid tenantId, string status)
    {
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"SUB{Guid.NewGuid():N}"[..12], FullName = "Review Subject",
            Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddYears(-3),
        };
        db.Employees.Add(employee);
        var template = new PerformanceScorecardTemplate { TenantId = tenantId, Name = "Std", IsActive = true };
        db.PerformanceScorecardTemplates.Add(template);
        var cycle = new PerformanceCycle { TenantId = tenantId, Name = "FY26", Status = "FinalApproval", DefaultScorecardTemplateId = template.Id };
        db.PerformanceCycles.Add(cycle);
        await db.SaveChangesAsync();

        var published = status is "Published" or "Acknowledged" or "Appealed";
        var review = new AppraisalReview
        {
            TenantId = tenantId, CycleId = cycle.Id, CycleName = cycle.Name, ScorecardTemplateId = template.Id,
            EmployeeId = employee.Id, EmployeeName = employee.FullName, Status = status,
            KpiScore = 85m, CompetencyScore = 80m, AttendanceScore = 90m, ProductivityScore = 75m, FeedbackScore = 70m, DisciplineScore = 95m,
            FinalScore = 81m, FinalRating = "Exceeds Expectations", CalibrationAdjustment = -2m, CalibrationNotes = CalibrationReason,
            SelfAssessmentNotes = "My year", ManagerNotes = ManagerNotes, HrNotes = HrNotes,
            PublishedAt = published ? DateTime.UtcNow.AddDays(-1) : null,
        };
        db.AppraisalReviews.Add(review);
        db.AppraisalScoreBreakdowns.Add(new AppraisalScoreBreakdown { TenantId = tenantId, ReviewId = review.Id, Component = "KPI", RawScore = 85m, Weight = 40m, WeightedScore = 34m });
        db.AppraisalCompetencyRatings.Add(new AppraisalCompetencyRating
        {
            TenantId = tenantId, ReviewId = review.Id, CompetencyId = Guid.NewGuid(), CompetencyName = "Ownership",
            SelfRating = 4m, SelfComments = "Owned the release", ManagerRating = 3m, ManagerComments = "Late twice", Weight = 100m,
        });
        db.AppraisalCalibrations.Add(new AppraisalCalibration
        {
            TenantId = tenantId, ReviewId = review.Id, CycleId = cycle.Id, OriginalScore = 83m, AdjustedScore = 81m, AdjustmentReason = CalibrationReason,
        });
        db.Feedback360.Add(new Feedback360
        {
            TenantId = tenantId, ReviewId = review.Id, ReviewerEmployeeId = 777, ReviewerName = "Named Peer", ReviewerRole = "Peer",
            IsAnonymous = false, Score = 3m, Comments = "Reliable.",
        });
        db.PerformanceAuditLogs.AddRange(
            new PerformanceAuditLog { TenantId = tenantId, EntityType = "AppraisalReview", EntityId = review.Id.ToString(), Action = "ManagerReviewSubmitted", OldValue = "Score:0.00", NewValue = "Score:81.00", Reason = ManagerNotes },
            new PerformanceAuditLog { TenantId = tenantId, EntityType = "AppraisalReview", EntityId = review.Id.ToString(), Action = "ScoreOverride", OldValue = "KPI:80", NewValue = "FinalScore:81", Reason = HrNotes });
        await db.SaveChangesAsync();
        return new Seed(review.Id, employee.Id);
    }

    /// <summary>The employee themself: the seeded Employee bundle plus the login link to their record.</summary>
    private static async Task<ClaimsPrincipal> EmployeeCallerAsync(ZayraDbContext db, Guid tenantId, int employeeId)
    {
        var caller = await CallerAsync(db, tenantId, "Employee");
        var claims = caller.Claims.Where(c => c.Type != "is_group_scope").ToList();
        claims.Add(new Claim("employee_id", employeeId.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static ReviewsController Reviews(ZayraDbContext db, ClaimsPrincipal caller) =>
        Bind(new ReviewsController(db, new PerformanceService(db), new DataScopeService(db),
            new HrmHierarchyService(db, new AuditService(db))), caller);

    private static JsonElement Json(IActionResult result)
    {
        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value;
        return JsonDocument.Parse(JsonSerializer.Serialize(value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })).RootElement.Clone();
    }
}
