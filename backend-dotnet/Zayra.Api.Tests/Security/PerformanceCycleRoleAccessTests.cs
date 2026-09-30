using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
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
/// F08 — the performance cycle, run by the REAL seeded role bundles through the REAL authorization pipeline
/// (<see cref="ProductionAuthorizationGate"/>).
///
/// <para>THE DEFECT. Every role-gated performance endpoint is authorised on a <c>performance.*</c> permission
/// (<see cref="LegacyRolePermissionResolver"/>), and the manager review checks <c>performance.write</c> itself.
/// The seeded HR Manager and Manager bundles held no <c>performance.*</c> key at all, so HR Manager could not
/// run the cycle its name is on, and a line manager could not set a goal for, or review, their own report.
/// The Performance menu needs the same keys, so neither role could even see the module.</para>
///
/// <para>THE BOUNDARY. The pipeline lets the permission satisfy the role gate, so the permission IS the gate.
/// Giving a Manager <c>performance.write</c> would have let them launch and advance cycles, edit scorecards,
/// calibrate and decide appeals, because the resolver inferred <c>performance.write</c> for all of those from
/// the action name. The resolver now asks each action for the tier of the audience its role gate names.</para>
/// </summary>
public class PerformanceCycleRoleAccessTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // ── The seeded bundles ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeededBundles_CarryThePerformanceTierOfEachAudience()
    {
        var (db, tenantId) = await NewTenantAsync("perf-bundles");

        (await PermissionsOfAsync(db, tenantId, "HR Manager")).Should().Contain(new[]
            { "performance.read", "performance.write", "performance.approve", "performance.cycle_manage" },
            "HR Manager runs the cycle: sets it up, calibrates and publishes");

        var manager = await PermissionsOfAsync(db, tenantId, "Manager");
        manager.Should().Contain(new[] { "performance.read", "performance.write" },
            "a line manager sets goals for and reviews their own reports");
        manager.Should().NotContain(new[] { "performance.approve", "performance.cycle_manage" },
            "a line manager must not calibrate, finalise or publish the ratings they wrote");

        var employee = await PermissionsOfAsync(db, tenantId, "Employee");
        employee.Should().Contain("performance.read", "an employee opens the module to self-assess and see their goals");
        employee.Should().NotContain(new[] { "performance.write", "performance.approve", "performance.cycle_manage" });
    }

    // ── The resolver asks for the tier of the audience the role gate names ─────────────────────

    [Fact]
    public void RoleGatedPerformanceActions_NeedTheTierOfTheAudienceTheirGateNames()
    {
        var wrong = new List<string>();
        foreach (var (controller, action, roles, verbs) in RoleGatedPerformanceActions())
        {
            var required = LegacyRolePermissionResolver.Resolve(controller, action, verbs);
            var namesLineManager = roles.Contains("Manager");
            var ok = namesLineManager
                ? required == "performance.write"
                : required is "performance.approve" or "performance.cycle_manage";
            if (!ok)
                wrong.Add($"{controller}.{action} [{string.Join(",", roles)}] resolves to {required ?? "(nothing)"}");
        }

        wrong.Should().BeEmpty(
            "an action whose gate names Manager is the reviewer tier (performance.write, held by line managers); " +
            "an action whose gate names only HR roles must need performance.approve or performance.cycle_manage, " +
            "never a key line managers and employees hold. Wrong:\n" + string.Join('\n', wrong));
    }

    // ── HR Manager ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HrManager_Calibrates()
    {
        var (db, tenantId) = await NewTenantAsync("perf-hr-calib");
        var w = await SeedAsync(db, tenantId, cycleStatus: "Calibration", reviewStatus: "ManagerReviewComplete");
        var hr = await CallerAsync(db, tenantId, "HR Manager");

        var status = await Adjust(db, hr, w, +5m);

        status.Should().Be(200);
        (await ReviewOf(db, w.ReportReviewId)).CalibrationAdjustment.Should().Be(5m);
    }

    [Fact]
    public async Task HrManager_Publishes()
    {
        var (db, tenantId) = await NewTenantAsync("perf-hr-publish");
        var w = await SeedAsync(db, tenantId, cycleStatus: "FinalApproval", reviewStatus: "FinalApproval");
        var hr = await CallerAsync(db, tenantId, "HR Manager");

        var status = await Publish(db, hr, w.ReportReviewId);

        status.Should().Be(200);
        (await ReviewOf(db, w.ReportReviewId)).Status.Should().Be("Published");
    }

    [Fact]
    public async Task HrManager_MovesTheCycleOn()
    {
        var (db, tenantId) = await NewTenantAsync("perf-hr-advance");
        var w = await SeedAsync(db, tenantId, cycleStatus: "Active", reviewStatus: "SelfAssessmentDue");
        var hr = await CallerAsync(db, tenantId, "HR Manager");

        (await Advance(db, hr, w.CycleId)).Should().Be(200);
        (await db.PerformanceCycles.AsNoTracking().SingleAsync(c => c.Id == w.CycleId)).Status.Should().Be("InReview");
    }

    [Fact]
    public async Task HrManager_SetsAndApprovesAGoal()
    {
        var (db, tenantId) = await NewTenantAsync("perf-hr-goal");
        var w = await SeedAsync(db, tenantId, cycleStatus: "Active", reviewStatus: "SelfAssessmentDue");
        var hr = await CallerAsync(db, tenantId, "HR Manager");

        (await CreateGoal(db, hr, w.ReportId, weight: 25m)).Should().Be(201);
        var goal = await db.EmployeeGoals.AsNoTracking().SingleAsync();
        (await ApproveGoal(db, hr, goal.Id)).Should().Be(200);
        (await db.EmployeeGoals.AsNoTracking().SingleAsync()).Status.Should().Be("Active");
    }

    [Fact]
    public async Task APerUserDeny_StillBeatsTheRoleNameOnTheGate()
    {
        // Why the fix grants permissions rather than letting the role name suffice: an HR Manager whose tenant
        // denied them performance.approve (the claim is removed at token issuance) must not publish, even though
        // "HR Manager" is written on the endpoint's role gate.
        var (db, tenantId) = await NewTenantAsync("perf-hr-deny");
        var w = await SeedAsync(db, tenantId, cycleStatus: "FinalApproval", reviewStatus: "FinalApproval");
        var bundle = await CallerAsync(db, tenantId, "HR Manager");
        var denied = new ClaimsPrincipal(new ClaimsIdentity(
            bundle.Claims.Where(c => !(c.Type == "permission" && c.Value == "performance.approve")), "Test"));
        denied.IsInRole("HR Manager").Should().BeTrue();

        (await Publish(db, denied, w.ReportReviewId)).Should().Be(403);
        (await ReviewOf(db, w.ReportReviewId)).Status.Should().Be("FinalApproval");
    }

    // ── Manager ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Manager_SubmitsTheManagerReview_ForADirectReport()
    {
        var (db, tenantId) = await NewTenantAsync("perf-mgr-review");
        var w = await SeedAsync(db, tenantId, cycleStatus: "InReview", reviewStatus: "SelfAssessmentSubmitted");
        var manager = await ManagerAsync(db, tenantId, w);

        var status = await ManagerReview(db, manager, w.ReportReviewId);

        status.Should().Be(200);
        var review = await ReviewOf(db, w.ReportReviewId);
        review.Status.Should().Be("ManagerReviewComplete");
        review.ReviewerManagerId.Should().Be(w.ManagerId, "the review is recorded against the line manager who wrote it");
    }

    [Fact]
    public async Task Manager_CannotReviewSomeoneWhoIsNotTheirReport()
    {
        var (db, tenantId) = await NewTenantAsync("perf-mgr-outsider");
        var w = await SeedAsync(db, tenantId, cycleStatus: "InReview", reviewStatus: "SelfAssessmentSubmitted");
        var manager = await ManagerAsync(db, tenantId, w);

        var status = await ManagerReview(db, manager, w.OutsiderReviewId);

        status.Should().Be(403);
        (await ReviewOf(db, w.OutsiderReviewId)).Status.Should().Be("SelfAssessmentSubmitted");
    }

    [Fact]
    public async Task Manager_SetsAndApprovesAGoal_ForADirectReport_ButNotForAnyoneElse()
    {
        var (db, tenantId) = await NewTenantAsync("perf-mgr-goal");
        var w = await SeedAsync(db, tenantId, cycleStatus: "Active", reviewStatus: "SelfAssessmentDue");
        var manager = await ManagerAsync(db, tenantId, w);

        (await CreateGoal(db, manager, w.ReportId, weight: 40m)).Should().Be(201);
        var goal = await db.EmployeeGoals.AsNoTracking().SingleAsync();
        (await ApproveGoal(db, manager, goal.Id)).Should().Be(200);
        (await db.EmployeeGoals.AsNoTracking().SingleAsync()).Status.Should().Be("Active");

        (await CreateGoal(db, manager, w.OutsiderId, weight: 40m)).Should().Be(403,
            "the manager's data scope is their reporting line");
        var outsiderGoal = await SeedGoalAsync(db, tenantId, w.OutsiderId);
        (await ApproveGoal(db, manager, outsiderGoal)).Should().Be(403);
    }

    public static IEnumerable<object[]> HrOnlyCycleSteps() => new[]
    {
        new object[] { "calibrate" }, new object[] { "override-score" }, new object[] { "publish" },
        new object[] { "advance-cycle" }, new object[] { "calibration-board" }, new object[] { "decide-appeal" },
    };

    [Theory]
    [MemberData(nameof(HrOnlyCycleSteps))]
    public async Task Manager_CannotCalibrateFinaliseOrPublish(string step)
    {
        // Every step here is on the manager's OWN report, so the data scope would allow it: the permission tier
        // is the only thing that stops a line manager signing off the rating they wrote.
        var (db, tenantId) = await NewTenantAsync("perf-mgr-sod");
        var (cycleStatus, reviewStatus) = step switch
        {
            "publish" => ("FinalApproval", "FinalApproval"),
            "advance-cycle" => ("Calibration", "ManagerReviewComplete"),
            "decide-appeal" => ("Published", "Appealed"),
            _ => ("Calibration", "ManagerReviewComplete"),
        };
        var w = await SeedAsync(db, tenantId, cycleStatus, reviewStatus);
        var manager = await ManagerAsync(db, tenantId, w);
        var before = await ReviewOf(db, w.ReportReviewId);

        var status = step switch
        {
            "calibrate" => await Adjust(db, manager, w, +20m),
            "override-score" => await Gate<ReviewsController>(manager, nameof(ReviewsController.OverrideScore),
                () => Reviews(db, manager).OverrideScore(w.ReportReviewId,
                    new ScoreOverrideRequest(99m, null, null, null, null, null, "Raised by the reviewer"), Ct)),
            "publish" => await Publish(db, manager, w.ReportReviewId),
            "advance-cycle" => await Advance(db, manager, w.CycleId),
            "calibration-board" => await Gate<CalibrationController>(manager, nameof(CalibrationController.GetBoard),
                () => Calibration(db, manager).GetBoard(w.CycleId, null, Ct)),
            "decide-appeal" => await Gate<ReviewsController>(manager, nameof(ReviewsController.RespondToAppeal),
                () => Reviews(db, manager).RespondToAppeal(w.AppealId!.Value,
                    new AppealResponseRequest("Upheld", "My report is right"), Ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };

        status.Should().Be(403, $"{step} is HR's step, not the line manager's");
        var after = await ReviewOf(db, w.ReportReviewId);
        after.Status.Should().Be(before.Status);
        after.CalibrationAdjustment.Should().Be(before.CalibrationAdjustment);
        after.KpiScore.Should().Be(before.KpiScore);
        (await db.PerformanceCycles.AsNoTracking().SingleAsync(c => c.Id == w.CycleId)).Status.Should().Be(cycleStatus);
    }

    // ── Employee ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Employee_SelfAssesses_ButCannotWriteAManagerReview()
    {
        var (db, tenantId) = await NewTenantAsync("perf-emp");
        var w = await SeedAsync(db, tenantId, cycleStatus: "Active", reviewStatus: "SelfAssessmentDue");
        var employee = await EmployeeAsync(db, tenantId, w);

        (await SelfAssess(db, employee, w.ReportReviewId)).Should().Be(200);
        (await ReviewOf(db, w.ReportReviewId)).Status.Should().Be("SelfAssessmentSubmitted");

        (await ManagerReview(db, employee, w.ReportReviewId)).Should().Be(403,
            "an employee does not write the manager's assessment of themselves");
        (await ReviewOf(db, w.ReportReviewId)).Status.Should().Be("SelfAssessmentSubmitted");
    }

    [Fact]
    public async Task Employee_SeesOnlyTheirOwnGoals_AndCannotSetOne()
    {
        var (db, tenantId) = await NewTenantAsync("perf-emp-goals");
        var w = await SeedAsync(db, tenantId, cycleStatus: "Active", reviewStatus: "SelfAssessmentDue");
        await SeedGoalAsync(db, tenantId, w.ReportId);
        await SeedGoalAsync(db, tenantId, w.OutsiderId);
        var employee = await EmployeeAsync(db, tenantId, w);

        var listed = (OkObjectResult)await Goals(db, employee).List(null, null, null, null, 1, 50, Ct);
        var items = (IEnumerable<EmployeeGoal>)listed.Value!.GetType().GetProperty("items")!.GetValue(listed.Value)!;
        items.Should().ContainSingle().Which.EmployeeId.Should().Be(w.ReportId);

        (await CreateGoal(db, employee, w.ReportId, weight: 30m)).Should().Be(403,
            "goals are set by the line manager or HR; the employee records progress against them");
    }

    // ── A self-assessment is the employee's own ────────────────────────────────────────────────

    [Theory]
    [InlineData("HR Manager")]
    [InlineData("HR Director")]
    [InlineData("Admin")]
    public async Task AnOrganisationWideCaller_CannotSelfAssessOnSomeoneElsesBehalf(string role)
    {
        // The data scope used to be the whole check: an organisation-wide caller passed it for any employee,
        // so HR could submit (and overwrite) the report's self-assessment and move the review on.
        var (db, tenantId) = await NewTenantAsync("perf-self-proxy");
        var w = await SeedAsync(db, tenantId, cycleStatus: "Active", reviewStatus: "SelfAssessmentDue");
        var hrEmployeeId = await SeedColleagueWithReviewAsync(db, tenantId, w, "Hana HR");
        var hr = await LinkedCallerAsync(db, tenantId, role, hrEmployeeId);

        var result = await Reviews(db, hr).SubmitSelfAssessment(w.ReportReviewId,
            new SelfAssessmentRequest("Written by HR", 99m, 99m, 99m, null), Ct);

        StatusOf(result).Should().Be(403);
        var body = System.Text.Json.JsonSerializer.SerializeToElement(((ObjectResult)result).Value);
        body.GetProperty("error").GetString().Should().Be("self_assessment_by_employee_only");
        body.GetProperty("message").GetString().Should().Contain("Only the employee");
        var review = await ReviewOf(db, w.ReportReviewId);
        review.Status.Should().Be("SelfAssessmentDue");
        review.SelfAssessmentNotes.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task AnHrCaller_StillSelfAssessesTheirOwnReview()
    {
        // HR are employees too. Their organisation-wide scope carries no caller employee id, so the check
        // has to find their own record from the account link rather than from the scope.
        var (db, tenantId) = await NewTenantAsync("perf-self-hr-own");
        var w = await SeedAsync(db, tenantId, cycleStatus: "Active", reviewStatus: "SelfAssessmentDue");
        var hrEmployeeId = await SeedColleagueWithReviewAsync(db, tenantId, w, "Hana HR");
        var hr = await LinkedCallerAsync(db, tenantId, "HR Manager", hrEmployeeId);
        var ownReviewId = (await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.EmployeeId == hrEmployeeId)).Id;

        (await SelfAssess(db, hr, ownReviewId)).Should().Be(200);
        (await ReviewOf(db, ownReviewId)).Status.Should().Be("SelfAssessmentSubmitted");
    }

    [Fact]
    public async Task AnOrganisationWideCallerWithNoEmployeeRecord_CannotSelfAssessAnyReview()
    {
        var (db, tenantId) = await NewTenantAsync("perf-self-unlinked");
        var w = await SeedAsync(db, tenantId, cycleStatus: "Active", reviewStatus: "SelfAssessmentDue");
        var admin = await CallerAsync(db, tenantId, "Admin");

        (await SelfAssess(db, admin, w.ReportReviewId)).Should().Be(403);
        (await ReviewOf(db, w.ReportReviewId)).Status.Should().Be("SelfAssessmentDue");
    }

    [Fact]
    public async Task ALineManager_CannotSelfAssessForTheirReport()
    {
        var (db, tenantId) = await NewTenantAsync("perf-self-mgr");
        var w = await SeedAsync(db, tenantId, cycleStatus: "Active", reviewStatus: "SelfAssessmentDue");
        var manager = await ManagerAsync(db, tenantId, w);

        (await SelfAssess(db, manager, w.ReportReviewId)).Should().Be(403);
        (await ReviewOf(db, w.ReportReviewId)).Status.Should().Be("SelfAssessmentDue");
    }

    // ── My Reviews is the caller's own; Team and HR views still list others ────────────────────

    [Fact]
    public async Task MyReviews_ListsOnlyTheCallersOwnReviews_ForEveryAudience()
    {
        var (db, tenantId) = await NewTenantAsync("perf-mine");
        var w = await SeedAsync(db, tenantId, cycleStatus: "Active", reviewStatus: "SelfAssessmentDue");
        var hrEmployeeId = await SeedColleagueWithReviewAsync(db, tenantId, w, "Hana HR");
        var managerReviewId = await SeedReviewAsync(db, tenantId, w, w.ManagerId);

        var hr = await LinkedCallerAsync(db, tenantId, "HR Manager", hrEmployeeId);
        (await ListedEmployeeIds(db, hr, view: "mine")).Should().Equal(new[] { hrEmployeeId },
            "HR's My Reviews is HR's own review, not the whole organisation's");

        var manager = await ManagerAsync(db, tenantId, w);
        (await ListedEmployeeIds(db, manager, view: "mine")).Should().Equal(new[] { w.ManagerId },
            "a manager's My Reviews is their own review, not their report's");
        managerReviewId.Should().NotBeEmpty();

        var employee = await EmployeeAsync(db, tenantId, w);
        (await ListedEmployeeIds(db, employee, view: "mine")).Should().Equal(new[] { w.ReportId });

        var unlinkedAdmin = await CallerAsync(db, tenantId, "Admin");
        (await ListedEmployeeIds(db, unlinkedAdmin, view: "mine")).Should().BeEmpty(
            "a caller with no employee record has no reviews of their own");
    }

    [Fact]
    public async Task TheTeamAndHrViews_StillListTheReviewsInTheCallersScope()
    {
        var (db, tenantId) = await NewTenantAsync("perf-team-view");
        var w = await SeedAsync(db, tenantId, cycleStatus: "Active", reviewStatus: "SelfAssessmentDue");
        var hrEmployeeId = await SeedColleagueWithReviewAsync(db, tenantId, w, "Hana HR");
        await SeedReviewAsync(db, tenantId, w, w.ManagerId);

        var manager = await ManagerAsync(db, tenantId, w);
        (await ListedEmployeeIds(db, manager, view: null)).Should().BeEquivalentTo(new[] { w.ManagerId, w.ReportId },
            "Team Reviews lists the manager's reporting line (the scope has always included their own record)");

        var hr = await LinkedCallerAsync(db, tenantId, "HR Manager", hrEmployeeId);
        (await ListedEmployeeIds(db, hr, view: null)).Should().BeEquivalentTo(
            new[] { w.ManagerId, w.ReportId, w.OutsiderId, hrEmployeeId }, "HR's views list the organisation");
    }

    // ── Analytics are tenant-wide, so they stay with HR ────────────────────────────────────────

    [Theory]
    [InlineData("HR Manager", 200)]
    [InlineData("HR Director", 200)]
    [InlineData("Manager", 403)]
    [InlineData("Employee", 403)]
    public async Task TenantWideAnalytics_StayWithTheHrTier(string role, int expected)
    {
        // Named top and low performers, per-manager bias flags and the raw audit feed, for the whole tenant.
        // Line managers and employees now hold performance.read, so the gate cannot be that key.
        var (db, tenantId) = await NewTenantAsync("perf-analytics");
        var w = await SeedAsync(db, tenantId, cycleStatus: "Published", reviewStatus: "Published");
        var caller = role switch
        {
            "Manager" => await ManagerAsync(db, tenantId, w),
            "Employee" => await EmployeeAsync(db, tenantId, w),
            _ => await CallerAsync(db, tenantId, role),
        };

        (await Gate<AnalyticsController>(caller, nameof(AnalyticsController.CycleAnalytics),
            () => Bind(new AnalyticsController(db), caller).CycleAnalytics(w.CycleId, Ct))).Should().Be(expected);
        (await Gate<AnalyticsController>(caller, nameof(AnalyticsController.Dashboard),
            () => Bind(new AnalyticsController(db), caller).Dashboard(Ct))).Should().Be(expected);
    }

    // ── One connected cycle ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACompleteReviewCycle_RunsOnTheSeededRoles_WithTheWeightsHeldThroughout()
    {
        var (db, tenantId) = await NewTenantAsync("perf-cycle");
        var w = await SeedAsync(db, tenantId, cycleStatus: "Active", reviewStatus: "SelfAssessmentDue", withOutsider: false);
        var hr = await CallerAsync(db, tenantId, "HR Manager");
        var manager = await ManagerAsync(db, tenantId, w);
        var employee = await EmployeeAsync(db, tenantId, w);

        // Goals: the line manager sets a 40% goal for the report and agrees it; the report records progress.
        (await CreateGoal(db, manager, w.ReportId, weight: 40m)).Should().Be(201);
        var goalId = (await db.EmployeeGoals.AsNoTracking().SingleAsync()).Id;
        (await ApproveGoal(db, manager, goalId)).Should().Be(200);
        (await Gate<GoalsController>(employee, nameof(GoalsController.UpdateProgress),
            () => Goals(db, employee).UpdateProgress(goalId, new ProgressUpdateRequest(60, "Half-year", null), Ct))).Should().Be(200);

        // Self-assessment, by the employee only.
        (await ManagerReview(db, employee, w.ReportReviewId)).Should().Be(403);
        (await SelfAssess(db, employee, w.ReportReviewId)).Should().Be(200);

        // HR opens manager review; the line manager cannot move the cycle.
        (await Advance(db, manager, w.CycleId)).Should().Be(403);
        (await Advance(db, hr, w.CycleId)).Should().Be(200);

        // Manager review, by the line manager: scored on the template's weights.
        (await ManagerReview(db, manager, w.ReportReviewId)).Should().Be(200);
        var reviewed = await ReviewOf(db, w.ReportReviewId);
        reviewed.Status.Should().Be("ManagerReviewComplete");
        reviewed.FinalScore.Should().Be(WeightedScore, "KPI 40%, competency 20%, and 10% each for the rest");

        // Calibration, by HR only.
        (await Advance(db, hr, w.CycleId)).Should().Be(200);
        (await Adjust(db, manager, w, +5m)).Should().Be(403);
        (await Adjust(db, hr, w, +5m)).Should().Be(200);
        (await ReviewOf(db, w.ReportReviewId)).FinalScore.Should().Be(WeightedScore + 5m);

        // Finalise and publish, by HR only.
        (await Advance(db, hr, w.CycleId)).Should().Be(200);
        (await ReviewOf(db, w.ReportReviewId)).Status.Should().Be("FinalApproval");
        (await Publish(db, manager, w.ReportReviewId)).Should().Be(403);
        (await Publish(db, hr, w.ReportReviewId)).Should().Be(200);

        // The employee acknowledges the published result.
        (await Gate<ReviewsController>(employee, nameof(ReviewsController.Acknowledge),
            () => Reviews(db, employee).Acknowledge(w.ReportReviewId, Ct))).Should().Be(200);

        var final = await ReviewOf(db, w.ReportReviewId);
        final.Status.Should().Be("Acknowledged");
        final.FinalScore.Should().Be(WeightedScore + 5m, "publishing does not re-weight the calibrated score");
        (await db.EmployeeGoals.AsNoTracking().SingleAsync()).Weight.Should().Be(40m,
            "the goal keeps the weight it was agreed at through the whole cycle");
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Manager review scores below, weighted 40/20/10/10/10/10: 32 + 14 + 9 + 7.5 + 8.5 + 9.5.</summary>
    private const decimal WeightedScore = 80.5m;

    private sealed record World(
        int ManagerId, int ReportId, int OutsiderId, Guid CycleId, Guid ReportReviewId, Guid OutsiderReviewId, Guid? AppealId);

    private static async Task<World> SeedAsync(
        ZayraDbContext db, Guid tenantId, string cycleStatus, string reviewStatus, bool withOutsider = true)
    {
        var manager = NewEmployee(tenantId, "Mona Manager");
        var outsider = NewEmployee(tenantId, "Omar Outsider");
        db.Employees.AddRange(manager, outsider);
        await db.SaveChangesAsync();
        var report = NewEmployee(tenantId, "Rami Report");
        report.ManagerEmployeeId = manager.Id;
        db.Employees.Add(report);

        var template = new PerformanceScorecardTemplate
        {
            TenantId = tenantId, Name = "Standard", IsDefault = true,
            KpiWeight = 40, CompetencyWeight = 20, AttendanceWeight = 10,
            ProductivityWeight = 10, FeedbackWeight = 10, DisciplineWeight = 10,
        };
        var cycle = new PerformanceCycle
        {
            TenantId = tenantId, Name = "FY26", Status = cycleStatus, EnableCalibration = true,
            DefaultScorecardTemplateId = template.Id,
        };
        db.PerformanceScorecardTemplates.Add(template);
        db.PerformanceCycles.Add(cycle);
        await db.SaveChangesAsync();

        AppraisalReview Review(Employee e) => new()
        {
            TenantId = tenantId, CycleId = cycle.Id, CycleName = cycle.Name, ScorecardTemplateId = template.Id,
            EmployeeId = e.Id, EmployeeName = e.FullName, Status = reviewStatus,
            KpiScore = 70m, CompetencyScore = 70m, AttendanceScore = 70m, ProductivityScore = 70m,
            FeedbackScore = 70m, DisciplineScore = 70m, FinalScore = 70m,
        };
        var reportReview = Review(report);
        db.AppraisalReviews.Add(reportReview);
        var outsiderReview = Review(outsider);
        if (withOutsider) db.AppraisalReviews.Add(outsiderReview);

        Guid? appealId = null;
        if (reviewStatus == "Appealed")
        {
            reportReview.IsAppealed = true;
            reportReview.PublishedAt = DateTime.UtcNow.AddDays(-2);
            var appeal = new AppraisalAppeal
            {
                TenantId = tenantId, ReviewId = reportReview.Id, EmployeeId = report.Id,
                EmployeeName = report.FullName, AppealReason = "The KPI target moved mid-year",
            };
            db.AppraisalAppeals.Add(appeal);
            appealId = appeal.Id;
        }
        await db.SaveChangesAsync();
        return new World(manager.Id, report.Id, outsider.Id, cycle.Id, reportReview.Id, outsiderReview.Id, appealId);
    }

    private static Employee NewEmployee(Guid tenantId, string name) => new()
    {
        TenantId = tenantId, EmployeeCode = $"PRF{Guid.NewGuid():N}"[..12], FullName = name,
        Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddYears(-2),
    };

    private static async Task<Guid> SeedGoalAsync(ZayraDbContext db, Guid tenantId, int employeeId)
    {
        var goal = new EmployeeGoal
        {
            TenantId = tenantId, EmployeeId = employeeId, EmployeeName = "seeded", Title = "Seeded goal",
            Category = "Individual", KpiType = "Quantitative", TargetValue = 100, Weight = 20, Status = "Draft",
        };
        db.EmployeeGoals.Add(goal);
        await db.SaveChangesAsync();
        return goal.Id;
    }

    /// <summary>A colleague outside the manager's line (for example an HR employee) with their own review in the cycle.</summary>
    private static async Task<int> SeedColleagueWithReviewAsync(ZayraDbContext db, Guid tenantId, World w, string name)
    {
        var colleague = NewEmployee(tenantId, name);
        db.Employees.Add(colleague);
        await db.SaveChangesAsync();
        await SeedReviewAsync(db, tenantId, w, colleague.Id);
        return colleague.Id;
    }

    private static async Task<Guid> SeedReviewAsync(ZayraDbContext db, Guid tenantId, World w, int employeeId)
    {
        var cycle = await db.PerformanceCycles.AsNoTracking().SingleAsync(c => c.Id == w.CycleId);
        var employee = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == employeeId);
        var review = new AppraisalReview
        {
            TenantId = tenantId, CycleId = cycle.Id, CycleName = cycle.Name,
            ScorecardTemplateId = cycle.DefaultScorecardTemplateId!.Value,
            EmployeeId = employeeId, EmployeeName = employee.FullName, Status = "SelfAssessmentDue",
        };
        db.AppraisalReviews.Add(review);
        await db.SaveChangesAsync();
        return review.Id;
    }

    /// <summary>The employees whose reviews the list returns, for the given view.</summary>
    private static async Task<int[]> ListedEmployeeIds(ZayraDbContext db, ClaimsPrincipal caller, string? view)
    {
        var listed = (OkObjectResult)await Reviews(db, caller).List(null, null, null, null, view, 1, 50, Ct);
        var items = (IEnumerable<AppraisalReview>)listed.Value!.GetType().GetProperty("items")!.GetValue(listed.Value)!;
        return items.Select(r => r.EmployeeId).ToArray();
    }

    /// <summary>The seeded role's real bundle, signed in as the employee record the user account is linked to.</summary>
    private static async Task<ClaimsPrincipal> LinkedCallerAsync(ZayraDbContext db, Guid tenantId, string role, int employeeId)
    {
        var caller = await CallerAsync(db, tenantId, role);
        return new ClaimsPrincipal(new ClaimsIdentity(
            caller.Claims.Append(new Claim("employee_id", employeeId.ToString())), "Test"));
    }

    private static Task<ClaimsPrincipal> ManagerAsync(ZayraDbContext db, Guid tenantId, World w) =>
        LinkedCallerAsync(db, tenantId, "Manager", w.ManagerId);

    private static Task<ClaimsPrincipal> EmployeeAsync(ZayraDbContext db, Guid tenantId, World w) =>
        LinkedCallerAsync(db, tenantId, "Employee", w.ReportId);

    private static Task<AppraisalReview> ReviewOf(ZayraDbContext db, Guid id) =>
        db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == id);

    private static Task<int> Gate<TController>(ClaimsPrincipal caller, string action, Func<Task<IActionResult>> invoke)
        where TController : ControllerBase =>
        ProductionAuthorizationGate.StatusAsync<TController>(caller, action, invoke);

    private static Task<int> ManagerReview(ZayraDbContext db, ClaimsPrincipal caller, Guid reviewId) =>
        Gate<ReviewsController>(caller, nameof(ReviewsController.SubmitManagerReview),
            () => Reviews(db, caller).SubmitManagerReview(reviewId,
                new ManagerReviewRequest(80m, 70m, 90m, 75m, 85m, 95m, "Delivered the migration.", null, null, null), Ct));

    private static Task<int> SelfAssess(ZayraDbContext db, ClaimsPrincipal caller, Guid reviewId) =>
        Gate<ReviewsController>(caller, nameof(ReviewsController.SubmitSelfAssessment),
            () => Reviews(db, caller).SubmitSelfAssessment(reviewId,
                new SelfAssessmentRequest("I shipped the migration.", 85m, 80m, 80m, null), Ct));

    private static Task<int> Publish(ZayraDbContext db, ClaimsPrincipal caller, Guid reviewId) =>
        Gate<ReviewsController>(caller, nameof(ReviewsController.Publish),
            () => Reviews(db, caller).Publish(reviewId, Ct));

    private static Task<int> Adjust(ZayraDbContext db, ClaimsPrincipal caller, World w, decimal points) =>
        Gate<CalibrationController>(caller, nameof(CalibrationController.AdjustScore),
            () => Calibration(db, caller).AdjustScore(w.CycleId,
                new CalibrationAdjustRequest(w.ReportReviewId, points, "Calibration panel, 2026-09-29"), Ct));

    private static Task<int> Advance(ZayraDbContext db, ClaimsPrincipal caller, Guid cycleId) =>
        Gate<CyclesController>(caller, nameof(CyclesController.Advance),
            () => Cycles(db, caller).Advance(cycleId, Ct));

    private static Task<int> CreateGoal(ZayraDbContext db, ClaimsPrincipal caller, int employeeId, decimal weight) =>
        Gate<GoalsController>(caller, nameof(GoalsController.Create),
            () => Goals(db, caller).Create(new GoalRequest(
                employeeId, "ignored", null, "Close support cases", "Quarterly target", "Individual", "Quantitative",
                "cases", TargetValue: 100, ActualValue: 0, Weight: weight, DueDate: new DateOnly(2026, 12, 31)), Ct));

    private static Task<int> ApproveGoal(ZayraDbContext db, ClaimsPrincipal caller, Guid goalId) =>
        Gate<GoalsController>(caller, nameof(GoalsController.Approve),
            () => Goals(db, caller).Approve(goalId, Ct));

    private static ReviewsController Reviews(ZayraDbContext db, ClaimsPrincipal caller) =>
        Bind(new ReviewsController(db, new PerformanceService(db), new DataScopeService(db),
            new HrmHierarchyService(db, new AuditService(db))), caller);

    private static GoalsController Goals(ZayraDbContext db, ClaimsPrincipal caller) =>
        Bind(new GoalsController(db, new DataScopeService(db), new HrmHierarchyService(db, new AuditService(db))), caller);

    private static CalibrationController Calibration(ZayraDbContext db, ClaimsPrincipal caller) =>
        Bind(new CalibrationController(db, new PerformanceService(db)), caller);

    private static CyclesController Cycles(ZayraDbContext db, ClaimsPrincipal caller) =>
        Bind(new CyclesController(db, new PerformanceService(db)), caller);

    /// <summary>Every performance action carrying an <c>[Authorize(Roles=…)]</c> gate, with the roles it names.</summary>
    private static IEnumerable<(string Controller, string Action, string[] Roles, string[] Verbs)> RoleGatedPerformanceActions()
    {
        var controllers = typeof(ReviewsController).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(ReviewsController).Namespace
                        && typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);
        foreach (var controller in controllers)
        {
            var classRoles = controller.GetCustomAttributes<AuthorizeAttribute>(true)
                .Where(a => !string.IsNullOrWhiteSpace(a.Roles)).SelectMany(a => a.Roles!.Split(',')).ToList();
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var verbs = method.GetCustomAttributes<HttpMethodAttribute>(true).SelectMany(a => a.HttpMethods).Distinct().ToArray();
                if (verbs.Length == 0) continue;
                var methodRoles = method.GetCustomAttributes<AuthorizeAttribute>(true)
                    .Where(a => !string.IsNullOrWhiteSpace(a.Roles)).SelectMany(a => a.Roles!.Split(',')).ToList();
                var roles = (methodRoles.Count > 0 ? methodRoles : classRoles).Select(r => r.Trim()).ToArray();
                if (roles.Length == 0) continue;
                yield return (controller.Name[..^"Controller".Length], method.Name, roles, verbs);
            }
        }
    }
}
