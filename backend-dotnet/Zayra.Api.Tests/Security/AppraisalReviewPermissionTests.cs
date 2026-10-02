using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Performance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Performance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The appraisal review, judged by the REAL seeded role bundles.
///
/// <para>THE DEFECT. <c>ReviewsController</c> accepted six keys that were never in the permission catalog
/// (<c>appraisal.manager_review / view_all / hr_calibration / finalize / publish</c> and
/// <c>sensitive_data.view</c>). Five were dead alternatives beside a live <c>performance.*</c> key. The sixth
/// was the only key on its branch, so the confidential-notes redaction ran for EVERY caller: not even Admin
/// could read the manager's assessment or HR's calibration reasons on the review a rating is judged on.</para>
///
/// <para>Now: manager review = <c>performance.write</c>; calibration, publish and the confidential notes =
/// <c>performance.approve</c>. The two tiers stay apart — writing a review does not let you sign off a
/// rating.</para>
///
/// <para>Publish and override-score also carry a legacy <c>[Authorize(Roles=…)]</c> gate, which the
/// production pipeline turns into a permission requirement (<see cref="LegacyRolePermissionResolver"/>).
/// These tests invoke the controller directly, so that gate is applied explicitly first: the status
/// asserted is the one the caller would actually receive.</para>
/// </summary>
public class AppraisalReviewPermissionTests
{
    private const string ManagerNotes = "Consistently late on the migration deliverables.";
    private const string HrNotes = "Calibrated down one band: see panel minutes 2026-09-12.";

    // ── Confidential notes ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Admin")]
    [InlineData("HR Director")]
    public async Task Get_ShowsTheNotesARatingIsJudgedOn_ToTheRatingApproverTier(string role)
    {
        var (db, tenantId) = await NewTenantAsync("appr-notes");
        var seed = await SeedReviewAsync(db, tenantId, reviewStatus: "FinalApproval", cycleStatus: "FinalApproval");
        var caller = await CallerAsync(db, tenantId, role);
        caller.HasClaim("permission", "performance.approve").Should().BeTrue(
            $"the seeded {role} bundle is the approver tier");

        var body = Json(await Reviews(db, caller).Get(seed.ReviewId, CancellationToken.None));

        body.GetProperty("review").GetProperty("managerNotes").GetString().Should().Be(ManagerNotes,
            "the person calibrating a rating must be able to read the assessment it rests on");
        body.GetProperty("review").GetProperty("hrNotes").GetString().Should().Be(HrNotes);

        var feedback = body.GetProperty("feedback360").EnumerateArray().ToList();
        feedback.Should().HaveCount(2, "the approver tier sees anonymous 360 feedback as well as named feedback");
        var anonymous = feedback.Single(f => f.GetProperty("isAnonymous").GetBoolean());
        anonymous.GetProperty("comments").GetString().Should().Be("Blocks the team's reviews.");
        anonymous.GetProperty("reviewerName").GetString().Should().Be("Anonymous",
            "anonymous means anonymous to every reader — the same masking FeedbackController.List360 applies");
        anonymous.GetProperty("reviewerEmployeeId").GetInt32().Should().Be(0,
            "the author's employee id would identify them as surely as their name");
    }

    [Fact]
    public async Task Get_RedactsTheNotes_ForARoleOutsideTheApproverTier()
    {
        // Payroll Manager reads employees organisation-wide (employees.read without manager.read), so the
        // data scope lets them open the review — the permission is the only thing standing in the way.
        var (db, tenantId) = await NewTenantAsync("appr-redact");
        var seed = await SeedReviewAsync(db, tenantId, reviewStatus: "FinalApproval", cycleStatus: "FinalApproval");
        var caller = await CallerAsync(db, tenantId, "Payroll Manager");
        caller.HasClaim("permission", "performance.approve").Should().BeFalse();

        var body = Json(await Reviews(db, caller).Get(seed.ReviewId, CancellationToken.None));

        body.GetProperty("review").GetProperty("managerNotes").GetString().Should().BeEmpty();
        body.GetProperty("review").GetProperty("hrNotes").GetString().Should().BeEmpty();
        body.GetProperty("feedback360").EnumerateArray()
            .Should().ContainSingle("anonymous feedback is withheld entirely outside the approver tier")
            .Which.GetProperty("reviewerName").GetString().Should().Be("Named Peer");
    }

    // ── Publish ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Admin", 200)]
    [InlineData("HR Director", 200)]
    [InlineData("Payroll Manager", 403)]
    public async Task Publish_IsTheApproverTiersDecision(string role, int expected)
    {
        var (db, tenantId) = await NewTenantAsync("appr-publish");
        var seed = await SeedReviewAsync(db, tenantId, reviewStatus: "FinalApproval", cycleStatus: "FinalApproval");
        var caller = await CallerAsync(db, tenantId, role);

        var status = await ThroughLegacyRoleGate(caller, nameof(ReviewsController.Publish),
            () => Reviews(db, caller).Publish(seed.ReviewId, CancellationToken.None));

        status.Should().Be(expected);
        (await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == seed.ReviewId)).Status
            .Should().Be(expected == 200 ? "Published" : "FinalApproval");
    }

    // ── HR calibration (override-score) ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Admin", 200)]
    [InlineData("HR Director", 200)]
    [InlineData("Payroll Manager", 403)]
    public async Task OverrideScore_IsTheApproverTiersDecision(string role, int expected)
    {
        var (db, tenantId) = await NewTenantAsync("appr-calib");
        var seed = await SeedReviewAsync(db, tenantId, reviewStatus: "Calibration", cycleStatus: "Calibration");
        var caller = await CallerAsync(db, tenantId, role);

        var status = await ThroughLegacyRoleGate(caller, nameof(ReviewsController.OverrideScore),
            () => Reviews(db, caller).OverrideScore(seed.ReviewId,
                new ScoreOverrideRequest(55m, null, null, null, null, null, "Calibration panel moved KPI down"),
                CancellationToken.None));

        status.Should().Be(expected);
        (await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == seed.ReviewId)).KpiScore
            .Should().Be(expected == 200 ? 55m : 70m);
    }

    // ── Manager review ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Admin", 200)]
    [InlineData("HR Director", 200)]
    [InlineData("Payroll Manager", 403)]
    public async Task ManagerReview_NeedsTheReviewerTier(string role, int expected)
    {
        var (db, tenantId) = await NewTenantAsync("appr-mgr");
        var seed = await SeedReviewAsync(db, tenantId, reviewStatus: "SelfAssessmentSubmitted", cycleStatus: "InReview");
        var caller = await CallerAsync(db, tenantId, role);

        // No [Authorize(Roles=…)] on this action, so no legacy gate: the controller's own check is the gate.
        var status = StatusOf(await Reviews(db, caller).SubmitManagerReview(seed.ReviewId,
            new ManagerReviewRequest(80m, 75m, 90m, 70m, 85m, 95m, "Solid year.", null, null, null),
            CancellationToken.None));

        status.Should().Be(expected);
        (await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == seed.ReviewId)).Status
            .Should().Be(expected == 200 ? "ManagerReviewComplete" : "SelfAssessmentSubmitted");
    }

    [Fact]
    public async Task TheReviewerTier_DoesNotBySelfCarryTheApproverTier()
    {
        // The separation the two keys exist for: a caller who may write reviews but not approve ratings
        // can review, and cannot publish.
        var (db, tenantId) = await NewTenantAsync("appr-tiers");
        var seed = await SeedReviewAsync(db, tenantId, reviewStatus: "FinalApproval", cycleStatus: "FinalApproval");
        var reviewerOnly = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim("tenant_id", tenantId.ToString()),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new System.Security.Claims.Claim("permission", "employees.write"),
            new System.Security.Claims.Claim("permission", "performance.write"),
        }, "Test"));

        StatusOf(await Reviews(db, reviewerOnly).Publish(seed.ReviewId, CancellationToken.None)).Should().Be(403);
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────

    private static ReviewsController Reviews(ZayraDbContext db, System.Security.Claims.ClaimsPrincipal caller) =>
        Bind(new ReviewsController(db, new PerformanceService(db), new DataScopeService(db),
            new HrmHierarchyService(db, new AuditService(db))), caller);

    /// <summary>
    /// The production pipeline for an action carrying <c>[Authorize(Roles=…)]</c>: the role name alone is never
    /// enough — the caller must hold the permission <see cref="LegacyRolePermissionResolver"/> infers for the
    /// action (PermissionAwareAuthorizationResultHandler), and only then does the action body run.
    /// </summary>
    private static async Task<int> ThroughLegacyRoleGate(
        System.Security.Claims.ClaimsPrincipal caller, string action, Func<Task<IActionResult>> invoke)
    {
        var required = LegacyRolePermissionResolver.Resolve("Reviews", action, new[] { "POST" });
        required.Should().NotBeNull($"Reviews.{action} carries a role gate, so the pipeline demands a permission");
        if (!caller.HasClaim(c => c.Type == "permission" && string.Equals(c.Value, required, StringComparison.OrdinalIgnoreCase)))
            return 403;
        return StatusOf(await invoke());
    }

    private sealed record Seed(Guid ReviewId);

    private static async Task<Seed> SeedReviewAsync(ZayraDbContext db, Guid tenantId, string reviewStatus, string cycleStatus)
    {
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"APR{Guid.NewGuid():N}"[..12], FullName = "Review Subject",
            Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddYears(-3),
        };
        db.Employees.Add(employee);
        var cycle = new PerformanceCycle { TenantId = tenantId, Name = "FY26", Status = cycleStatus };
        db.PerformanceCycles.Add(cycle);
        await db.SaveChangesAsync();

        var review = new AppraisalReview
        {
            TenantId = tenantId, CycleId = cycle.Id, CycleName = cycle.Name,
            EmployeeId = employee.Id, EmployeeName = employee.FullName,
            Status = reviewStatus, KpiScore = 70m, FinalScore = 70m,
            ManagerNotes = ManagerNotes, HrNotes = HrNotes,
        };
        db.AppraisalReviews.Add(review);
        db.Feedback360.AddRange(
            new Feedback360
            {
                TenantId = tenantId, ReviewId = review.Id, ReviewerEmployeeId = 4242, ReviewerName = "Secret Author",
                ReviewerRole = "Peer", IsAnonymous = true, Score = 2m, Comments = "Blocks the team's reviews.",
            },
            new Feedback360
            {
                TenantId = tenantId, ReviewId = review.Id, ReviewerEmployeeId = 4343, ReviewerName = "Named Peer",
                ReviewerRole = "Peer", IsAnonymous = false, Score = 4m, Comments = "Reliable.",
            });
        await db.SaveChangesAsync();
        return new Seed(review.Id);
    }

    private static JsonElement Json(IActionResult result)
    {
        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value;
        return JsonDocument.Parse(JsonSerializer.Serialize(value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })).RootElement.Clone();
    }
}
