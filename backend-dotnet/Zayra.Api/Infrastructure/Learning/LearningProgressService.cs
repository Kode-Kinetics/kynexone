using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Learning;

/// <summary>
/// Notified after a learning assignment's completion has committed (LRN → other modules, e.g. SUC development-plan actions
/// linked to the assignment). Register with DI; every registration is called. Implementations must be idempotent.
/// </summary>
public interface ILearningAssignmentCompletedListener
{
    Task OnAssignmentCompletedAsync(Guid tenantId, Guid assignmentId, CancellationToken ct);
}

// ── Contracts ───────────────────────────────────────────────────────────────────────────────────────────────────────

public static class LearningNextActions
{
    public const string Open = "open";
    public const string TakeAssessment = "take_assessment";
    public const string Acknowledge = "acknowledge";
    public const string Locked = "locked";
    public const string Blocked = "blocked";
    public const string Done = "done";
    public const string Closed = "closed";
    public const string PendingVerification = "pending_verification";
}

public sealed record EnrollmentItemDto(Guid Id, int Sequence, string CourseCode, int CourseVersion, string Title, string? TitleAr, string ContentKind,
    string CompletionRule, bool IsRequired, string Status, bool IsBlocked, string? BlockedByTitle, string? BlockedByTitleAr, decimal? ProgressPercent,
    decimal? ScorePercent, int AttemptsUsed, int AttemptAllowance, int? EstimatedMinutes, DateTime? FirstLaunchedAtUtc, DateTime? CompletedAtUtc,
    string? CompletionSource, string NextAction);

public sealed record AssignmentViewDto(Guid Id, int EmployeeId, string EmployeeName, string EmployeeCode, Guid PathId, string PathName, string? PathNameAr,
    string Status, bool IsMandatory, DateOnly? DueDate, bool IsOverdue, int Cycle, string Source, int RequiredCount, int RequiredCompleted,
    decimal ProgressPercent, DateTime AssignedAtUtc, DateTime? CompletedAtUtc, string? CancelReason, IReadOnlyList<EnrollmentItemDto> Items,
    bool AwaitingVerification = false);

public sealed record CredentialDto(Guid Id, string CertificateNumber, string VerificationCode, string Status, int EmployeeId, string EmployeeName,
    string? EmployeeNameAr, string EmployeeCode, string CourseCode, int CourseVersion, string CourseTitle, string? CourseTitleAr, string PathName,
    string? PathNameAr, string CompletionSource, decimal? ScorePercent, DateOnly CompletedOn, DateOnly? ExpiresOn, DateTime IssuedAtUtc,
    Guid? SupersedesCredentialId, string? SupersedeReason, DateTime? SupersededAtUtc, DateTime? RevokedAtUtc, string? RevokeReason, string? CompanyName);

public sealed record AttemptSummaryDto(Guid Id, int AttemptNo, string Status, decimal? ScorePercent, decimal PassMarkPercent, DateTime StartedAtUtc,
    DateTime? SubmittedAtUtc);

public sealed record EnrollmentDetailDto(EnrollmentItemDto Item, Guid AssignmentId, string AssignmentStatus, string PathName, string? PathNameAr,
    string? Description, string? DescriptionAr, decimal? PassMarkPercent, int QuestionCount, IReadOnlyList<AttemptSummaryDto> Attempts,
    Guid? OpenAttemptId, Guid? CredentialId, string? LinkUrl);

public sealed record LaunchDto(string Kind, string? LessonHtml, string? LinkUrl, string? LaunchUrl, Guid? SessionId, EnrollmentDetailDto Enrollment);

public sealed record AttemptDto(Guid Id, int AttemptNo, string Status, IReadOnlyList<ClientQuestion> Questions, decimal PassMarkPercent,
    int AttemptsUsed, int AttemptAllowance, DateTime StartedAtUtc);

public sealed record AttemptResultDto(Guid AttemptId, int AttemptNo, string Status, decimal ScorePercent, decimal PassMarkPercent, int Correct,
    int Total, int AttemptsRemaining, string EnrollmentStatus, Guid? CredentialId, string? CertificateNumber);

public sealed record SubmitAttemptInput(Dictionary<string, List<string>>? Answers);

public sealed record TranscriptEntryDto(Guid EnrollmentId, string CourseCode, int CourseVersion, string Title, string? TitleAr, string PathName,
    string? PathNameAr, int Cycle, DateTime CompletedAtUtc, string CompletionSource, decimal? ScorePercent, Guid? CredentialId,
    string? CertificateNumber, string? CredentialStatus, DateOnly? ExpiresOn);

public sealed record MyLearningDto(IReadOnlyList<AssignmentViewDto> Assignments, IReadOnlyList<CredentialDto> Credentials,
    IReadOnlyList<TranscriptEntryDto> Transcript);

public sealed record CorrectCredentialInput(string? Reason, DateOnly? CompletedOn, decimal? ScorePercent);

/// <summary>Revoking reopens the learning (the person is non-compliant again) with the new due date HR sets.</summary>
public sealed record RevokeCredentialInput(string? Reason, DateOnly? DueDate);

/// <summary>
/// The learner's journey and the evidence it produces (LRN-04..08, 12): view, launch, acknowledge, attempt, completion,
/// credential — plus HR's record corrections (reset, revoke, correct; LRN-07/08, audited LRN-15).
///
/// <para><b>Completion is never claimed, only evidenced.</b> The only writers of a Completed enrollment are
/// <see cref="CompleteAsync"/>'s three callers: a passed attempt scored here, an acknowledgement on a course whose rule
/// is Acknowledgement and that was opened first, and a cmi5 statement satisfying moveOn (LearningCmi5Service). No
/// endpoint accepts a status, score or completion from the client.</para>
/// </summary>
public sealed class LearningProgressService(ZayraDbContext db, IAuditService audit, TimeProvider clock, ITenantClock tenantClock, IServiceProvider services)
{
    // ── Employee: reads ─────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<MyLearningDto> MyLearningAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var assignments = await db.LearningAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.EmployeeId == employeeId && a.Status != LearningAssignmentStatuses.Cancelled)
            .OrderBy(a => a.Status == LearningAssignmentStatuses.Completed).ThenBy(a => a.DueDate).ToListAsync(ct);
        var views = await AssignmentViewsAsync(tenantId, assignments, ct);
        var credentials = await CredentialsAsync(tenantId, db.LearningCredentials.AsNoTracking().Where(c => c.TenantId == tenantId && c.EmployeeId == employeeId), ct);
        return new MyLearningDto(views, credentials, await TranscriptAsync(tenantId, employeeId, ct));
    }

    public async Task<EnrollmentDetailDto> EnrollmentDetailAsync(Guid tenantId, int employeeId, Guid enrollmentId, CancellationToken ct)
    {
        var enrollment = await OwnEnrollmentAsync(tenantId, employeeId, enrollmentId, tracked: false, ct);
        return await DetailAsync(enrollment, ct);
    }

    public async Task<CredentialDto> MyCredentialAsync(Guid tenantId, int employeeId, Guid credentialId, CancellationToken ct)
    {
        var list = await CredentialsAsync(tenantId, db.LearningCredentials.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.EmployeeId == employeeId && c.Id == credentialId), ct);
        return list.SingleOrDefault() ?? throw LearningException.NotFound("Certificate");
    }

    // ── Employee: launch, acknowledge ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the content and records LAUNCH evidence only (LRN-05). A lesson view or link open also marks the item
    /// in progress; it never completes it. cmi5 creates a session with a one-time fetch URL (LearningCmi5Service).
    /// </summary>
    public async Task<LaunchDto> LaunchAsync(LearningActor actor, int employeeId, Guid enrollmentId, string publicApiBase, string? returnUrl, CancellationToken ct)
    {
        var enrollment = await OwnEnrollmentAsync(actor.TenantId, employeeId, enrollmentId, tracked: true, ct);
        var (assignment, course) = await GuardActionableAsync(enrollment, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        Touch(enrollment, assignment, now);

        string? launchUrl = null;
        Guid? sessionId = null;
        if (course.ContentKind == LearningContentKinds.Cmi5)
        {
            var cmi5 = services.GetRequiredService<LearningCmi5Service>();
            (launchUrl, sessionId) = await cmi5.CreateSessionAsync(actor.Request, enrollment, course, publicApiBase, returnUrl, ct);
        }
        else
        {
            enrollment.Version++;
            assignment.Version++;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("learning.content.opened", nameof(LearningEnrollment), enrollment.Id.ToString(), actor.Request,
                JsonSerializer.Serialize(new { enrollment.EmployeeId, course.Code, course.VersionNumber, course.ContentKind }), ct);
        }
        return new LaunchDto(course.ContentKind, course.ContentKind == LearningContentKinds.Lesson ? course.LessonHtml : null,
            course.ContentKind == LearningContentKinds.Link ? course.LinkUrl : null, launchUrl, sessionId, await DetailAsync(enrollment, ct));
    }

    /// <summary>
    /// Acknowledgement completion (§3.1): only for a course whose rule is Acknowledgement (Lesson/Link, no assessment),
    /// and only after the content was opened. Anything assessment- or cmi5-gated is refused (LRN-12).
    /// </summary>
    public async Task<EnrollmentDetailDto> AcknowledgeAsync(LearningActor actor, int employeeId, Guid enrollmentId, CancellationToken ct)
    {
        var enrollment = await OwnEnrollmentAsync(actor.TenantId, employeeId, enrollmentId, tracked: true, ct);
        var (assignment, course) = await GuardActionableAsync(enrollment, ct);
        if (course.CompletionRule != LearningCompletionRules.Acknowledgement)
            throw LearningException.Conflict("completion_rule_mismatch",
                course.CompletionRule == LearningCompletionRules.Assessment
                    ? "This course is completed by passing its assessment, not by acknowledging it."
                    : "This course is completed by the learning content itself, not by acknowledging it.");
        if (enrollment.FirstLaunchedAtUtc is null)
            throw LearningException.Conflict("content_not_opened", "Open the course content before you acknowledge it.");
        var now = clock.GetUtcNow().UtcDateTime;
        var credential = await CompleteAsync(enrollment, assignment, course, LearningCompletionSources.Acknowledgement, "ack", null, now, ct);
        await db.SaveChangesAsync(ct);
        await AuditCompletionAsync(actor.Request, enrollment, assignment, credential, ct);
        return await DetailAsync(enrollment, ct);
    }

    // ── Employee: assessment ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Starts an attempt, or returns the open one (a second click resumes, it never burns a second attempt).</summary>
    public async Task<AttemptDto> StartAttemptAsync(LearningActor actor, int employeeId, Guid enrollmentId, CancellationToken ct)
    {
        var enrollment = await OwnEnrollmentAsync(actor.TenantId, employeeId, enrollmentId, tracked: true, ct);
        var (assignment, course) = await GuardActionableAsync(enrollment, ct);
        if (course.CompletionRule != LearningCompletionRules.Assessment)
            throw LearningException.Conflict("no_assessment", "This course has no assessment.");
        var bank = LearningAssessment.Parse(course.AssessmentJson);

        var open = await db.LearningAttempts.AsNoTracking().FirstOrDefaultAsync(a => a.EnrollmentId == enrollment.Id && a.Status == LearningAttemptStatuses.InProgress, ct);
        if (open is not null) return AttemptView(open, bank, enrollment);
        if (enrollment.AttemptsUsed >= enrollment.AttemptAllowance)
            throw LearningException.Conflict("attempts_exhausted", "You have used every attempt. HR can reset your attempts.");

        var now = clock.GetUtcNow().UtcDateTime;
        var attempt = new LearningAttempt
        {
            TenantId = enrollment.TenantId, CompanyId = enrollment.CompanyId, EnrollmentId = enrollment.Id, EmployeeId = enrollment.EmployeeId,
            AttemptNo = enrollment.AttemptsUsed + 1, Status = LearningAttemptStatuses.InProgress,
            QuestionOrderJson = JsonSerializer.Serialize(LearningAssessment.AttemptOrder(bank, course.ShuffleQuestions)),
            PassMarkPercent = course.PassMarkPercent ?? 100m, StartedAtUtc = now,
        };
        // An attempt is used when it is started, so the questions cannot be previewed for free.
        enrollment.AttemptsUsed++;
        Touch(enrollment, assignment, now);
        enrollment.Version++;
        assignment.Version++;
        db.LearningAttempts.Add(attempt);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException || (ex is DbUpdateException u && LearningDb.IsUniqueViolation(u)))
        {
            // A concurrent start won: return its attempt.
            db.ChangeTracker.Clear();
            var winner = await db.LearningAttempts.AsNoTracking().FirstOrDefaultAsync(a => a.EnrollmentId == enrollment.Id && a.Status == LearningAttemptStatuses.InProgress, ct)
                ?? throw LearningException.Conflict("concurrent_change", "Your attempt could not be started. Please try again.");
            var fresh = await db.LearningEnrollments.AsNoTracking().FirstAsync(e => e.Id == enrollment.Id, ct);
            return AttemptView(winner, bank, fresh);
        }
        await audit.WriteAsync("learning.attempt.started", nameof(LearningAttempt), attempt.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { attempt.EnrollmentId, attempt.AttemptNo }), ct);
        return AttemptView(attempt, bank, enrollment);
    }

    /// <summary>
    /// Scores an attempt on the server (LRN-06). Submitting the same attempt again returns the stored result and changes
    /// nothing (double submit is idempotent). A fail with attempts left allows a retake; the last fail locks the item (LRN-07).
    /// </summary>
    public async Task<AttemptResultDto> SubmitAttemptAsync(LearningActor actor, int employeeId, Guid attemptId, SubmitAttemptInput input, CancellationToken ct)
    {
        var attempt = await db.LearningAttempts.FirstOrDefaultAsync(a => a.TenantId == actor.TenantId && a.Id == attemptId && a.EmployeeId == employeeId, ct)
            ?? throw LearningException.NotFound("Attempt");
        var enrollment = await db.LearningEnrollments.FirstAsync(e => e.Id == attempt.EnrollmentId, ct);
        var course = await db.LearningCourses.AsNoTracking().FirstAsync(c => c.Id == enrollment.CourseId, ct);
        var bank = LearningAssessment.Parse(course.AssessmentJson);
        if (attempt.Status != LearningAttemptStatuses.InProgress) return await StoredResultAsync(attempt, enrollment, bank, ct);

        var assignment = await db.LearningAssignments.FirstAsync(a => a.Id == enrollment.AssignmentId, ct);
        if (!LearningAssignmentStatuses.Active.Contains(assignment.Status) || enrollment.Status is LearningEnrollmentStatuses.Cancelled)
            throw LearningException.Conflict("assignment_not_live", "This learning is no longer assigned to you.");

        var answers = ValidAnswers(bank, input.Answers);
        var score = LearningAssessment.Score(bank, answers, attempt.PassMarkPercent);
        var now = clock.GetUtcNow().UtcDateTime;
        attempt.AnswersJson = JsonSerializer.Serialize(answers);
        attempt.ScorePercent = score.ScorePercent;
        attempt.Status = score.Passed ? LearningAttemptStatuses.Passed : LearningAttemptStatuses.Failed;
        attempt.SubmittedAtUtc = now;
        attempt.Version++;
        enrollment.ScorePercent = score.ScorePercent;
        enrollment.LastActivityAtUtc = now;

        LearningCredential? credential = null;
        if (score.Passed && !LearningEnrollmentStatuses.IsDone(enrollment.Status))
            credential = await CompleteAsync(enrollment, assignment, course, LearningCompletionSources.Assessment, attempt.Id.ToString(), score.ScorePercent, now, ct);
        else if (!score.Passed && enrollment.AttemptsUsed >= enrollment.AttemptAllowance && !LearningEnrollmentStatuses.IsDone(enrollment.Status))
        {
            enrollment.Status = LearningEnrollmentStatuses.Locked;
            enrollment.LockedAtUtc = now;
        }
        enrollment.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The same attempt was submitted concurrently and the other request won: answer with what it stored.
            db.ChangeTracker.Clear();
            var stored = await db.LearningAttempts.AsNoTracking().FirstAsync(a => a.Id == attemptId, ct);
            var current = await db.LearningEnrollments.AsNoTracking().FirstAsync(e => e.Id == stored.EnrollmentId, ct);
            if (stored.Status == LearningAttemptStatuses.InProgress) throw;
            return await StoredResultAsync(stored, current, bank, ct);
        }
        await audit.WriteAsync(score.Passed ? "learning.attempt.passed" : "learning.attempt.failed", nameof(LearningAttempt), attempt.Id.ToString(),
            actor.Request, JsonSerializer.Serialize(new { attempt.EnrollmentId, attempt.AttemptNo, attempt.ScorePercent, attempt.PassMarkPercent }), ct);
        if (enrollment.Status == LearningEnrollmentStatuses.Locked)
            await audit.WriteAsync("learning.enrollment.locked", nameof(LearningEnrollment), enrollment.Id.ToString(), actor.Request,
                JsonSerializer.Serialize(new { enrollment.AttemptsUsed, enrollment.AttemptAllowance }), ct);
        if (score.Passed) await AuditCompletionAsync(actor.Request, enrollment, assignment, credential, ct);
        return new AttemptResultDto(attempt.Id, attempt.AttemptNo, attempt.Status, score.ScorePercent, attempt.PassMarkPercent, score.Correct, score.Total,
            Math.Max(0, enrollment.AttemptAllowance - enrollment.AttemptsUsed), enrollment.Status, credential?.Id, credential?.CertificateNumber);
    }

    /// <summary>
    /// The submission must fit the assessment: known question ids, at most the options a question has, short ids. Anything
    /// else is refused before it is stored (a submission is evidence, so it is never trimmed silently).
    /// </summary>
    private static Dictionary<string, IReadOnlyList<string>> ValidAnswers(AssessmentBank bank, Dictionary<string, List<string>>? raw)
    {
        raw ??= [];
        var questions = bank.Questions.ToDictionary(q => q.Id);
        if (raw.Count > questions.Count)
            throw LearningException.Invalid("The answers do not match this assessment.");
        foreach (var (questionId, chosen) in raw)
        {
            if (!questions.TryGetValue(questionId, out var question) || chosen is null || chosen.Count > question.Options.Count
                || chosen.Any(id => string.IsNullOrEmpty(id) || id.Length > 32))
                throw LearningException.Invalid("The answers do not match this assessment.");
        }
        return raw.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }

    private async Task<AttemptResultDto> StoredResultAsync(LearningAttempt attempt, LearningEnrollment enrollment, AssessmentBank bank, CancellationToken ct)
    {
        var answers = string.IsNullOrWhiteSpace(attempt.AnswersJson)
            ? new Dictionary<string, IReadOnlyList<string>>()
            : JsonSerializer.Deserialize<Dictionary<string, List<string>>>(attempt.AnswersJson)!.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
        var score = LearningAssessment.Score(bank, answers, attempt.PassMarkPercent);
        var credential = attempt.Status == LearningAttemptStatuses.Passed
            ? await db.LearningCredentials.AsNoTracking().Where(c => c.EnrollmentId == enrollment.Id).OrderBy(c => c.IssuedAtUtc).FirstOrDefaultAsync(ct)
            : null;
        return new AttemptResultDto(attempt.Id, attempt.AttemptNo, attempt.Status, attempt.ScorePercent ?? score.ScorePercent, attempt.PassMarkPercent,
            score.Correct, score.Total, Math.Max(0, enrollment.AttemptAllowance - enrollment.AttemptsUsed), enrollment.Status, credential?.Id,
            credential?.CertificateNumber);
    }

    // ── Completion pipeline (the only writer of a Completed enrollment) ─────────────────────────────────────────────

    /// <summary>
    /// Records completion evidence from one source. A content-reported (cmi5) completion on a version that requires HR
    /// verification stops at PendingVerification: no credential, and the item does not count towards the path. Every
    /// other completion is final at once: the credential is issued (superseding the person's current credential for the
    /// same course), and the assignment completes when every required item is done. Changes tracked entities only; the
    /// caller saves (one transaction) and then calls <see cref="AuditCompletionAsync"/>.
    /// </summary>
    internal async Task<LearningCredential?> CompleteAsync(LearningEnrollment enrollment, LearningAssignment assignment, LearningCourse course,
        string source, string evidenceRef, decimal? score, DateTime now, CancellationToken ct)
    {
        if (LearningEnrollmentStatuses.IsDone(enrollment.Status)) return null;
        enrollment.CompletionSource = source;
        enrollment.CompletionEvidenceRef = evidenceRef.Length <= 64 ? evidenceRef : evidenceRef[..64];
        enrollment.CompletedAtUtc = now;
        enrollment.LastActivityAtUtc = now;
        enrollment.LockedAtUtc = null;
        if (score is not null) enrollment.ScorePercent = score;
        if (source != LearningCompletionSources.Cmi5) enrollment.ProgressPercent = 100m;

        if (source == LearningCompletionSources.Cmi5 && course.RequiresHrVerification)
        {
            enrollment.Status = LearningEnrollmentStatuses.PendingVerification;
            if (assignment.Status == LearningAssignmentStatuses.Assigned)
            {
                assignment.Status = LearningAssignmentStatuses.InProgress;
                assignment.StartedAtUtc ??= now;
            }
            assignment.Version++;
            return null;
        }
        enrollment.Status = LearningEnrollmentStatuses.Completed;
        return await FinalizeAsync(enrollment, assignment, course, now, ct);
    }

    /// <summary>Issues the credential and completes the assignment for an enrollment that is now Completed.</summary>
    private async Task<LearningCredential?> FinalizeAsync(LearningEnrollment enrollment, LearningAssignment assignment, LearningCourse course,
        DateTime now, CancellationToken ct)
    {
        LearningCredential? credential = null;
        if (course.IssuesCertificate || course.ValidityMonths is not null)
        {
            var completedOn = await LocalDateAsync(enrollment.TenantId, enrollment.CompletedAtUtc ?? now, ct);
            credential = new LearningCredential
            {
                TenantId = enrollment.TenantId, CompanyId = enrollment.CompanyId, EmployeeId = enrollment.EmployeeId, EnrollmentId = enrollment.Id,
                CourseId = course.Id, CourseCode = course.Code, PathId = assignment.PathId,
                CertificateNumber = await UniqueCertificateNumberAsync(enrollment.TenantId, now, ct),
                VerificationCode = LearningTokens.Code(10), Status = LearningCredentialStatuses.Active,
                CompletionSource = enrollment.CompletionSource ?? LearningCompletionSources.Assessment,
                ScorePercent = enrollment.ScorePercent, IssuedAtUtc = now, CompletedOn = completedOn,
                ExpiresOn = course.ValidityMonths is int months ? completedOn.AddMonths(months) : null,
            };
            // The person's current credential(s) for this course are replaced, whichever assignment opened this cycle —
            // a renewal of a path with several certificate courses supersedes each of them, not just the one that
            // triggered the renewal. History is kept: old rows become Superseded, the newest is named as predecessor.
            var current = await LearningDb.TenantRows(db.LearningCredentials, enrollment.TenantId)
                .Where(c => c.EmployeeId == enrollment.EmployeeId && c.CourseCode == course.Code
                            && (c.Status == LearningCredentialStatuses.Active || c.Status == LearningCredentialStatuses.Expired))
                .OrderByDescending(c => c.IssuedAtUtc).ToListAsync(ct);
            foreach (var previous in current)
            {
                previous.Status = LearningCredentialStatuses.Superseded;
                previous.SupersededAtUtc = now;
                previous.Version++;
            }
            if (current.Count > 0)
            {
                credential.SupersedesCredentialId = current[0].Id;
                credential.SupersedeReason = assignment.Source == LearningAssignmentSources.Renewal ? "Renewal" : "Completed again";
            }
            db.LearningCredentials.Add(credential);
        }

        var siblings = await LearningDb.TenantRows(db.LearningEnrollments, enrollment.TenantId)
            .Where(e => e.AssignmentId == assignment.Id && e.Id != enrollment.Id).ToListAsync(ct);
        var allRequiredDone = siblings.Where(e => e.IsRequired).All(e => e.Status == LearningEnrollmentStatuses.Completed);
        if (allRequiredDone && (enrollment.IsRequired || siblings.Any(e => e.IsRequired)))
        {
            assignment.Status = LearningAssignmentStatuses.Completed;
            assignment.CompletedAtUtc = now;
        }
        else if (assignment.Status == LearningAssignmentStatuses.Assigned)
        {
            assignment.Status = LearningAssignmentStatuses.InProgress;
            assignment.StartedAtUtc ??= now;
        }
        assignment.Version++;
        return credential;
    }

    /// <summary>The tenant's calendar date of an instant (Saudi tenants on Riyadh time; see TenantClock).</summary>
    internal async Task<DateOnly> LocalDateAsync(Guid tenantId, DateTime instantUtc, CancellationToken ct)
    {
        var setting = await db.TenantLocalizationSettings.AsNoTracking().Where(l => l.TenantId == tenantId)
            .Select(l => new { l.CountryCode, l.DefaultTimezone }).FirstOrDefaultAsync(ct);
        return Zayra.Api.Infrastructure.Common.TenantClock.LocalDate(setting?.CountryCode, setting?.DefaultTimezone,
            DateTime.SpecifyKind(instantUtc, DateTimeKind.Utc));
    }

    // ── HR: verification of content-reported completion (learning.records.correct; never one's own record) ─────────

    /// <summary>Verifies a content-reported completion: the item counts as completed and the credential is issued.</summary>
    public async Task<AssignmentViewDto> VerifyAsync(LearningActor actor, Guid enrollmentId, CancellationToken ct)
    {
        var enrollment = await db.LearningEnrollments.FirstOrDefaultAsync(e => e.TenantId == actor.TenantId && e.Id == enrollmentId, ct);
        if (enrollment is null || !actor.Scope.CanAccessEmployee(enrollment.EmployeeId)) throw LearningException.NotFound("Enrollment");
        actor.RefuseSelf(enrollment.EmployeeId);
        if (enrollment.Status != LearningEnrollmentStatuses.PendingVerification)
            throw LearningException.Conflict("not_pending_verification", "Only a completion waiting for verification can be verified.");
        var assignment = await db.LearningAssignments.FirstAsync(a => a.Id == enrollment.AssignmentId, ct);
        var course = await db.LearningCourses.AsNoTracking().FirstAsync(c => c.Id == enrollment.CourseId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        enrollment.Status = LearningEnrollmentStatuses.Completed;
        enrollment.VerifiedAtUtc = now;
        enrollment.VerifiedByUserId = actor.UserId;
        enrollment.Version++;
        var credential = await FinalizeAsync(enrollment, assignment, course, now, ct);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("learning.enrollment.verified", nameof(LearningEnrollment), enrollment.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { enrollment.EmployeeId, enrollment.CompletionEvidenceRef, reportedAtUtc = enrollment.CompletedAtUtc }), ct);
        await AuditCompletionAsync(actor.Request, enrollment, assignment, credential, ct);
        return await AssignmentViewAsync(actor, assignment.Id, ct);
    }

    /// <summary>Rejects a content-reported completion: the item is back in progress; the statements stay as evidence.</summary>
    public async Task<AssignmentViewDto> RejectVerificationAsync(LearningActor actor, Guid enrollmentId, string? reason, CancellationToken ct)
    {
        var why = Reason(reason);
        var enrollment = await db.LearningEnrollments.FirstOrDefaultAsync(e => e.TenantId == actor.TenantId && e.Id == enrollmentId, ct);
        if (enrollment is null || !actor.Scope.CanAccessEmployee(enrollment.EmployeeId)) throw LearningException.NotFound("Enrollment");
        actor.RefuseSelf(enrollment.EmployeeId);
        if (enrollment.Status != LearningEnrollmentStatuses.PendingVerification)
            throw LearningException.Conflict("not_pending_verification", "Only a completion waiting for verification can be rejected.");
        var evidence = enrollment.CompletionEvidenceRef;
        ClearCompletion(enrollment);
        enrollment.ScorePercent = null;
        enrollment.Status = LearningEnrollmentStatuses.InProgress;
        // The rejected report must not count again: a relaunch starts a new cmi5 registration.
        await LearningCmi5Service.StartNewRegistrationAsync(db, enrollment, clock.GetUtcNow().UtcDateTime, ct);
        enrollment.Version++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("learning.enrollment.verification_rejected", nameof(LearningEnrollment), enrollment.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { enrollment.EmployeeId, evidence, reason = why }), ct);
        return await AssignmentViewAsync(actor, enrollment.AssignmentId, ct);
    }

    private static void ClearCompletion(LearningEnrollment enrollment)
    {
        enrollment.CompletionSource = null;
        enrollment.CompletionEvidenceRef = null;
        enrollment.CompletedAtUtc = null;
        enrollment.VerifiedAtUtc = null;
        enrollment.VerifiedByUserId = null;
        enrollment.ProgressPercent = null;
    }

    internal async Task AuditCompletionAsync(RequestContext request, LearningEnrollment enrollment, LearningAssignment assignment,
        LearningCredential? credential, CancellationToken ct)
    {
        if (enrollment.Status == LearningEnrollmentStatuses.PendingVerification)
        {
            // Content-reported, not yet a completion: "completed" is audited once, when HR verifies it.
            await audit.WriteAsync("learning.enrollment.completion_reported", nameof(LearningEnrollment), enrollment.Id.ToString(), request,
                JsonSerializer.Serialize(new { enrollment.EmployeeId, enrollment.CourseId, enrollment.CompletionSource, enrollment.CompletionEvidenceRef, enrollment.ScorePercent }), ct);
            return;
        }
        await audit.WriteAsync("learning.enrollment.completed", nameof(LearningEnrollment), enrollment.Id.ToString(), request,
            JsonSerializer.Serialize(new { enrollment.EmployeeId, enrollment.CourseId, enrollment.CompletionSource, enrollment.CompletionEvidenceRef, enrollment.ScorePercent }), ct);
        if (credential is not null)
            await audit.WriteAsync("learning.credential.issued", nameof(LearningCredential), credential.Id.ToString(), request,
                JsonSerializer.Serialize(new { credential.EmployeeId, credential.CertificateNumber, credential.CourseCode, credential.ExpiresOn, credential.SupersedesCredentialId }), ct);
        if (assignment.Status == LearningAssignmentStatuses.Completed)
        {
            await audit.WriteAsync("learning.assignment.completed", nameof(LearningAssignment), assignment.Id.ToString(), request,
                JsonSerializer.Serialize(new { assignment.EmployeeId, assignment.PathId, assignment.Cycle }), ct);
            // After the completion committed: modules that follow an assignment (SUC development-plan actions) hear about it.
            // A listener failure never undoes the learner's completion; listeners also reconcile on read and in their sweep.
            foreach (var listener in services.GetServices<ILearningAssignmentCompletedListener>())
            {
                try { await listener.OnAssignmentCompletedAsync(assignment.TenantId, assignment.Id, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    services.GetService<ILogger<LearningProgressService>>()?.LogWarning(
                        "Learning: a completion listener failed for assignment {AssignmentId} ({Error}).", assignment.Id, ex.GetType().Name);
                }
            }
        }
    }

    private async Task<string> UniqueCertificateNumberAsync(Guid tenantId, DateTime now, CancellationToken ct)
    {
        for (var i = 0; i < 5; i++)
        {
            var number = LearningTokens.NewCertificateNumber(now);
            if (!await LearningDb.TenantRows(db.LearningCredentials, tenantId).AnyAsync(c => c.CertificateNumber == number, ct)) return number;
        }
        throw new InvalidOperationException("Could not allocate a unique certificate number.");
    }

    // ── HR: record corrections (learning.records.correct) ───────────────────────────────────────────────────────────

    /// <summary>LRN-07: an item locked by exhausted attempts is reopened with another full set of attempts. Reason required, audited.</summary>
    public async Task<AssignmentViewDto> ResetAttemptsAsync(LearningActor actor, Guid enrollmentId, string? reason, CancellationToken ct)
    {
        var why = Reason(reason);
        var enrollment = await db.LearningEnrollments.FirstOrDefaultAsync(e => e.TenantId == actor.TenantId && e.Id == enrollmentId, ct);
        if (enrollment is null || !actor.Scope.CanAccessEmployee(enrollment.EmployeeId)) throw LearningException.NotFound("Enrollment");
        actor.RefuseSelf(enrollment.EmployeeId);
        if (enrollment.Status != LearningEnrollmentStatuses.Locked)
            throw LearningException.Conflict("enrollment_not_locked", "Only an item locked after its last failed attempt can be reset.");
        var course = await db.LearningCourses.AsNoTracking().FirstAsync(c => c.Id == enrollment.CourseId, ct);
        var before = enrollment.AttemptAllowance;
        enrollment.AttemptAllowance += course.MaxAttempts ?? 1;
        enrollment.Status = LearningEnrollmentStatuses.InProgress;
        enrollment.LockedAtUtc = null;
        enrollment.ResetCount++;
        enrollment.Version++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("learning.attempts.reset", nameof(LearningEnrollment), enrollment.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { enrollment.EmployeeId, reason = why, allowanceBefore = before, allowanceAfter = enrollment.AttemptAllowance, enrollment.ResetCount }), ct);
        return await AssignmentViewAsync(actor, enrollment.AssignmentId, ct);
    }

    /// <summary>
    /// LRN-08: revocation keeps the row and its history, and REOPENS the learning behind it: the enrollment is back in
    /// progress (with a fresh set of attempts for an assessment) and the assignment is live again with the due date HR
    /// sets, so the person counts as non-compliant until they complete it again. If a newer live assignment of the path
    /// already exists, that one carries the obligation and nothing older is reopened.
    /// </summary>
    public async Task<CredentialDto> RevokeAsync(LearningActor actor, Guid credentialId, RevokeCredentialInput input, CancellationToken ct)
    {
        var why = Reason(input.Reason);
        var credential = await db.LearningCredentials.FirstOrDefaultAsync(c => c.TenantId == actor.TenantId && c.Id == credentialId, ct);
        if (credential is null || !actor.Scope.CanAccessEmployee(credential.EmployeeId)) throw LearningException.NotFound("Certificate");
        actor.RefuseSelf(credential.EmployeeId);
        if (credential.Status is not (LearningCredentialStatuses.Active or LearningCredentialStatuses.Expired))
            throw LearningException.Conflict("credential_not_current", "Only a current or expired certificate can be revoked.");
        var today = await tenantClock.TodayAsync(actor.TenantId, ct);
        if (input.DueDate is not DateOnly due || due < today)
            throw LearningException.Invalid("Set the date by which the employee must complete the course again (today or later).");
        var now = clock.GetUtcNow().UtcDateTime;
        credential.Status = LearningCredentialStatuses.Revoked;
        credential.RevokedAtUtc = now;
        credential.RevokedByUserId = actor.UserId;
        credential.RevokeReason = why;
        credential.Version++;

        var enrollment = await LearningDb.TenantRows(db.LearningEnrollments, actor.TenantId).FirstAsync(e => e.Id == credential.EnrollmentId, ct);
        var assignment = await LearningDb.TenantRows(db.LearningAssignments, actor.TenantId).FirstAsync(a => a.Id == enrollment.AssignmentId, ct);
        var newerLive = await LearningDb.TenantRows(db.LearningAssignments, actor.TenantId).AnyAsync(a => a.EmployeeId == assignment.EmployeeId
            && a.PathId == assignment.PathId && a.Id != assignment.Id && LearningAssignmentStatuses.Active.Contains(a.Status), ct);
        var reopened = false;
        if (!newerLive && assignment.Status != LearningAssignmentStatuses.Cancelled && enrollment.Status == LearningEnrollmentStatuses.Completed)
        {
            var course = await db.LearningCourses.AsNoTracking().FirstAsync(c => c.Id == enrollment.CourseId, ct);
            ClearCompletion(enrollment);
            enrollment.ScorePercent = null;
            enrollment.Status = enrollment.FirstLaunchedAtUtc is null ? LearningEnrollmentStatuses.NotStarted : LearningEnrollmentStatuses.InProgress;
            if (course.CompletionRule == LearningCompletionRules.Assessment)
                enrollment.AttemptAllowance = enrollment.AttemptsUsed + (course.MaxAttempts ?? 1);
            if (course.ContentKind == LearningContentKinds.Cmi5)
                await LearningCmi5Service.StartNewRegistrationAsync(db, enrollment, now, ct);
            enrollment.Version++;
            var othersDone = await LearningDb.TenantRows(db.LearningEnrollments, actor.TenantId)
                .AnyAsync(e => e.AssignmentId == assignment.Id && e.Id != enrollment.Id && e.Status == LearningEnrollmentStatuses.Completed, ct);
            assignment.Status = othersDone || enrollment.FirstLaunchedAtUtc is not null ? LearningAssignmentStatuses.InProgress : LearningAssignmentStatuses.Assigned;
            assignment.CompletedAtUtc = null;
            assignment.DueDate = due;
            assignment.DueReminderSentAtUtc = null;
            assignment.OverdueNoticeSentAtUtc = null;
            assignment.Version++;
            reopened = true;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("learning.credential.revoked", nameof(LearningCredential), credential.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { credential.EmployeeId, credential.CertificateNumber, reason = why, reopened, dueDate = due }), ct);
        if (reopened)
            await audit.WriteAsync("learning.assignment.reopened", nameof(LearningAssignment), assignment.Id.ToString(), actor.Request,
                JsonSerializer.Serialize(new { assignment.EmployeeId, assignment.PathId, enrollmentId = enrollment.Id, dueDate = due, revokedCredential = credential.Id }), ct);
        return await HrCredentialAsync(actor, credential.Id, ct);
    }

    /// <summary>LRN-08: a correction never edits a credential — it supersedes it with a new row (new number) and keeps the old one.</summary>
    public async Task<CredentialDto> CorrectAsync(LearningActor actor, Guid credentialId, CorrectCredentialInput input, CancellationToken ct)
    {
        var why = Reason(input.Reason);
        var old = await db.LearningCredentials.FirstOrDefaultAsync(c => c.TenantId == actor.TenantId && c.Id == credentialId, ct);
        if (old is null || !actor.Scope.CanAccessEmployee(old.EmployeeId)) throw LearningException.NotFound("Certificate");
        actor.RefuseSelf(old.EmployeeId);
        if (old.Status is not (LearningCredentialStatuses.Active or LearningCredentialStatuses.Expired))
            throw LearningException.Conflict("credential_not_current", "Only a current or expired certificate can be corrected.");
        if (input.ScorePercent is < 0 or > 100) throw LearningException.Invalid("A score must be between 0 and 100.");
        var now = clock.GetUtcNow().UtcDateTime;
        var completedOn = input.CompletedOn ?? old.CompletedOn;
        var today = await tenantClock.TodayAsync(actor.TenantId, ct);
        if (completedOn > today) throw LearningException.Invalid("The completion date cannot be in the future.");
        var course = await db.LearningCourses.AsNoTracking().FirstAsync(c => c.Id == old.CourseId, ct);
        var replacement = new LearningCredential
        {
            TenantId = old.TenantId, CompanyId = old.CompanyId, EmployeeId = old.EmployeeId, EnrollmentId = old.EnrollmentId, CourseId = old.CourseId,
            CourseCode = old.CourseCode, PathId = old.PathId, CertificateNumber = await UniqueCertificateNumberAsync(old.TenantId, now, ct),
            VerificationCode = LearningTokens.Code(10), CompletionSource = old.CompletionSource,
            ScorePercent = input.ScorePercent ?? old.ScorePercent, IssuedAtUtc = now, CompletedOn = completedOn,
            ExpiresOn = course.ValidityMonths is int months ? completedOn.AddMonths(months) : null,
            SupersedesCredentialId = old.Id, SupersedeReason = why,
        };
        replacement.Status = replacement.ExpiresOn is { } expires && expires <= today
            ? LearningCredentialStatuses.Expired : LearningCredentialStatuses.Active;
        // A current certificate never states a score below the version's pass mark.
        if (replacement.Status == LearningCredentialStatuses.Active && course.PassMarkPercent is decimal mark && replacement.ScorePercent is decimal stated && stated < mark)
            throw new LearningException(400, "score_below_pass_mark", "A valid certificate cannot show a score below the pass mark. Revoke it instead.");
        old.Status = LearningCredentialStatuses.Superseded;
        old.SupersededAtUtc = now;
        old.Version++;
        db.LearningCredentials.Add(replacement);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("learning.credential.corrected", nameof(LearningCredential), replacement.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new
            {
                replacement.EmployeeId, supersedes = old.Id, oldNumber = old.CertificateNumber, newNumber = replacement.CertificateNumber, reason = why,
                completedOn = new { from = old.CompletedOn, to = replacement.CompletedOn }, score = new { from = old.ScorePercent, to = replacement.ScorePercent },
            }), ct);
        return await HrCredentialAsync(actor, replacement.Id, ct);
    }

    // ── HR: reads ───────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AssignmentViewDto>> ListAssignmentsAsync(LearningActor actor, Guid? pathId, int? employeeId, string? status,
        CancellationToken ct)
    {
        var query = db.LearningAssignments.AsNoTracking().Where(a => a.TenantId == actor.TenantId);
        if (!actor.Scope.IsUnrestricted)
        {
            var allowed = actor.Scope.AllowedEmployeeIds!.ToList();
            query = query.Where(a => allowed.Contains(a.EmployeeId));
        }
        if (pathId is Guid p) query = query.Where(a => a.PathId == p);
        if (employeeId is int e) query = query.Where(a => a.EmployeeId == e);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(a => a.Status == status);
        var rows = await query.OrderByDescending(a => a.AssignedAtUtc).Take(1000).ToListAsync(ct);
        return await AssignmentViewsAsync(actor.TenantId, rows, ct);
    }

    public async Task<AssignmentViewDto> AssignmentViewAsync(LearningActor actor, Guid assignmentId, CancellationToken ct)
    {
        var assignment = await db.LearningAssignments.AsNoTracking().FirstOrDefaultAsync(a => a.TenantId == actor.TenantId && a.Id == assignmentId, ct);
        if (assignment is null || !actor.Scope.CanAccessEmployee(assignment.EmployeeId)) throw LearningException.NotFound("Assignment");
        return (await AssignmentViewsAsync(actor.TenantId, [assignment], ct)).Single();
    }

    public async Task<IReadOnlyList<AttemptSummaryDto>> AttemptsAsync(LearningActor actor, Guid enrollmentId, CancellationToken ct)
    {
        var enrollment = await db.LearningEnrollments.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == actor.TenantId && e.Id == enrollmentId, ct);
        if (enrollment is null || !actor.Scope.CanAccessEmployee(enrollment.EmployeeId)) throw LearningException.NotFound("Enrollment");
        return await db.LearningAttempts.AsNoTracking().Where(a => a.EnrollmentId == enrollmentId).OrderBy(a => a.AttemptNo)
            .Select(a => new AttemptSummaryDto(a.Id, a.AttemptNo, a.Status, a.ScorePercent, a.PassMarkPercent, a.StartedAtUtc, a.SubmittedAtUtc))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CredentialDto>> ListCredentialsAsync(LearningActor actor, int? employeeId, string? status, CancellationToken ct)
    {
        var query = db.LearningCredentials.AsNoTracking().Where(c => c.TenantId == actor.TenantId);
        if (!actor.Scope.IsUnrestricted)
        {
            var allowed = actor.Scope.AllowedEmployeeIds!.ToList();
            query = query.Where(c => allowed.Contains(c.EmployeeId));
        }
        if (employeeId is int e) query = query.Where(c => c.EmployeeId == e);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(c => c.Status == status);
        return await CredentialsAsync(actor.TenantId, query.OrderByDescending(c => c.IssuedAtUtc).Take(1000), ct);
    }

    public async Task<CredentialDto> HrCredentialAsync(LearningActor actor, Guid credentialId, CancellationToken ct)
    {
        var credential = (await CredentialsAsync(actor.TenantId, db.LearningCredentials.AsNoTracking()
            .Where(c => c.TenantId == actor.TenantId && c.Id == credentialId), ct)).SingleOrDefault();
        if (credential is null || !actor.Scope.CanAccessEmployee(credential.EmployeeId)) throw LearningException.NotFound("Certificate");
        return credential;
    }

    /// <summary>Verification: the certificate number AND its printed verification code must both match.</summary>
    public async Task<CredentialDto> VerifyAsync(LearningActor actor, string? number, string? code, CancellationToken ct)
    {
        var n = (number ?? string.Empty).Trim().ToUpperInvariant();
        var c = (code ?? string.Empty).Trim().ToUpperInvariant();
        var row = await db.LearningCredentials.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == actor.TenantId && x.CertificateNumber == n, ct);
        if (row is null || !LearningTokens.HashEquals(LearningTokens.Sha256Hex(row.VerificationCode), LearningTokens.Sha256Hex(c)))
            throw LearningException.NotFound("Certificate");
        return (await CredentialsAsync(actor.TenantId, db.LearningCredentials.AsNoTracking().Where(x => x.Id == row.Id), ct)).Single();
    }

    // ── Shared reads ────────────────────────────────────────────────────────────────────────────────────────────────

    internal async Task<IReadOnlyList<AssignmentViewDto>> AssignmentViewsAsync(Guid tenantId, IReadOnlyList<LearningAssignment> assignments, CancellationToken ct)
    {
        if (assignments.Count == 0) return [];
        var ids = assignments.Select(a => a.Id).ToList();
        var enrollments = await db.LearningEnrollments.AsNoTracking().Where(e => ids.Contains(e.AssignmentId)).OrderBy(e => e.Sequence).ToListAsync(ct);
        var courseIds = enrollments.Select(e => e.CourseId).Distinct().ToList();
        var courses = await db.LearningCourses.AsNoTracking().Where(c => courseIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var itemIds = enrollments.Select(e => e.PathItemId).Distinct().ToList();
        var items = await db.LearningPathItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var pathIds = assignments.Select(a => a.PathId).Distinct().ToList();
        var paths = await db.LearningPaths.AsNoTracking().Where(p => pathIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var employeeIds = assignments.Select(a => a.EmployeeId).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && employeeIds.Contains(e.Id))
            .Select(e => new { e.Id, e.FullName, e.EmployeeCode, e.Status, e.JoiningDate }).ToDictionaryAsync(e => e.Id, ct);
        var today = await tenantClock.TodayAsync(tenantId, ct);

        return assignments.Select(a =>
        {
            var mine = enrollments.Where(e => e.AssignmentId == a.Id).ToList();
            var byItem = mine.ToDictionary(e => e.PathItemId);
            var dtos = mine.Select(e => ItemDto(e, courses[e.CourseId], items.GetValueOrDefault(e.PathItemId), byItem, courses, a)).ToList();
            var required = mine.Where(e => e.IsRequired).ToList();
            var done = required.Count(e => e.Status == LearningEnrollmentStatuses.Completed);
            var path = paths[a.PathId];
            var employee = employees.GetValueOrDefault(a.EmployeeId);
            return new AssignmentViewDto(a.Id, a.EmployeeId, employee?.FullName ?? string.Empty, employee?.EmployeeCode ?? string.Empty, a.PathId,
                path.Name, path.NameAr, a.Status, a.IsMandatory, a.DueDate,
                LearningOverdue.IsOverdue(a.Status, a.DueDate, today,
                    employee is not null && LearningOverdue.IsChased(employee.Status, employee.JoiningDate, today),
                    LearningOverdue.IsAwaitingVerification(mine.Select(e => (e.IsRequired, e.Status)))),
                a.Cycle, a.Source, required.Count, done, required.Count == 0 ? 0m : Math.Round(done * 100m / required.Count, 2),
                a.AssignedAtUtc, a.CompletedAtUtc, a.CancelReason, dtos,
                LearningAssignmentStatuses.Active.Contains(a.Status) && LearningOverdue.IsAwaitingVerification(mine.Select(e => (e.IsRequired, e.Status))));
        }).ToList();
    }

    private static EnrollmentItemDto ItemDto(LearningEnrollment e, LearningCourse course, LearningPathItem? item,
        IReadOnlyDictionary<Guid, LearningEnrollment> byItem, IReadOnlyDictionary<Guid, LearningCourse> courses, LearningAssignment assignment)
    {
        LearningEnrollment? blocker = null;
        if (item?.PrerequisiteItemId is Guid pre && byItem.TryGetValue(pre, out var prerequisite) && prerequisite.Status != LearningEnrollmentStatuses.Completed)
            blocker = prerequisite;
        var live = LearningAssignmentStatuses.Active.Contains(assignment.Status);
        var next = e.Status switch
        {
            LearningEnrollmentStatuses.Completed => LearningNextActions.Done,
            LearningEnrollmentStatuses.PendingVerification => LearningNextActions.PendingVerification,
            LearningEnrollmentStatuses.Locked => LearningNextActions.Locked,
            LearningEnrollmentStatuses.Cancelled => LearningNextActions.Closed,
            _ when !live => LearningNextActions.Closed,
            _ when blocker is not null => LearningNextActions.Blocked,
            _ when course.CompletionRule == LearningCompletionRules.Assessment && e.FirstLaunchedAtUtc is not null => LearningNextActions.TakeAssessment,
            _ when course.CompletionRule == LearningCompletionRules.Acknowledgement && e.FirstLaunchedAtUtc is not null => LearningNextActions.Acknowledge,
            _ => LearningNextActions.Open,
        };
        var blockerCourse = blocker is null ? null : courses.GetValueOrDefault(blocker.CourseId);
        return new EnrollmentItemDto(e.Id, e.Sequence, course.Code, course.VersionNumber, course.Title, course.TitleAr, course.ContentKind,
            course.CompletionRule, e.IsRequired, e.Status, blocker is not null, blockerCourse?.Title, blockerCourse?.TitleAr, e.ProgressPercent,
            e.ScorePercent, e.AttemptsUsed, e.AttemptAllowance, course.EstimatedMinutes, e.FirstLaunchedAtUtc, e.CompletedAtUtc, e.CompletionSource, next);
    }

    private async Task<EnrollmentDetailDto> DetailAsync(LearningEnrollment enrollment, CancellationToken ct)
    {
        var assignment = await db.LearningAssignments.AsNoTracking().FirstAsync(a => a.Id == enrollment.AssignmentId, ct);
        var view = (await AssignmentViewsAsync(enrollment.TenantId, [assignment], ct)).Single();
        var item = view.Items.Single(i => i.Id == enrollment.Id);
        var course = await db.LearningCourses.AsNoTracking().FirstAsync(c => c.Id == enrollment.CourseId, ct);
        var attempts = await db.LearningAttempts.AsNoTracking().Where(a => a.EnrollmentId == enrollment.Id).OrderBy(a => a.AttemptNo)
            .Select(a => new AttemptSummaryDto(a.Id, a.AttemptNo, a.Status, a.ScorePercent, a.PassMarkPercent, a.StartedAtUtc, a.SubmittedAtUtc))
            .ToListAsync(ct);
        var credentialId = await db.LearningCredentials.AsNoTracking().Where(c => c.EnrollmentId == enrollment.Id)
            .OrderByDescending(c => c.IssuedAtUtc).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        return new EnrollmentDetailDto(item, assignment.Id, assignment.Status, view.PathName, view.PathNameAr, course.Description, course.DescriptionAr,
            course.PassMarkPercent, LearningAssessment.Parse(course.AssessmentJson).Questions.Count, attempts,
            attempts.FirstOrDefault(a => a.Status == LearningAttemptStatuses.InProgress)?.Id, credentialId,
            // Never hand out the material of an item that is still waiting for its prerequisite.
            course.ContentKind == LearningContentKinds.Link && !item.IsBlocked ? course.LinkUrl : null);
    }

    internal async Task<IReadOnlyList<CredentialDto>> CredentialsAsync(Guid tenantId, IQueryable<LearningCredential> query, CancellationToken ct)
    {
        var rows = await query.ToListAsync(ct);
        if (rows.Count == 0) return [];
        var courseIds = rows.Select(r => r.CourseId).Distinct().ToList();
        var courses = await db.LearningCourses.AsNoTracking().Where(c => courseIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var pathIds = rows.Select(r => r.PathId).Distinct().ToList();
        var paths = await db.LearningPaths.AsNoTracking().Where(p => pathIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var employeeIds = rows.Select(r => r.EmployeeId).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && employeeIds.Contains(e.Id))
            .Select(e => new { e.Id, e.FullName, e.ArabicName, e.EmployeeCode, e.CompanyId }).ToDictionaryAsync(e => e.Id, ct);
        var companyIds = employees.Values.Select(e => e.CompanyId).OfType<Guid>().Distinct().ToList();
        var companies = await db.Companies.AsNoTracking().Where(c => c.TenantId == tenantId && companyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.LegalNameEn, ct);
        return rows.Select(r =>
        {
            var course = courses[r.CourseId];
            var path = paths[r.PathId];
            var employee = employees.GetValueOrDefault(r.EmployeeId);
            return new CredentialDto(r.Id, r.CertificateNumber, r.VerificationCode, r.Status, r.EmployeeId, employee?.FullName ?? string.Empty,
                string.IsNullOrWhiteSpace(employee?.ArabicName) ? null : employee.ArabicName, employee?.EmployeeCode ?? string.Empty, course.Code,
                course.VersionNumber, course.Title, course.TitleAr, path.Name, path.NameAr, r.CompletionSource, r.ScorePercent, r.CompletedOn, r.ExpiresOn,
                r.IssuedAtUtc, r.SupersedesCredentialId, r.SupersedeReason, r.SupersededAtUtc, r.RevokedAtUtc, r.RevokeReason,
                employee?.CompanyId is Guid cid ? companies.GetValueOrDefault(cid) : null);
        }).OrderByDescending(c => c.IssuedAtUtc).ToList();
    }

    private async Task<IReadOnlyList<TranscriptEntryDto>> TranscriptAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var done = await db.LearningEnrollments.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.EmployeeId == employeeId && e.Status == LearningEnrollmentStatuses.Completed)
            .ToListAsync(ct);
        if (done.Count == 0) return [];
        var courseIds = done.Select(e => e.CourseId).Distinct().ToList();
        var courses = await db.LearningCourses.AsNoTracking().Where(c => courseIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var assignmentIds = done.Select(e => e.AssignmentId).Distinct().ToList();
        var assignments = await db.LearningAssignments.AsNoTracking().Where(a => assignmentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var pathIds = assignments.Values.Select(a => a.PathId).Distinct().ToList();
        var paths = await db.LearningPaths.AsNoTracking().Where(p => pathIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var enrollmentIds = done.Select(e => e.Id).ToList();
        var credentials = await db.LearningCredentials.AsNoTracking().Where(c => enrollmentIds.Contains(c.EnrollmentId)).ToListAsync(ct);
        return done.Select(e =>
        {
            var course = courses[e.CourseId];
            var assignment = assignments[e.AssignmentId];
            var path = paths[assignment.PathId];
            // The certificate to show is the current one in the chain (a correction supersedes; the newest row wins).
            var credential = credentials.Where(c => c.EnrollmentId == e.Id).OrderByDescending(c => c.IssuedAtUtc).FirstOrDefault();
            return new TranscriptEntryDto(e.Id, course.Code, course.VersionNumber, course.Title, course.TitleAr, path.Name, path.NameAr, assignment.Cycle,
                e.CompletedAtUtc!.Value, e.CompletionSource!, e.ScorePercent, credential?.Id, credential?.CertificateNumber, credential?.Status,
                credential?.ExpiresOn);
        }).OrderByDescending(t => t.CompletedAtUtc).ToList();
    }

    // ── Guards ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The caller's OWN enrollment. Anyone else's (or another tenant's) is "not found" — never "forbidden", which would confirm it exists.</summary>
    private async Task<LearningEnrollment> OwnEnrollmentAsync(Guid tenantId, int employeeId, Guid enrollmentId, bool tracked, CancellationToken ct)
    {
        var query = tracked ? db.LearningEnrollments : db.LearningEnrollments.AsNoTracking();
        return await query.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == enrollmentId && e.EmployeeId == employeeId, ct)
               ?? throw LearningException.NotFound("Course");
    }

    /// <summary>The assignment is live, the item is not done/locked/cancelled, and its prerequisite is complete (LRN-02, server-side).</summary>
    private async Task<(LearningAssignment Assignment, LearningCourse Course)> GuardActionableAsync(LearningEnrollment enrollment, CancellationToken ct)
    {
        var assignment = await db.LearningAssignments.FirstAsync(a => a.Id == enrollment.AssignmentId, ct);
        if (!LearningAssignmentStatuses.Active.Contains(assignment.Status) && enrollment.Status != LearningEnrollmentStatuses.Completed)
            throw LearningException.Conflict("assignment_not_live", "This learning is no longer assigned to you.");
        if (enrollment.Status == LearningEnrollmentStatuses.Completed)
            throw LearningException.Conflict("already_completed", "You have already completed this course.");
        if (enrollment.Status == LearningEnrollmentStatuses.PendingVerification)
            throw LearningException.Conflict("awaiting_verification", "Your completion is waiting for HR to verify it.");
        if (enrollment.Status == LearningEnrollmentStatuses.Locked)
            throw LearningException.Conflict("enrollment_locked", "You have used every attempt. Ask HR to reset your attempts.");
        if (enrollment.Status == LearningEnrollmentStatuses.Cancelled)
            throw LearningException.Conflict("assignment_not_live", "This learning is no longer assigned to you.");
        var item = await db.LearningPathItems.AsNoTracking().FirstAsync(i => i.Id == enrollment.PathItemId, ct);
        if (item.PrerequisiteItemId is Guid pre)
        {
            var prerequisite = await db.LearningEnrollments.AsNoTracking()
                .FirstOrDefaultAsync(e => e.AssignmentId == enrollment.AssignmentId && e.PathItemId == pre, ct);
            if (prerequisite is not null && prerequisite.Status != LearningEnrollmentStatuses.Completed)
                throw LearningException.Conflict("prerequisite_incomplete", "Complete the earlier course in this path first.");
        }
        var course = await db.LearningCourses.AsNoTracking().FirstAsync(c => c.Id == enrollment.CourseId, ct);
        return (assignment, course);
    }

    private static void Touch(LearningEnrollment enrollment, LearningAssignment assignment, DateTime now)
    {
        enrollment.FirstLaunchedAtUtc ??= now;
        enrollment.LastActivityAtUtc = now;
        if (enrollment.Status == LearningEnrollmentStatuses.NotStarted) enrollment.Status = LearningEnrollmentStatuses.InProgress;
        if (assignment.Status == LearningAssignmentStatuses.Assigned)
        {
            assignment.Status = LearningAssignmentStatuses.InProgress;
            assignment.StartedAtUtc ??= now;
        }
    }

    private static AttemptDto AttemptView(LearningAttempt attempt, AssessmentBank bank, LearningEnrollment enrollment) =>
        new(attempt.Id, attempt.AttemptNo, attempt.Status,
            LearningAssessment.ForClient(bank, JsonSerializer.Deserialize<List<string>>(attempt.QuestionOrderJson) ?? []),
            attempt.PassMarkPercent, enrollment.AttemptsUsed, enrollment.AttemptAllowance, attempt.StartedAtUtc);

    private static string Reason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500
            ? throw LearningException.Invalid("Give a reason (up to 500 characters). It is kept in the audit trail.")
            : reason.Trim();
}
