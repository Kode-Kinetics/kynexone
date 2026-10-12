using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Infrastructure.Learning;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Learning.LearningTestWorld;

namespace Zayra.Api.Tests.Learning;

/// <summary>
/// LRN-03 (DB-enforced duplicates, eligibility refusals), LRN-09 (idempotent reminders), LRN-11 (scoped reports that
/// reconcile to rows) and LRN-12 (every unauthorised completion or read path refused).
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class LearningAuthorizationReportTests(PostgresFixture fx)
{
    [Fact]
    public async Task ConcurrentDuplicateAssignment_LeavesOneRow_AndTheLoserGetsA409()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("RACE", [Lesson("ONE")]);

        async Task<object> AssignOnce()
        {
            try { return await w.AssignAsync(pathId, [w.Ops1]); }
            catch (LearningException ex) { return ex; }
        }
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => AssignOnce()));

        outcomes.OfType<AssignmentBulkResult>().Should().ContainSingle(r => r.Created == 1);
        outcomes.OfType<LearningException>().Should().HaveCount(3).And.OnlyContain(e => e.Status == 409 && e.Code == "duplicate_assignment");
        await using var db = w.Db();
        (await db.LearningAssignments.AsNoTracking().CountAsync(a => a.PathId == pathId && a.EmployeeId == w.Ops1)).Should().Be(1);
        var index = await fx.GetIndexDefinitionAsync("ux_learning_assignments_one_active_per_employee_path");
        index.Should().Contain("UNIQUE").And.Contain("WHERE").And.Contain("Assigned").And.Contain("InProgress");
    }

    [Fact]
    public async Task BulkAssignment_ReportsCreatedSkippedRefused_WithReasons()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("ELIG", [Lesson("OPSONLY")],
            eligibility: new LearningEligibilityCriteria(DepartmentIds: [Guid.Empty]));
        // Make Ops the only eligible department, and suspend Ops2.
        await using (var db = w.Db())
        {
            var path = await db.LearningPaths.SingleAsync(p => p.Id == pathId);
            path.EligibilityJson = new LearningEligibilityCriteria(DepartmentIds: [w.DeptOps]).Serialize();
            (await db.Employees.SingleAsync(e => e.Id == w.Ops2)).Status = EmployeeStatuses.Suspended;
            await db.SaveChangesAsync();
        }
        await w.AssignAsync(pathId, [w.Manager]);
        var result = await w.AssignAsync(pathId, [w.Manager, w.Ops1, w.Ops2, w.Sales1, w.B1]);
        result.Created.Should().Be(1);
        result.Skipped.Should().Be(1);
        result.Refused.Should().Be(3);
        result.Results.Single(r => r.EmployeeId == w.Manager).Reasons.Should().Equal("already_assigned");
        result.Results.Single(r => r.EmployeeId == w.Ops2).Reasons.Should().Contain("employee_not_active");
        result.Results.Single(r => r.EmployeeId == w.Sales1).Reasons.Should().Equal("department_not_eligible");
        result.Results.Single(r => r.EmployeeId == w.B1).Reasons.Should().Equal("employee_not_in_scope");
    }

    [Fact]
    public async Task EmployeeCannotActOnAnotherEmployeesRecord_OrSelfCompleteGatedItems()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("NEG",
            [Lesson("QUIZZED", LearningCompletionRules.Assessment), Cmi5("UNIT", Cmi5MoveOnValues.Completed)]);
        await w.AssignAsync(pathId, [w.Ops1, w.Ops2]);
        var quizOfOps1 = await w.EnrollmentAsync(w.Ops1, pathId, 1);
        var unitOfOps2 = await w.EnrollmentAsync(w.Ops2, pathId, 2);
        var quizOfOps2 = await w.EnrollmentAsync(w.Ops2, pathId, 1);

        Guid attemptOfOps1;
        var (s1, a1) = w.Employee(w.Ops1);
        await using (s1) attemptOfOps1 = (await Get<LearningProgressService>(s1).StartAttemptAsync(a1, w.Ops1, quizOfOps1.Id, default)).Id;

        var (scope, ops2) = w.Employee(w.Ops2);
        await using (scope)
        {
            var progress = Get<LearningProgressService>(scope);
            async Task<LearningException> Refused(Func<Task> call) => (await call.Should().ThrowAsync<LearningException>()).Which;

            // Someone else's enrollment or attempt: not found (never "forbidden", which would confirm it exists).
            (await Refused(() => progress.EnrollmentDetailAsync(w.TenantA, w.Ops2, quizOfOps1.Id, default))).Status.Should().Be(404);
            (await Refused(() => progress.LaunchAsync(ops2, w.Ops2, quizOfOps1.Id, "https://x", null, default))).Status.Should().Be(404);
            (await Refused(() => progress.AcknowledgeAsync(ops2, w.Ops2, quizOfOps1.Id, default))).Status.Should().Be(404);
            (await Refused(() => progress.StartAttemptAsync(ops2, w.Ops2, quizOfOps1.Id, default))).Status.Should().Be(404);
            (await Refused(() => progress.SubmitAttemptAsync(ops2, w.Ops2, attemptOfOps1, new SubmitAttemptInput(Correct()), default))).Status.Should().Be(404);

            // Their own assessment- or cmi5-gated items cannot be self-marked complete.
            await progress.LaunchAsync(ops2, w.Ops2, quizOfOps2.Id, "https://x", null, default);
            (await Refused(() => progress.AcknowledgeAsync(ops2, w.Ops2, quizOfOps2.Id, default))).Code.Should().Be("completion_rule_mismatch");
            (await Refused(() => progress.AcknowledgeAsync(ops2, w.Ops2, unitOfOps2.Id, default))).Code.Should().Be("completion_rule_mismatch");
        }
        await using var db = w.Db();
        (await db.LearningEnrollments.AsNoTracking().CountAsync(e => e.PathItemId == quizOfOps1.PathItemId && e.Status == LearningEnrollmentStatuses.Completed))
            .Should().Be(0);
        (await db.LearningAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptOfOps1)).Status.Should().Be(LearningAttemptStatuses.InProgress);
    }

    [Fact]
    public async Task ManagerSeesOnlyTheirTeam_CompanyScopedHrOnlyTheirCompany_TenantBNothingOfTenantA()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("SCOPE", [Lesson("ALL")]);
        var all = await w.AssignAsync(pathId, [w.Manager, w.Ops1, w.Ops2, w.Sales1, w.Sales2, w.Remote1]);
        all.Created.Should().Be(6);
        var salesAssignment = all.Results.Single(r => r.EmployeeId == w.Sales1).AssignmentId!.Value;
        var remoteAssignment = all.Results.Single(r => r.EmployeeId == w.Remote1).AssignmentId!.Value;

        // Manager: own reporting tree (themself + Ops1 + Ops2) only.
        await using (var scope = w.As(w.ManagerA()))
        {
            var actor = await ActorAsync(scope, w.TenantA);
            var rows = await Get<LearningReportService>(scope).StatusMatrixAsync(actor, null, null, null, null, default);
            rows.Select(r => r.EmployeeId).Should().BeEquivalentTo(new[] { w.Manager, w.Ops1, w.Ops2 });
            var other = () => Get<LearningProgressService>(scope).AssignmentViewAsync(actor, salesAssignment, default);
            (await other.Should().ThrowAsync<LearningException>()).Which.Status.Should().Be(404);
        }

        // HR limited to company A1: never sees the A2 employee, by list, by id or by assignment.
        await using (var scope = w.As(w.HrA1Only()))
        {
            var actor = await ActorAsync(scope, w.TenantA);
            var listed = await Get<LearningProgressService>(scope).ListAssignmentsAsync(actor, pathId, null, null, default);
            listed.Select(r => r.EmployeeId).Should().NotContain(w.Remote1).And.HaveCount(5);
            var byId = () => Get<LearningProgressService>(scope).AssignmentViewAsync(actor, remoteAssignment, default);
            (await byId.Should().ThrowAsync<LearningException>()).Which.Status.Should().Be(404);
            var summary = await Get<LearningReportService>(scope).SummaryAsync(actor, default);
            summary.Kpis.Single(k => k.Key == "assigned").Value.Should().Be(5);
        }

        // Tenant B's HR and employee cannot read tenant A ids.
        await using (var scope = w.As(w.HrB()))
        {
            var actor = await ActorAsync(scope, w.TenantB);
            var byId = () => Get<LearningProgressService>(scope).AssignmentViewAsync(actor, salesAssignment, default);
            (await byId.Should().ThrowAsync<LearningException>()).Which.Status.Should().Be(404);
            var course = () => Get<LearningCatalogService>(scope).GetPathAsync(w.TenantB, pathId, default);
            (await course.Should().ThrowAsync<LearningException>()).Which.Status.Should().Be(404);
            (await Get<LearningReportService>(scope).StatusMatrixAsync(actor, null, null, null, null, default)).Should().BeEmpty();
        }
        var enrollmentA = await w.EnrollmentAsync(w.Ops1, pathId, 1);
        await using (var scope = w.As(w.EmployeeB()))
        {
            var call = () => Get<LearningProgressService>(scope).EnrollmentDetailAsync(w.TenantB, w.B1, enrollmentA.Id, default);
            (await call.Should().ThrowAsync<LearningException>()).Which.Status.Should().Be(404);
        }
    }

    [Fact]
    public async Task ReportTotals_ReconcileWithTheRowsAndTheDatabase()
    {
        await using var w = await CreateAsync(fx);
        var (pathA, _) = await w.PublishedPathAsync("RA", [Lesson("RA1")]);
        var (pathB, _) = await w.PublishedPathAsync("RB", [Lesson("RB1", LearningCompletionRules.Assessment, validityMonths: 1)]);
        await w.AssignAsync(pathA, [w.Ops1, w.Ops2, w.Sales1, w.Sales2], w.Clock.Today.AddDays(5));
        await w.AssignAsync(pathB, [w.Ops1, w.Sales1, w.Remote1], w.Clock.Today.AddDays(1));
        // Ops1 completes path A; Sales1 completes path B (its credential expires within 30 days).
        foreach (var (employee, path, quiz) in new[] { (w.Ops1, pathA, false), (w.Sales1, pathB, true) })
        {
            var enrollment = await w.EnrollmentAsync(employee, path, 1);
            var (s, a) = w.Employee(employee);
            await using (s)
            {
                var progress = Get<LearningProgressService>(s);
                await progress.LaunchAsync(a, employee, enrollment.Id, "https://x", null, default);
                if (quiz)
                {
                    var attempt = await progress.StartAttemptAsync(a, employee, enrollment.Id, default);
                    await progress.SubmitAttemptAsync(a, employee, attempt.Id, new SubmitAttemptInput(Correct()), default);
                }
                else await progress.AcknowledgeAsync(a, employee, enrollment.Id, default);
            }
        }
        // One cancelled assignment is in nobody's totals.
        await using (var scope = w.As(w.HrA()))
        {
            var actor = await ActorAsync(scope, w.TenantA);
            var row = (await Get<LearningProgressService>(scope).ListAssignmentsAsync(actor, pathA, w.Sales2, null, default)).Single();
            await Get<LearningAssignmentService>(scope).CancelAsync(actor, row.Id, "Moved to another role", default);
        }
        w.Clock.Advance(TimeSpan.FromDays(3)); // path B (due +1) is now overdue for its open assignment

        await using var hr = w.As(w.HrA());
        var hrActor = await ActorAsync(hr, w.TenantA);
        var reports = Get<LearningReportService>(hr);
        var status = await reports.StatusMatrixAsync(hrActor, null, null, null, null, default);
        var byPath = await reports.CompletionAsync(hrActor, "path", default);
        var byDepartment = await reports.CompletionAsync(hrActor, "department", default);
        var overdue = await reports.StatusMatrixAsync(hrActor, null, null, null, true, default);
        var expiring = await reports.ExpiringAsync(hrActor, 30, default);
        var summary = await reports.SummaryAsync(hrActor, default);

        await using var db = w.Db();
        var direct = await db.LearningAssignments.AsNoTracking()
            .Where(a => a.TenantId == w.TenantA && a.Status != LearningAssignmentStatuses.Cancelled).ToListAsync();
        status.Should().HaveCount(direct.Count).And.HaveCount(6);
        byPath.Sum(r => r.Assigned).Should().Be(direct.Count);
        byDepartment.Sum(r => r.Assigned).Should().Be(direct.Count);
        byPath.Should().OnlyContain(r => r.Assigned == r.Completed + r.InProgress + r.NotStarted);
        byPath.Sum(r => r.Completed).Should().Be(direct.Count(a => a.Status == LearningAssignmentStatuses.Completed)).And.Be(2);
        overdue.Should().HaveCount(direct.Count(a => a.Status != LearningAssignmentStatuses.Completed && a.DueDate < w.Clock.Today)).And.HaveCount(2);
        byPath.Sum(r => r.Overdue).Should().Be(overdue.Count);
        expiring.Should().ContainSingle(e => e.EmployeeId == w.Sales1);
        summary.Kpis.Single(k => k.Key == "assigned").Value.Should().Be(status.Count);
        summary.Kpis.Single(k => k.Key == "completed").Value.Should().Be(status.Count(r => r.Status == LearningAssignmentStatuses.Completed));
        summary.Kpis.Single(k => k.Key == "overdue").Value.Should().Be(overdue.Count);
        summary.Kpis.Single(k => k.Key == "open").Value.Should().Be((await reports.StatusMatrixAsync(hrActor, null, null, "open", null, default)).Count);
        summary.Kpis.Single(k => k.Key == "expiring").Value.Should().Be(expiring.Count);
        summary.CompletionRatePercent.Should().Be(Math.Round(2 * 100m / 6, 2, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public async Task ReminderSweep_RunTwice_SendsOneNotificationPerEvent()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("REMIND", [Lesson("R1")]);
        await w.AssignAsync(pathId, [w.Ops1], w.Clock.Today.AddDays(3));   // due soon
        await w.AssignAsync(pathId, [w.Ops2], w.Clock.Today.AddDays(1));   // will be overdue
        w.Clock.Advance(TimeSpan.FromDays(2));

        LearningSweepResult first, second;
        await using (var scope = w.System()) first = await Get<LearningSweepService>(scope).RunTenantAsync(w.TenantA, default);
        await using (var scope = w.System()) second = await Get<LearningSweepService>(scope).RunTenantAsync(w.TenantA, default);

        first.DueReminders.Should().Be(1);
        first.OverdueNotices.Should().Be(1);
        first.Failures.Should().Be(0);
        second.DueReminders.Should().Be(0);
        second.OverdueNotices.Should().Be(0);

        await using var db = w.Db();
        var deliveries = await db.NotificationDeliveries.AsNoTracking().Where(d => d.TenantId == w.TenantA && d.EventCode.StartsWith("LEARNING_")).ToListAsync();
        deliveries.GroupBy(d => (d.EventCode, d.EntityId, d.Channel)).Should().OnlyContain(g => g.Count() == 1, "one delivery per event, entity and channel");
        deliveries.Select(d => d.EventCode).Distinct().Should().BeEquivalentTo(new[] { LearningEventCodes.Due, LearningEventCodes.Overdue });
        var assignments = await db.LearningAssignments.AsNoTracking().Where(a => a.PathId == pathId).ToListAsync();
        assignments.Single(a => a.EmployeeId == w.Ops1).DueReminderSentAtUtc.Should().NotBeNull();
        assignments.Single(a => a.EmployeeId == w.Ops2).OverdueNoticeSentAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task SweepWorker_RunsEveryEnabledTenant_UnderItsLease()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("WORKER", [Lesson("W1")]);
        await w.AssignAsync(pathId, [w.Ops1], w.Clock.Today.AddDays(2));
        var worker = new LearningSweepWorker(w.Services.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LearningSweepWorker>.Instance,
            Microsoft.Extensions.Options.Options.Create(new LearningOptions()));
        var results = await worker.DrainOnceAsync(default);
        worker.LastSweepOutcome.Should().Be(Zayra.Api.Infrastructure.Operations.SweepOutcome.Completed);
        results.Should().ContainKey(w.TenantA);
        results[w.TenantA].DueReminders.Should().Be(1);
    }
}
