using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// SUC — Succession and career development (capability-closure register SUC-01..09, SUC_DESIGN.md).
//
// Seven tables, the module's whole cap (SCHEMA_IMPACT.md). Every row is company-scoped operational data
// (ICompanyScopedOperational, company_id NOT NULL): a critical position and a talent pool belong to the company
// whose position or people they cover, and every record about a person carries the company of the target or
// of the employee. Vocabularies are C# constants; SuccessionModelConfiguration builds each named CHECK from the
// same array, so the database and the code cannot disagree about a status value.
//
// Nothing here ranks people. Candidates are ordered manually, with a written rationale, on a slate. Reads of the
// person go through SuccessionCandidateFacts, which carries no protected characteristic (SUC-08).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

public static class SuccessionRiskLevels
{
    public const string Low = "Low";
    public const string Medium = "Medium";
    public const string High = "High";
    public static readonly string[] All = [Low, Medium, High];
}

public static class SuccessionImpactLevels
{
    public const string Low = "Low";
    public const string Medium = "Medium";
    public const string High = "High";
    public const string Critical = "Critical";
    public static readonly string[] All = [Low, Medium, High, Critical];
}

public static class CriticalPositionStatuses
{
    public const string Active = "Active";
    /// <summary>Terminal. Open nominations are withdrawn (PositionRetired) and any open slate is cancelled.</summary>
    public const string Retired = "Retired";
    public static readonly string[] All = [Active, Retired];
}

public static class TalentPoolStatuses
{
    public const string Active = "Active";
    public const string Archived = "Archived";
    public static readonly string[] All = [Active, Archived];
}

public static class ReadinessLevels
{
    public const string ReadyNow = "ReadyNow";
    public const string OneToTwoYears = "OneToTwoYears";
    public const string ThreePlusYears = "ThreePlusYears";
    public static readonly string[] All = [ReadyNow, OneToTwoYears, ThreePlusYears];
}

public static class NominationStatuses
{
    public const string Proposed = "Proposed";
    public const string InReview = "InReview";
    /// <summary>Readiness confirmed in a calibration session. Required before a candidate can go on a slate.</summary>
    public const string Calibrated = "Calibrated";
    /// <summary>On the current Approved slate of a critical position. Position targets only.</summary>
    public const string OnSlate = "OnSlate";
    /// <summary>Terminal: HR declined it, with a reason.</summary>
    public const string Declined = "Declined";
    /// <summary>Terminal: the nominator withdrew it, or the system did (termination, retired position, appointment).</summary>
    public const string Withdrawn = "Withdrawn";
    public static readonly string[] All = [Proposed, InReview, Calibrated, OnSlate, Declined, Withdrawn];
    public static readonly string[] Closed = [Declined, Withdrawn];
    public static readonly string[] Live = [Proposed, InReview, Calibrated, OnSlate];
}

public static class NominationExitReasons
{
    public const string Declined = "Declined";
    public const string Withdrawn = "Withdrawn";
    public const string Terminated = "Terminated";
    public const string Ineligible = "Ineligible";
    public const string PositionRetired = "PositionRetired";
    public const string Appointed = "Appointed";
    public const string RecordMerged = "RecordMerged";
    public static readonly string[] All = [Declined, Withdrawn, Terminated, Ineligible, PositionRetired, Appointed, RecordMerged];
}

/// <summary>The performance axis of the 9-box. Unrated = no eligible review: no placement is ever guessed.</summary>
public static class PerformanceBandValues
{
    public const string Low = "Low";
    public const string Moderate = "Moderate";
    public const string High = "High";
    public const string Unrated = "Unrated";
    public static readonly string[] All = [Low, Moderate, High, Unrated];
}

public static class TalentAssessmentStatuses
{
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
    /// <summary>Immutable (trigger). A later assessment in the same period supersedes it.</summary>
    public const string Calibrated = "Calibrated";
    public const string Superseded = "Superseded";
    public static readonly string[] All = [Draft, Submitted, Calibrated, Superseded];
}

public static class SlateStatuses
{
    public const string Draft = "Draft";
    /// <summary>Immutable from here on (trigger); an approval request is open.</summary>
    public const string Submitted = "Submitted";
    /// <summary>Terminal. "Revise" clones it into a new Draft version.</summary>
    public const string ReturnedForRevision = "ReturnedForRevision";
    public const string Approved = "Approved";
    public const string Superseded = "Superseded";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = [Draft, Submitted, ReturnedForRevision, Approved, Superseded, Cancelled];
    public static readonly string[] Open = [Draft, Submitted];
}

public static class DevelopmentPlanStatuses
{
    public const string Draft = "Draft";
    public const string Active = "Active";
    public const string OnHold = "OnHold";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = [Draft, Active, OnHold, Completed, Cancelled];
    /// <summary>The plan an employee is working on. At most one per employee (partial unique index).</summary>
    public static readonly string[] Current = [Active, OnHold];
}

public static class DevelopmentActionTypes
{
    /// <summary>Linked to a learning_assignments row created through the LRN service.</summary>
    public const string Learning = "Learning";
    /// <summary>Linked to an EmployeeGoal.</summary>
    public const string Goal = "Goal";
    public const string Experience = "Experience";
    /// <summary>Names a mentor employee.</summary>
    public const string Mentoring = "Mentoring";
    public const string Other = "Other";
    public static readonly string[] All = [Learning, Goal, Experience, Mentoring, Other];
}

public static class DevelopmentActionStatuses
{
    public const string NotStarted = "NotStarted";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = [NotStarted, InProgress, Completed, Cancelled];
    public static readonly string[] Open = [NotStarted, InProgress];
}

public static class DevelopmentCompletionMethods
{
    public const string Manual = "Manual";
    /// <summary>The linked learning assignment reached Completed (LRN completion evidence).</summary>
    public const string LinkedLearningCompleted = "LinkedLearningCompleted";
    /// <summary>The linked EmployeeGoal reached Completed.</summary>
    public const string LinkedGoalCompleted = "LinkedGoalCompleted";
    public static readonly string[] All = [Manual, LinkedLearningCompleted, LinkedGoalCompleted];
}

/// <summary>
/// A position designated as critical for succession (SUC-01).
///
/// <para>WHY A TABLE. <see cref="Position"/> is the budgeted-slot authority and stays so; criticality, risk of loss,
/// impact of vacancy, the accountable executive and the designation's own lifecycle (designated, retired, with a
/// reason) are succession facts that most positions never have. A side table keeps them out of the position row and
/// lets a position be designated, retired and designated again with history. Owner: HR. Retention: as the
/// nominations and slates that hang off it (FK Restrict; never hard-deleted — retire is a status).</para>
/// </summary>
public class SuccessionCriticalPosition : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>The position's company. Designation is refused for a position with no company.</summary>
    public Guid? CompanyId { get; set; }
    public Guid PositionId { get; set; }
    public string RiskOfLoss { get; set; } = SuccessionRiskLevels.Medium;
    public string ImpactOfVacancy { get; set; } = SuccessionImpactLevels.High;
    public string Rationale { get; set; } = string.Empty;
    /// <summary>The executive accountable for this position's succession. Reads only the positions they own.</summary>
    public int? AccountableEmployeeId { get; set; }
    public string Status { get; set; } = CriticalPositionStatuses.Active;
    public DateOnly EffectiveFrom { get; set; }
    public DateTime? RetiredAtUtc { get; set; }
    public string? RetiredReason { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedBy { get; set; }
    public int Version { get; set; }
}

/// <summary>
/// A named talent pool (e.g. "Future finance leaders") whose members are nominations targeting it (SUC-02).
///
/// <para>WHY A TABLE. A pool is a development population, not a position: it has its own owner, purpose and
/// lifecycle, and no existing table groups people for development. Membership is a nomination row (one candidate,
/// one target), so a pool needs no member table of its own. Owner: HR.</para>
/// </summary>
public class TalentPool : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string? NameAr { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public int? OwnerEmployeeId { get; set; }
    public string Status { get; set; } = TalentPoolStatuses.Active;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedBy { get; set; }
    public int Version { get; set; }
}

/// <summary>
/// One candidate against exactly one target — a critical position or a talent pool (SUC-02, SUC-06).
///
/// <para>WHY A TABLE. A nomination has its own state machine (Proposed → InReview → Calibrated → OnSlate, or Declined
/// / Withdrawn), its own evidence and readiness history, and outlives the slates it appears on. Competing candidates
/// for one position are separate rows. The candidate's position, company and manager at nomination are
/// snapshotted so a later transfer is detected (needs_review), not silently absorbed.</para>
/// </summary>
public class SuccessionNomination : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>The TARGET's company (the position's or the pool's). The candidate may sit in another company.</summary>
    public Guid? CompanyId { get; set; }
    public int EmployeeId { get; set; }
    public Guid? CriticalPositionId { get; set; }
    public Guid? TalentPoolId { get; set; }
    public string Status { get; set; } = NominationStatuses.Proposed;
    public string? Readiness { get; set; }
    public string Evidence { get; set; } = string.Empty;
    /// <summary>jsonb array of AppraisalCompetencyRating ids the nominator cites. Ids only: the ratings stay where they are.</summary>
    public string CompetencyEvidenceJson { get; set; } = "[]";
    public Guid? NominatedByUserId { get; set; }
    public int? NominatedByEmployeeId { get; set; }
    public DateTime NominatedAtUtc { get; set; } = DateTime.UtcNow;

    // Readiness history: the previous value, when and why it changed (audited too).
    public string? ReadinessPrevious { get; set; }
    public DateTime? ReadinessChangedAtUtc { get; set; }
    public string? ReadinessReason { get; set; }
    public Guid? ReadinessChangedByUserId { get; set; }

    // The candidate at nomination (history; a transfer since then flags needs_review).
    public Guid? CandidatePositionIdAt { get; set; }
    public Guid? CandidateCompanyIdAt { get; set; }
    public int? CandidateManagerIdAt { get; set; }

    public bool NeedsReview { get; set; }
    public string? ReviewReason { get; set; }

    public string? ExitReason { get; set; }
    public string? ExitNote { get; set; }
    public DateTime? ExitedAtUtc { get; set; }
    public Guid? ExitedByUserId { get; set; }

    /// <summary>Set by SuccessionRetentionJob once evidence was redacted, 3 years after employment ended.</summary>
    public DateTime? AnonymisedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public int Version { get; set; }
}

/// <summary>
/// A talent-review assessment of one employee for one review period (SUC-03, SUC-08). Append-only.
///
/// <para>WHY A TABLE. <see cref="AppraisalReview"/> rates past performance; potential is a different judgement with
/// its own evidence, assessor and calibration, and the 9-box needs both side by side as they were at the time. The
/// row snapshots the performance input (review id, score, band, band rule version) so the placement is reproducible
/// after the appraisal changes. A calibrated row is immutable (trigger); a re-assessment supersedes it.</para>
/// </summary>
public class TalentAssessment : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>The employee's company at assessment.</summary>
    public Guid? CompanyId { get; set; }
    public int EmployeeId { get; set; }
    /// <summary>e.g. 2026-H2.</summary>
    public string ReviewPeriod { get; set; } = string.Empty;
    public int? AssessorEmployeeId { get; set; }
    public Guid? AssessorUserId { get; set; }
    /// <summary>1 Limited · 2 Growth · 3 High.</summary>
    public int ProposedPotential { get; set; }
    public string PotentialEvidence { get; set; } = string.Empty;

    // Performance snapshot (SUC-08: the inputs travel with the placement).
    public Guid? AppraisalReviewId { get; set; }
    public decimal? PerformanceScore { get; set; }
    public string PerformanceBand { get; set; } = PerformanceBandValues.Unrated;
    public string BandRuleVersion { get; set; } = string.Empty;

    // Calibration.
    public int? CalibratedPotential { get; set; }
    public string? CalibrationReason { get; set; }
    public Guid? CalibratedByUserId { get; set; }
    public int? CalibratedByEmployeeId { get; set; }
    public DateTime? CalibratedAtUtc { get; set; }

    public string Status { get; set; } = TalentAssessmentStatuses.Submitted;
    public Guid? SupersedesId { get; set; }
    public DateTime? SupersededAtUtc { get; set; }

    /// <summary>Generated (stored): P{potential}-{band}, NULL when Unrated. Never written by the application.</summary>
    public string? NineBoxCell { get; set; }

    public DateTime? AnonymisedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public int Version { get; set; }
}

/// <summary>
/// One version of a critical position's succession slate (SUC-06): the ordered candidates as approved.
///
/// <para>WHY A TABLE. The slate is what was approved, by whom, when — a historical record that must not change when a
/// candidate's readiness later does. Each version holds an immutable JSON snapshot (incumbent; per candidate:
/// nomination, names, readiness, assessment, 9-box cell and its inputs, manual order and rationale, rule versions)
/// and its SHA-256. Immutable from Submitted on (trigger). Approving a version supersedes the previous approved one in
/// the same transaction.</para>
/// </summary>
public class SuccessionSlateVersion : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid CriticalPositionId { get; set; }
    public int VersionNo { get; set; } = 1;
    public string Status { get; set; } = SlateStatuses.Draft;
    public string SnapshotJson { get; set; } = "{}";
    public string SnapshotSha256 { get; set; } = string.Empty;
    public Guid? ApprovalRequestId { get; set; }
    public Guid? SubmittedByUserId { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public Guid? DecidedByUserId { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public DateOnly? EffectiveFrom { get; set; }
    public DateTime? SupersededAtUtc { get; set; }
    public DateTime? ReturnedAtUtc { get; set; }
    public string? ReturnReason { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? CancelReason { get; set; }
    public Guid? RevisedFromId { get; set; }
    public DateTime? AnonymisedAtUtc { get; set; }
    /// <summary>
    /// SHA-256 of the snapshot as the retention job left it (names pseudonymised, free text redacted). The original
    /// <see cref="SnapshotSha256"/> is kept; this second hash lets a reader tell "anonymised and intact" from "tampered".
    /// </summary>
    public string? AnonymisedSha256 { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public int Version { get; set; }
}

/// <summary>
/// An employee's development plan (SUC-04): owner, purpose, review cadence.
///
/// <para>WHY A TABLE. No existing table holds a development plan: goals (<see cref="EmployeeGoal"/>) are appraisal
/// targets and learning assignments are training; a plan groups both with experiences and mentoring, under an owner
/// and a review date. The optional nomination link is never serialised to the employee or the line manager (a plan
/// must not reveal slate membership). One current (Active/OnHold) plan per employee, DB-enforced.</para>
/// </summary>
public class DevelopmentPlan : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public int EmployeeId { get; set; }
    /// <summary>HR-only. FK SET NULL. Never in an employee or manager payload.</summary>
    public Guid? NominationId { get; set; }
    public int? OwnerEmployeeId { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string Status { get; set; } = DevelopmentPlanStatuses.Draft;
    public DateOnly NextReviewOn { get; set; }
    public DateTime? LastReviewedAtUtc { get; set; }
    public string? LastReviewNote { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? CancelReason { get; set; }
    /// <summary>DEVPLAN_REVIEW_DUE dedupe: the review date the reminder was sent for.</summary>
    public DateOnly? ReviewReminderSentFor { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public int Version { get; set; }
}

/// <summary>
/// One action in a development plan (SUC-04). Learning actions link a learning_assignments row (created through the
/// LRN service, never duplicated here) and Goal actions an EmployeeGoal; their completion follows the linked record.
///
/// <para>WHY A TABLE. Part of the plan aggregate (same owner and retention): each action has its own owner, due date,
/// status and completion method, which a JSON column on the plan could not index for the overdue report.</para>
/// </summary>
public class DevelopmentPlanAction : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid PlanId { get; set; }
    public string ActionType { get; set; } = DevelopmentActionTypes.Other;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? LearningAssignmentId { get; set; }
    public Guid? EmployeeGoalId { get; set; }
    public int? MentorEmployeeId { get; set; }
    public int? OwnerEmployeeId { get; set; }
    public DateOnly? DueOn { get; set; }
    public int SortOrder { get; set; }
    public string Status { get; set; } = DevelopmentActionStatuses.NotStarted;
    public string? ProgressNote { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? CompletionMethod { get; set; }
    /// <summary>
    /// The employee proposed (in self-service) that this manual action is done. It stays open until the plan owner or HR
    /// confirms it; reports count only confirmed completions.
    /// </summary>
    public DateTime? CompletionProposedAtUtc { get; set; }
    /// <summary>DEVPLAN_ACTION_OVERDUE dedupe: set once when the overdue notice was enqueued.</summary>
    public DateTime? OverdueNoticeSentAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public int Version { get; set; }
}
