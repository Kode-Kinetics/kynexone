using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Infrastructure.Succession;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Succession.SuccessionTestWorld;

namespace Zayra.Api.Tests.Succession;

/// <summary>
/// SUC-07 — restricted access. The access matrix (HR Director, HR Manager, accountable executive, line manager, the
/// employee with the visibility setting off and on, a peer, an unrelated manager, Admin, another company, another tenant)
/// and the approval leak paths: a slate approval never appears in a manager's team queue, the all-requests payload
/// carries no names, and no candidate — nor anyone reporting to one — can decide or even open it.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class SuccessionAccessTests(PostgresFixture fx)
{
    private sealed record World(SuccessionTestWorld W, CriticalPositionSummary Cfo, NominationDto Cara, NominationDto Cody, SlateVersionDto Approved,
        SlateVersionDto Pending, DevelopmentPlanDto Plan);

    /// <summary>CFO with an approved v1 (Cara, Cody), a submitted v2 waiting for a decision, and Cara's plan linked to her nomination.</summary>
    private async Task<World> ArrangeAsync(SuccessionTestWorld w)
    {
        var cfo = await w.DesignateCfoAsync();
        var cara = await w.CalibratedCandidateAsync(cfo.Id, w.Cara, 3, ReadinessLevels.ReadyNow);
        var cody = await w.CalibratedCandidateAsync(cfo.Id, w.Cody, 2, ReadinessLevels.OneToTwoYears);
        var v1 = await w.SubmitSlateAsync((await w.DraftSlateAsync(cfo.Id, (cara.Id, 1), (cody.Id, 2))).Id);
        await w.DecideAsync(w.HrDirectorP(), v1.ApprovalRequestId!.Value, "Approve", "Agreed.");
        var v2 = await w.Run<SuccessionSlateService, SlateVersionDto>(w.HrManagerP(), (s, a) => s.ReviseAsync(a, v1.Id, default));
        v2 = await w.SubmitSlateAsync(v2.Id);
        var today = DateOnly.FromDateTime(w.Clock.GetUtcNow().UtcDateTime);
        var plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.CreateAsync(a,
            new PlanInput(w.Cara, "Prepare Cara for a finance leadership role.", w.Manager, today.AddMonths(3), cara.Id), default));
        plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.AddActionAsync(a, plan.Id,
            new ActionInput("Other", "Present the board pack", null, null, null, null, null, null, null), default));
        plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.ActivateAsync(a, plan.Id, default));
        return new World(w, cfo, cara, cody, v1, v2, plan);
    }

    private static async Task<int> StatusOf(Func<Task> call)
    {
        try { await call(); return 200; }
        catch (SuccessionException ex) { return ex.Status; }
    }

    [Fact]
    public async Task SUC07_TheAccessMatrix()
    {
        await using var w = await CreateAsync(fx);
        var x = await ArrangeAsync(w);

        Task<int> ReadSlate(System.Security.Claims.ClaimsPrincipal who, Guid slate) =>
            StatusOf(() => w.Run<SuccessionSlateService, SlateVersionDto>(who, (s, a) => s.GetAsync(a, slate, default)));
        Task<int> ReadPosition(System.Security.Claims.ClaimsPrincipal who) =>
            StatusOf(() => w.Run<SuccessionPositionService, (SuccessionCriticalPosition, CriticalPositionSummary, bool)>(who, (s, a) => s.GetAsync(a, x.Cfo.Id, default)));
        Task<int> ReadPlan(System.Security.Claims.ClaimsPrincipal who) =>
            StatusOf(() => w.Run<DevelopmentPlanService, DevelopmentPlanDto>(who, (s, a) => s.GetAsync(a, x.Plan.Id, default)));

        // HR Director and HR Manager: everything in their companies.
        (await ReadSlate(w.HrDirectorP(), x.Approved.Id)).Should().Be(200);
        (await ReadSlate(w.HrManagerP(), x.Pending.Id)).Should().Be(200);
        (await ReadPlan(w.HrManagerP())).Should().Be(200);

        // The accountable executive: their own position and its approved versions, never a pending one.
        var mine = await w.Run<SuccessionPositionService, IReadOnlyList<CriticalPositionSummary>>(w.ManagerP(w.Exec), (s, a) => s.MineAsync(a, default));
        mine.Should().ContainSingle(p => p.Id == x.Cfo.Id);
        (await ReadPosition(w.ManagerP(w.Exec))).Should().Be(200);
        (await ReadSlate(w.ManagerP(w.Exec), x.Approved.Id)).Should().Be(200);
        (await ReadSlate(w.ManagerP(w.Exec), x.Pending.Id)).Should().Be(404);
        var versions = await w.Run<SuccessionSlateService, IReadOnlyList<SlateVersionDto>>(w.ManagerP(w.Exec), (s, a) => s.ListAsync(a, x.Cfo.Id, default));
        versions.Should().OnlyContain(v => v.Status == SlateStatuses.Approved || v.Status == SlateStatuses.Superseded).And.OnlyContain(v => v.ReadOnly);

        // The candidates' line manager: their plans, never the slate, the position or the plan's nomination link.
        (await ReadSlate(w.ManagerP(w.Manager), x.Approved.Id)).Should().Be(404);
        (await ReadPosition(w.ManagerP(w.Manager))).Should().Be(404);
        (await ReadPlan(w.ManagerP(w.Manager))).Should().Be(200);
        var mineAsNominator = await w.Run<SuccessionNominationService, IReadOnlyList<MyNominationDto>>(w.ManagerP(w.Manager), (s, a) => s.MineAsync(a, default));
        mineAsNominator.Should().BeEmpty("HR nominated these two; a manager sees only their own nominations");

        // Peer and unrelated manager: nothing.
        (await ReadSlate(w.EmployeeP(w.Pete), x.Approved.Id)).Should().Be(404);
        (await ReadPlan(w.EmployeeP(w.Pete))).Should().Be(404);
        (await ReadPosition(w.EmployeeP(w.Pete))).Should().Be(404);
        (await ReadSlate(w.ManagerP(w.Uma), x.Approved.Id)).Should().Be(404);
        (await ReadPlan(w.ManagerP(w.Uma))).Should().Be(404);
        var umasPlans = await w.Run<DevelopmentPlanService, IReadOnlyList<DevelopmentPlanDto>>(w.ManagerP(w.Uma), (s, a) => s.ListAsync(a, null, "all", default));
        umasPlans.Should().BeEmpty();

        // The candidate herself (an employee): not the slate, her own plan only through ESS.
        (await ReadSlate(w.EmployeeP(w.Cara), x.Approved.Id)).Should().Be(404);

        // Admin: withheld slate access, and approvals.override is no way around it.
        (await ReadSlate(w.AdminP(), x.Approved.Id)).Should().Be(404);
        (await ReadSlate(w.AdminP(), x.Pending.Id)).Should().Be(404);
        var adminDecides = () => w.DecideAsync(w.AdminP(), x.Pending.ApprovalRequestId!.Value, "Approve", "override");
        (await adminDecides.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Link your login");
        var linkedAdmin = Principal(w.TenantA, w.UserOf[w.Uma], w.Uma, AdminPermissions, ["Admin"]); // unrelated to the slate
        var linkedAdminDecides = () => w.DecideAsync(linkedAdmin, x.Pending.ApprovalRequestId!.Value, "Approve", "override");
        (await linkedAdminDecides.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("routed");

        // Another company (HR scoped to A2) and another tenant: 404, as for an id that does not exist.
        (await ReadPosition(w.HrManagerA2Only())).Should().Be(404);
        (await ReadSlate(w.HrManagerA2Only(), x.Approved.Id)).Should().Be(404);
        (await ReadPlan(w.HrManagerA2Only())).Should().Be(404);
        var a2List = await w.Run<SuccessionNominationService, IReadOnlyList<NominationDto>>(w.HrManagerA2Only(), (s, a) => s.ListAsync(a, x.Cfo.Id, null, null, "all", default));
        a2List.Should().BeEmpty();
        (await StatusOf(() => w.Run<SuccessionPositionService, (SuccessionCriticalPosition, CriticalPositionSummary, bool)>(w.HrDirectorB(), (s, a) => s.GetAsync(a, x.Cfo.Id, default))))
            .Should().Be(404);
        (await ReadSlate(w.HrDirectorB(), x.Approved.Id)).Should().Be(404);
        (await StatusOf(() => w.Run<SuccessionNominationService, NominationDto>(w.HrDirectorB(), (s, a) => s.GetAsync(a, x.Cara.Id, default)))).Should().Be(404);
    }

    [Fact]
    public async Task SUC07_TheEmployeeSeesOnlyTheirOwnPlan_AndMembershipsOnlyWhenTheTenantSwitchesVisibilityOn()
    {
        await using var w = await CreateAsync(fx);
        var x = await ArrangeAsync(w);
        async Task<MyDevelopmentDto> Mine(int employee)
        {
            await using var scope = w.As(w.EmployeeP(employee));
            return await Get<DevelopmentPlanService>(scope).MineAsync(w.TenantA, employee, default);
        }

        var off = await Mine(w.Cara);
        off.Current!.Id.Should().Be(x.Plan.Id);
        off.CandidateVisibility.Should().BeFalse();
        off.Memberships.Should().BeEmpty();
        (await Mine(w.Pete)).Current.Should().BeNull("a peer has no plan, and never sees another's");

        await using (var db = w.Db())
        {
            db.TenantFeatureFlags.Add(new TenantFeatureFlag { TenantId = w.TenantA, FeatureKey = FeatureKeys.SuccessionCandidateVisibility, IsEnabled = true });
            await db.SaveChangesAsync();
        }
        // The platform flag endpoint invalidates the cached module state the same way.
        await using (var scope = w.System()) Get<Zayra.Api.Infrastructure.Modules.ITenantModuleService>(scope).Invalidate(w.TenantA);
        var on = await Mine(w.Cara);
        on.CandidateVisibility.Should().BeTrue();
        on.Memberships.Should().ContainSingle().Which.Should().Be(new MyMembershipDto("position", "Chief Financial Officer", null, "Considered", ReadinessLevels.ReadyNow));
        var json = JsonSerializer.Serialize(on);
        json.Should().NotContain("Cody").And.NotContain("كودي").And.NotContain("P3-").And.NotContain(x.Approved.Id.ToString());
    }

    [Fact]
    public async Task SUC07_ASlateApproval_IsNotInTheTeamQueue_AndTheAllRequestsPayloadNamesNobody()
    {
        await using var w = await CreateAsync(fx);
        var x = await ArrangeAsync(w);

        await using (var scope = w.As(w.ManagerP(w.Manager)))
        {
            var team = await Get<IApprovalWorkflowService>(scope).GetRequestsAsync(w.TenantA, null, null, "team", 1, 100, Context(w.ManagerP(w.Manager)), default);
            team.Items.Should().NotContain(r => r.EntityName == SuccessionSlateApprovalSync.EntityName,
                "a slate approval is about nobody, so it never appears among a manager's reports' requests");
            var mine = await Get<IApprovalWorkflowService>(scope).GetRequestsAsync(w.TenantA, null, null, "mine", 1, 100, Context(w.ManagerP(w.Manager)), default);
            mine.Items.Should().NotContain(r => r.EntityName == SuccessionSlateApprovalSync.EntityName);
            var one = await Get<IApprovalWorkflowService>(scope).GetRequestAsync(w.TenantA, x.Pending.ApprovalRequestId!.Value, Context(w.ManagerP(w.Manager)), default);
            one.Should().BeNull("the candidates' manager cannot open the slate's approval");
        }

        await using (var scope = w.As(w.HrManagerP()))
        {
            var all = await Get<IApprovalWorkflowService>(scope).GetRequestsAsync(w.TenantA, null, SuccessionSlateApprovalSync.EntityName, "all", 1, 100,
                Context(w.HrManagerP()), default);
            all.Items.Should().HaveCount(2);
            var payload = JsonSerializer.Serialize(all);
            foreach (var name in new[] { "Cara", "Cody", "كارا", "كودي", "Candidate" })
                payload.Should().NotContain(name, "the all-requests view shows ids and the snapshot hash only");
            all.Items.Should().OnlyContain(r => r.RequestedForEmployeeId == null && r.Title.StartsWith("Succession slate v"));
        }

        await using (var db = w.Db())
        {
            var audits = await db.AuditLogs.IgnoreQueryFilters().Where(a => a.TenantId == w.TenantA && a.Action.StartsWith("succession.")).ToListAsync();
            audits.Should().NotBeEmpty();
            string.Join("|", audits.Select(a => a.Metadata ?? "")).Should().NotContain("Cara Candidate");
            var deliveries = await db.NotificationDeliveries.IgnoreQueryFilters().Where(d => d.TenantId == w.TenantA && d.EventCode.StartsWith("SUCCESSION_")).ToListAsync();
            JsonSerializer.Serialize(deliveries).Should().NotContain("Cara").And.NotContain("Cody");
        }
    }

    [Fact]
    public async Task SUC07_ACandidate_AndAnyoneReportingToOne_CanNeitherDecideNorOpenTheSlate()
    {
        await using var w = await CreateAsync(fx);
        var x = await ArrangeAsync(w);
        var approvalId = x.Pending.ApprovalRequestId!.Value;

        var candidate = () => w.DecideAsync(w.HrDirectorAs(w.Cara), approvalId, "Approve", "I approve myself");
        (await candidate.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("candidate on this succession slate");
        var report = () => w.DecideAsync(w.HrDirectorAs(w.Rana), approvalId, "Approve", "My boss is on it");
        (await report.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("report to a candidate");

        await using (var scope = w.As(w.HrDirectorAs(w.Cara)))
        {
            var seen = await Get<IApprovalWorkflowService>(scope).GetRequestAsync(w.TenantA, approvalId, Context(w.HrDirectorAs(w.Cara)), default);
            seen.Should().BeNull("CanDecideRequestAsync consults the same slate SoD, so the candidate is not even shown the request");
        }
        (await StatusOf(() => w.Run<SuccessionSlateService, SlateVersionDto>(w.HrDirectorAs(w.Cara), (s, a) => s.GetAsync(a, x.Pending.Id, default))))
            .Should().Be(404);
        (await StatusOf(() => w.Run<SuccessionSlateService, SlateVersionDto>(w.HrDirectorAs(w.Rana), (s, a) => s.GetAsync(a, x.Pending.Id, default))))
            .Should().Be(404);

        // The routed HR Director who is not involved decides it.
        await w.DecideAsync(w.HrDirectorP(), approvalId, "Approve", "Agreed.");
        (await w.SlateRowAsync(x.Pending.Id)).Status.Should().Be(SlateStatuses.Approved);
        (await w.SlateRowAsync(x.Approved.Id)).Status.Should().Be(SlateStatuses.Superseded);
    }
}
