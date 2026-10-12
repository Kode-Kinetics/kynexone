using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// LRN — Learning management (capability-closure register LRN-01..15, REGISTER.md §3.1).
//
// Ten tables, the module's whole cap (SCHEMA_IMPACT.md). The catalogue (courses, paths, path items, rules) is
// tenant-wide configuration; every record about a person (assignment, enrollment, attempt, credential, cmi5 session,
// xAPI statement) is company-scoped operational data stamped from the employee's company.
//
// Vocabularies are C# constants; LearningModelConfiguration builds each named CHECK from the same array, so the
// database and the code cannot disagree about a status value.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

public static class LearningCourseStatuses
{
    /// <summary>Editable. Never assignable.</summary>
    public const string Draft = "Draft";
    /// <summary>Immutable. New assignments pin the latest Published version of a code.</summary>
    public const string Published = "Published";
    /// <summary>Immutable and no longer assignable. Enrollments already pinned to it continue.</summary>
    public const string Retired = "Retired";
    public static readonly string[] All = [Draft, Published, Retired];
}

public static class LearningContentKinds
{
    /// <summary>Sanitised rich text rendered in-app (LearningHtmlSanitizer).</summary>
    public const string Lesson = "Lesson";
    /// <summary>An external URL opened in a new tab (video, document).</summary>
    public const string Link = "Link";
    /// <summary>A cmi5 assignable unit, launched in its own window (§3.1).</summary>
    public const string Cmi5 = "Cmi5";
    public static readonly string[] All = [Lesson, Link, Cmi5];
}

/// <summary>Where completion evidence may come from (§3.1 item 3). Exactly one per course version.</summary>
public static class LearningCompletionRules
{
    /// <summary>A passed native assessment on this version.</summary>
    public const string Assessment = "Assessment";
    /// <summary>A cmi5 statement that satisfies the version's moveOn.</summary>
    public const string Cmi5MoveOn = "Cmi5MoveOn";
    /// <summary>The employee acknowledges a Lesson/Link with no assessment after opening it. Labelled on the transcript.</summary>
    public const string Acknowledgement = "Acknowledgement";
    public static readonly string[] All = [Assessment, Cmi5MoveOn, Acknowledgement];
}

/// <summary>cmi5 moveOn values (cmi5 Quartz §13.1.4).</summary>
public static class Cmi5MoveOnValues
{
    public const string Passed = "Passed";
    public const string Completed = "Completed";
    public const string CompletedAndPassed = "CompletedAndPassed";
    public const string CompletedOrPassed = "CompletedOrPassed";
    public const string NotApplicable = "NotApplicable";
    public static readonly string[] All = [Passed, Completed, CompletedAndPassed, CompletedOrPassed, NotApplicable];
}

public static class LearningPathStatuses
{
    public const string Draft = "Draft";
    public const string Active = "Active";
    public const string Archived = "Archived";
    public static readonly string[] All = [Draft, Active, Archived];
}

public static class LearningRuleTargets
{
    public const string Company = "Company";
    public const string Department = "Department";
    public const string Designation = "Designation";
    public const string Grade = "Grade";
    public static readonly string[] All = [Company, Department, Designation, Grade];
}

public static class LearningAssignmentStatuses
{
    public const string Assigned = "Assigned";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = [Assigned, InProgress, Completed, Cancelled];
    /// <summary>The statuses the partial unique index treats as live: one per (employee, path).</summary>
    public static readonly string[] Active = [Assigned, InProgress];
}

public static class LearningAssignmentSources
{
    public const string Employee = "Employee";
    public const string Department = "Department";
    public const string Designation = "Designation";
    public const string Grade = "Grade";
    public const string Rule = "Rule";
    public const string Renewal = "Renewal";
    public static readonly string[] All = [Employee, Department, Designation, Grade, Rule, Renewal];
}

public static class LearningEnrollmentStatuses
{
    public const string NotStarted = "NotStarted";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    /// <summary>Assessment attempts exhausted. Only HR (learning.records.correct) can reset it, with a reason.</summary>
    public const string Locked = "Locked";
    /// <summary>The assignment was cancelled before this item was completed.</summary>
    public const string Cancelled = "Cancelled";
    /// <summary>
    /// Content-reported completion (cmi5) on a course version that requires HR verification: the evidence is recorded,
    /// but the item does not count as completed and no credential exists until an HR user (never the learner) verifies.
    /// </summary>
    public const string PendingVerification = "PendingVerification";
    public static readonly string[] All = [NotStarted, InProgress, Completed, Locked, Cancelled, PendingVerification];
    /// <summary>Statuses that carry completion evidence (completed, or completed and waiting for verification).</summary>
    public static readonly string[] Evidenced = [Completed, PendingVerification];
    public static bool IsDone(string status) => status is Completed or PendingVerification;
}

/// <summary>The three completion-evidence sources (§3.1 item 3). Nothing else completes an enrollment.</summary>
public static class LearningCompletionSources
{
    public const string Assessment = "Assessment";
    public const string Cmi5 = "Cmi5";
    public const string Acknowledgement = "Acknowledgement";
    public static readonly string[] All = [Assessment, Cmi5, Acknowledgement];
}

public static class LearningAttemptStatuses
{
    public const string InProgress = "InProgress";
    public const string Passed = "Passed";
    public const string Failed = "Failed";
    public static readonly string[] All = [InProgress, Passed, Failed];
}

public static class LearningCredentialStatuses
{
    public const string Active = "Active";
    public const string Expired = "Expired";
    public const string Revoked = "Revoked";
    /// <summary>Replaced by a later row (correction or renewal). The successor names this row in SupersedesCredentialId.</summary>
    public const string Superseded = "Superseded";
    public static readonly string[] All = [Active, Expired, Revoked, Superseded];
}

public static class LearningSessionStatuses
{
    /// <summary>Session issued; launch recorded. The fetch URL has not been used yet.</summary>
    public const string Launched = "Launched";
    /// <summary>The AU fetched its auth token.</summary>
    public const string Fetched = "Fetched";
    /// <summary>The AU sent cmi5 "initialized".</summary>
    public const string Initialized = "Initialized";
    /// <summary>The AU sent cmi5 "terminated".</summary>
    public const string Terminated = "Terminated";
    /// <summary>The session ended without "terminated" (token expired, or a relaunch replaced it). The LMS records "abandoned".</summary>
    public const string Abandoned = "Abandoned";
    public static readonly string[] All = [Launched, Fetched, Initialized, Terminated, Abandoned];
    public static readonly string[] Open = [Launched, Fetched, Initialized];
}

/// <summary>
/// One VERSION of a course. Course identity is (tenant, <see cref="Code"/>); <see cref="VersionNumber"/> counts its
/// versions. A Published or Retired version is immutable — editing one creates the next Draft version.
///
/// <para>WHY A TABLE. Nothing in the schema holds learning content, a question bank, a pass mark or a completion rule.
/// HR letters and policy documents are documents to read, not things to complete with evidence. L&amp;D (HR) owns it;
/// versions are kept for as long as any enrollment, attempt or credential points at them (FK Restrict), because a
/// transcript must show the exact version and question bank a person was assessed on.</para>
///
/// <para>Tenant-wide configuration, not company-scoped: one catalogue serves every company of a group. Company
/// targeting happens on paths (eligibility) and assignments (stamped from the employee).</para>
/// </summary>
public class LearningCourse : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Stable identity across versions, e.g. FIRE-SAFETY. Upper-case, unique per tenant with the version number.</summary>
    public string Code { get; set; } = string.Empty;
    public int VersionNumber { get; set; } = 1;
    public string Status { get; set; } = LearningCourseStatuses.Draft;

    public string Title { get; set; } = string.Empty;
    public string? TitleAr { get; set; }
    public string? Description { get; set; }
    public string? DescriptionAr { get; set; }
    public int? EstimatedMinutes { get; set; }

    public string ContentKind { get; set; } = LearningContentKinds.Lesson;
    /// <summary>Lesson only. Already sanitised by LearningHtmlSanitizer on the way in; rendered as-is.</summary>
    public string? LessonHtml { get; set; }
    /// <summary>Link only. http(s) URL.</summary>
    public string? LinkUrl { get; set; }
    /// <summary>Cmi5 only. The AU launch URL (absolute http(s)).</summary>
    public string? Cmi5AuUrl { get; set; }
    /// <summary>Cmi5 only. The AU's activity IRI (the object.id its statements must carry).</summary>
    public string? Cmi5ActivityId { get; set; }
    /// <summary>Cmi5 only. One of <see cref="Cmi5MoveOnValues"/>.</summary>
    public string? Cmi5MoveOn { get; set; }
    /// <summary>Cmi5 only. scheme://host[:port] of <see cref="Cmi5AuUrl"/>: the one origin allowed to call the xAPI endpoints.</summary>
    public string? Cmi5AuOrigin { get; set; }

    public string CompletionRule { get; set; } = LearningCompletionRules.Acknowledgement;

    /// <summary>Assessment only. Immutable JSON snapshot of the question bank WITH the correct answers. Never sent to an employee.</summary>
    public string? AssessmentJson { get; set; }
    /// <summary>Assessment: the pass mark (above 0). Cmi5 with a Passed-family moveOn: the mastery score sent to the unit
    /// in LMS.LaunchData; a "passed" statement below it is refused. 0–100.</summary>
    public decimal? PassMarkPercent { get; set; }
    /// <summary>Assessment only. 1–20.</summary>
    public int? MaxAttempts { get; set; }
    public bool ShuffleQuestions { get; set; }

    /// <summary>Months a credential from this version stays valid. Null = never expires.</summary>
    public int? ValidityMonths { get; set; }
    public bool IssuesCertificate { get; set; } = true;
    /// <summary>
    /// Cmi5 only. A cmi5 completion is evidence REPORTED BY THE CONTENT (the learner's browser holds the session token),
    /// so when set, a content-reported completion waits in PendingVerification until an HR user verifies it, and only
    /// then is the credential issued. Defaults on for a cmi5 version that issues a certificate or has a validity period.
    /// </summary>
    public bool RequiresHrVerification { get; set; }

    public DateTime? PublishedAtUtc { get; set; }
    public Guid? PublishedByUserId { get; set; }
    public DateTime? RetiredAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    /// <summary>Optimistic concurrency token (edits of a Draft, publish, retire).</summary>
    public int Version { get; set; }
}

/// <summary>
/// An ordered set of courses assigned together, with eligibility and an effective window.
///
/// <para>WHY A TABLE. Assignment, prerequisites, eligibility and progress are all about a set of courses, not one —
/// no existing table models a curriculum. L&amp;D (HR) owns it; kept while any assignment points at it (FK Restrict).
/// Tenant-wide configuration (not company-scoped): eligibility names the companies it applies to.</para>
/// </summary>
public class LearningPath : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string? Description { get; set; }
    public string Status { get; set; } = LearningPathStatuses.Draft;
    /// <summary>
    /// Eligibility lists as JSON <c>{"companyIds":[],"departmentIds":[],"designationIds":[],"gradeIds":[]}</c>. An empty
    /// list means "any". Read whole with the path and evaluated in LearningEligibility, never filtered on in SQL.
    /// </summary>
    public string EligibilityJson { get; set; } = "{}";
    public DateOnly? EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    /// <summary>Days before a credential from this path expires that the sweep opens a renewal assignment.</summary>
    public int RenewalWindowDays { get; set; } = 30;
    public DateTime? ActivatedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public int Version { get; set; }
}

/// <summary>
/// One course in a path, in order. <see cref="CourseId"/> names a version row of the course identity; assignment
/// resolves the latest Published version of that course's code and pins it on the enrollment.
///
/// <para>WHY A TABLE. Order, required/optional and prerequisites are per (path, course) and are enforced server-side
/// on launch and attempt. Part of the path aggregate: same owner and retention as <see cref="LearningPath"/>.</para>
/// </summary>
public class LearningPathItem : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PathId { get; set; }
    public Guid CourseId { get; set; }
    public int Sequence { get; set; }
    public bool IsRequired { get; set; } = true;
    /// <summary>A REQUIRED item earlier in the same path that must be completed before this one can be launched or attempted.</summary>
    public Guid? PrerequisiteItemId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A saved assignment rule: "everyone in department X gets path P, due N days after assignment". The sweep applies it
/// to employees who newly match.
///
/// <para>WHY A TABLE. A one-off bulk assignment is just assignments; a rule is a standing instruction with its own
/// lifecycle (active/inactive, last applied) that must survive to catch future hires and transfers. L&amp;D (HR) owns
/// it; kept while assignments name it (FK SET NULL keeps the assignment if a rule is ever removed).</para>
/// </summary>
public class LearningAssignmentRule : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PathId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TargetKind { get; set; } = LearningRuleTargets.Department;
    public Guid TargetId { get; set; }
    public int DueInDays { get; set; } = 30;
    public bool IsMandatory { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTime? LastAppliedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public int Version { get; set; }
}

/// <summary>
/// Employee × path, one learning cycle. The DB partial unique index allows one live (Assigned/InProgress) row per
/// (tenant, employee, path); a renewal is a new row with the next <see cref="Cycle"/>.
///
/// <para>WHY A TABLE. Due dates, mandatory flags, reminders and overdue state are per person per path — no existing
/// table assigns work with evidence of completion. HR owns it; kept with the employee's training record (it is the
/// evidence of mandatory training having been assigned), never hard-deleted: cancellation is a status.</para>
/// </summary>
public class LearningAssignment : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public int EmployeeId { get; set; }
    public Guid PathId { get; set; }
    public int Cycle { get; set; } = 1;
    public string Source { get; set; } = LearningAssignmentSources.Employee;
    public Guid? RuleId { get; set; }
    /// <summary>Renewal only: the credential whose expiry opened this cycle.</summary>
    public Guid? RenewalOfCredentialId { get; set; }
    public string Status { get; set; } = LearningAssignmentStatuses.Assigned;
    public bool IsMandatory { get; set; } = true;
    public DateOnly? DueDate { get; set; }

    // Org placement at assignment time (history: reports by the department someone was in when assigned).
    public Guid? DepartmentId { get; set; }
    public Guid? DesignationId { get; set; }
    public Guid? GradeId { get; set; }

    public DateTime AssignedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? AssignedByUserId { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? CancelReason { get; set; }

    /// <summary>Reminder dedupe: set when LEARNING_DUE was enqueued for this assignment; the sweep never sends it twice.</summary>
    public DateTime? DueReminderSentAtUtc { get; set; }
    /// <summary>Reminder dedupe: set when LEARNING_OVERDUE was enqueued.</summary>
    public DateTime? OverdueNoticeSentAtUtc { get; set; }
    /// <summary>When the sweep last found nobody to tell (no login, no manager login); retried at most once a day.</summary>
    public DateTime? NoRecipientAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public int Version { get; set; }
}

/// <summary>
/// Assignment × path item: one person's progress on one course, pinned to the course VERSION current at assignment.
///
/// <para>WHY A TABLE. Launch, progress and completion evidence are per course within an assignment, and must stay
/// pinned to the version (and question bank) the person actually took. Part of the assignment aggregate: same owner
/// and retention.</para>
/// </summary>
public class LearningEnrollment : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid AssignmentId { get; set; }
    public int EmployeeId { get; set; }
    public Guid PathItemId { get; set; }
    /// <summary>The pinned course VERSION row.</summary>
    public Guid CourseId { get; set; }
    public int Sequence { get; set; }
    public bool IsRequired { get; set; } = true;
    public string Status { get; set; } = LearningEnrollmentStatuses.NotStarted;

    /// <summary>Launch evidence: first time the content was opened (lesson view, link open, cmi5 session issued).</summary>
    public DateTime? FirstLaunchedAtUtc { get; set; }
    public DateTime? LastActivityAtUtc { get; set; }
    /// <summary>
    /// The cmi5 registration of this enrollment (cmi5 units only): every launch uses it, and moveOn is evaluated only over
    /// its statements. HR rejecting a reported completion, or revoking the credential, starts a new one — so the old
    /// statements stay as history but can never complete the enrollment again.
    /// </summary>
    public Guid? Cmi5Registration { get; set; }
    /// <summary>Progress evidence (0–100) from cmi5 "progressed" or a lesson view. Never completion.</summary>
    public decimal? ProgressPercent { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
    /// <summary>One of <see cref="LearningCompletionSources"/>.</summary>
    public string? CompletionSource { get; set; }
    /// <summary>The evidence row: attempt id, xAPI statement id, or "ack".</summary>
    public string? CompletionEvidenceRef { get; set; }
    public decimal? ScorePercent { get; set; }

    /// <summary>Attempts allowed so far: the version's MaxAttempts, plus MaxAttempts again per HR reset.</summary>
    public int AttemptAllowance { get; set; }
    public int AttemptsUsed { get; set; }
    public int ResetCount { get; set; }
    public DateTime? LockedAtUtc { get; set; }
    /// <summary>When an HR user verified a content-reported (cmi5) completion, and who.</summary>
    public DateTime? VerifiedAtUtc { get; set; }
    public Guid? VerifiedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public int Version { get; set; }
}

/// <summary>
/// One assessment attempt. Server-side scored; the question order and the submitted answers are kept as evidence.
///
/// <para>WHY A TABLE. Attempt limits, retakes and the audit of a score need each attempt as its own row; no existing
/// table holds a scored submission. HR owns it; retained with the enrollment.</para>
/// </summary>
public class LearningAttempt : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid EnrollmentId { get; set; }
    public int EmployeeId { get; set; }
    public int AttemptNo { get; set; }
    public string Status { get; set; } = LearningAttemptStatuses.InProgress;
    /// <summary>JSON array of question ids in the order shown (shuffled when the version asks).</summary>
    public string QuestionOrderJson { get; set; } = "[]";
    /// <summary>JSON object questionId → selected option ids, as submitted.</summary>
    public string? AnswersJson { get; set; }
    public decimal? ScorePercent { get; set; }
    /// <summary>The pass mark in force when the attempt started (witness).</summary>
    public decimal PassMarkPercent { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAtUtc { get; set; }
    public int Version { get; set; }
}

/// <summary>
/// A certificate / qualification issued on completion: number, expiry, revocation, and the supersede chain
/// (correction or renewal adds a NEW row; the old one becomes Superseded and stays as history).
///
/// <para>WHY A TABLE. A qualification has its own lifecycle (expiry, revocation, renewal) independent of the
/// enrollment that earned it; EmployeeDocument holds uploaded files, not issued evidence with a verification number.
/// HR owns it; kept as long as the employee record (training evidence), never deleted — revocation is a status.</para>
/// </summary>
public class LearningCredential : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public int EmployeeId { get; set; }
    public Guid EnrollmentId { get; set; }
    /// <summary>The course VERSION completed.</summary>
    public Guid CourseId { get; set; }
    public string CourseCode { get; set; } = string.Empty;
    /// <summary>The path the enrollment belongs to (renewal reassigns this path).</summary>
    public Guid PathId { get; set; }
    /// <summary>Human-facing, unique per tenant: LRN-yyyy-XXXXXXXX.</summary>
    public string CertificateNumber { get; set; } = string.Empty;
    /// <summary>Random, unguessable code printed on the certificate; quoted together with the number to verify it.</summary>
    public string VerificationCode { get; set; } = string.Empty;
    public string Status { get; set; } = LearningCredentialStatuses.Active;
    public string CompletionSource { get; set; } = LearningCompletionSources.Assessment;
    public decimal? ScorePercent { get; set; }
    public DateTime IssuedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>The completion date the credential attests (a correction may restate it).</summary>
    public DateOnly CompletedOn { get; set; }
    public DateOnly? ExpiresOn { get; set; }

    public DateTime? RevokedAtUtc { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public string? RevokeReason { get; set; }

    /// <summary>The row this one replaces (correction or renewal).</summary>
    public Guid? SupersedesCredentialId { get; set; }
    public string? SupersedeReason { get; set; }
    public DateTime? SupersededAtUtc { get; set; }

    /// <summary>Renewal: the assignment opened for this credential's next cycle.</summary>
    public Guid? RenewalAssignmentId { get; set; }
    /// <summary>Reminder dedupe for LEARNING_EXPIRING.</summary>
    public DateTime? ExpiringNoticeSentAtUtc { get; set; }
    /// <summary>Reminder dedupe for LEARNING_EXPIRED.</summary>
    public DateTime? ExpiredNoticeSentAtUtc { get; set; }
    /// <summary>When the sweep last found nobody to tell (no login, no manager login); retried at most once a day.</summary>
    public DateTime? NoRecipientAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public int Version { get; set; }
}

/// <summary>
/// One cmi5 launch (§3.1): the registration, the one-time fetch token, and the short-lived auth token — both stored
/// only as SHA-256 hashes.
///
/// <para>WHY A TABLE. A launch is a credential with its own expiry and single use; no existing table holds one (ERP API
/// keys are long-lived machine keys; welcome codes are login bootstrap). HR owns it; the token hashes are worthless
/// after expiry, and rows are kept with the enrollment as launch evidence.</para>
///
/// <para>NO CompanyId, deliberately. The AU calls in anonymously (its credential is the session token), and an
/// anonymous request in strict scope mode resolves to an EMPTY company scope, so a company-scoped table would read as
/// empty and every cmi5 call would fail in production only. The session is reached by its token, never listed; its
/// company is its enrollment's, which IS company-scoped.</para>
/// </summary>
public class LearningLaunchSession : ITenantOwned
{
    /// <summary>Also the cmi5 session id (context extension https://w3id.org/xapi/cmi5/context/extensions/sessionid).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid EnrollmentId { get; set; }
    public int EmployeeId { get; set; }
    /// <summary>cmi5 registration: constant across every session of one enrollment.</summary>
    public Guid Registration { get; set; }
    public string ActivityId { get; set; } = string.Empty;
    /// <summary>The actor account name (Employee.PublicId) and homePage the AU must send back.</summary>
    public string ActorAccountName { get; set; } = string.Empty;
    public string ActorHomePage { get; set; } = string.Empty;
    public string AuOrigin { get; set; } = string.Empty;
    public string MoveOn { get; set; } = Cmi5MoveOnValues.Completed;
    public string? ReturnUrl { get; set; }
    public string Status { get; set; } = LearningSessionStatuses.Launched;

    /// <summary>SHA-256 hex of the one-time fetch token (in the fetch URL).</summary>
    public string FetchTokenHash { get; set; } = string.Empty;
    public DateTime FetchExpiresAtUtc { get; set; }
    public DateTime? FetchUsedAtUtc { get; set; }
    /// <summary>SHA-256 hex of the auth secret returned once by fetch. Null until fetched.</summary>
    public string? AuthTokenHash { get; set; }
    public DateTime? AuthExpiresAtUtc { get; set; }

    public DateTime LaunchedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? InitializedAtUtc { get; set; }
    public DateTime? LastStatementAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public int Version { get; set; }
}

/// <summary>
/// An xAPI statement received from (or issued by the LMS for) a cmi5 session. Unique by (tenant, statement id), which
/// is what makes a duplicate callback a no-op.
///
/// <para>WHY A TABLE. KynexOne is the minimal LRS for cmi5 (§3.1): the statements are the progress and completion
/// evidence and must be idempotent by id. No existing table stores third-party evidence. HR owns it; retained with the
/// enrollment (they are what a completion is proven by). No CompanyId for the same reason as
/// <see cref="LearningLaunchSession"/>: written by an anonymous AU call; reached only through its session.</para>
/// </summary>
public class LearningXapiStatement : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid SessionId { get; set; }
    public Guid EnrollmentId { get; set; }
    public Guid StatementId { get; set; }
    public Guid Registration { get; set; }
    public string Verb { get; set; } = string.Empty;
    public bool? ResultSuccess { get; set; }
    public bool? ResultCompletion { get; set; }
    public decimal? ScoreScaled { get; set; }
    public decimal? ProgressPercent { get; set; }
    /// <summary>True for statements the LMS itself issued (launched, satisfied, abandoned).</summary>
    public bool IssuedByLms { get; set; }
    /// <summary>The statement as stored (with the LMS-set "stored" and "authority").</summary>
    public string StatementJson { get; set; } = "{}";
    public DateTime StoredAtUtc { get; set; } = DateTime.UtcNow;
}
