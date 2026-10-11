using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Learning;
using Zayra.Api.Infrastructure.Succession;
using Zayra.Api.Models;
using Zayra.Api.Tests.Learning;
using static Zayra.Api.Tests.Succession.SuccessionTestWorld;

namespace Zayra.Api.Tests.Succession;

/// <summary>
/// The independent review's probes, kept as regressions that now expect refusal or masking (Integration Owner rulings
/// F1–F8): a manager's nomination response is masked; Admin and any role without slate access cannot act on candidates;
/// a candidate — or anyone reporting to one — never sees or acts on their own position's slates, nominations or
/// assessments; employees only start, note or propose; leavers never count; anonymisation cannot reorder a slate;
/// linked learning re-opens with a revoked certificate; audit rows carry ids only.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class SuccessionReviewRegressionTests(PostgresFixture fx)
{
    private static async Task<(SuccessionTestWorld W, CriticalPositionSummary Cfo, NominationDto Cara, NominationDto Cody, SlateVersionDto V1)> Arrange(PostgresFixture fx)
    {
        var w = await CreateAsync(fx);
        var cfo = await w.DesignateCfoAsync();
        var cara = await w.CalibratedCandidateAsync(cfo.Id, w.Cara, 3, ReadinessLevels.ReadyNow);
        var cody = await w.CalibratedCandidateAsync(cfo.Id, w.Cody, 2, ReadinessLevels.OneToTwoYears);
        var v1 = await w.SubmitSlateAsync((await w.DraftSlateAsync(cfo.Id, (cara.Id, 1), (cody.Id, 2))).Id);
        await w.DecideAsync(w.HrDirectorP(), v1.ApprovalRequestId!.Value, "Approve", "Agreed.");
        return (w, cfo, cara, cody, v1);
    }

    private static async Task<int> StatusOf(Func<Task> call)
    {
        try { await call(); return 200; }
        catch (SuccessionException ex) { return ex.Status; }
    }

    [Fact]
    public async Task F1_AManagerNominatingAReport_GetsTheMaskedView_NoNineBoxNoSlateCount()
    {
        var (w, _, _, _, _) = await Arrange(fx);
        await using var _w = w;
        var pool = await w.Run<SuccessionPositionService, TalentPoolDto>(w.HrManagerP(), (s, a) => s.CreatePoolAsync(a,
            new PoolInput(w.CompanyA1, "FIN-LEAD", "Finance leaders", null, "Finance leadership bench for the region.", null), default));
        var result = await w.Run<SuccessionNominationService, object>(w.ManagerP(w.Manager), (s, a) => s.NominateAsync(a,
            new NominateInput(w.Cara, null, pool.Id, null, "Ran the close end to end this year.", null), default));
        result.Should().BeOfType<MyNominationDto>("a caller without succession.slates.read gets the masked view");
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var field in new[] { "currentAssessment", "nineBoxCell", "readyNowOnApprovedSlates", "overCommitted", "readiness", "performanceScore", "calibratedPotential" })
            json.Should().NotContain(field);
        ((MyNominationDto)result).Stage.Should().Be("Proposed");
    }

    [Fact]
    public async Task F2_AdminAndSlatesManageWithoutRead_CannotReachACandidate_ThroughAnyWriteEndpoint()
    {
        var (w, cfo, cara, _, v1) = await Arrange(fx);
        await using var _w = w;
        var version = (await w.NominationRowAsync(cara.Id)).Version;
        foreach (var who in new[] { w.AdminP(), Principal(w.TenantA, Guid.NewGuid(), null, SlatesManageWithoutReadPermissions, ["Custom"]) })
        {
            (await StatusOf(() => w.Run<SuccessionNominationService, NominationDto>(who, (s, a) => s.ChangeReadinessAsync(a, cara.Id,
                new ReadinessInput(ReadinessLevels.OneToTwoYears, "Probing the candidate record from a config role", version), default)))).Should().Be(403);
            (await StatusOf(() => w.Run<SuccessionNominationService, NominationDto>(who, (s, a) => s.DeclineAsync(a, cara.Id, new ReasonInput("probe reason"), default)))).Should().Be(403);
            (await StatusOf(() => w.Run<SuccessionSlateService, SlateVersionDto>(who, (s, a) => s.ReviseAsync(a, v1.Id, default)))).Should().Be(403);
            (await StatusOf(() => w.Run<SuccessionSlateService, SlateVersionDto>(who, (s, a) => s.CreateDraftAsync(a, cfo.Id,
                new SlateDraftInput([new SlateCandidateInput(cara.Id, 1, "Probe rationale text")], null), default)))).Should().Be(403);
            (await StatusOf(() => w.Run<SuccessionNominationService, IReadOnlyList<NominationDto>>(who, (s, a) => s.ListAsync(a, cfo.Id, null, null, "all", default)))).Should().Be(403);
        }
        // Configuration responses carry no candidate data.
        var positions = await w.Run<SuccessionPositionService, IReadOnlyList<CriticalPositionSummary>>(w.AdminP(), (s, a) => s.ListAsync(a, null, default));
        var text = JsonSerializer.Serialize(positions);
        text.Should().NotContain("Cara").And.NotContain("Cody").And.NotContain(cara.Id.ToString());
        (await w.NominationRowAsync(cara.Id)).Readiness.Should().Be(ReadinessLevels.ReadyNow);
    }

    [Fact]
    public async Task F4_ACandidateWithHrKeys_NeverSeesOrActsOnTheirOwnPositions_Slates_Nominations_Or_Assessments()
    {
        var (w, cfo, cara, cody, v1) = await Arrange(fx);
        await using var _w = w;
        var caraHr = Principal(w.TenantA, w.UserOf[w.Cara], w.Cara, HrManagerPermissions, ["HR Manager"]);
        var ranaHr = Principal(w.TenantA, w.UserOf[w.Rana], w.Rana, HrManagerPermissions, ["HR Manager"]);
        foreach (var who in new[] { caraHr, ranaHr })
        {
            (await StatusOf(() => w.Run<SuccessionSlateService, IReadOnlyList<SlateVersionDto>>(who, (s, a) => s.ListAsync(a, cfo.Id, default)))).Should().Be(404);
            (await StatusOf(() => w.Run<SuccessionSlateService, SlateVersionDto>(who, (s, a) => s.GetAsync(a, v1.Id, default)))).Should().Be(404);
            (await StatusOf(() => w.Run<SuccessionNominationService, IReadOnlyList<NominationDto>>(who, (s, a) => s.ListAsync(a, cfo.Id, null, null, "all", default)))).Should().Be(404);
            (await StatusOf(() => w.Run<SuccessionNominationService, NominationDto>(who, (s, a) => s.GetAsync(a, cody.Id, default)))).Should().Be(404);
            (await StatusOf(() => w.Run<TalentAssessmentService, NineBoxDto>(who, (s, a) => s.NineBoxAsync(a, "2026-H2", cfo.Id, null, default)))).Should().Be(404);
            var assessments = await w.Run<TalentAssessmentService, IReadOnlyList<TalentAssessmentDto>>(who, (s, a) => s.ListAsync(a, "2026-H2", null, null, default));
            assessments.Select(x => x.Employee.EmployeeId).Should().NotContain([w.Cara, w.Cody]);
            var box = await w.Run<TalentAssessmentService, NineBoxDto>(who, (s, a) => s.NineBoxAsync(a, "2026-H2", null, null, default));
            box.Cells.SelectMany(c => c.People).Select(p => p.Employee.EmployeeId).Should().NotContain([w.Cara, w.Cody]);
            var all = await w.Run<SuccessionNominationService, IReadOnlyList<NominationDto>>(who, (s, a) => s.ListAsync(a, null, null, null, "all", default));
            all.Should().NotContain(n => n.Target.Id == cfo.Id);

            var version = (await w.NominationRowAsync(cody.Id)).Version;
            foreach (var write in new Func<Task>[]
            {
                () => w.Run<SuccessionNominationService, NominationDto>(who, (s, a) => s.ChangeReadinessAsync(a, cody.Id,
                    new ReadinessInput(ReadinessLevels.ThreePlusYears, "Competitor downgrading a rival", version), default)),
                () => w.Run<SuccessionNominationService, NominationDto>(who, (s, a) => s.DeclineAsync(a, cody.Id, new ReasonInput("Competitor declines rival"), default)),
                () => w.Run<SuccessionSlateService, SlateVersionDto>(who, (s, a) => s.ReviseAsync(a, v1.Id, default)),
                () => w.Run<SuccessionSlateService, SlateVersionDto>(who, (s, a) => s.CreateDraftAsync(a, cfo.Id,
                    new SlateDraftInput([new SlateCandidateInput(cara.Id, 1, "Self-serving rationale text")], null), default)),
            })
                (await StatusOf(write)).Should().Be(409);
        }
        (await w.NominationRowAsync(cody.Id)).Readiness.Should().Be(ReadinessLevels.OneToTwoYears);

        // The reviewer's probe 3: an HR Manager who is a successor for another critical role (here: the HR Director's
        // controller role) is refused on that role, while still working normally elsewhere.
        var controller = await w.Run<SuccessionPositionService, CriticalPositionSummary>(w.HrDirectorP(), (s, a) => s.DesignateAsync(a,
            new DesignateInput(w.ControllerPosition, "High", "High", "The HR Director's own deputy role.", null, null), default));
        await w.NominateAsync(controller.Id, w.HrManager, by: w.HrDirectorP());
        var rival = await w.NominateAsync(controller.Id, w.Pete, by: w.HrDirectorP());
        (await StatusOf(() => w.Run<SuccessionSlateService, IReadOnlyList<SlateVersionDto>>(w.HrManagerP(), (s, a) => s.ListAsync(a, controller.Id, default)))).Should().Be(404);
        (await StatusOf(() => w.Run<SuccessionNominationService, NominationDto>(w.HrManagerP(), (s, a) => s.StartReviewAsync(a, rival.Id, default)))).Should().Be(409);
        (await StatusOf(() => w.Run<SuccessionSlateService, IReadOnlyList<SlateVersionDto>>(w.HrManagerP(), (s, a) => s.ListAsync(a, cfo.Id, default)))).Should().Be(200);
    }

    [Fact]
    public async Task F5_TheEmployeeStartsNotesAndProposes_TheOwnerConfirms_CancelAndDueDatesAreRefused()
    {
        var (w, _, cara, _, _) = await Arrange(fx);
        await using var _w = w;
        var today = DateOnly.FromDateTime(w.Clock.GetUtcNow().UtcDateTime);
        var plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.CreateAsync(a,
            new PlanInput(w.Cara, "Prepare Cara for a finance leadership role.", w.Manager, today.AddMonths(3), cara.Id), default));
        plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.AddActionAsync(a, plan.Id,
            new ActionInput("Other", "Present the board pack", null, today.AddDays(5), null, null, null, null, null), default));
        plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.AddActionAsync(a, plan.Id,
            new ActionInput("Mentoring", "Mentoring with the CFO", null, today.AddDays(5), null, null, null, null, w.Exec), default));
        plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.ActivateAsync(a, plan.Id, default));
        var a0 = plan.Actions[0];
        var a1 = plan.Actions[1];
        await using (var scope = w.As(w.EmployeeP(w.Cara)))
        {
            var svc = Get<DevelopmentPlanService>(scope);
            var ctx = Context(w.EmployeeP(w.Cara));
            async Task<int> Try(Guid id, ActionUpdateInput input)
            {
                try { await svc.UpdateMyActionAsync(w.TenantA, w.Cara, ctx, id, input, default); return 200; }
                catch (SuccessionException ex) { return ex.Status; }
            }
            (await Try(a0.Id, new ActionUpdateInput(DevelopmentActionStatuses.Cancelled, null, null, a0.Version))).Should().Be(403);
            (await Try(a1.Id, new ActionUpdateInput(DevelopmentActionStatuses.Completed, null, null, a1.Version))).Should().Be(403);
            (await Try(a1.Id, new ActionUpdateInput(null, null, today.AddYears(2), a1.Version))).Should().Be(403);
            var mine = await svc.UpdateMyActionAsync(w.TenantA, w.Cara, ctx, a1.Id,
                new ActionUpdateInput(null, "Three sessions held so far.", null, a1.Version, ProposeCompletion: true), default);
            var proposed = mine.Current!.Actions.Single(x => x.Id == a1.Id);
            proposed.Status.Should().Be(DevelopmentActionStatuses.InProgress);
            proposed.CompletionProposedAtUtc.Should().NotBeNull();
            mine.Current.Progress.Completed.Should().Be(0, "a proposal is not a completion");
        }
        var plans = await w.Run<SuccessionReportService, SuccessionReportDto>(w.HrDirectorP(), (s, a) => s.SummaryAsync(a, default));
        plans.Metrics.Single(m => m.Key == "actionsCompletedRate").Numerator.Should().Be(0);

        var current = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.ManagerP(w.Manager), (s, a) => s.GetAsync(a, plan.Id, default));
        var confirmed = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.ManagerP(w.Manager), (s, a) => s.ConfirmCompletionAsync(a, a1.Id,
            current.Actions.Single(x => x.Id == a1.Id).Version, default));
        confirmed.Actions.Single(x => x.Id == a1.Id).Status.Should().Be(DevelopmentActionStatuses.Completed);
        confirmed.Progress.Completed.Should().Be(1);
        var noProposal = () => w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.ManagerP(w.Manager), (s, a) => s.ConfirmCompletionAsync(a, a0.Id, a0.Version, default));
        (await noProposal.Should().ThrowAsync<SuccessionException>()).Which.Code.Should().Be("no_proposal");
    }

    [Fact]
    public async Task F6_ALeaver_IsRefusedOnASlate_AndStopsCountingAsCoverage_BeforeTheSweep()
    {
        await using var w = await CreateAsync(fx);
        var cfo = await w.DesignateCfoAsync();
        var cara = await w.CalibratedCandidateAsync(cfo.Id, w.Cara, 3, ReadinessLevels.ReadyNow);
        var cody = await w.CalibratedCandidateAsync(cfo.Id, w.Cody, 2, ReadinessLevels.OneToTwoYears);
        var v1 = await w.SubmitSlateAsync((await w.DraftSlateAsync(cfo.Id, (cara.Id, 1), (cody.Id, 2))).Id);
        await w.DecideAsync(w.HrDirectorP(), v1.ApprovalRequestId!.Value, "Approve", "Agreed.");
        await using (var db = w.Db())
        {
            (await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == w.Cara)).Status = EmployeeStatuses.Terminated;
            await db.SaveChangesAsync();
        }
        // No sweep has run: the nomination is still OnSlate.
        (await w.NominationRowAsync(cara.Id)).Status.Should().Be(NominationStatuses.OnSlate);
        var summary = await w.Run<SuccessionReportService, SuccessionReportDto>(w.HrDirectorP(), (s, a) => s.SummaryAsync(a, default));
        summary.Metrics.Single(m => m.Key == "coverage").Numerator.Should().Be(0);
        summary.Metrics.Single(m => m.Key == "benchReadyNow").Value.Should().Be(0);
        summary.Metrics.Single(m => m.Key == "gapStaleSlate").Value.Should().Be(1);
        var bench = await w.Run<SuccessionReportService, DrillResult>(w.HrDirectorP(), (s, a) => s.DrillAsync(a, "bench:ReadyNow", default));
        bench.Candidates.Should().BeEmpty();
        var revise = () => w.Run<SuccessionSlateService, SlateVersionDto>(w.HrManagerP(), (s, a) => s.CreateDraftAsync(a, cfo.Id,
            new SlateDraftInput([new SlateCandidateInput(cara.Id, 1, "Keeps the leaver on the slate.")], null), default));
        (await revise.Should().ThrowAsync<SuccessionException>()).Which.Code.Should().BeOneOf("candidate_ineligible", "slate_open");
        var v2 = await w.Run<SuccessionSlateService, SlateVersionDto>(w.HrManagerP(), (s, a) => s.ReviseAsync(a, v1.Id, default));
        var withLeaver = () => w.Run<SuccessionSlateService, SlateVersionDto>(w.HrManagerP(), (s, a) => s.UpdateDraftAsync(a, v2.Id,
            new SlateDraftInput([new SlateCandidateInput(cara.Id, 1, "Keeps the leaver on the slate.")], v2.Version), default));
        (await withLeaver.Should().ThrowAsync<SuccessionException>()).Which.Code.Should().Be("candidate_ineligible");
    }

    [Fact]
    public async Task F7_Anonymisation_CanRewriteNamesOnly_AndTheHashStatesTellAnonymisedFromTampered()
    {
        var (w, cfo, cara, _, v1) = await Arrange(fx);
        await using var _w = w;
        var now = DateTime.UtcNow;
        await using (var db = w.Db())
        {
            await new SuccessionRetentionJob().AnonymiseAsync(db, w.TenantA, w.Cara, now, default);
            await db.SaveChangesAsync();
        }
        var slate = await w.Run<SuccessionSlateService, SlateVersionDto>(w.HrManagerP(), (s, a) => s.GetAsync(a, v1.Id, default));
        slate.Integrity.Should().Be(SlateIntegrity.Anonymised);
        slate.HashVerified.Should().BeTrue();

        // A "re-anonymisation" that also moves a candidate's readiness or order is refused by the trigger.
        await using (var db = w.Db())
        {
            var reorder = async () => await db.Database.ExecuteSqlRawAsync(
                "UPDATE succession_slate_versions SET snapshot_json = jsonb_set(snapshot_json, '{{candidates,0,readiness}}', '\"ThreePlusYears\"'), " +
                "anonymised_at_utc = anonymised_at_utc + interval '1 minute', anonymised_sha256 = repeat('a', 64) WHERE id = {0}", v1.Id);
            (await reorder.Should().ThrowAsync<Npgsql.PostgresException>()).Which.MessageText.Should().Contain("immutable");
        }
        // A tamper outside the trigger (the guard disabled by an operator) is reported as such.
        await using (var db = w.Db())
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE succession_slate_versions DISABLE TRIGGER trg_succession_slate_versions__guard");
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE succession_slate_versions SET snapshot_json = jsonb_set(snapshot_json, '{{candidates,0,order}}', '9') WHERE id = {0}", v1.Id);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE succession_slate_versions ENABLE TRIGGER trg_succession_slate_versions__guard");
            }
        }
        (await w.Run<SuccessionSlateService, SlateVersionDto>(w.HrManagerP(), (s, a) => s.GetAsync(a, v1.Id, default))).Integrity.Should().Be(SlateIntegrity.Tampered);
    }

    [Fact]
    public async Task F8_ACompletedAssignmentCannotBeLinked_AndARevokedCertificateReopensThePlanAction()
    {
        await using var w = await CreateAsync(fx);
        var today = DateOnly.FromDateTime(w.Clock.GetUtcNow().UtcDateTime);
        Guid pathId;
        LearningActor hrLearning;
        await using (var scope = w.As(w.HrDirectorP()))
        {
            hrLearning = new LearningActor(w.TenantA, w.UserOf[w.HrDirector], "127.0.0.1", "test",
                await Get<IDataScopeService>(scope).ResolveAsync(w.HrDirectorP(), w.TenantA, default));
            var catalog = Get<LearningCatalogService>(scope);
            var course = await catalog.CreateCourseAsync(hrLearning, LearningTestWorld.Lesson("REV", validityMonths: 12) with { IssuesCertificate = true }, default);
            var published = await catalog.PublishCourseAsync(hrLearning, course.Id, null, default);
            var path = await catalog.CreatePathAsync(hrLearning, new PathInput("REV", "Revocable path", null, null, null, null, null, 30,
                [new PathItemInput(published.Id, true, null)], null), default);
            await catalog.ActivatePathAsync(hrLearning, path.Id, default);
            pathId = path.Id;
        }
        var plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.CreateAsync(a,
            new PlanInput(w.Cody, "Certify Cody before the audit season.", null, today.AddMonths(3), null), default));
        plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.AddActionAsync(a, plan.Id,
            new ActionInput("Learning", "Complete the revocable path", null, today.AddDays(20), null, pathId, null, null, null), default));
        var action = plan.Actions.Single();

        // Cody completes it.
        await using (var db = w.Db())
        {
            var enrollment = await db.LearningEnrollments.IgnoreQueryFilters().SingleAsync(e => e.AssignmentId == action.LearningAssignmentId);
            await using var scope = w.As(w.EmployeeP(w.Cody));
            var me = new LearningActor(w.TenantA, w.UserOf[w.Cody], null, "test", new DataScope { Level = DataScopeLevel.Own, CallerEmployeeId = w.Cody, AllowedEmployeeIds = [w.Cody] });
            var progress = Get<LearningProgressService>(scope);
            await progress.LaunchAsync(me, w.Cody, enrollment.Id, "https://app.kynexone.test", null, default);
            await progress.AcknowledgeAsync(me, w.Cody, enrollment.Id, default);
        }
        (await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.GetAsync(a, plan.Id, default)))
            .Actions.Single().Status.Should().Be(DevelopmentActionStatuses.Completed);

        // Linking that completed assignment to a new action is refused.
        var relink = () => w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.AddActionAsync(a, plan.Id,
            new ActionInput("Learning", "Already done", null, null, null, null, action.LearningAssignmentId, null, null), default));
        (await relink.Should().ThrowAsync<SuccessionException>()).Which.Code.Should().Be("assignment_completed");

        // HR revokes the certificate (LRN reopens the learning); the plan action reopens on the next read.
        Guid credentialId;
        await using (var db = w.Db())
            credentialId = await db.LearningCredentials.IgnoreQueryFilters().Where(c => c.EmployeeId == w.Cody).Select(c => c.Id).SingleAsync();
        await using (var scope = w.As(w.HrDirectorP()))
            await Get<LearningProgressService>(scope).RevokeAsync(hrLearning, credentialId, new RevokeCredentialInput("Issued in error.", today.AddDays(10)), default);
        var reopened = (await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.GetAsync(a, plan.Id, default))).Actions.Single();
        reopened.Status.Should().Be(DevelopmentActionStatuses.InProgress);
        reopened.CompletionMethod.Should().BeNull();
    }

    [Fact]
    public async Task F3_SuccessionAuditRows_CarryIdsOnly()
    {
        var (w, cfo, cara, cody, _) = await Arrange(fx);
        await using var _w = w;
        var version = (await w.NominationRowAsync(cody.Id)).Version;
        await w.Run<SuccessionNominationService, NominationDto>(w.HrManagerP(), (s, a) => s.ChangeReadinessAsync(a, cody.Id,
            new ReadinessInput(ReadinessLevels.ReadyNow, "Cody Candidate closed the audit with no findings.", version), default));
        var today = DateOnly.FromDateTime(w.Clock.GetUtcNow().UtcDateTime);
        await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.CreateAsync(a,
            new PlanInput(w.Cara, "Prepare Cara for the CFO role.", null, today.AddMonths(3), cara.Id), default));
        await using var db = w.Db();
        var rows = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == w.TenantA && x.Action.StartsWith("succession.")).ToListAsync();
        rows.Should().HaveCountGreaterThan(10);
        var all = string.Join("\n", rows.Select(r => r.Metadata ?? string.Empty));
        foreach (var banned in new[] { "EmployeeId", "employeeId", "Readiness", "readiness", "ReadyNow", "OneToTwoYears", "Potential", "potential",
                     "PerformanceBand", "linkedToNomination", "Cara", "Cody", "audit with no findings", "\"from\"", "\"to\"" })
            all.Should().NotContain(banned, $"succession audit metadata carries ids only ({banned})");
    }
}
