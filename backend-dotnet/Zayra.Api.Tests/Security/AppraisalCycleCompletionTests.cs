using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Performance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Performance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Security.AppraisalScoringIntegrityTests;
using static Zayra.Api.Tests.Security.SeededRoleBundles;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// What lets one appraisal cycle reach the end: HR can waive a self-assessment nobody will submit or exclude a
/// leaver (each with a written reason in the audit trail), and a correction after publication goes back through
/// publish and acknowledgement instead of silently changing a result the employee already acknowledged.
/// </summary>
public class AppraisalCycleCompletionTests
{
    // ── Waive a self-assessment ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HR_CanWaiveAnOutstandingSelfAssessment_WithAReason_AndTheManagerReviewOpens()
    {
        var (db, tenantId) = await NewTenantAsync("waive");
        var s = await SeedReviewAsync(db, tenantId, "SelfAssessmentDue", "InReview");
        var hr = await CallerAsync(db, tenantId, "HR Director");

        StatusOf(await Reviews(db, hr).WaiveSelfAssessment(s.ReviewId, new ReasonRequest("  "), default)).Should().Be(400);
        StatusOf(await Reviews(db, hr).WaiveSelfAssessment(s.ReviewId, new ReasonRequest("On long-term sick leave since March."), default)).Should().Be(200);

        (await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == s.ReviewId)).Status.Should().Be("ManagerReview");
        var audit = await db.PerformanceAuditLogs.AsNoTracking().SingleAsync(a => a.EntityId == s.ReviewId.ToString() && a.Action == "SelfAssessmentWaived");
        audit.Reason.Should().Be("On long-term sick leave since March.");

        StatusOf(await Reviews(db, hr).WaiveSelfAssessment(s.ReviewId, new ReasonRequest("again"), default))
            .Should().Be(409, "only an outstanding self-assessment can be waived");
    }

    [Fact]
    public async Task AReviewerWithoutTheApproverTier_CannotWaiveOrExclude()
    {
        var (db, tenantId) = await NewTenantAsync("waive-tier");
        var s = await SeedReviewAsync(db, tenantId, "SelfAssessmentDue", "InReview");
        var manager = await CallerAsync(db, tenantId, "Manager");
        manager.HasClaim("permission", "performance.approve").Should().BeFalse();

        StatusOf(await Reviews(db, manager).WaiveSelfAssessment(s.ReviewId, new ReasonRequest("Absent"), default)).Should().Be(403);
        StatusOf(await Reviews(db, manager).Exclude(s.ReviewId, new ReasonRequest("Leaver"), default)).Should().Be(403);
        (await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == s.ReviewId)).Status.Should().Be("SelfAssessmentDue");
    }

    // ── Exclude a leaver, and the cycle still reaches Published ────────────────────────────────

    [Fact]
    public async Task OneSilentEmployee_ExcludedWithAReason_NoLongerBlocksTheCycle()
    {
        var (db, tenantId) = await NewTenantAsync("exclude");
        var done = await SeedReviewAsync(db, tenantId, "ManagerReviewComplete", "InReview");
        // A second employee in the SAME cycle who never self-assessed (left the company).
        var leaver = new Employee { TenantId = tenantId, EmployeeCode = $"LVR{Guid.NewGuid():N}"[..12], FullName = "Gone Person", Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddYears(-1) };
        db.Employees.Add(leaver);
        await db.SaveChangesAsync();
        var stuck = new AppraisalReview
        {
            TenantId = tenantId, CycleId = done.CycleId, CycleName = "FY26", EmployeeId = leaver.Id, EmployeeName = leaver.FullName,
            ScorecardTemplateId = (await db.AppraisalReviews.SingleAsync(r => r.Id == done.ReviewId)).ScorecardTemplateId, Status = "SelfAssessmentDue",
        };
        db.AppraisalReviews.Add(stuck);
        db.PerformanceCycleEmployees.Add(new PerformanceCycleEmployee { TenantId = tenantId, CycleId = done.CycleId, EmployeeId = leaver.Id, Status = "Active" });
        await db.SaveChangesAsync();

        var hr = await CallerAsync(db, tenantId, "HR Director");
        var cycles = Bind(new CyclesController(db, new PerformanceService(db), new DataScopeService(db)), hr);

        StatusOf(await cycles.Advance(done.CycleId, default)).Should().Be(409, "the leaver's review is still open");

        StatusOf(await Reviews(db, hr).Exclude(stuck.Id, new ReasonRequest(""), default)).Should().Be(400);
        StatusOf(await Reviews(db, hr).Exclude(stuck.Id, new ReasonRequest("Resigned 2026-09-30; not appraised this cycle."), default)).Should().Be(200);
        (await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == stuck.Id)).Status.Should().Be("Excluded");
        (await db.PerformanceCycleEmployees.AsNoTracking().SingleAsync(e => e.CycleId == done.CycleId && e.EmployeeId == leaver.Id)).Status.Should().Be("Excluded");
        (await db.PerformanceAuditLogs.AsNoTracking().SingleAsync(a => a.EntityId == stuck.Id.ToString() && a.Action == "ExcludedFromCycle"))
            .Reason.Should().Be("Resigned 2026-09-30; not appraised this cycle.");

        StatusOf(await cycles.Advance(done.CycleId, default)).Should().Be(200);   // InReview -> FinalApproval (no calibration)
        StatusOf(await Reviews(db, hr).Publish(done.ReviewId, default)).Should().Be(200);
        StatusOf(await cycles.Advance(done.CycleId, default)).Should().Be(200);   // FinalApproval -> Published
        (await db.PerformanceCycles.AsNoTracking().SingleAsync(c => c.Id == done.CycleId)).Status.Should().Be("Published");
    }

    [Fact]
    public async Task APublishedReview_CannotBeExcluded()
    {
        var (db, tenantId) = await NewTenantAsync("exclude-pub");
        var s = await SeedReviewAsync(db, tenantId, "Published", "FinalApproval");
        var hr = await CallerAsync(db, tenantId, "HR Director");

        StatusOf(await Reviews(db, hr).Exclude(s.ReviewId, new ReasonRequest("Leaver"), default)).Should().Be(409);
        (await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == s.ReviewId)).Status.Should().Be("Published");
    }

    // ── Override after publication ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SelfAssessmentDue")]
    [InlineData("SelfAssessmentSubmitted")]
    [InlineData("Appealed")]
    [InlineData("Excluded")]
    public async Task OverrideScore_IsRefused_WhereThereIsNoManagerResultToCorrect_OrAnAppealIsOpen(string status)
    {
        var (db, tenantId) = await NewTenantAsync("ovr-state");
        var s = await SeedReviewAsync(db, tenantId, status, "InReview");
        var hr = await CallerAsync(db, tenantId, "HR Director");

        var result = await Reviews(db, hr).OverrideScore(s.ReviewId, new ScoreOverrideRequest(55m, null, null, null, null, null, "Panel"), default);

        StatusOf(result).Should().Be(409);
        (await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == s.ReviewId)).KpiScore.Should().Be(70m);
    }

    [Fact]
    public async Task OverrideAfterPublication_WithdrawsTheResult_AndItMustBeRepublishedAndReacknowledged()
    {
        var (db, tenantId) = await NewTenantAsync("ovr-post");
        var s = await SeedReviewAsync(db, tenantId, "Acknowledged", "Published");
        var review = await db.AppraisalReviews.SingleAsync(r => r.Id == s.ReviewId);
        review.PublishedAt = DateTime.UtcNow.AddDays(-5); review.AcknowledgedAt = DateTime.UtcNow.AddDays(-4);
        await db.SaveChangesAsync();
        var hr = await CallerAsync(db, tenantId, "HR Director");

        StatusOf(await Reviews(db, hr).OverrideScore(s.ReviewId, new ScoreOverrideRequest(90m, null, null, null, null, null, "  "), default))
            .Should().Be(400, "a correction after publication needs a written reason");

        StatusOf(await Reviews(db, hr).OverrideScore(s.ReviewId,
            new ScoreOverrideRequest(90m, null, null, null, null, null, "Q3 sales were booked to the wrong person."), default)).Should().Be(200);

        var stored = await db.AppraisalReviews.AsNoTracking().SingleAsync(r => r.Id == s.ReviewId);
        stored.KpiScore.Should().Be(90m);
        stored.Status.Should().Be("FinalApproval", "the acknowledged result is withdrawn until HR re-issues it");
        var actions = await db.PerformanceAuditLogs.AsNoTracking().Where(a => a.EntityId == s.ReviewId.ToString()).Select(a => new { a.Action, a.Reason, a.OldValue, a.NewValue }).ToListAsync();
        actions.Should().Contain(a => a.Action == "ScoreOverride" && a.Reason == "Q3 sales were booked to the wrong person.");
        actions.Should().Contain(a => a.Action == "WithdrawnForCorrection" && a.OldValue == "Acknowledged" && a.NewValue == "FinalApproval");

        // Re-issue reaches the employee again, and they acknowledge the corrected result.
        StatusOf(await Reviews(db, hr).Publish(s.ReviewId, default)).Should().Be(200);
        var employee = await EmployeeCallerAsync(db, tenantId, s.EmployeeId);
        StatusOf(await Reviews(db, employee).Acknowledge(s.ReviewId, default)).Should().Be(200);
    }

    // ── Probation: a decided review is not reopened ────────────────────────────────────────────

    [Theory]
    [InlineData("Closed")]
    [InlineData("HRApproved")]
    public async Task ProbationManagerReview_CannotReopenADecidedReview(string status)
    {
        var (db, tenantId) = await NewTenantAsync("prob-closed");
        var employee = new Employee { TenantId = tenantId, EmployeeCode = $"PRB{Guid.NewGuid():N}"[..12], FullName = "Probationer", Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddMonths(-3) };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        var probation = new ProbationReview
        {
            TenantId = tenantId, EmployeeId = employee.Id, EmployeeName = employee.FullName, Status = status, HrDecision = "Confirmed",
            ManagerRecommendation = "Confirm", ProbationStartDate = new DateOnly(2026, 6, 1), ProbationEndDate = new DateOnly(2026, 8, 31),
        };
        db.ProbationReviews.Add(probation);
        await db.SaveChangesAsync();
        var hr = await CallerAsync(db, tenantId, "HR Director");
        var controller = Bind(new ProbationController(db, new DataScopeService(db)), hr);

        var result = await controller.ManagerReview(probation.Id, new ProbationManagerReviewRequest("Changed my mind", 2m, "Terminate", null), default);

        StatusOf(result).Should().Be(409);
        var stored = await db.ProbationReviews.AsNoTracking().SingleAsync(p => p.Id == probation.Id);
        stored.Status.Should().Be(status);
        stored.ManagerRecommendation.Should().Be("Confirm");
    }

    // ── Dashboard counts ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dashboard_CountsWhatIsActuallyWaiting()
    {
        var (db, tenantId) = await NewTenantAsync("dash");
        var a = await SeedReviewAsync(db, tenantId, "SelfAssessmentSubmitted", "InReview");
        var b = await SeedReviewAsync(db, tenantId, "ManagerReview", "InReview");
        var c = await SeedReviewAsync(db, tenantId, "ManagerReviewComplete", "Calibration", enableCalibration: true);
        var d = await SeedReviewAsync(db, tenantId, "ManagerReviewComplete", "InReview");   // not in calibration yet
        _ = (a, b, d);
        var hr = await CallerAsync(db, tenantId, "HR Director");
        var analytics = Bind(new AnalyticsController(db, new DataScopeService(db)), hr);

        var body = Json(await analytics.Dashboard(default)).GetProperty("pendingActions");
        body.GetProperty("managerReviewPending").GetInt32().Should().Be(2, "a submitted self-assessment is waiting for its manager too");
        body.GetProperty("calibrationPending").GetInt32().Should().Be(1, "reviews of a cycle in Calibration that HR has to calibrate");
        _ = c;
    }
}
