using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Learning;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Learning.LearningTestWorld;

namespace Zayra.Api.Tests.Learning;

/// <summary>Assessment attempts, prerequisites, locking and HR's audited corrections (LRN-02/06/07/08/12/15).</summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class LearningAssessmentRecordsTests(PostgresFixture fx)
{
    [Fact]
    public async Task DoubleSubmit_SequentialAndConcurrent_ScoresOnce()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("DBL", [Lesson("QUIZ", LearningCompletionRules.Assessment, validityMonths: 6)]);
        await w.AssignAsync(pathId, [w.Ops1, w.Ops2]);

        // Sequential: the second submit returns the stored result and changes nothing.
        var enrollment = await w.EnrollmentAsync(w.Ops1, pathId, 1);
        var (scope, me) = w.Employee(w.Ops1);
        AttemptDto attempt;
        await using (scope)
        {
            var progress = Get<LearningProgressService>(scope);
            attempt = await progress.StartAttemptAsync(me, w.Ops1, enrollment.Id, default);
            // Starting again resumes the open attempt instead of burning another.
            (await progress.StartAttemptAsync(me, w.Ops1, enrollment.Id, default)).Id.Should().Be(attempt.Id);
            var first = await progress.SubmitAttemptAsync(me, w.Ops1, attempt.Id, new SubmitAttemptInput(Correct()), default);
            var second = await progress.SubmitAttemptAsync(me, w.Ops1, attempt.Id, new SubmitAttemptInput(Wrong()), default);
            second.Should().BeEquivalentTo(first, "a repeated submit is answered from what was stored, whatever it sends");
        }

        // Concurrent: two submits of one attempt from two requests.
        var other = await w.EnrollmentAsync(w.Ops2, pathId, 1);
        Guid attemptId;
        var (s2, e2) = w.Employee(w.Ops2);
        await using (s2) attemptId = (await Get<LearningProgressService>(s2).StartAttemptAsync(e2, w.Ops2, other.Id, default)).Id;
        async Task<AttemptResultDto> Submit()
        {
            var (s, a) = w.Employee(w.Ops2);
            await using (s) return await Get<LearningProgressService>(s).SubmitAttemptAsync(a, w.Ops2, attemptId, new SubmitAttemptInput(Correct()), default);
        }
        var results = await Task.WhenAll(Submit(), Submit());
        results.Should().OnlyContain(r => r.Status == LearningAttemptStatuses.Passed);
        results[0].CertificateNumber.Should().Be(results[1].CertificateNumber);

        await using var db = w.Db();
        foreach (var id in new[] { enrollment.Id, other.Id })
        {
            (await db.LearningAttempts.AsNoTracking().CountAsync(a => a.EnrollmentId == id)).Should().Be(1);
            (await db.LearningCredentials.AsNoTracking().CountAsync(c => c.EnrollmentId == id)).Should().Be(1);
            (await db.LearningEnrollments.AsNoTracking().SingleAsync(e => e.Id == id)).AttemptsUsed.Should().Be(1);
            (await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "learning.enrollment.completed" && a.EntityId == id.ToString())).Should().Be(1);
        }
    }

    [Fact]
    public async Task Prerequisites_AreEnforcedOnTheServer()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("PRE", [Lesson("FIRST"), Lesson("SECOND", LearningCompletionRules.Assessment)], chainPrerequisites: true);
        await w.AssignAsync(pathId, [w.Ops1]);
        var first = await w.EnrollmentAsync(w.Ops1, pathId, 1);
        var second = await w.EnrollmentAsync(w.Ops1, pathId, 2);

        var (scope, me) = w.Employee(w.Ops1);
        await using (scope)
        {
            var progress = Get<LearningProgressService>(scope);
            var launch = () => progress.LaunchAsync(me, w.Ops1, second.Id, "https://api.kynexone.test", null, default);
            (await launch.Should().ThrowAsync<LearningException>()).Which.Code.Should().Be("prerequisite_incomplete");
            var attempt = () => progress.StartAttemptAsync(me, w.Ops1, second.Id, default);
            (await attempt.Should().ThrowAsync<LearningException>()).Which.Code.Should().Be("prerequisite_incomplete");

            await progress.LaunchAsync(me, w.Ops1, first.Id, "https://api.kynexone.test", null, default);
            await progress.AcknowledgeAsync(me, w.Ops1, first.Id, default);
            (await progress.StartAttemptAsync(me, w.Ops1, second.Id, default)).Questions.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task ExhaustedAttempts_Lock_UntilHrResetsWithReason_Audited()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("LOCK", [Lesson("HARD", LearningCompletionRules.Assessment, maxAttempts: 2)]);
        await w.AssignAsync(pathId, [w.Sales1]);
        var enrollment = await w.EnrollmentAsync(w.Sales1, pathId, 1);

        var (scope, me) = w.Employee(w.Sales1);
        await using (scope)
        {
            var progress = Get<LearningProgressService>(scope);
            for (var i = 0; i < 2; i++)
            {
                var attempt = await progress.StartAttemptAsync(me, w.Sales1, enrollment.Id, default);
                await progress.SubmitAttemptAsync(me, w.Sales1, attempt.Id, new SubmitAttemptInput(Wrong()), default);
            }
            var locked = () => progress.StartAttemptAsync(me, w.Sales1, enrollment.Id, default);
            (await locked.Should().ThrowAsync<LearningException>()).Which.Code.Should().Be("enrollment_locked");
        }

        await using (var hr = w.As(w.HrA()))
        {
            var progress = Get<LearningProgressService>(hr);
            var actor = await ActorAsync(hr, w.TenantA);
            var noReason = () => progress.ResetAttemptsAsync(actor, enrollment.Id, " ", default);
            (await noReason.Should().ThrowAsync<LearningException>()).Which.Code.Should().Be("invalid_request");
            var view = await progress.ResetAttemptsAsync(actor, enrollment.Id, "Retraining session attended on 30 Sep", default);
            view.Items.Single().Status.Should().Be(LearningEnrollmentStatuses.InProgress);
            view.Items.Single().AttemptAllowance.Should().Be(4);
            var twice = () => progress.ResetAttemptsAsync(actor, enrollment.Id, "again", default);
            (await twice.Should().ThrowAsync<LearningException>()).Which.Code.Should().Be("enrollment_not_locked");
        }

        var (again, me2) = w.Employee(w.Sales1);
        await using (again)
        {
            var progress = Get<LearningProgressService>(again);
            var attempt = await progress.StartAttemptAsync(me2, w.Sales1, enrollment.Id, default);
            attempt.AttemptNo.Should().Be(3);
            (await progress.SubmitAttemptAsync(me2, w.Sales1, attempt.Id, new SubmitAttemptInput(Correct()), default)).Status
                .Should().Be(LearningAttemptStatuses.Passed);
        }
        await using var db = w.Db();
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "learning.attempts.reset" && a.EntityId == enrollment.Id.ToString());
        audit.UserId.Should().Be(w.HrUserA);
        audit.Metadata.Should().Contain("Retraining session attended on 30 Sep");
        (await db.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == "learning.enrollment.locked" && a.EntityId == enrollment.Id.ToString())).Should().BeTrue();
    }

    [Fact]
    public async Task Revocation_And_Correction_KeepHistory()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("CERT", [Lesson("FORKLIFT", LearningCompletionRules.Assessment, validityMonths: 24)]);
        await w.AssignAsync(pathId, [w.Ops1, w.Ops2]);
        var credentials = new Dictionary<int, Guid>();
        foreach (var id in new[] { w.Ops1, w.Ops2 })
        {
            var enrollment = await w.EnrollmentAsync(id, pathId, 1);
            var (s, a) = w.Employee(id);
            await using (s)
            {
                var progress = Get<LearningProgressService>(s);
                var attempt = await progress.StartAttemptAsync(a, id, enrollment.Id, default);
                credentials[id] = (await progress.SubmitAttemptAsync(a, id, attempt.Id, new SubmitAttemptInput(Correct()), default)).CredentialId!.Value;
            }
        }

        await using var hr = w.As(w.HrA());
        var progressHr = Get<LearningProgressService>(hr);
        var actor = await ActorAsync(hr, w.TenantA);
        var revoked = await progressHr.RevokeAsync(actor, credentials[w.Ops1],
            new RevokeCredentialInput("Licence withdrawn after incident 2026-118", w.Clock.Today.AddDays(14)), default);
        revoked.Status.Should().Be(LearningCredentialStatuses.Revoked);
        revoked.RevokeReason.Should().Be("Licence withdrawn after incident 2026-118");
        var revokeTwice = () => progressHr.RevokeAsync(actor, credentials[w.Ops1], new RevokeCredentialInput("again", w.Clock.Today.AddDays(14)), default);
        (await revokeTwice.Should().ThrowAsync<LearningException>()).Which.Code.Should().Be("credential_not_current");

        var completedOn = w.Clock.Today.AddDays(-3);
        var corrected = await progressHr.CorrectAsync(actor, credentials[w.Ops2],
            new CorrectCredentialInput("Course was taken on paper three days earlier", completedOn, 95m), default);
        corrected.Id.Should().NotBe(credentials[w.Ops2]);
        corrected.SupersedesCredentialId.Should().Be(credentials[w.Ops2]);
        corrected.CompletedOn.Should().Be(completedOn);
        corrected.ExpiresOn.Should().Be(completedOn.AddMonths(24));
        corrected.ScorePercent.Should().Be(95m);
        var correctOld = () => progressHr.CorrectAsync(actor, credentials[w.Ops2], new CorrectCredentialInput("again", null, null), default);
        (await correctOld.Should().ThrowAsync<LearningException>()).Which.Code.Should().Be("credential_not_current");

        await using var db = w.Db();
        var old = await db.LearningCredentials.AsNoTracking().SingleAsync(c => c.Id == credentials[w.Ops2]);
        old.Status.Should().Be(LearningCredentialStatuses.Superseded);
        old.CertificateNumber.Should().NotBe(corrected.CertificateNumber);
        var actions = await db.AuditLogs.AsNoTracking().Where(a => a.TenantId == w.TenantA).Select(a => a.Action).ToListAsync();
        actions.Should().Contain(["learning.credential.revoked", "learning.credential.corrected"]);

        // Verification needs the number AND the printed code.
        var verified = await progressHr.VerifyAsync(actor, corrected.CertificateNumber, corrected.VerificationCode, default);
        verified.Status.Should().Be(LearningCredentialStatuses.Active);
        var guess = () => progressHr.VerifyAsync(actor, corrected.CertificateNumber, "WRONGCODE1", default);
        (await guess.Should().ThrowAsync<LearningException>()).Which.Status.Should().Be(404);
    }

    [Fact]
    public async Task TheEmployeeNeverReceivesTheAnswerKey()
    {
        await using var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync("KEY", [Lesson("SECRET", LearningCompletionRules.Assessment)]);
        await w.AssignAsync(pathId, [w.Ops1]);
        var enrollment = await w.EnrollmentAsync(w.Ops1, pathId, 1);
        var (scope, me) = w.Employee(w.Ops1);
        await using (scope)
        {
            var attempt = await Get<LearningProgressService>(scope).StartAttemptAsync(me, w.Ops1, enrollment.Id, default);
            var wire = JsonSerializer.Serialize(attempt, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            wire.Should().NotContainEquivalentOf("correct");
            wire.Should().Contain("The stairs");
        }
        // HR without learning.manage gets the course without the key; the controller passes includeAnswerKey only for it.
        await using var hr = w.As(w.HrA());
        var course = await Get<LearningCatalogService>(hr).GetCourseAsync(w.TenantA, enrollment.CourseId, includeAnswerKey: false, default);
        course.Questions.Should().BeNull();
        course.QuestionCount.Should().Be(2);
    }
}
