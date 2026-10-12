using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Succession;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Succession.SuccessionTestWorld;

namespace Zayra.Api.Tests.Succession;

/// <summary>
/// SUC-09 (transfer, termination and ineligibility, vacant role, retired position, appointment, conflicting reviewers)
/// and SUC-05 (bench, coverage, gaps and trend computed from rows; every figure equals the rows its drill returns).
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class SuccessionEdgeAndReportTests(PostgresFixture fx)
{
    private static async Task<SuccessionSweepResult> SweepAsync(SuccessionTestWorld w)
    {
        await using var scope = w.System();
        return await Get<SuccessionSweepService>(scope).RunTenantAsync(w.TenantA, default);
    }

    private static async Task UpdateEmployeeAsync(SuccessionTestWorld w, int id, Action<Employee> change)
    {
        await using var db = w.Db();
        var e = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
        change(e);
        e.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SUC09_Transfer_Flags_Termination_AutoWithdraws_Idempotently_AndSuspensionOnlyFlags()
    {
        await using var w = await CreateAsync(fx);
        var cfo = await w.DesignateCfoAsync();
        var cara = await w.CalibratedCandidateAsync(cfo.Id, w.Cara, 3, ReadinessLevels.ReadyNow);
        var cody = await w.NominateAsync(cfo.Id, w.Cody);
        var pete = await w.NominateAsync(cfo.Id, w.Pete);
        var v1 = await w.SubmitSlateAsync((await w.DraftSlateAsync(cfo.Id, (cara.Id, 1))).Id);
        await w.DecideAsync(w.HrDirectorP(), v1.ApprovalRequestId!.Value, "Approve", null);

        await UpdateEmployeeAsync(w, w.Cody, e => e.ManagerEmployeeId = w.Uma);
        await UpdateEmployeeAsync(w, w.Pete, e => e.Status = EmployeeStatuses.Suspended);
        await UpdateEmployeeAsync(w, w.Cara, e => e.Status = EmployeeStatuses.Offboarded);

        var first = await SweepAsync(w);
        first.Withdrawn.Should().Be(1);
        first.Flagged.Should().Be(2);
        var caraRow = await w.NominationRowAsync(cara.Id);
        caraRow.Status.Should().Be(NominationStatuses.Withdrawn);
        caraRow.ExitReason.Should().Be(NominationExitReasons.Terminated);
        caraRow.Readiness.Should().Be(ReadinessLevels.ReadyNow, "history is kept");
        var codyRow = await w.NominationRowAsync(cody.Id);
        codyRow.Status.Should().Be(NominationStatuses.Proposed);
        codyRow.NeedsReview.Should().BeTrue();
        codyRow.ReviewReason.Should().Contain("moved");
        (await w.NominationRowAsync(pete.Id)).ReviewReason.Should().Contain("suspended");

        var again = await SweepAsync(w);
        again.Withdrawn.Should().Be(0);
        again.Flagged.Should().Be(0, "a flag is raised once");
        await using (var db = w.Db())
            (await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.EntityId == cara.Id.ToString() && a.Action == "succession.nomination.auto_withdrawn"))
                .Should().Be(1);

        // The approved slate still names Cara, so it is stale and no longer counts her.
        var summary = (await w.Run<SuccessionPositionService, IReadOnlyList<CriticalPositionSummary>>(w.HrManagerP(), (s, a) => s.ListAsync(a, null, default)))
            .Single(p => p.Id == cfo.Id);
        summary.Gaps.Should().Contain([SuccessionGaps.StaleSlate, SuccessionGaps.NoReadyNow]);
        summary.ReadyNow.Should().Be(0);
        var versions = await w.Run<SuccessionSlateService, IReadOnlyList<SlateVersionDto>>(w.HrManagerP(), (s, a) => s.ListAsync(a, cfo.Id, default));
        versions.Single().ClosedSinceApproval.Should().Equal(cara.Id);

        // HR reviews the transfer and clears the flag; the snapshot of the candidate's placement is refreshed.
        var cleared = await w.Run<SuccessionNominationService, NominationDto>(w.HrManagerP(), (s, a) => s.ClearReviewFlagAsync(a, cody.Id,
            new ReasonInput("Reviewed: still a credible successor."), default));
        cleared.NeedsReview.Should().BeFalse();
        (await SweepAsync(w)).Flagged.Should().Be(0);
    }

    [Fact]
    public async Task SUC09_AppointedToThePosition_WithdrawsWithReasonAppointed()
    {
        await using var w = await CreateAsync(fx);
        var cfo = await w.DesignateCfoAsync();
        var cody = await w.NominateAsync(cfo.Id, w.Cody);
        await using (var db = w.Db())
        {
            var position = await db.Positions.IgnoreQueryFilters().SingleAsync(p => p.Id == w.CfoPosition);
            position.IncumbentEmployeeId = w.Cody;
            await db.SaveChangesAsync();
        }
        (await SweepAsync(w)).Appointed.Should().Be(1);
        (await w.NominationRowAsync(cody.Id)).ExitReason.Should().Be(NominationExitReasons.Appointed);
    }

    [Fact]
    public async Task SUC09_RetiringAPosition_CancelsItsOpenSlateAndPendingApproval_AndWithdrawsItsNominations()
    {
        await using var w = await CreateAsync(fx);
        var cfo = await w.DesignateCfoAsync();
        var cara = await w.CalibratedCandidateAsync(cfo.Id, w.Cara, 3, ReadinessLevels.ReadyNow);
        var pending = await w.SubmitSlateAsync((await w.DraftSlateAsync(cfo.Id, (cara.Id, 1))).Id);

        var retired = await w.Run<SuccessionPositionService, CriticalPositionSummary>(w.HrManagerP(), (s, a) => s.RetireAsync(a, cfo.Id, "Role merged into Group CFO", default));
        retired.Status.Should().Be(CriticalPositionStatuses.Retired);
        retired.Gaps.Should().BeEmpty("a retired designation is not a coverage gap");
        var slate = await w.SlateRowAsync(pending.Id);
        slate.Status.Should().Be(SlateStatuses.Cancelled);
        slate.CancelReason.Should().Contain("merged");
        await using (var db = w.Db())
        {
            var approval = await db.ApprovalRequests.IgnoreQueryFilters().SingleAsync(a => a.Id == pending.ApprovalRequestId);
            approval.Status.Should().Be("Cancelled");
            approval.CompletedAtUtc.Should().NotBeNull();
        }
        var n = await w.NominationRowAsync(cara.Id);
        n.Status.Should().Be(NominationStatuses.Withdrawn);
        n.ExitReason.Should().Be(NominationExitReasons.PositionRetired);

        var decide = () => w.DecideAsync(w.HrDirectorP(), pending.ApprovalRequestId!.Value, "Approve", null);
        await decide.Should().ThrowAsync<InvalidOperationException>("the approval was closed with the position");
        var twice = () => w.Run<SuccessionPositionService, CriticalPositionSummary>(w.HrManagerP(), (s, a) => s.RetireAsync(a, cfo.Id, "again", default));
        (await twice.Should().ThrowAsync<SuccessionException>()).Which.Code.Should().Be("position_retired");

        // A designation whose position was closed in Organization is retired by the sweep.
        var controller = await w.Run<SuccessionPositionService, CriticalPositionSummary>(w.HrManagerP(), (s, a) => s.DesignateAsync(a,
            new DesignateInput(w.ControllerPosition, "Medium", "High", "Owns month-end close and controls.", null, null), default));
        await using (var db = w.Db())
        {
            (await db.Positions.IgnoreQueryFilters().SingleAsync(p => p.Id == w.ControllerPosition)).Status = PositionStatuses.Closed;
            await db.SaveChangesAsync();
        }
        (await SweepAsync(w)).PositionsRetired.Should().Be(1);
        await using (var db = w.Db())
            (await db.SuccessionCriticalPositions.IgnoreQueryFilters().SingleAsync(p => p.Id == controller.Id)).Status.Should().Be(CriticalPositionStatuses.Retired);
    }

    [Fact]
    public async Task SUC09_ConflictingReviewers_Are409()
    {
        await using var w = await CreateAsync(fx);
        var cfo = await w.DesignateCfoAsync();
        await w.NominateAsync(cfo.Id, w.Cara);
        await w.NominateAsync(cfo.Id, w.Cody);

        async Task<string?> AssessAs(int assessor, int candidate)
        {
            try
            {
                await w.AssessAsync(candidate, 2, by: w.HrDirectorAs(assessor));
                return null;
            }
            catch (SuccessionException ex) when (ex.Status == 409) { return ex.Code + ":" + System.Text.Json.JsonSerializer.Serialize(ex.Details); }
        }
        (await AssessAs(w.Cara, w.Cara)).Should().Contain("reviewer_conflict").And.Contain("self");
        (await AssessAs(w.Rana, w.Cara)).Should().Contain("manager_chain", "Rana reports to Cara");
        (await AssessAs(w.Cody, w.Cara)).Should().Contain("competing_nominee", "Cody is nominated for the same position");
        (await AssessAs(w.Pete, w.Cara)).Should().BeNull("an uninvolved reviewer may assess");

        // The same rule binds calibration.
        var cody = await w.AssessAsync(w.Cody, 2);
        var calibrate = () => w.CalibrateAssessmentAsync(cody.Id, 2, null, cody.Version, w.HrDirectorAs(w.Cara));
        (await calibrate.Should().ThrowAsync<SuccessionException>()).Which.Code.Should().Be("reviewer_conflict");
    }

    [Fact]
    public async Task SUC05_And09_BenchCoverageGapsAndTrend_ReconcileToTheirRows_AndAVacantRoleIsAGap()
    {
        await using var w = await CreateAsync(fx);
        var cfo = await w.DesignateCfoAsync();
        var controller = await w.Run<SuccessionPositionService, CriticalPositionSummary>(w.HrManagerP(), (s, a) => s.DesignateAsync(a,
            new DesignateInput(w.ControllerPosition, "High", "High", "Owns month-end close and controls.", null, null), default));
        var ops = await w.Run<SuccessionPositionService, CriticalPositionSummary>(w.HrManagerP(), (s, a) => s.DesignateAsync(a,
            new DesignateInput(w.A2Position, "Low", "Medium", "Runs the logistics network in Alpha Logistics.", null, null), default));
        controller.Vacant.Should().BeTrue();
        controller.Gaps.Should().Contain([SuccessionGaps.VacantNoReadyNow, SuccessionGaps.HighRisk]);
        ops.Gaps.Should().NotContain(SuccessionGaps.HighRisk);

        var cara = await w.CalibratedCandidateAsync(cfo.Id, w.Cara, 3, ReadinessLevels.ReadyNow);
        var cody = await w.CalibratedCandidateAsync(cfo.Id, w.Cody, 2, ReadinessLevels.OneToTwoYears);
        var v1 = await w.SubmitSlateAsync((await w.DraftSlateAsync(cfo.Id, (cara.Id, 1), (cody.Id, 2))).Id);
        await w.DecideAsync(w.HrDirectorP(), v1.ApprovalRequestId!.Value, "Approve", null);

        var report = await w.Run<SuccessionReportService, SuccessionReportDto>(w.HrDirectorP(), (s, a) => s.SummaryAsync(a, default));
        decimal M(string key) => report.Metrics.Single(m => m.Key == key).Value;
        report.ActivePositions.Should().Be(3);
        M("coverage").Should().Be(33.3m);
        report.Metrics.Single(m => m.Key == "coverage").Numerator.Should().Be(1);
        M("benchReadyNow").Should().Be(1);
        M("benchOneToTwoYears").Should().Be(1);
        M("gapNoApprovedSlate").Should().Be(2);
        M("gapVacantNoReadyNow").Should().Be(2, "the controller role has no incumbent, the A2 role neither");

        // Every figure equals the rows behind it.
        foreach (var metric in report.Metrics.Where(m => m.Drill.StartsWith("gap:") || m.Drill.StartsWith("bench:") || m.Drill == "positions"
                                                          || m.Drill is "coverage" or "extendedCoverage"))
        {
            var drill = await w.Run<SuccessionReportService, DrillResult>(w.HrDirectorP(), (s, a) => s.DrillAsync(a, metric.Drill, default));
            var rows = drill.Kind == "candidates" ? drill.Candidates.Count : drill.Positions.Count;
            rows.Should().Be((int)(metric.Numerator ?? metric.Value), $"{metric.Key} must equal the rows its drill returns");
        }
        var bench = await w.Run<SuccessionReportService, DrillResult>(w.HrDirectorP(), (s, a) => s.DrillAsync(a, "bench:ReadyNow", default));
        bench.Candidates.Single().Candidate.EmployeeId.Should().Be(w.Cara);

        // Candidate rows are withheld from a reports reader without slate access (Admin).
        var adminBench = () => w.Run<SuccessionReportService, DrillResult>(w.AdminP(), (s, a) => s.DrillAsync(a, "bench:ReadyNow", default));
        (await adminBench.Should().ThrowAsync<SuccessionException>()).Which.Status.Should().Be(403);

        // Company scope: HR of Alpha Logistics sees one position.
        var a2 = await w.Run<SuccessionReportService, SuccessionReportDto>(w.HrManagerA2Only(), (s, a) => s.SummaryAsync(a, default));
        a2.ActivePositions.Should().Be(1);

        // The trend takes the version effective at each month end.
        var trend = await w.Run<SuccessionReportService, IReadOnlyList<TrendPoint>>(w.HrDirectorP(), (s, a) => s.TrendAsync(a, 3, default));
        trend.Should().HaveCount(3);
        trend[^1].ActivePositions.Should().Be(3);
        trend[^1].WithReadyNow.Should().Be(1);
        trend[0].ActivePositions.Should().Be(0, "nothing was designated two months ago");

        // Over-commitment: Ready-now on three approved slates is flagged.
        foreach (var target in new[] { controller.Id, ops.Id })
        {
            var n = await w.NominateAsync(target, w.Cara);
            n = await w.Run<SuccessionNominationService, NominationDto>(w.HrManagerP(), (s, a) => s.StartReviewAsync(a, n.Id, default));
            n = await w.Run<SuccessionNominationService, NominationDto>(w.HrDirectorP(), (s, a) => s.CalibrateAsync(a, n.Id, new ReadinessInput(ReadinessLevels.ReadyNow, null, n.Version), default));
            var v = await w.SubmitSlateAsync((await w.DraftSlateAsync(target, (n.Id, 1))).Id);
            await w.DecideAsync(w.HrDirectorP(), v.ApprovalRequestId!.Value, "Approve", null);
        }
        var caraNow = await w.Run<SuccessionNominationService, NominationDto>(w.HrManagerP(), (s, a) => s.GetAsync(a, cara.Id, default));
        caraNow.ReadyNowOnApprovedSlates.Should().Be(3);
        caraNow.OverCommitted.Should().BeTrue();
    }
}
