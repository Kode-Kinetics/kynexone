using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Learning;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Learning.LearningTestWorld;

namespace Zayra.Api.Tests.Learning;

/// <summary>
/// Lifecycle edges: multi-course renewal, sweep paging and notice routing, separation of duties on HR record decisions,
/// revocation reopening the learning, leavers, effective dates, archived paths, blocked links and the tenant's own date.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class LearningLifecycleTests(PostgresFixture fx)
{
    private static async Task PassAsync(LearningTestWorld w, int employee, Guid enrollmentId)
    {
        var (scope, me) = w.Employee(employee);
        await using (scope)
        {
            var progress = Get<LearningProgressService>(scope);
            var attempt = await progress.StartAttemptAsync(me, employee, enrollmentId, default);
            (await progress.SubmitAttemptAsync(me, employee, attempt.Id, new SubmitAttemptInput(Correct()), default)).Status
                .Should().Be(LearningAttemptStatuses.Passed);
        }
    }

    private static async Task AcknowledgeAsync(LearningTestWorld w, int employee, Guid enrollmentId)
    {
        var (scope, me) = w.Employee(employee);
        await using (scope)
        {
            var progress = Get<LearningProgressService>(scope);
            await progress.LaunchAsync(me, employee, enrollmentId, "https://api.kynexone.test", null, default);
            await progress.AcknowledgeAsync(me, employee, enrollmentId, default);
        }
    }

    private static async Task<LearningSweepResult> SweepAsync(LearningTestWorld w)
    {
        await using var scope = w.System();
        return await Get<LearningSweepService>(scope).RunTenantAsync(w.TenantA, default);
    }

    [Fact]
    public async Task RenewingAPathWithTwoCertificateCourses_SupersedesBoth_AndNothingExpires()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("TWO",
        [
            Lesson("C1", LearningCompletionRules.Assessment, validityMonths: 12),
            Lesson("C2", LearningCompletionRules.Assessment, validityMonths: 12),
        ]);
        await w.AssignAsync(pathId, [w.Ops1]);
        await PassAsync(w, w.Ops1, (await w.EnrollmentAsync(w.Ops1, pathId, 1)).Id);
        await PassAsync(w, w.Ops1, (await w.EnrollmentAsync(w.Ops1, pathId, 2)).Id);
        List<Guid> originals;
        await using (var db = w.Db())
            originals = await db.LearningCredentials.AsNoTracking().Where(c => c.EmployeeId == w.Ops1).Select(c => c.Id).ToListAsync();
        originals.Should().HaveCount(2);

        w.Clock.Advance(TimeSpan.FromDays(345));
        var window = await SweepAsync(w);
        window.ExpiringNotices.Should().Be(2);
        window.RenewalsOpened.Should().Be(1, "both credentials are renewed by one new cycle of the path");
        await PassAsync(w, w.Ops1, (await w.EnrollmentAsync(w.Ops1, pathId, 1, cycle: 2)).Id);
        await PassAsync(w, w.Ops1, (await w.EnrollmentAsync(w.Ops1, pathId, 2, cycle: 2)).Id);

        w.Clock.Advance(TimeSpan.FromDays(30));
        (await SweepAsync(w)).Expired.Should().Be(0);

        await using var check = w.Db();
        var all = await check.LearningCredentials.AsNoTracking().Where(c => c.EmployeeId == w.Ops1).ToListAsync();
        all.Where(c => originals.Contains(c.Id)).Should().OnlyContain(c => c.Status == LearningCredentialStatuses.Superseded);
        all.Where(c => !originals.Contains(c.Id)).Should().HaveCount(2).And.OnlyContain(c => c.Status == LearningCredentialStatuses.Active);
        all.Where(c => !originals.Contains(c.Id)).Select(c => c.SupersedesCredentialId).Should().BeEquivalentTo(originals);
        (await check.NotificationDeliveries.AsNoTracking().CountAsync(d => d.TenantId == w.TenantA && d.EventCode == LearningEventCodes.Expired))
            .Should().Be(0);
    }

    [Fact]
    public async Task Sweep_PagesThroughMoreRowsThanAPage_AndNeverWritesATenantWideNotice()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("PAGE", [Lesson("P1", validityMonths: 12)]);
        int[] everyone = [w.Ops1, w.Ops2, w.Sales1, w.Sales2, w.Manager];
        (await w.AssignAsync(pathId, everyone, w.Clock.Today.AddDays(3))).Created.Should().Be(5);

        // Page size is 2 in the test world. Ops1 and the manager have logins; Ops2 has none but a manager who does;
        // Sales1/Sales2 have neither, so they get no notice at all (and stay pending for a later run).
        var due = await SweepAsync(w);
        due.DueReminders.Should().Be(3);
        due.RoutedToManager.Should().Be(1);
        due.NoRecipient.Should().Be(2);
        due.Failures.Should().Be(0);
        // Nobody to tell is remembered: not retried on the next hourly run, retried after a day.
        (await SweepAsync(w)).NoRecipient.Should().Be(0);
        w.Clock.Advance(TimeSpan.FromHours(25));
        (await SweepAsync(w)).NoRecipient.Should().Be(2);
        await using (var db = w.Db())
        {
            (await db.Notifications.AsNoTracking().CountAsync(n => n.TenantId == w.TenantA && n.UserId == null)).Should().Be(0);
            var routed = await db.Notifications.AsNoTracking().Where(n => n.TenantId == w.TenantA && n.UserId == w.ManagerUser).ToListAsync();
            routed.Should().Contain(n => n.Title.Contains("Ola Ops"));
            (await db.LearningAssignments.AsNoTracking().CountAsync(a => a.PathId == pathId && a.DueReminderSentAtUtc == null)).Should().Be(2);
        }

        foreach (var id in everyone) await AcknowledgeAsync(w, id, (await w.EnrollmentAsync(id, pathId, 1)).Id);
        w.Clock.Advance(TimeSpan.FromDays(370));
        var expiry = await SweepAsync(w);
        expiry.Expired.Should().Be(5);
        expiry.Failures.Should().Be(0);
        await using var after = w.Db();
        (await after.LearningCredentials.AsNoTracking().CountAsync(c => c.PathId == pathId && c.Status == LearningCredentialStatuses.Expired)).Should().Be(5);
        (await after.Notifications.AsNoTracking().CountAsync(n => n.TenantId == w.TenantA && n.UserId == null)).Should().Be(0);
    }

    [Fact]
    public async Task HrCannotDecideOnTheirOwnRecord_AndACorrectionCannotLeaveAValidCertificateBelowThePassMark()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("SELF", [Lesson("SC", LearningCompletionRules.Assessment, validityMonths: 12, maxAttempts: 1)]);
        await w.AssignAsync(pathId, [w.Ops1]);
        var enrollment = await w.EnrollmentAsync(w.Ops1, pathId, 1);
        await PassAsync(w, w.Ops1, enrollment.Id);
        Guid credentialId;
        await using (var db = w.Db())
            credentialId = (await db.LearningCredentials.AsNoTracking().SingleAsync(c => c.EnrollmentId == enrollment.Id)).Id;

        await using (var selfHr = w.As(Principal(w.TenantA, w.Ops1User, w.Ops1, HrPermissions)))
        {
            var actor = await ActorAsync(selfHr, w.TenantA);
            var progress = Get<LearningProgressService>(selfHr);
            var calls = new Func<Task>[]
            {
                () => progress.RevokeAsync(actor, credentialId, new RevokeCredentialInput("Own record", w.Clock.Today.AddDays(5)), default),
                () => progress.CorrectAsync(actor, credentialId, new CorrectCredentialInput("Own record", null, 100m), default),
                () => progress.ResetAttemptsAsync(actor, enrollment.Id, "Own record", default),
                () => progress.VerifyAsync(actor, enrollment.Id, default),
                () => progress.RejectVerificationAsync(actor, enrollment.Id, "Own record", default),
            };
            foreach (var call in calls)
            {
                var error = (await call.Should().ThrowAsync<LearningException>()).Which;
                error.Status.Should().Be(403);
                error.Code.Should().Be("self_decision_forbidden");
            }
        }

        await using var hr = w.As(w.HrA());
        var hrActor = await ActorAsync(hr, w.TenantA);
        var hrProgress = Get<LearningProgressService>(hr);
        var below = () => hrProgress.CorrectAsync(hrActor, credentialId, new CorrectCredentialInput("Re-marked", null, 50m), default);
        (await below.Should().ThrowAsync<LearningException>()).Which.Code.Should().Be("score_below_pass_mark");
        (await hrProgress.CorrectAsync(hrActor, credentialId, new CorrectCredentialInput("Re-marked", null, 75m), default)).ScorePercent.Should().Be(75m);
    }

    [Fact]
    public async Task Revocation_ReopensTheLearningWithHrsDueDate_AndReCompletionIssuesANewCertificate()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("RV", [Lesson("RV1", LearningCompletionRules.Assessment, validityMonths: 12, maxAttempts: 1)]);
        await w.AssignAsync(pathId, [w.Ops1]);
        var enrollment = await w.EnrollmentAsync(w.Ops1, pathId, 1);
        await PassAsync(w, w.Ops1, enrollment.Id);
        Guid oldId;
        await using (var db = w.Db())
            oldId = (await db.LearningCredentials.AsNoTracking().SingleAsync(c => c.EnrollmentId == enrollment.Id)).Id;

        await using (var hr = w.As(w.HrA()))
        {
            var actor = await ActorAsync(hr, w.TenantA);
            var progress = Get<LearningProgressService>(hr);
            var noDate = () => progress.RevokeAsync(actor, oldId, new RevokeCredentialInput("Incident 118", null), default);
            (await noDate.Should().ThrowAsync<LearningException>()).Which.Status.Should().Be(400);
            var past = () => progress.RevokeAsync(actor, oldId, new RevokeCredentialInput("Incident 118", w.Clock.Today.AddDays(-1)), default);
            (await past.Should().ThrowAsync<LearningException>()).Which.Status.Should().Be(400);
            (await progress.RevokeAsync(actor, oldId, new RevokeCredentialInput("Incident 118", w.Clock.Today.AddDays(14)), default)).Status
                .Should().Be(LearningCredentialStatuses.Revoked);
        }

        await using (var db = w.Db())
        {
            var reopened = await db.LearningEnrollments.AsNoTracking().SingleAsync(e => e.Id == enrollment.Id);
            reopened.Status.Should().NotBe(LearningEnrollmentStatuses.Completed);
            reopened.CompletedAtUtc.Should().BeNull();
            reopened.AttemptAllowance.Should().Be(reopened.AttemptsUsed + 1, "a fresh set of attempts");
            var assignment = await db.LearningAssignments.AsNoTracking().SingleAsync(a => a.Id == enrollment.AssignmentId);
            LearningAssignmentStatuses.Active.Should().Contain(assignment.Status);
            assignment.DueDate.Should().Be(w.Clock.Today.AddDays(14));
            (await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "learning.assignment.reopened" && a.EntityId == assignment.Id.ToString())).Should().Be(1);
        }

        await PassAsync(w, w.Ops1, enrollment.Id);
        await using var after = w.Db();
        var credentials = await after.LearningCredentials.AsNoTracking().Where(c => c.EnrollmentId == enrollment.Id).ToListAsync();
        credentials.Single(c => c.Id == oldId).Status.Should().Be(LearningCredentialStatuses.Revoked);
        credentials.Single(c => c.Id != oldId).Status.Should().Be(LearningCredentialStatuses.Active);
        (await after.LearningAssignments.AsNoTracking().SingleAsync(a => a.Id == enrollment.AssignmentId)).Status.Should().Be(LearningAssignmentStatuses.Completed);
    }

    [Fact]
    public async Task Leavers_OpenAssignmentsAreClosedByTheSweep_AndLeaveOverdueCountsAndNotices()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("LEFT", [Lesson("L1")]);
        await w.AssignAsync(pathId, [w.Ops2, w.Sales1], w.Clock.Today.AddDays(5));
        await using (var db = w.Db())
        {
            var leaver = await db.Employees.SingleAsync(e => e.Id == w.Sales1);
            leaver.Status = EmployeeStatuses.Terminated;
            await db.SaveChangesAsync();
        }
        w.Clock.Advance(TimeSpan.FromDays(10));

        await using (var hr = w.As(w.HrA()))
            (await Get<LearningReportService>(hr).SummaryAsync(await ActorAsync(hr, w.TenantA), default)).Kpis.Single(k => k.Key == "overdue").Value
                .Should().Be(1, "a leaver is not counted as overdue");

        var first = await SweepAsync(w);
        first.ClosedForLeavers.Should().Be(1);
        first.OverdueNotices.Should().Be(1);
        (await SweepAsync(w)).ClosedForLeavers.Should().Be(0);

        await using var check = w.Db();
        var closed = await check.LearningAssignments.AsNoTracking().SingleAsync(a => a.PathId == pathId && a.EmployeeId == w.Sales1);
        closed.Status.Should().Be(LearningAssignmentStatuses.Cancelled);
        closed.CancelReason.Should().Be("Employee left");
        closed.OverdueNoticeSentAtUtc.Should().BeNull();
        (await check.LearningEnrollments.AsNoTracking().Where(e => e.AssignmentId == closed.Id).Select(e => e.Status).ToListAsync())
            .Should().OnlyContain(s => s == LearningEnrollmentStatuses.Cancelled);
        (await check.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "learning.assignment.cancelled" && a.EntityId == closed.Id.ToString())).Should().Be(1);
    }

    [Fact]
    public async Task Overdue_CountsSuspendedPeopleLabelled_AndInvitedOnlyOnceTheyHaveStarted()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("STAT", [Lesson("S1")]);
        await w.AssignAsync(pathId, [w.Ops2, w.Sales1, w.Sales2], w.Clock.Today.AddDays(2));
        await using (var db = w.Db())
        {
            (await db.Employees.SingleAsync(e => e.Id == w.Ops2)).Status = EmployeeStatuses.Suspended;
            var notStarted = await db.Employees.SingleAsync(e => e.Id == w.Sales1);
            notStarted.Status = EmployeeStatuses.Invited;
            notStarted.JoiningDate = DateTime.SpecifyKind(w.Clock.Today.AddDays(60).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            var started = await db.Employees.SingleAsync(e => e.Id == w.Sales2);
            started.Status = EmployeeStatuses.Invited;
            started.JoiningDate = DateTime.SpecifyKind(w.Clock.Today.AddDays(-1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            await db.SaveChangesAsync();
        }
        w.Clock.Advance(TimeSpan.FromDays(5));

        await using var hr = w.As(w.HrA());
        var actor = await ActorAsync(hr, w.TenantA);
        var reports = Get<LearningReportService>(hr);
        (await reports.SummaryAsync(actor, default)).Kpis.Single(k => k.Key == "overdue").Value.Should().Be(2);
        var overdue = await reports.StatusMatrixAsync(actor, pathId, null, null, true, default);
        overdue.Select(r => r.EmployeeId).Should().BeEquivalentTo([w.Ops2, w.Sales2]);
        overdue.Single(r => r.EmployeeId == w.Ops2).EmployeeStatus.Should().Be(EmployeeStatuses.Suspended);
        (await reports.StatusMatrixAsync(actor, pathId, null, null, null, default)).Single(r => r.EmployeeId == w.Sales1).Overdue.Should().BeFalse();
        (await reports.CompletionAsync(actor, "path", default)).Single(r => r.GroupId == pathId).Overdue.Should().Be(2);
        (await SweepAsync(w)).ClosedForLeavers.Should().Be(0, "suspended and invited people have not left");
    }

    [Fact]
    public async Task OptionalTraining_GetsNoRemindersOrOverdueNotices_AndIsNeverCountedOverdue()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("OPT", [Lesson("O1")]);
        await w.AssignAsync(pathId, [w.Ops1], w.Clock.Today.AddDays(3));
        await using (var hr = w.As(w.HrA()))
            await Get<LearningAssignmentService>(hr).AssignAsync(await ActorAsync(hr, w.TenantA),
                new AssignInput(pathId, "Employee", [w.Manager], null, w.Clock.Today.AddDays(3), null, false), default);

        (await SweepAsync(w)).DueReminders.Should().Be(1, "only the mandatory assignment is reminded");
        w.Clock.Advance(TimeSpan.FromDays(6));
        (await SweepAsync(w)).OverdueNotices.Should().Be(1);

        await using (var hr = w.As(w.HrA()))
        {
            var actor = await ActorAsync(hr, w.TenantA);
            var reports = Get<LearningReportService>(hr);
            (await reports.SummaryAsync(actor, default)).Kpis.Single(k => k.Key == "overdue").Value.Should().Be(1);
            (await reports.StatusMatrixAsync(actor, pathId, null, null, true, default)).Select(r => r.EmployeeId).Should().Equal(w.Ops1);
            (await reports.StatusMatrixAsync(actor, pathId, null, null, null, default)).Single(r => r.EmployeeId == w.Manager).Overdue.Should().BeFalse();
        }
        var (scope, _) = w.Employee(w.Manager);
        await using (scope)
            (await Get<LearningProgressService>(scope).MyLearningAsync(w.TenantA, w.Manager, default)).Assignments.Single().IsOverdue.Should().BeFalse();
        await using var db = w.Db();
        var managerAssignment = await db.LearningAssignments.AsNoTracking().SingleAsync(a => a.PathId == pathId && a.EmployeeId == w.Manager);
        (await db.NotificationDeliveries.AsNoTracking().CountAsync(d => d.TenantId == w.TenantA && d.EntityId == managerAssignment.Id.ToString())).Should().Be(0);
    }

    [Fact]
    public async Task TheLockedKpiCountsAssignments_AndEqualsItsDrillDownRows()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("LOCK2",
        [
            Lesson("LK1", LearningCompletionRules.Assessment, maxAttempts: 1),
            Lesson("LK2", LearningCompletionRules.Assessment, maxAttempts: 1),
        ]);
        await w.AssignAsync(pathId, [w.Ops1]);
        foreach (var sequence in new[] { 1, 2 })
        {
            var enrollment = await w.EnrollmentAsync(w.Ops1, pathId, sequence);
            var (scope, me) = w.Employee(w.Ops1);
            await using (scope)
            {
                var progress = Get<LearningProgressService>(scope);
                var attempt = await progress.StartAttemptAsync(me, w.Ops1, enrollment.Id, default);
                (await progress.SubmitAttemptAsync(me, w.Ops1, attempt.Id, new SubmitAttemptInput(Wrong()), default)).EnrollmentStatus
                    .Should().Be(LearningEnrollmentStatuses.Locked);
            }
        }
        await using var hr = w.As(w.HrA());
        var actor = await ActorAsync(hr, w.TenantA);
        var reports = Get<LearningReportService>(hr);
        var rows = await reports.LockedAsync(actor, default);
        rows.Should().ContainSingle();
        (await reports.SummaryAsync(actor, default)).Kpis.Single(k => k.Key == "locked").Value.Should().Be(rows.Count);
    }

    [Fact]
    public async Task APublishRetriedAfterATransientCommitFailure_LeavesOnePublishedVersion()
    {
        await using var w = await CreateAsync(fx);
        Guid draftId;
        await using (var hr = w.As(w.HrA()))
        {
            var actor = await ActorAsync(hr, w.TenantA);
            var catalog = Get<LearningCatalogService>(hr);
            var v1 = await catalog.CreateCourseAsync(actor, Lesson("RETRY"), default);
            await catalog.PublishCourseAsync(actor, v1.Id, null, default);
            draftId = (await catalog.NewVersionAsync(actor, v1.Id, default)).Id;
        }

        var fault = new TransientCommitFault();
        w.ExtraInterceptors = [fault];
        await using (var hr = w.As(w.HrA()))
        {
            var published = await Get<LearningCatalogService>(hr).PublishCourseAsync(await ActorAsync(hr, w.TenantA), draftId, null, default);
            published.Status.Should().Be(LearningCourseStatuses.Published);
        }
        w.ExtraInterceptors = [];
        fault.Faults.Should().BeGreaterThan(0, "the first COMMIT failed and the delegate ran again");

        await using var db = w.Db();
        var versions = await db.LearningCourses.AsNoTracking().Where(c => c.TenantId == w.TenantA && c.Code == "RETRY")
            .OrderBy(c => c.VersionNumber).ToListAsync();
        versions.Select(c => c.Status).Should().Equal(LearningCourseStatuses.Retired, LearningCourseStatuses.Published);
        versions[1].PublishedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ASubmitWhoseCommitLandsButTheConnectionDrops_StillRecordsItsAuditRowsExactlyOnce()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("LOSTAUDIT", [Lesson("LA1", LearningCompletionRules.Assessment, validityMonths: 12)]);
        await w.AssignAsync(pathId, [w.Ops1]);
        var enrollment = await w.EnrollmentAsync(w.Ops1, pathId, 1);
        Guid attemptId;
        var (start, me) = w.Employee(w.Ops1);
        await using (start)
            attemptId = (await Get<LearningProgressService>(start).StartAttemptAsync(me, w.Ops1, enrollment.Id, default)).Id;

        // The COMMIT reaches the server and succeeds; the client then loses the connection, so the execution strategy
        // runs the delegate again — which finds the attempt already scored and takes the stored-result path.
        var fault = new CommittedThenDisconnectFault();
        w.ExtraInterceptors = [fault];
        var (scope, actor) = w.Employee(w.Ops1);
        var progress = Get<LearningProgressService>(scope);
        w.ExtraInterceptors = [];
        AttemptResultDto result;
        await using (scope)
            result = await progress.SubmitAttemptAsync(actor, w.Ops1, attemptId, new SubmitAttemptInput(Correct()), default);
        fault.Faults.Should().BeGreaterThan(0, "the first COMMIT landed and then the connection dropped");
        result.Status.Should().Be(LearningAttemptStatuses.Passed);
        result.CredentialId.Should().NotBeNull();

        // A second, client-side retry of the same submit changes nothing and adds no audit rows.
        var (again, actorAgain) = w.Employee(w.Ops1);
        await using (again)
            (await Get<LearningProgressService>(again).SubmitAttemptAsync(actorAgain, w.Ops1, attemptId, new SubmitAttemptInput(Correct()), default))
                .CredentialId.Should().Be(result.CredentialId);

        await using var db = w.Db();
        int Count(string action, Guid id) => db.AuditLogs.AsNoTracking().Count(a => a.TenantId == w.TenantA && a.Action == action && a.EntityId == id.ToString());
        Count("learning.attempt.passed", attemptId).Should().Be(1);
        Count("learning.enrollment.completed", enrollment.Id).Should().Be(1);
        Count("learning.credential.issued", result.CredentialId!.Value).Should().Be(1);
        Count("learning.assignment.completed", enrollment.AssignmentId).Should().Be(1);
    }

    /// <summary>The first COMMIT succeeds on the server, then the client sees a transient connection loss.</summary>
    private sealed class CommittedThenDisconnectFault : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public int Faults;
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
            Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref Faults) == 1)
                throw new Npgsql.NpgsqlException("Simulated connection loss after COMMIT", new System.IO.IOException("connection reset by peer"));
            return base.TransactionCommittedAsync(transaction, eventData, ct);
        }
    }

    /// <summary>A transient connection loss on the first COMMIT (before it reaches the server); later commits go through.</summary>
    private sealed class TransientCommitFault : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public int Faults;
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref Faults) == 1)
                throw new Npgsql.NpgsqlException("Simulated connection loss during COMMIT", new System.IO.IOException("connection reset by peer"));
            return base.TransactionCommittingAsync(transaction, eventData, result, ct);
        }
    }

    [Fact]
    public async Task APathOutsideItsEffectiveDates_CannotBeAssigned()
    {
        await using var w = await CreateAsync(fx);
        await using var hr = w.As(w.HrA());
        var actor = await ActorAsync(hr, w.TenantA);
        var catalog = Get<LearningCatalogService>(hr);
        var course = await catalog.CreateCourseAsync(actor, Lesson("EFF"), default);
        await catalog.PublishCourseAsync(actor, course.Id, null, default);
        var path = await catalog.CreatePathAsync(actor, new PathInput("FUTURE", "Future path", null, null, null, w.Clock.Today.AddDays(10), null, 30,
            [new PathItemInput(course.Id, true, null)], null), default);
        await catalog.ActivatePathAsync(actor, path.Id, default);
        var assign = () => Get<LearningAssignmentService>(hr).AssignAsync(actor, new AssignInput(path.Id, "Employee", [w.Ops1], null, null, null, true), default);
        (await assign.Should().ThrowAsync<LearningException>()).Which.Code.Should().Be("path_not_effective");
    }

    [Fact]
    public async Task AnArchivedPath_StillGetsItsExpiringNotice_ButNoRenewalCycle_AndIsNotRetried()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("ARCH", [Lesson("A1", validityMonths: 12)]);
        await w.AssignAsync(pathId, [w.Ops1]);
        var enrollment = await w.EnrollmentAsync(w.Ops1, pathId, 1);
        await AcknowledgeAsync(w, w.Ops1, enrollment.Id);
        await using (var hr = w.As(w.HrA()))
            await Get<LearningCatalogService>(hr).ArchivePathAsync(await ActorAsync(hr, w.TenantA), pathId, default);

        w.Clock.Advance(TimeSpan.FromDays(345));
        var first = await SweepAsync(w);
        first.RenewalsOpened.Should().Be(0);
        first.ExpiringNotices.Should().Be(1);
        first.Failures.Should().Be(0);
        (await SweepAsync(w)).ExpiringNotices.Should().Be(0);
        await using var db = w.Db();
        var credential = await db.LearningCredentials.AsNoTracking().SingleAsync(c => c.EnrollmentId == enrollment.Id);
        credential.RenewalAssignmentId.Should().BeNull();
        credential.ExpiringNoticeSentAtUtc.Should().NotBeNull();
        (await db.LearningAssignments.AsNoTracking().CountAsync(a => a.PathId == pathId)).Should().Be(1);
    }

    [Fact]
    public async Task ALinkBlockedByItsPrerequisite_DoesNotRevealItsAddress()
    {
        await using var w = await CreateAsync(fx);
        var link = new CourseInput("LINK1", "Link course", null, null, null, 5, LearningContentKinds.Link, null, "https://learn.example.test/fire",
            null, null, null, LearningCompletionRules.Acknowledgement, null, null, null, false, null, false, null);
        var (pathId, _) = await w.PublishedPathAsync("LNK", [Lesson("INTRO"), link], chainPrerequisites: true);
        await w.AssignAsync(pathId, [w.Ops1]);
        var intro = await w.EnrollmentAsync(w.Ops1, pathId, 1);
        var linked = await w.EnrollmentAsync(w.Ops1, pathId, 2);

        var (scope, _) = w.Employee(w.Ops1);
        await using (scope)
        {
            var blocked = await Get<LearningProgressService>(scope).EnrollmentDetailAsync(w.TenantA, w.Ops1, linked.Id, default);
            blocked.Item.IsBlocked.Should().BeTrue();
            blocked.LinkUrl.Should().BeNull();
        }
        await AcknowledgeAsync(w, w.Ops1, intro.Id);
        var (again, _) = w.Employee(w.Ops1);
        await using (again)
            (await Get<LearningProgressService>(again).EnrollmentDetailAsync(w.TenantA, w.Ops1, linked.Id, default)).LinkUrl
                .Should().Be("https://learn.example.test/fire");
    }

    [Fact]
    public async Task ASubmissionThatDoesNotFitTheAssessment_IsRefused_AndUsesNoAttempt()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("ANS", [Lesson("QZ", LearningCompletionRules.Assessment)]);
        await w.AssignAsync(pathId, [w.Ops1]);
        var enrollment = await w.EnrollmentAsync(w.Ops1, pathId, 1);
        var (scope, me) = w.Employee(w.Ops1);
        await using (scope)
        {
            var progress = Get<LearningProgressService>(scope);
            var attempt = await progress.StartAttemptAsync(me, w.Ops1, enrollment.Id, default);
            var bad = new[]
            {
                new Dictionary<string, List<string>> { ["q9"] = ["q9o1"] },
                new Dictionary<string, List<string>> { ["q1"] = ["q1o1", "q1o2", "q1o3", "q1o1"] },
                new Dictionary<string, List<string>> { ["q1"] = [new string('x', 33)] },
                new Dictionary<string, List<string>> { ["q1"] = ["q1o2"], ["q2"] = ["true"], ["q3"] = ["x"] },
            };
            foreach (var answers in bad)
            {
                var submit = () => progress.SubmitAttemptAsync(me, w.Ops1, attempt.Id, new SubmitAttemptInput(answers), default);
                (await submit.Should().ThrowAsync<LearningException>()).Which.Status.Should().Be(400);
            }
            (await progress.SubmitAttemptAsync(me, w.Ops1, attempt.Id, new SubmitAttemptInput(Correct()), default)).AttemptNo.Should().Be(1);
        }
    }

    [Fact]
    public async Task DueDatesUseTheTenantsOwnDate_NotUtc()
    {
        await using var w = await CreateAsync(fx);
        await using (var db = w.Db())
        {
            var setting = await db.TenantLocalizationSettings.FirstOrDefaultAsync(l => l.TenantId == w.TenantA);
            if (setting is null) db.TenantLocalizationSettings.Add(setting = new TenantLocalizationSetting { TenantId = w.TenantA });
            setting.CountryCode = "SA";
            await db.SaveChangesAsync();
        }
        var (pathId, _) = await w.PublishedPathAsync("TZ", [Lesson("T1")]);
        // 23:00 UTC on 1 Oct is 02:00 on 2 Oct in Riyadh.
        w.Clock.Advance(TimeSpan.FromHours(15));
        await using var hr = w.As(w.HrA());
        var result = await Get<LearningAssignmentService>(hr).AssignAsync(await ActorAsync(hr, w.TenantA),
            new AssignInput(pathId, "Employee", [w.Ops1], null, null, 1, true), default);
        await using var check = w.Db();
        (await check.LearningAssignments.AsNoTracking().SingleAsync(a => a.Id == result.Results.Single().AssignmentId)).DueDate
            .Should().Be(new DateOnly(2026, 10, 3));
    }
}
