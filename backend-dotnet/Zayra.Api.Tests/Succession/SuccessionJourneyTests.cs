using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Performance;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Infrastructure.Succession;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Succession.SuccessionTestWorld;

namespace Zayra.Api.Tests.Succession;

/// <summary>
/// SUC-01 (critical positions on the org authority), SUC-02 (competing nominations, readiness, evidence), SUC-03 (talent
/// review, calibration with and without a reason, the 9-box with its inputs, Unrated never placed, calibrated rows
/// immutable) and SUC-06 (slate submit → return → revise → approve → supersede, snapshot history kept, one winner of a
/// concurrent approval) — on PostgreSQL, through the real services and the real approval engine.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class SuccessionJourneyTests(PostgresFixture fx)
{
    [Fact]
    public async Task SubmittingASlateNobodyCouldDecide_IsRefusedWith422_AndTheSlateStaysADraft()
    {
        await using var w = await CreateAsync(fx);
        var cfo = await w.DesignateCfoAsync();
        var cara = await w.CalibratedCandidateAsync(cfo.Id, w.Cara, 3, ReadinessLevels.ReadyNow);
        var draft = await w.DraftSlateAsync(cfo.Id, (cara.Id, 1));
        await using (var db = fx.CreateDb())
            // The only HR Director login leaves; nobody holds Admin or approvals.override in this tenant.
            await TestApprovalConfig.DeactivateAsync(db, await TestApprovalConfig.EnsureDeciderAsync(db, w.TenantA, "HR Director"));

        var refused = await FluentActions.Invoking(() => w.SubmitSlateAsync(draft.Id)).Should().ThrowAsync<SuccessionException>();

        refused.Which.Status.Should().Be(422);
        refused.Which.Code.Should().Be(ApprovalNoEligibleDeciderException.ErrorCode);
        (await w.SlateRowAsync(draft.Id)).Status.Should().Be(SlateStatuses.Draft);
        await using (var db = fx.CreateDb())
            (await db.ApprovalRequests.IgnoreQueryFilters().AnyAsync(a => a.EntityId == draft.Id.ToString())).Should().BeFalse();
    }

    [Fact]
    public async Task SUC01_Designation_UsesThePositionAuthority_AndRefusesNoCompanyAndDuplicates()
    {
        await using var w = await CreateAsync(fx);
        var cfo = await w.DesignateCfoAsync();
        cfo.PositionCode.Should().Be("CFO");
        cfo.CompanyId.Should().Be(w.CompanyA1);
        cfo.Incumbent!.EmployeeId.Should().Be(w.Incumbent);
        cfo.Vacant.Should().BeFalse();
        cfo.Gaps.Should().Contain([SuccessionGaps.NoApprovedSlate, SuccessionGaps.NoReadyNow, SuccessionGaps.HighRisk]);

        var noCompany = () => w.Run<SuccessionPositionService, CriticalPositionSummary>(w.HrManagerP(), (s, a) => s.DesignateAsync(a,
            new DesignateInput(w.NoCompanyPosition, "Low", "Low", "No company, so it cannot be critical.", null, null), default));
        (await noCompany.Should().ThrowAsync<SuccessionException>()).Which.Code.Should().Be("position_without_company");

        var twice = () => w.DesignateCfoAsync();
        (await twice.Should().ThrowAsync<SuccessionException>()).Which.Code.Should().Be("already_critical");
        (await fx.GetIndexDefinitionAsync("ux_succession_critical_positions_one_active")).Should().Contain("UNIQUE").And.Contain("Active");

        // Retired, then designated again: history kept, a second row.
        await w.Run<SuccessionPositionService, CriticalPositionSummary>(w.HrManagerP(), (s, a) => s.RetireAsync(a, cfo.Id, "Reorganised", default));
        var again = await w.DesignateCfoAsync();
        again.Id.Should().NotBe(cfo.Id);
    }

    [Fact]
    public async Task SUC02_CompetingCandidates_AreSeparateRows_AndADuplicateLiveNominationIs409()
    {
        await using var w = await CreateAsync(fx);
        var cfo = await w.DesignateCfoAsync();
        var cara = await w.NominateAsync(cfo.Id, w.Cara, ReadinessLevels.ReadyNow);
        var cody = await w.NominateAsync(cfo.Id, w.Cody);
        cara.Status.Should().Be(NominationStatuses.Proposed);
        cody.Target.Title.Should().Be("Chief Financial Officer");

        var list = await w.Run<SuccessionNominationService, IReadOnlyList<NominationDto>>(w.HrManagerP(), (s, a) => s.ListAsync(a, cfo.Id, null, null, null, default));
        list.Select(n => n.Candidate.EmployeeId).Should().BeEquivalentTo([w.Cara, w.Cody]);
        list.Single(n => n.Candidate.EmployeeId == w.Cara).CurrentAssessment.Should().BeNull();

        async Task<object> Once()
        {
            try { return await w.NominateAsync(cfo.Id, w.Pete); }
            catch (SuccessionException ex) { return ex; }
        }
        var raced = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Once()));
        raced.OfType<NominationDto>().Should().ContainSingle();
        raced.OfType<SuccessionException>().Should().HaveCount(3).And.OnlyContain(e => e.Code == "duplicate_nomination");
        (await fx.GetIndexDefinitionAsync("ux_succession_nominations_one_live_per_position")).Should().Contain("UNIQUE").And.Contain("Declined");

        // The incumbent and the accountable executive cannot be candidates for their own position.
        var incumbent = () => w.NominateAsync(cfo.Id, w.Incumbent);
        (await incumbent.Should().ThrowAsync<SuccessionException>()).Which.Code.Should().Be("reviewer_conflict");
        var exec = () => w.NominateAsync(cfo.Id, w.Exec);
        (await exec.Should().ThrowAsync<SuccessionException>()).Which.Code.Should().Be("reviewer_conflict");

        // Cited competency ratings must belong to the candidate's own appraisals.
        var foreign = () => w.Run<SuccessionNominationService, object>(w.HrManagerP(), (s, a) => s.NominateAsync(a,
            new NominateInput(w.Uma, cfo.Id, null, null, "Strong analytical evidence from projects.", [Guid.NewGuid()]), default));
        (await foreign.Should().ThrowAsync<SuccessionException>()).Which.Status.Should().Be(400);
    }

    [Fact]
    public async Task SUC03_Calibration_KeepingThePotentialNeedsNoReason_MovingItNeeds20Characters_AndTheRowFreezes()
    {
        await using var w = await CreateAsync(fx);
        var cara = await w.AssessAsync(w.Cara, 2);
        cara.PerformanceBand.Should().Be(PerformanceBandValues.High);
        cara.PerformanceScore.Should().Be(82m);
        cara.AppraisalReviewId.Should().Be(w.CaraReview);
        cara.BandRuleVersion.Should().Be(PerformanceBands.DefaultVersion);
        cara.NineBoxCell.Should().Be("P2-High");

        var cody = await w.AssessAsync(w.Cody, 2);
        var kept = await w.CalibrateAssessmentAsync(cody.Id, 2, null, cody.Version);
        kept.Status.Should().Be(TalentAssessmentStatuses.Calibrated);
        kept.CalibrationReason.Should().BeNull();
        kept.NineBoxCell.Should().Be("P2-Moderate");

        var tooShort = () => w.CalibrateAssessmentAsync(cara.Id, 3, "Strong year.", cara.Version);
        (await tooShort.Should().ThrowAsync<SuccessionException>()).Which.Status.Should().Be(400);
        var moved = await w.CalibrateAssessmentAsync(cara.Id, 3, "Panel evidence: led two cross-company programmes.", cara.Version);
        moved.CalibratedPotential.Should().Be(3);
        moved.NineBoxCell.Should().Be("P3-High");
        moved.CellLabelEn.Should().Be("Future leader");
        moved.CellLabelAr.Should().Be("قائد المستقبل");

        // The database enforces the 20-character rule too, and freezes a calibrated row.
        await using (var db = w.Db())
        {
            var bypass = async () => await db.Database.ExecuteSqlRawAsync(
                "UPDATE talent_assessments SET calibrated_potential = 1, calibration_reason = 'short' WHERE id = {0}", cody.Id);
            (await bypass.Should().ThrowAsync<Npgsql.PostgresException>()).Which.MessageText.Should().Contain("immutable");
            var delete = async () => await db.Database.ExecuteSqlRawAsync("DELETE FROM talent_assessments WHERE id = {0}", cara.Id);
            (await delete.Should().ThrowAsync<Npgsql.PostgresException>()).Which.MessageText.Should().Contain("cannot be deleted");
            var check = async () => await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO talent_assessments (id, tenant_id, company_id, employee_id, review_period, proposed_potential, potential_evidence, performance_band, band_rule_version, calibrated_potential, calibration_reason, status, created_at_utc, updated_at_utc, version) " +
                "VALUES (gen_random_uuid(), {0}, {1}, {2}, '2025-H1', 1, 'evidence', 'Unrated', 'v', 3, 'too short', 'Submitted', now(), now(), 0)",
                w.TenantA, w.CompanyA1, w.Pete);
            (await check.Should().ThrowAsync<Npgsql.PostgresException>()).Which.ConstraintName.Should().Be("ck_talent_assessments__calibration_reason");
        }

        // Re-calibrating the same row is refused; a re-assessment supersedes it and keeps it as history.
        var again = () => w.CalibrateAssessmentAsync(cara.Id, 2, "Changing my mind after the panel closed.", moved.Version);
        (await again.Should().ThrowAsync<SuccessionException>()).Which.Code.Should().Be("invalid_transition");
        var reassessed = await w.Run<TalentAssessmentService, TalentAssessmentDto>(w.HrManagerP(), (s, a) => s.CreateAsync(a,
            new AssessInput(w.Cara, "2026-H2", 3, "Reassessed after the board reshuffle and new remit.", null, Supersede: true), default));
        reassessed.SupersedesId.Should().Be(cara.Id);
        await using (var db = w.Db())
        {
            var old = await db.TalentAssessments.IgnoreQueryFilters().SingleAsync(a => a.Id == cara.Id);
            old.Status.Should().Be(TalentAssessmentStatuses.Superseded);
            old.CalibratedPotential.Should().Be(3);
        }
    }

    [Fact]
    public async Task SUC03_Unrated_GetsNoBox_AndTheNineBoxShowsEveryPlacementsInputs()
    {
        await using var w = await CreateAsync(fx);
        var pete = await w.AssessAsync(w.Pete, 3);
        pete.PerformanceBand.Should().Be(PerformanceBandValues.Unrated);
        pete.PerformanceScore.Should().BeNull();
        pete.NineBoxCell.Should().BeNull("no eligible appraisal: no placement is guessed");

        // An appraisal under appeal is not eligible either.
        var rana = await w.AssessAsync(w.Rana, 2);
        rana.PerformanceBand.Should().Be(PerformanceBandValues.Unrated);

        var ulf = await w.AssessAsync(w.Ulf, 1);
        ulf.NineBoxCell.Should().Be("P1-Low");
        var cara = await w.AssessAsync(w.Cara, 3);
        await w.CalibrateAssessmentAsync(cara.Id, 3, null, cara.Version);

        var box = await w.Run<TalentAssessmentService, NineBoxDto>(w.HrManagerP(), (s, a) => s.NineBoxAsync(a, "2026-H2", null, null, default));
        box.Cells.Should().HaveCount(9);
        box.Unrated.Select(u => u.Employee.EmployeeId).Should().BeEquivalentTo([w.Pete, w.Rana]);
        var top = box.Cells.Single(c => c.Code == "P3-High");
        top.LabelEn.Should().Be("Future leader");
        var placed = top.People.Single();
        placed.Employee.EmployeeId.Should().Be(w.Cara);
        placed.Provisional.Should().BeFalse();
        placed.AppraisalReviewId.Should().Be(w.CaraReview);
        placed.PerformanceScore.Should().Be(82m);
        placed.BandRuleVersion.Should().Be(PerformanceBands.DefaultVersion);
        placed.Assessor!.EmployeeId.Should().Be(w.HrManager);
        box.Cells.Single(c => c.Code == "P1-Low").People.Single().Provisional.Should().BeTrue("Ulf's assessment is not calibrated yet");
    }

    [Theory]
    [InlineData(3, "High", "P3-High", "Future leader")]
    [InlineData(3, "Moderate", "P3-Moderate", "Emerging leader")]
    [InlineData(3, "Low", "P3-Low", "Untapped potential")]
    [InlineData(2, "High", "P2-High", "High performer")]
    [InlineData(2, "Moderate", "P2-Moderate", "Core contributor")]
    [InlineData(2, "Low", "P2-Low", "Inconsistent performer")]
    [InlineData(1, "High", "P1-High", "Trusted professional")]
    [InlineData(1, "Moderate", "P1-Moderate", "Effective in role")]
    [InlineData(1, "Low", "P1-Low", "Below expectations")]
    public void SUC03_TheNineBoxMappingTable(int potential, string band, string code, string label)
    {
        NineBox.CellFor(potential, band).Should().Be(code);
        NineBox.Cells.Single(c => c.Code == code).LabelEn.Should().Be(label);
        NineBox.CellFor(potential, PerformanceBandValues.Unrated).Should().BeNull();
        PerformanceBands.Band(74.99m, PerformanceBands.Default).Should().Be(PerformanceBandValues.Moderate);
        PerformanceBands.Band(75m, PerformanceBands.Default).Should().Be(PerformanceBandValues.High);
        PerformanceBands.Band(59.99m, PerformanceBands.Default).Should().Be(PerformanceBandValues.Low);
        PerformanceBands.Band(null, PerformanceBands.Default).Should().Be(PerformanceBandValues.Unrated);
    }

    [Fact]
    public void SUC03_AConfiguredScaleIsTheBandAuthority()
    {
        var scale = Guid.NewGuid();
        PerformanceRatingOption O(string label, decimal min, int order) => new() { ScaleId = scale, Label = label, MinScore = min, MaxScore = min + 9.99m, SortOrder = order };
        var rule = PerformanceBands.FromOptions(scale, [O("Outstanding", 90, 1), O("Exceeds", 80, 2), O("Meets", 65, 3), O("Developing", 50, 4), O("Unsatisfactory", 0, 5)])!;
        rule.HighFrom.Should().Be(80m);
        rule.ModerateFrom.Should().Be(65m);
        rule.Version.Should().StartWith("scale-");
        PerformanceBands.Band(79m, rule).Should().Be(PerformanceBandValues.Moderate);
        PerformanceBands.FromOptions(scale, [O("A", 50, 1), O("B", 0, 2)]).Should().BeNull("two options cannot form three bands");
    }

    [Fact]
    public async Task SUC06_SubmitReturnReviseApproveSupersede_AndReadinessChangeKeepsTheApprovedSnapshot()
    {
        await using var w = await CreateAsync(fx);
        var cfo = await w.DesignateCfoAsync();
        var cara = await w.CalibratedCandidateAsync(cfo.Id, w.Cara, 3, ReadinessLevels.ReadyNow);
        var cody = await w.CalibratedCandidateAsync(cfo.Id, w.Cody, 2, ReadinessLevels.OneToTwoYears);

        // v1: submitted with ids and a hash only; no subject on the shared request.
        var v1 = await w.DraftSlateAsync(cfo.Id, (cara.Id, 1), (cody.Id, 2));
        v1.Snapshot!.Candidates.Select(c => c.EmployeeId).Should().Equal(w.Cara, w.Cody);
        v1 = await w.SubmitSlateAsync(v1.Id);
        v1.Status.Should().Be(SlateStatuses.Submitted);
        v1.HashVerified.Should().BeTrue();
        await using (var db = w.Db())
        {
            var approval = await db.ApprovalRequests.IgnoreQueryFilters().SingleAsync(a => a.Id == v1.ApprovalRequestId);
            approval.RequestedForEmployeeId.Should().BeNull();
            approval.EntityName.Should().Be(SuccessionSlateApprovalSync.EntityName);
            approval.Title.Should().Be($"Succession slate v1 · {v1.SnapshotSha256[..12]}");
            approval.Payload.Should().Contain(v1.SnapshotSha256).And.NotContain("Cara").And.NotContain("كارا");
            approval.CurrentApproverRole.Should().Be("HR Director");
        }

        // Frozen from submission: an edit is refused by the service and by the database.
        var edit = () => w.Run<SuccessionSlateService, SlateVersionDto>(w.HrManagerP(), (s, a) => s.UpdateDraftAsync(a, v1.Id, new SlateDraftInput([], null), default));
        (await edit.Should().ThrowAsync<SuccessionException>()).Which.Code.Should().Be("slate_not_draft");
        await using (var db = w.Db())
        {
            var tamper = async () => await db.Database.ExecuteSqlRawAsync("UPDATE succession_slate_versions SET snapshot_json = '{{}}'::jsonb WHERE id = {0}", v1.Id);
            (await tamper.Should().ThrowAsync<Npgsql.PostgresException>()).Which.MessageText.Should().Contain("immutable");
        }

        // The approver returns it; a return needs a reason.
        var noReason = () => w.DecideAsync(w.HrDirectorP(), v1.ApprovalRequestId!.Value, "Reject", null);
        await noReason.Should().ThrowAsync<InvalidOperationException>();
        await w.DecideAsync(w.HrDirectorP(), v1.ApprovalRequestId!.Value, "Reject", "Cody's readiness needs evidence from the audit cycle.");
        var returned = await w.SlateRowAsync(v1.Id);
        returned.Status.Should().Be(SlateStatuses.ReturnedForRevision);
        returned.ReturnReason.Should().Contain("audit cycle");

        // Revise: a new Draft version cloned from the returned one.
        var v2 = await w.Run<SuccessionSlateService, SlateVersionDto>(w.HrManagerP(), (s, a) => s.ReviseAsync(a, v1.Id, default));
        v2.VersionNo.Should().Be(2);
        v2.RevisedFromId.Should().Be(v1.Id);
        v2.Snapshot!.Candidates.Select(c => c.NominationId).Should().Equal(cara.Id, cody.Id);
        v2 = await w.SubmitSlateAsync(v2.Id);
        await w.DecideAsync(w.HrDirectorP(), v2.ApprovalRequestId!.Value, "Approve", "Agreed.");
        var approved2 = await w.SlateRowAsync(v2.Id);
        approved2.Status.Should().Be(SlateStatuses.Approved);
        approved2.EffectiveFrom.Should().NotBeNull();
        (await w.NominationRowAsync(cara.Id)).Status.Should().Be(NominationStatuses.OnSlate);
        (await w.NominationRowAsync(cody.Id)).Status.Should().Be(NominationStatuses.OnSlate);

        // Readiness change after approval: the nomination changes, the approved snapshot keeps what was approved.
        var current = await w.Run<SuccessionNominationService, NominationDto>(w.HrManagerP(), (s, a) => s.GetAsync(a, cody.Id, default));
        var noWhy = () => w.Run<SuccessionNominationService, NominationDto>(w.HrManagerP(), (s, a) => s.ChangeReadinessAsync(a, cody.Id,
            new ReadinessInput(ReadinessLevels.ReadyNow, null, current.Version), default));
        await noWhy.Should().ThrowAsync<SuccessionException>();
        var changed = await w.Run<SuccessionNominationService, NominationDto>(w.HrManagerP(), (s, a) => s.ChangeReadinessAsync(a, cody.Id,
            new ReadinessInput(ReadinessLevels.ReadyNow, "Closed the FY2026 audit with no findings.", current.Version), default));
        changed.Readiness.Should().Be(ReadinessLevels.ReadyNow);
        changed.ReadinessPrevious.Should().Be(ReadinessLevels.OneToTwoYears);
        SlateSnapshot.Parse((await w.SlateRowAsync(v2.Id)).SnapshotJson)!.Candidates.Single(c => c.NominationId == cody.Id).Readiness
            .Should().Be(ReadinessLevels.OneToTwoYears, "the approved v2 snapshot keeps the readiness it was approved with");
        var history = await w.Run<SuccessionNominationService, IReadOnlyList<HistoryEntry>>(w.HrManagerP(), (s, a) => s.HistoryAsync(a, cody.Id, default));
        history.Select(h => h.Action).Should().Contain("succession.nomination.readiness_changed");

        // v3 drops Cody: approving it supersedes v2 in the same transaction and puts Cody back to Calibrated.
        var v3 = await w.Run<SuccessionSlateService, SlateVersionDto>(w.HrManagerP(), (s, a) => s.ReviseAsync(a, v2.Id, default));
        v3 = await w.Run<SuccessionSlateService, SlateVersionDto>(w.HrManagerP(), (s, a) => s.UpdateDraftAsync(a, v3.Id,
            new SlateDraftInput([new SlateCandidateInput(cara.Id, 1, "Sole successor after the restructure.")], v3.Version), default));
        v3 = await w.SubmitSlateAsync(v3.Id);
        await w.DecideAsync(w.HrDirectorP(), v3.ApprovalRequestId!.Value, "Approve", null);
        (await w.SlateRowAsync(v3.Id)).Status.Should().Be(SlateStatuses.Approved);
        var superseded = await w.SlateRowAsync(v2.Id);
        superseded.Status.Should().Be(SlateStatuses.Superseded);
        superseded.SupersededAtUtc.Should().NotBeNull();
        (await w.NominationRowAsync(cody.Id)).Status.Should().Be(NominationStatuses.Calibrated);
        var versions = await w.Run<SuccessionSlateService, IReadOnlyList<SlateVersionDto>>(w.HrManagerP(), (s, a) => s.ListAsync(a, cfo.Id, default));
        versions.Select(v => (v.VersionNo, v.Status)).Should().Equal((3, "Approved"), (2, "Superseded"), (1, "ReturnedForRevision"));
        versions.Where(v => v.Status != SlateStatuses.Draft).Should().OnlyContain(v => v.HashVerified);

        // The one-approved rule is a deferred exclusion constraint, checked at commit.
        await using (var db = w.Db())
        {
            var second = async () => await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO succession_slate_versions (id, tenant_id, company_id, critical_position_id, version_no, status, snapshot_json, snapshot_sha256, " +
                "approval_request_id, submitted_at_utc, approved_at_utc, effective_from, created_at_utc, updated_at_utc, version) " +
                "VALUES (gen_random_uuid(), {0}, {1}, {2}, 99, 'Approved', '{{}}'::jsonb, 'x', {3}, now(), now(), current_date, now(), now(), 0)",
                w.TenantA, w.CompanyA1, cfo.Id, v3.ApprovalRequestId!.Value);
            (await second.Should().ThrowAsync<Npgsql.PostgresException>()).Which.ConstraintName.Should().Be("ex_succession_slate_versions__one_approved");
        }
    }

    [Fact]
    public async Task SUC06_TwoApproversAtOnce_OneWins_AndTheSlateIsApprovedOnce()
    {
        await using var w = await CreateAsync(fx);
        var cfo = await w.DesignateCfoAsync();
        var cara = await w.CalibratedCandidateAsync(cfo.Id, w.Cara, 3, ReadinessLevels.ReadyNow);
        var v1 = await w.SubmitSlateAsync((await w.DraftSlateAsync(cfo.Id, (cara.Id, 1))).Id);
        var second = Principal(w.TenantA, Guid.NewGuid(), null, HrDirectorPermissions, ["HR Director"]);

        async Task<object?> Decide(System.Security.Claims.ClaimsPrincipal who)
        {
            try { return await w.DecideAsync(who, v1.ApprovalRequestId!.Value, "Approve", "ok"); }
            catch (InvalidOperationException ex) { return ex; }
        }
        var outcomes = await Task.WhenAll(Decide(w.HrDirectorP()), Decide(second), Decide(w.HrDirectorP()), Decide(second));
        outcomes.OfType<Zayra.Api.Application.Approvals.ApprovalRequestDto>().Should().ContainSingle();
        outcomes.OfType<InvalidOperationException>().Should().HaveCount(3);
        (await w.SlateRowAsync(v1.Id)).Status.Should().Be(SlateStatuses.Approved);
        await using var db = w.Db();
        (await db.ApprovalDecisions.IgnoreQueryFilters().CountAsync(d => d.ApprovalRequestId == v1.ApprovalRequestId)).Should().Be(1);
        (await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.EntityId == v1.Id.ToString() && a.Action == "succession.slate.approved")).Should().Be(1);
    }

    [Fact]
    public async Task SUC06_OnlyCalibratedCandidatesGoOnASlate_AndOneOpenVersionPerPosition()
    {
        await using var w = await CreateAsync(fx);
        var cfo = await w.DesignateCfoAsync();
        var proposed = await w.NominateAsync(cfo.Id, w.Pete);
        var draft = () => w.DraftSlateAsync(cfo.Id, (proposed.Id, 1));
        (await draft.Should().ThrowAsync<SuccessionException>()).Which.Code.Should().Be("candidate_not_calibrated");

        var cara = await w.CalibratedCandidateAsync(cfo.Id, w.Cara, 3, ReadinessLevels.ReadyNow);
        await w.DraftSlateAsync(cfo.Id, (cara.Id, 1));
        var secondOpen = () => w.DraftSlateAsync(cfo.Id, (cara.Id, 1));
        (await secondOpen.Should().ThrowAsync<SuccessionException>()).Which.Code.Should().Be("slate_open");

        var gap = () => w.Run<SuccessionSlateService, SlateVersionDto>(w.HrManagerP(), (s, a) => s.CreateDraftAsync(a, cfo.Id,
            new SlateDraftInput([new SlateCandidateInput(cara.Id, 2, "Ordered with a gap in the numbering.")], null), default));
        (await gap.Should().ThrowAsync<SuccessionException>()).Which.Status.Should().Be(400);
    }
}
