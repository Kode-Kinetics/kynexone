using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Learning;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Learning.LearningTestWorld;

namespace Zayra.Api.Tests.Learning;

/// <summary>
/// LRN golden journey on real PostgreSQL: assign → lesson acknowledged → assessment failed → retaken and passed →
/// credential → time passes → renewal cycle opened and notified → credential expires → renewal completed → the old
/// credential is superseded, history kept. LRN-01/03/04/05/06/07/08/09/10/15.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class LearningJourneyTests(PostgresFixture fx)
{
    [Fact]
    public async Task GoldenJourney_AssignLearnFailPassCertifyExpireRenewSupersede()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, courses) = await w.PublishedPathAsync("SAFETY",
        [
            Lesson("INDUCTION"),
            Lesson("FIRE", LearningCompletionRules.Assessment, validityMonths: 12, maxAttempts: 3, passMark: 70m),
        ], chainPrerequisites: true);

        // LRN-03: ≥3 employees across two departments; the bulk result reports every outcome.
        var assigned = await w.AssignAsync(pathId, [w.Ops1, w.Ops2, w.Sales1, w.Sales2], w.Clock.Today.AddDays(20));
        assigned.Created.Should().Be(4);
        assigned.Skipped.Should().Be(0);
        assigned.Refused.Should().Be(0);
        await using (var db = w.Db())
        {
            var rows = await db.LearningAssignments.AsNoTracking().Where(a => a.TenantId == w.TenantA && a.PathId == pathId).ToListAsync();
            rows.Select(r => r.DepartmentId).Distinct().Should().BeEquivalentTo(new Guid?[] { w.DeptOps, w.DeptSales });
            rows.Should().OnlyContain(r => r.CompanyId == w.CompanyA1 && r.Status == LearningAssignmentStatuses.Assigned && r.Cycle == 1);
            (await db.AuditLogs.AsNoTracking().CountAsync(a => a.TenantId == w.TenantA && a.Action == "learning.assignment.created")).Should().Be(4);
        }

        var (scope, me) = w.Employee(w.Ops1);
        await using (scope)
        {
            var progress = Get<LearningProgressService>(scope);
            // LRN-04: the employee sees the path, its due date and 0% progress; the assessment item is blocked by its prerequisite.
            var mine = await progress.MyLearningAsync(w.TenantA, w.Ops1, default);
            var assignment = mine.Assignments.Single();
            assignment.DueDate.Should().Be(w.Clock.Today.AddDays(20));
            assignment.ProgressPercent.Should().Be(0m);
            assignment.Items[0].NextAction.Should().Be(LearningNextActions.Open);
            assignment.Items[1].NextAction.Should().Be(LearningNextActions.Blocked);

            // LRN-05: launch is evidence of launch only.
            var lesson = assignment.Items[0];
            var launched = await progress.LaunchAsync(me, w.Ops1, lesson.Id, "https://api.kynexone.test", null, default);
            launched.Kind.Should().Be(LearningContentKinds.Lesson);
            launched.LessonHtml.Should().Contain("<strong>nearest exit</strong>").And.NotContain("<script");
            launched.Enrollment.Item.Status.Should().Be(LearningEnrollmentStatuses.InProgress);
            launched.Enrollment.Item.CompletedAtUtc.Should().BeNull();
            var acknowledged = await progress.AcknowledgeAsync(me, w.Ops1, lesson.Id, default);
            acknowledged.Item.Status.Should().Be(LearningEnrollmentStatuses.Completed);
            acknowledged.Item.CompletionSource.Should().Be(LearningCompletionSources.Acknowledgement);

            // LRN-06/07: fail, then pass on the retake.
            var fire = (await progress.MyLearningAsync(w.TenantA, w.Ops1, default)).Assignments.Single().Items[1];
            fire.NextAction.Should().Be(LearningNextActions.Open);
            await progress.LaunchAsync(me, w.Ops1, fire.Id, "https://api.kynexone.test", null, default);
            var first = await progress.StartAttemptAsync(me, w.Ops1, fire.Id, default);
            first.Questions.Should().HaveCount(2);
            var failed = await progress.SubmitAttemptAsync(me, w.Ops1, first.Id, new SubmitAttemptInput(Wrong()), default);
            failed.Status.Should().Be(LearningAttemptStatuses.Failed);
            failed.ScorePercent.Should().Be(0m);
            failed.AttemptsRemaining.Should().Be(2);
            failed.CredentialId.Should().BeNull();

            var second = await progress.StartAttemptAsync(me, w.Ops1, fire.Id, default);
            second.AttemptNo.Should().Be(2);
            var passed = await progress.SubmitAttemptAsync(me, w.Ops1, second.Id, new SubmitAttemptInput(Correct()), default);
            passed.Status.Should().Be(LearningAttemptStatuses.Passed);
            passed.ScorePercent.Should().Be(100m);
            passed.EnrollmentStatus.Should().Be(LearningEnrollmentStatuses.Completed);
            passed.CertificateNumber.Should().MatchRegex(@"^LRN-2026-[A-Z2-9]{8}$");

            // LRN-08: transcript and certificate.
            var after = await progress.MyLearningAsync(w.TenantA, w.Ops1, default);
            after.Assignments.Single().Status.Should().Be(LearningAssignmentStatuses.Completed);
            after.Assignments.Single().ProgressPercent.Should().Be(100m);
            after.Transcript.Should().HaveCount(2);
            after.Transcript.Should().Contain(t => t.CompletionSource == LearningCompletionSources.Acknowledgement && t.CredentialId != null);
            var certificate = after.Credentials.Single(c => c.CourseCode == "FIRE");
            certificate.ExpiresOn.Should().Be(w.Clock.Today.AddMonths(12));
            certificate.Status.Should().Be(LearningCredentialStatuses.Active);
        }

        Guid oldCredentialId;
        await using (var db = w.Db())
        {
            oldCredentialId = (await db.LearningCredentials.AsNoTracking().SingleAsync(c => c.EmployeeId == w.Ops1 && c.CourseCode == "FIRE")).Id;
            var actions = await db.AuditLogs.AsNoTracking().Where(a => a.TenantId == w.TenantA).Select(a => a.Action).ToListAsync();
            actions.Should().Contain(["learning.attempt.failed", "learning.attempt.passed", "learning.enrollment.completed",
                "learning.credential.issued", "learning.assignment.completed", "learning.content.opened"]);
        }

        // LRN-10: 11 months 10 days later the credential is inside the 30-day renewal window.
        w.Clock.Advance(TimeSpan.FromDays(345));
        await using (var sweepScope = w.System())
        {
            var result = await Get<LearningSweepService>(sweepScope).RunTenantAsync(w.TenantA, default);
            result.RenewalsOpened.Should().Be(1);
            result.ExpiringNotices.Should().Be(1);
            result.Failures.Should().Be(0);
        }
        Guid renewalId;
        await using (var db = w.Db())
        {
            var credential = await db.LearningCredentials.AsNoTracking().SingleAsync(c => c.Id == oldCredentialId);
            credential.RenewalAssignmentId.Should().NotBeNull();
            renewalId = credential.RenewalAssignmentId!.Value;
            var renewal = await db.LearningAssignments.AsNoTracking().SingleAsync(a => a.Id == renewalId);
            renewal.Cycle.Should().Be(2);
            renewal.Source.Should().Be(LearningAssignmentSources.Renewal);
            renewal.RenewalOfCredentialId.Should().Be(oldCredentialId);
            renewal.DueDate.Should().Be(credential.ExpiresOn);
            // A renewal repeats only what expires: the FIRE course, not the induction.
            (await db.LearningEnrollments.AsNoTracking().CountAsync(e => e.AssignmentId == renewalId)).Should().Be(1);
            (await db.NotificationDeliveries.AsNoTracking().CountAsync(d => d.TenantId == w.TenantA && d.EventCode == LearningEventCodes.Expiring
                && d.EntityId == oldCredentialId.ToString())).Should().BeGreaterThan(0);
        }

        // ... and past its expiry it is Expired (history kept), with one LEARNING_EXPIRED.
        w.Clock.Advance(TimeSpan.FromDays(25));
        await using (var sweepScope = w.System())
            (await Get<LearningSweepService>(sweepScope).RunTenantAsync(w.TenantA, default)).Expired.Should().Be(1);
        await using (var db = w.Db())
        {
            (await db.LearningCredentials.AsNoTracking().SingleAsync(c => c.Id == oldCredentialId)).Status.Should().Be(LearningCredentialStatuses.Expired);
            (await db.NotificationDeliveries.AsNoTracking().AnyAsync(d => d.EventCode == LearningEventCodes.Expired && d.EntityId == oldCredentialId.ToString()))
                .Should().BeTrue();
        }

        // The employee completes the renewal: a new credential that supersedes the old one.
        var (renewScope, renewer) = w.Employee(w.Ops1);
        await using (renewScope)
        {
            var progress = Get<LearningProgressService>(renewScope);
            var item = (await progress.MyLearningAsync(w.TenantA, w.Ops1, default)).Assignments.Single(a => a.Id == renewalId).Items.Single();
            await progress.LaunchAsync(renewer, w.Ops1, item.Id, "https://api.kynexone.test", null, default);
            var attempt = await progress.StartAttemptAsync(renewer, w.Ops1, item.Id, default);
            var result = await progress.SubmitAttemptAsync(renewer, w.Ops1, attempt.Id, new SubmitAttemptInput(Correct()), default);
            result.Status.Should().Be(LearningAttemptStatuses.Passed);
        }
        await using (var db = w.Db())
        {
            var chain = await db.LearningCredentials.AsNoTracking().Where(c => c.EmployeeId == w.Ops1 && c.CourseCode == "FIRE")
                .OrderBy(c => c.IssuedAtUtc).ToListAsync();
            chain.Should().HaveCount(2);
            chain[0].Status.Should().Be(LearningCredentialStatuses.Superseded);
            chain[0].SupersededAtUtc.Should().NotBeNull();
            chain[1].Status.Should().Be(LearningCredentialStatuses.Active);
            chain[1].SupersedesCredentialId.Should().Be(chain[0].Id);
            chain[1].SupersedeReason.Should().Be("Renewal");
            chain[1].ExpiresOn.Should().Be(w.Clock.Today.AddMonths(12));
            (await db.LearningAssignments.AsNoTracking().SingleAsync(a => a.Id == renewalId)).Status.Should().Be(LearningAssignmentStatuses.Completed);
        }
    }

    [Fact]
    public async Task PublishedVersionsAreImmutable_AndAssignmentsPinTheVersionPublishedAtTheTime()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, courses) = await w.PublishedPathAsync("PIN", [Lesson("POLICY")]);
        await w.AssignAsync(pathId, [w.Ops1]);

        await using var scope = w.As(w.HrA());
        var actor = await ActorAsync(scope, w.TenantA);
        var catalog = Get<LearningCatalogService>(scope);
        var edit = () => catalog.UpdateCourseAsync(actor, courses[0], Lesson("POLICY") with { Title = "Changed" }, default);
        (await edit.Should().ThrowAsync<LearningException>()).Which.Code.Should().Be("course_immutable");

        var draft = await catalog.NewVersionAsync(actor, courses[0], default);
        draft.VersionNumber.Should().Be(2);
        draft.Status.Should().Be(LearningCourseStatuses.Draft);
        var again = () => catalog.NewVersionAsync(actor, courses[0], default);
        (await again.Should().ThrowAsync<LearningException>()).Which.Code.Should().Be("draft_exists");
        await catalog.UpdateCourseAsync(actor, draft.Id, Lesson("POLICY") with { Title = "Policy v2", Version = draft.Version }, default);
        var v2 = await catalog.PublishCourseAsync(actor, draft.Id, null, default);
        v2.Versions.Single(v => v.VersionNumber == 1).Status.Should().Be(LearningCourseStatuses.Retired);

        await w.AssignAsync(pathId, [w.Ops2]);
        (await w.EnrollmentAsync(w.Ops1, pathId, 1)).CourseId.Should().Be(courses[0], "an enrollment stays pinned to the version it was assigned");
        (await w.EnrollmentAsync(w.Ops2, pathId, 1)).CourseId.Should().Be(v2.Id, "a new assignment pins the version published now");
    }

    [Fact]
    public async Task SavedRules_AssignNewMatches_OnTheSweep_Once()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("RULED", [Lesson("CODE")]);
        await using (var scope = w.As(w.HrA()))
        {
            var (rule, applied) = await Get<LearningCatalogService>(scope).CreateRuleAsync(await ActorAsync(scope, w.TenantA),
                new RuleInput(pathId, null, LearningRuleTargets.Department, w.DeptSales, 14, true), default);
            applied.Created.Should().Be(2);
            rule.TargetName.Should().Be("Sales");
        }

        // A new hire into Sales is picked up by the sweep; the existing two are not assigned again.
        int hire;
        await using (var db = w.Db())
        {
            var employee = new Employee
            {
                TenantId = w.TenantA, EmployeeCode = $"N-{Guid.NewGuid():N}"[..12], FullName = "Nadia New", Status = EmployeeStatuses.Active,
                CompanyId = w.CompanyA1, DepartmentId = w.DeptSales, JoiningDate = DateTime.UtcNow,
            };
            db.Employees.Add(employee);
            await db.SaveChangesAsync();
            hire = employee.Id;
        }
        await using (var sweep = w.System())
        {
            var result = await Get<LearningSweepService>(sweep).RunTenantAsync(w.TenantA, default);
            result.AssignedByRules.Should().Be(1);
        }
        await using (var sweep = w.System())
            (await Get<LearningSweepService>(sweep).RunTenantAsync(w.TenantA, default)).AssignedByRules.Should().Be(0);
        await using (var db = w.Db())
        {
            var rows = await db.LearningAssignments.AsNoTracking().Where(a => a.PathId == pathId).ToListAsync();
            rows.Should().HaveCount(3);
            rows.Should().ContainSingle(a => a.EmployeeId == hire && a.Source == LearningAssignmentSources.Rule && a.DueDate == w.Clock.Today.AddDays(14));
        }
    }
}
