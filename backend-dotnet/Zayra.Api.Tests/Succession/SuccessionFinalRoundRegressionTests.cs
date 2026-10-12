using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Infrastructure.Succession;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Succession.SuccessionTestWorld;

namespace Zayra.Api.Tests.Succession;

/// <summary>
/// Final-round rulings (N1–N6) kept as regressions: the conflict-of-interest chain covers every reporting line
/// (direct, dotted-line, stored skip-level); the caller's employee is resolved from the claim or the login link and a
/// slate reader with neither is refused; nobody manages their own development plan; succession audit metadata is
/// entity ids only and candidate counts are withheld without slate access; anonymisation writes only the
/// deterministic pseudonym.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class SuccessionFinalRoundRegressionTests(PostgresFixture fx)
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
        catch (InvalidOperationException) { return 400; }
    }

    private static async Task<string?> CodeOf(Func<Task> call)
    {
        try { await call(); return null; }
        catch (SuccessionException ex) { return ex.Code; }
    }

    private static async Task SetLines(SuccessionTestWorld w, int employee, int? manager, int? supervisor, int? secondLevel)
    {
        await using var db = w.Db();
        var e = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == employee);
        e.ManagerEmployeeId = manager; e.SupervisorEmployeeId = supervisor; e.SecondLevelManagerEmployeeId = secondLevel;
        await db.SaveChangesAsync();
    }

    private static Task<IReadOnlyList<SlateVersionDto>> ListSlates(SuccessionTestWorld w, System.Security.Claims.ClaimsPrincipal who, Guid position) =>
        w.Run<SuccessionSlateService, IReadOnlyList<SlateVersionDto>>(who, (s, a) => s.ListAsync(a, position, default));

    private static async Task<SlateVersionDto> PendingRevisionAsync(SuccessionTestWorld w, SlateVersionDto approved)
    {
        var v2 = await w.Run<SuccessionSlateService, SlateVersionDto>(w.HrManagerP(), (s, a) => s.ReviseAsync(a, approved.Id, default));
        return await w.SubmitSlateAsync(v2.Id);
    }

    // ── N1: every reporting line counts, for reading and for deciding ──

    [Fact]
    public async Task E1_DottedLineSupervisorOfACandidate_IsBarredFromReadingAndDeciding()
    {
        var (w, cfo, _, _, v1) = await Arrange(fx);
        await using var _w = w;
        await SetLines(w, w.Rana, w.Uma, w.Cara, null); // direct line elsewhere; dotted line to candidate Cara
        (await StatusOf(() => ListSlates(w, w.HrDirectorAs(w.Rana), cfo.Id))).Should().Be(404);
        var v2 = await PendingRevisionAsync(w, v1);
        (await StatusOf(() => w.DecideAsync(w.HrDirectorAs(w.Rana), v2.ApprovalRequestId!.Value, "Approve", "ok"))).Should().NotBe(200);
        (await w.SlateRowAsync(v2.Id)).Status.Should().Be(SlateStatuses.Submitted);
    }

    [Fact]
    public async Task E2_StoredSkipLevelManagerIsACandidate_IsBarredFromReadingAndDeciding()
    {
        var (w, cfo, _, _, v1) = await Arrange(fx);
        await using var _w = w;
        await SetLines(w, w.Rana, null, null, w.Cara); // only the stored skip-level column points at Cara
        (await StatusOf(() => ListSlates(w, w.HrDirectorAs(w.Rana), cfo.Id))).Should().Be(404);
        var v2 = await PendingRevisionAsync(w, v1);
        (await StatusOf(() => w.DecideAsync(w.HrDirectorAs(w.Rana), v2.ApprovalRequestId!.Value, "Approve", "ok"))).Should().NotBe(200);
        (await w.SlateRowAsync(v2.Id)).Status.Should().Be(SlateStatuses.Submitted);
    }

    // ── N2: identity from the claim OR the login link; never fail open ──

    [Fact]
    public async Task E3_CandidateLoginWithoutEmployeeClaim_IsResolvedThroughTheLoginLink_AndBarred()
    {
        var (w, cfo, _, cody, _) = await Arrange(fx);
        await using var _w = w;
        var caraNoClaim = Principal(w.TenantA, w.UserOf[w.Cara], null, HrManagerPermissions, ["HR Manager"]);
        (await StatusOf(() => ListSlates(w, caraNoClaim, cfo.Id))).Should().Be(404);
        (await StatusOf(() => w.Run<SuccessionNominationService, NominationDto>(caraNoClaim, (s, a) => s.GetAsync(a, cody.Id, default)))).Should().Be(404);
        var nine = await w.Run<TalentAssessmentService, NineBoxDto>(caraNoClaim, (s, a) => s.NineBoxAsync(a, null, null, null, default));
        JsonSerializer.Serialize(nine).Should().NotContain("Cody");
    }

    [Fact]
    public async Task N2_SlateReaderWithNoResolvableEmployee_IsRefusedCandidateReads_AndDecisions()
    {
        var (w, cfo, cara, _, v1) = await Arrange(fx);
        await using var _w = w;
        var orphanHr = Principal(w.TenantA, Guid.NewGuid(), null, HrManagerPermissions, ["HR Manager"]);
        (await CodeOf(() => ListSlates(w, orphanHr, cfo.Id))).Should().Be("no_employee_record");
        (await StatusOf(() => ListSlates(w, orphanHr, cfo.Id))).Should().Be(403);
        (await CodeOf(() => w.Run<SuccessionNominationService, NominationDto>(orphanHr, (s, a) => s.GetAsync(a, cara.Id, default)))).Should().Be("no_employee_record");
        (await CodeOf(() => w.Run<SuccessionNominationService, IReadOnlyList<NominationDto>>(orphanHr, (s, a) => s.ListAsync(a, cfo.Id, null, null, "all", default))))
            .Should().Be("no_employee_record");
        (await CodeOf(() => w.Run<SuccessionReportService, DrillResult>(orphanHr, (s, a) => s.DrillAsync(a, "bench:ReadyNow", default)))).Should().Be("no_employee_record");

        var orphanDirector = Principal(w.TenantA, Guid.NewGuid(), null, HrDirectorPermissions, ["HR Director"]);
        var v2 = await PendingRevisionAsync(w, v1);
        (await StatusOf(() => w.DecideAsync(orphanDirector, v2.ApprovalRequestId!.Value, "Approve", "ok"))).Should().NotBe(200);
        (await w.SlateRowAsync(v2.Id)).Status.Should().Be(SlateStatuses.Submitted);
    }

    // ── N3: nobody manages their own development plan ──

    [Fact]
    public async Task E5_ManagerAndHr_CannotCreateOrManageTheirOwnPlan()
    {
        var (w, _, _, _, _) = await Arrange(fx);
        await using var _w = w;
        var today = DateOnly.FromDateTime(w.Clock.GetUtcNow().UtcDateTime);
        (await CodeOf(() => w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.ManagerP(w.Manager), (s, a) => s.CreateAsync(a,
            new PlanInput(w.Manager, "My own leadership development plan.", w.Manager, today.AddMonths(3), null), default)))).Should().Be("self_decision_forbidden");
        (await CodeOf(() => w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.CreateAsync(a,
            new PlanInput(w.HrManager, "My own HR leadership development plan.", w.HrManager, today.AddMonths(3), null), default)))).Should().Be("self_decision_forbidden");

        // A plan HR Director wrote for the HR manager: the HR manager can neither change, activate nor complete it.
        var plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrDirectorP(), (s, a) => s.CreateAsync(a,
            new PlanInput(w.HrManager, "Prepare Hamid for an HR director role.", w.HrDirector, today.AddMonths(3), null), default));
        plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrDirectorP(), (s, a) => s.AddActionAsync(a, plan.Id,
            new ActionInput("Other", "Lead the policy review", null, today.AddDays(5), null, null, null, null, null), default));
        var act = plan.Actions[0];
        var status = await StatusOf(() => w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.AddActionAsync(a, plan.Id,
            new ActionInput("Other", "Self-graded stretch task", null, today.AddDays(5), null, null, null, null, null), default)));
        status.Should().Be(403);
        (await CodeOf(() => w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.ActivateAsync(a, plan.Id, default))))
            .Should().Be("self_decision_forbidden");
        (await CodeOf(() => w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.UpdateActionAsync(a, act.Id,
            new ActionUpdateInput(DevelopmentActionStatuses.Completed, null, null, act.Version), default)))).Should().Be("self_decision_forbidden");
        await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrDirectorP(), (s, a) => s.ActivateAsync(a, plan.Id, default));
    }

    // ── N4: audit metadata is entity ids only; candidate counts need slate access ──

    [Fact]
    public async Task N4_AuditMetadataHasNoTargetsOrReasons_AndCountsAreWithheldWithoutSlateAccess()
    {
        var (w, cfo, cara, _, _) = await Arrange(fx);
        await using var _w = w;
        var pool = await w.Run<SuccessionPositionService, TalentPoolDto>(w.HrManagerP(), (s, a) => s.CreatePoolAsync(a,
            new PoolInput(w.CompanyA1, "FIN-LEAD", "Finance leaders", null, "Finance leadership bench for the region.", null), default));
        await w.Run<SuccessionNominationService, object>(w.ManagerP(w.Manager), (s, a) => s.NominateAsync(a,
            new NominateInput(w.Cody, null, pool.Id, null, "Ran the close end to end this year.", null), default));
        var today = DateOnly.FromDateTime(w.Clock.GetUtcNow().UtcDateTime);
        var plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.CreateAsync(a,
            new PlanInput(w.Cara, "Prepare Cara for the CFO role.", null, today.AddMonths(3), cara.Id), default));
        await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.AddActionAsync(a, plan.Id,
            new ActionInput("Mentoring", "Mentoring with the CFO", null, today.AddDays(5), null, null, null, null, w.Exec), default));

        await using (var db = w.Db())
        {
            var rows = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.TenantId == w.TenantA && x.Action.StartsWith("succession.")).ToListAsync();
            rows.Should().HaveCountGreaterThan(10);
            var all = string.Join("\n", rows.Select(r => r.Metadata ?? string.Empty));
            foreach (var banned in new[] { "CriticalPositionId", "TalentPoolId", "\"PositionId\"", "CompanyId", "LearningAssignmentId", "EmployeeGoalId",
                         "nominator", "\"reason\"", "nominationIds", "RiskOfLoss", "ImpactOfVacancy", "\"Code\"" })
                all.Should().NotContain(banned, $"succession audit metadata is entity ids only ({banned})");
            var aboutPeople = string.Join("\n", rows.Where(r => !r.Action.StartsWith("succession.position.") && !r.Action.StartsWith("succession.pool."))
                .Select(r => r.Metadata ?? string.Empty));
            aboutPeople.Should().NotContain(cfo.Id.ToString()).And.NotContain(pool.Id.ToString(), "candidate rows do not name their target");
        }

        var forAdmin = (await w.Run<SuccessionPositionService, IReadOnlyList<CriticalPositionSummary>>(w.AdminP(), (s, a) => s.ListAsync(a, null, default)))
            .Single(p => p.Id == cfo.Id);
        forAdmin.LiveNominations.Should().BeNull();
        forAdmin.ReadyNow.Should().BeNull();
        forAdmin.NeedsReviewCount.Should().BeNull();
        var forHr = (await w.Run<SuccessionPositionService, IReadOnlyList<CriticalPositionSummary>>(w.HrManagerP(), (s, a) => s.ListAsync(a, null, default)))
            .Single(p => p.Id == cfo.Id);
        forHr.LiveNominations.Should().Be(2);
        forHr.ReadyNow.Should().Be(1);
        var pools = await w.Run<SuccessionPositionService, IReadOnlyList<TalentPoolDto>>(w.AdminP(), (s, a) => s.ListPoolsAsync(a, false, default));
        pools.Single(p => p.Id == pool.Id).LiveMembers.Should().BeNull();
        var positionsDrill = await w.Run<SuccessionReportService, DrillResult>(w.AdminP(), (s, a) => s.DrillAsync(a, "positions", default));
        JsonSerializer.Serialize(positionsDrill).Should().Contain("\"LiveNominations\":null");
    }

    // ── N6: anonymisation may only write the deterministic pseudonym ──

    [Fact]
    public async Task N6_SnapshotRewrite_MustBeTheDeterministicPseudonym()
    {
        var (w, _, _, _, v1) = await Arrange(fx);
        await using var _w = w;
        await using var db = w.Db();
        var sqlPseudonym = await db.Database.SqlQueryRaw<string>("SELECT succession_pseudonym({0}, {1}) AS \"Value\"", w.TenantA, w.Cara).SingleAsync();
        sqlPseudonym.Should().Be(SuccessionRetentionJob.PseudonymOf(w.TenantA, w.Cara)).And.MatchRegex("^Former employee #[0-9A-F]{8}$");

        const string rename = """
            UPDATE succession_slate_versions SET
                snapshot_json = jsonb_set(jsonb_set(jsonb_set(snapshot_json, '{{candidates,0,nameEn}}', to_jsonb({1}::text)),
                                '{{candidates,0,nameAr}}', to_jsonb({1}::text)), '{{candidates,0,employeeCode}}', '""'::jsonb),
                anonymised_at_utc = now(), anonymised_sha256 = 'probe'
            WHERE id = {0}
            """;
        var smuggled = () => db.Database.ExecuteSqlRawAsync(rename, v1.Id, "Cody Candidate");
        (await smuggled.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("immutable");
        var arbitrary = () => db.Database.ExecuteSqlRawAsync(rename, v1.Id, "Former employee #00000000");
        (await arbitrary.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("immutable");

        var firstCandidate = SlateSnapshot.Parse((await w.SlateRowAsync(v1.Id)).SnapshotJson)!.Candidates[0].EmployeeId;
        (await db.Database.ExecuteSqlRawAsync(rename, v1.Id, SuccessionRetentionJob.PseudonymOf(w.TenantA, firstCandidate))).Should().Be(1);
    }

    [Fact]
    public async Task N6_RetentionJob_WritesExactlyThePseudonym()
    {
        var (w, _, _, _, v1) = await Arrange(fx);
        await using var _w = w;
        var now = DateTime.UtcNow;
        await using (var db = w.Db())
        {
            var e = await db.Employees.IgnoreQueryFilters().SingleAsync(x => x.Id == w.Cara);
            e.Status = EmployeeStatuses.Offboarded;
            db.EmployeeOffboardings.Add(new EmployeeOffboarding
            {
                TenantId = w.TenantA, EmployeeId = w.Cara, EmployeeName = e.FullName, LastWorkingDay = DateOnly.FromDateTime(now.AddYears(-4)),
            });
            await db.SaveChangesAsync();
        }
        await using (var db = w.Db())
        {
            await new SuccessionRetentionJob().AnonymiseAsync(db, w.TenantA, w.Cara, now, default);
            await db.SaveChangesAsync();
        }
        var snapshot = SlateSnapshot.Parse((await w.SlateRowAsync(v1.Id)).SnapshotJson)!;
        var cara = snapshot.Candidates.Single(c => c.EmployeeId == w.Cara);
        cara.NameEn.Should().Be(SuccessionRetentionJob.PseudonymOf(w.TenantA, w.Cara));
        cara.NameAr.Should().Be(cara.NameEn);
        cara.EmployeeCode.Should().BeEmpty();
    }
}
