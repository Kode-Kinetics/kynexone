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
/// SUC-04: development plans with owners and review dates, Learning actions created through the LRN service (never a
/// copy) and Goal actions linked to EmployeeGoal; linked completion (the employee finishes the learning → the action
/// completes itself), overdue and review-due notices sent once, one current plan per employee, and the nomination link
/// withheld from the line manager and the employee.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class SuccessionDevelopmentPlanTests(PostgresFixture fx)
{
    private static async Task<Guid> PublishedPathAsync(SuccessionTestWorld w, string code)
    {
        await using var scope = w.As(w.HrDirectorP());
        var dataScope = await Get<IDataScopeService>(scope).ResolveAsync(w.HrDirectorP(), w.TenantA, default);
        var actor = new LearningActor(w.TenantA, w.UserOf[w.HrDirector], "127.0.0.1", "test", dataScope);
        var catalog = Get<LearningCatalogService>(scope);
        var course = await catalog.CreateCourseAsync(actor, LearningTestWorld.Lesson(code), default);
        var published = await catalog.PublishCourseAsync(actor, course.Id, null, default);
        var path = await catalog.CreatePathAsync(actor, new PathInput(code, $"{code} leadership path", $"مسار {code}", null, null, null, null, 30,
            [new PathItemInput(published.Id, true, null)], null), default);
        await catalog.ActivatePathAsync(actor, path.Id, default);
        return path.Id;
    }

    private static async Task<Guid> GoalAsync(SuccessionTestWorld w, int employee, string title)
    {
        await using var db = w.Db();
        var goal = new EmployeeGoal { TenantId = w.TenantA, EmployeeId = employee, Title = title, Status = "Active", TargetValue = 100, Weight = 100 };
        db.EmployeeGoals.Add(goal);
        await db.SaveChangesAsync();
        return goal.Id;
    }

    [Fact]
    public async Task SUC04_LearningAndGoalActions_CompleteThemselvesWhenTheLinkedRecordCompletes()
    {
        await using var w = await CreateAsync(fx);
        var cfo = await w.DesignateCfoAsync();
        var nomination = await w.NominateAsync(cfo.Id, w.Cara);
        var pathId = await PublishedPathAsync(w, "LEAD");
        var goalId = await GoalAsync(w, w.Cara, "Run the FY2027 budget cycle");
        var today = DateOnly.FromDateTime(w.Clock.GetUtcNow().UtcDateTime);

        var plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.CreateAsync(a,
            new PlanInput(w.Cara, "Prepare Cara to step into the CFO role within two years.", w.Manager, today.AddMonths(3), nomination.Id), default));
        plan.Status.Should().Be(DevelopmentPlanStatuses.Draft);
        plan.NominationId.Should().Be(nomination.Id, "HR with slate access sees the link");
        var activateEmpty = () => w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.ActivateAsync(a, plan.Id, default));
        (await activateEmpty.Should().ThrowAsync<SuccessionException>()).Which.Code.Should().Be("plan_empty");

        plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.AddActionAsync(a, plan.Id,
            new ActionInput("Learning", "Complete the leadership path", null, today.AddDays(30), null, pathId, null, null, null), default));
        plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.AddActionAsync(a, plan.Id,
            new ActionInput("Goal", "Own the budget cycle", null, today.AddDays(90), null, null, null, goalId, null), default));
        plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.AddActionAsync(a, plan.Id,
            new ActionInput("Mentoring", "Monthly sessions with the CFO", null, null, null, null, null, null, w.Incumbent), default));
        plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.ActivateAsync(a, plan.Id, default));
        plan.Status.Should().Be(DevelopmentPlanStatuses.Active);
        var learningAction = plan.Actions.Single(a => a.ActionType == "Learning");
        learningAction.LearningAssignmentId.Should().NotBeNull();
        learningAction.LearningPathName.Should().Be("LEAD leadership path");

        // The assignment is a real LRN row (created through LearningAssignmentService, audited there), not a copy.
        await using (var db = w.Db())
        {
            var assignment = await db.LearningAssignments.IgnoreQueryFilters().SingleAsync(a => a.Id == learningAction.LearningAssignmentId);
            assignment.EmployeeId.Should().Be(w.Cara);
            assignment.PathId.Should().Be(pathId);
            assignment.IsMandatory.Should().BeFalse();
            (await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.EntityId == assignment.Id.ToString() && a.Action == "learning.assignment.created"))
                .Should().Be(1);
        }

        // A linked action's status is not set by hand.
        var manual = () => w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.UpdateActionAsync(a, learningAction.Id,
            new ActionUpdateInput("Completed", null, null, learningAction.Version), default));
        (await manual.Should().ThrowAsync<SuccessionException>()).Which.Code.Should().Be("linked_action");

        // Cara completes the learning in ESS: the action completes itself, by the LRN completion hook.
        await using (var db = w.Db())
        {
            var enrollment = await db.LearningEnrollments.IgnoreQueryFilters().SingleAsync(e => e.AssignmentId == learningAction.LearningAssignmentId);
            await using var scope = w.As(w.EmployeeP(w.Cara));
            var employeeActor = new LearningActor(w.TenantA, w.UserOf[w.Cara], null, "test",
                new DataScope { Level = DataScopeLevel.Own, CallerEmployeeId = w.Cara, AllowedEmployeeIds = [w.Cara] });
            var progress = Get<LearningProgressService>(scope);
            await progress.LaunchAsync(employeeActor, w.Cara, enrollment.Id, "https://app.kynexone.test", null, default);
            await progress.AcknowledgeAsync(employeeActor, w.Cara, enrollment.Id, default);
        }
        await using (var db = w.Db())
        {
            var action = await db.DevelopmentPlanActions.IgnoreQueryFilters().SingleAsync(a => a.Id == learningAction.Id);
            action.Status.Should().Be(DevelopmentActionStatuses.Completed, "the hook ran on the learner's own request, before any read");
            action.CompletionMethod.Should().Be(DevelopmentCompletionMethods.LinkedLearningCompleted);
            (await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.EntityId == action.Id.ToString() && a.Action == "succession.devplan.action_synced"))
                .Should().Be(1);
        }

        // The goal completes in Performance: the next read reconciles it.
        await using (var db = w.Db())
        {
            var goal = await db.EmployeeGoals.IgnoreQueryFilters().SingleAsync(g => g.Id == goalId);
            goal.Status = "Completed";
            goal.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.GetAsync(a, plan.Id, default));
        plan.Actions.Single(a => a.ActionType == "Goal").CompletionMethod.Should().Be(DevelopmentCompletionMethods.LinkedGoalCompleted);
        plan.Progress.Completed.Should().Be(2);
        plan.Progress.PercentComplete.Should().Be(66.7m);

        // Complete is refused while an action is open; the mentoring action is closed by hand, then the plan completes.
        var early = () => w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.CompleteAsync(a, plan.Id, default));
        (await early.Should().ThrowAsync<SuccessionException>()).Which.Code.Should().Be("open_actions");
        var mentoring = plan.Actions.Single(a => a.ActionType == "Mentoring");
        await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.ManagerP(w.Manager), (s, a) => s.UpdateActionAsync(a, mentoring.Id,
            new ActionUpdateInput("Completed", "Six sessions held.", null, mentoring.Version), default));
        plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.CompleteAsync(a, plan.Id, default));
        plan.Status.Should().Be(DevelopmentPlanStatuses.Completed);
        plan.Progress.PercentComplete.Should().Be(100m);
    }

    [Fact]
    public async Task SUC04_OverdueActionsAndReviewDates_AreNoticedOnce_AndAReviewMovesTheDateForward()
    {
        await using var w = await CreateAsync(fx);
        var today = DateOnly.FromDateTime(w.Clock.GetUtcNow().UtcDateTime);
        var plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.ManagerP(w.Manager), (s, a) => s.CreateAsync(a,
            new PlanInput(w.Cody, "Broaden Cody's commercial exposure this year.", null, today.AddDays(7), null), default));
        plan.Owner!.EmployeeId.Should().Be(w.Manager, "the line manager who created it owns it by default");
        plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.ManagerP(w.Manager), (s, a) => s.AddActionAsync(a, plan.Id,
            new ActionInput("Experience", "Shadow the pricing committee", null, today.AddDays(3), null, null, null, null, null), default));
        plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.ManagerP(w.Manager), (s, a) => s.ActivateAsync(a, plan.Id, default));

        // A second current plan for the same person is refused by the database.
        var second = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.CreateAsync(a,
            new PlanInput(w.Cody, "A competing plan for the same person.", null, today.AddDays(30), null), default));
        second = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.AddActionAsync(a, second.Id,
            new ActionInput("Other", "Anything", null, null, null, null, null, null, null), default));
        var activate = () => w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.ActivateAsync(a, second.Id, default));
        (await activate.Should().ThrowAsync<SuccessionException>()).Which.Code.Should().Be("plan_exists");
        (await fx.GetIndexDefinitionAsync("ux_development_plans_one_current_per_employee")).Should().Contain("UNIQUE").And.Contain("OnHold");

        w.Clock.Advance(TimeSpan.FromDays(10));
        plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.GetAsync(a, plan.Id, default));
        plan.Actions.Single().IsOverdue.Should().BeTrue();
        plan.Progress.OnTrack.Should().BeFalse();
        plan.Progress.ReviewOverdue.Should().BeTrue();

        SuccessionSweepResult first, again;
        await using (var scope = w.System()) first = await Get<SuccessionSweepService>(scope).RunTenantAsync(w.TenantA, default);
        await using (var scope = w.System()) again = await Get<SuccessionSweepService>(scope).RunTenantAsync(w.TenantA, default);
        first.ReviewReminders.Should().Be(1);
        first.OverdueNotices.Should().Be(1);
        again.ReviewReminders.Should().Be(0, "each review date is reminded once");
        again.OverdueNotices.Should().Be(0, "each overdue action is noticed once");
        await using (var db = w.Db())
        {
            var p = await db.DevelopmentPlans.IgnoreQueryFilters().SingleAsync(x => x.Id == plan.Id);
            p.ReviewReminderSentFor.Should().Be(p.NextReviewOn);
            (await db.DevelopmentPlanActions.IgnoreQueryFilters().SingleAsync(x => x.PlanId == plan.Id)).OverdueNoticeSentAtUtc.Should().NotBeNull();
        }

        plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.ManagerP(w.Manager), (s, a) => s.ReviewAsync(a, plan.Id,
            new PlanReviewInput("Pricing committee moved to next quarter.", DateOnly.FromDateTime(w.Clock.GetUtcNow().UtcDateTime).AddMonths(3), plan.Version), default));
        plan.LastReviewedAtUtc.Should().NotBeNull();
        plan.Progress.ReviewOverdue.Should().BeFalse();
    }

    [Fact]
    public async Task SUC04_TheNominationLink_IsHiddenFromTheLineManagerAndTheEmployee()
    {
        await using var w = await CreateAsync(fx);
        var cfo = await w.DesignateCfoAsync();
        var nomination = await w.NominateAsync(cfo.Id, w.Cara);
        var today = DateOnly.FromDateTime(w.Clock.GetUtcNow().UtcDateTime);
        var plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.CreateAsync(a,
            new PlanInput(w.Cara, "Prepare for the next role with stretch work.", w.Manager, today.AddMonths(2), nomination.Id), default));
        plan = await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.AddActionAsync(a, plan.Id,
            new ActionInput("Other", "Lead the audit response", null, null, null, null, null, null, null), default));
        await w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.HrManagerP(), (s, a) => s.ActivateAsync(a, plan.Id, default));

        var asManager = await w.Run<DevelopmentPlanService, IReadOnlyList<DevelopmentPlanDto>>(w.ManagerP(w.Manager), (s, a) => s.ListAsync(a, null, null, default));
        asManager.Should().ContainSingle(p => p.Id == plan.Id).Which.NominationId.Should().BeNull();
        System.Text.Json.JsonSerializer.Serialize(asManager).Should().NotContain(nomination.Id.ToString());

        // A manager cannot link a plan to a nomination either.
        var link = () => w.Run<DevelopmentPlanService, DevelopmentPlanDto>(w.ManagerP(w.Manager), (s, a) => s.CreateAsync(a,
            new PlanInput(w.Cody, "Try to attach a nomination as a manager.", null, today.AddMonths(2), nomination.Id), default));
        (await link.Should().ThrowAsync<SuccessionException>()).Which.Status.Should().Be(404);

        await using var scope = w.As(w.EmployeeP(w.Cara));
        var mine = await Get<DevelopmentPlanService>(scope).MineAsync(w.TenantA, w.Cara, default);
        mine.Current!.NominationId.Should().BeNull();
        mine.CandidateVisibility.Should().BeFalse();
        mine.Memberships.Should().BeEmpty();
        System.Text.Json.JsonSerializer.Serialize(mine).Should().NotContain(nomination.Id.ToString()).And.NotContain("Chief Financial Officer");
    }
}
