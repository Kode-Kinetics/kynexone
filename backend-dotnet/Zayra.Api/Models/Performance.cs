using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

// ── Performance Cycle ──────────────────────────────────────────────────────────

public class PerformanceCycle : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CycleType { get; set; } = "Annual"; // Annual/SemiAnnual/Quarterly/Monthly/Probation/Project
    public DateOnly ReviewPeriodStart { get; set; }
    public DateOnly ReviewPeriodEnd { get; set; }
    public string Status { get; set; } = "Draft"; // Draft/Active/InReview/Calibration/FinalApproval/Published/Closed
    public bool EnableCalibration { get; set; } = true;
    public bool Enable360Feedback { get; set; } = false;
    public bool EnableSelfAssessment { get; set; } = true;
    public bool EnableForcedDistribution { get; set; } = false;
    public DateOnly? SelfAssessmentDeadline { get; set; }
    public DateOnly? ManagerReviewDeadline { get; set; }
    public DateOnly? CalibrationDeadline { get; set; }
    public Guid? DefaultScorecardTemplateId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? DepartmentId { get; set; }
    public string Notes { get; set; } = string.Empty;
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LaunchedAtUtc { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
}

// ── Scorecard Template ─────────────────────────────────────────────────────────

public class PerformanceScorecardTemplate : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    public string DesignationTitle { get; set; } = string.Empty;
    public string Grade { get; set; } = string.Empty;
    public decimal KpiWeight { get; set; } = 40;
    public decimal CompetencyWeight { get; set; } = 20;
    public decimal AttendanceWeight { get; set; } = 10;
    public decimal ProductivityWeight { get; set; } = 15;
    public decimal FeedbackWeight { get; set; } = 10;
    public decimal DisciplineWeight { get; set; } = 5;
    public decimal MinPassingScore { get; set; } = 60;
    public bool RequiresCalibration { get; set; } = true;
    public bool Requires360Feedback { get; set; } = false;
    public bool IsDefault { get; set; } = false;
    public bool IsActive { get; set; } = true;
    public string RatingLabels { get; set; } = string.Empty; // JSON
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

// ── Rating Scale ───────────────────────────────────────────────────────────────

public class PerformanceRatingScale : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ScalePoints { get; set; } = 5;
    public bool IsDefault { get; set; } = false;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class PerformanceRatingOption : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ScaleId { get; set; }
    public string Label { get; set; } = string.Empty; // Outstanding/Exceeds/Meets/Developing/Unsatisfactory
    public decimal MinScore { get; set; }
    public decimal MaxScore { get; set; }
    public string Color { get; set; } = "#64748b";
    public int SortOrder { get; set; }
}

// ── Cycle Employee Enrollment ──────────────────────────────────────────────────

public class PerformanceCycleEmployee : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CycleId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    public string DesignationTitle { get; set; } = string.Empty;
    public Guid? ScorecardTemplateId { get; set; }
    public string Status { get; set; } = "Enrolled"; // Enrolled/Active/Completed/Excluded
    public DateTime EnrolledAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Competency Library ─────────────────────────────────────────────────────────

public class Competency : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = "Core"; // Core/Leadership/Technical/Role
    public string Description { get; set; } = string.Empty;
    public string BehavioralIndicators { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class RoleCompetency : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CompetencyId { get; set; }
    public string DepartmentName { get; set; } = string.Empty;
    public string DesignationTitle { get; set; } = string.Empty;
    public string ExpectedLevel { get; set; } = string.Empty; // Foundational/Proficient/Advanced/Expert
    public decimal Weight { get; set; } = 100;
}

// ── Employee Goal / KPI ────────────────────────────────────────────────────────

public class EmployeeGoal : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CycleId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "Individual"; // Company/Department/Individual
    public string KpiType { get; set; } = "Quantitative"; // Quantitative/Qualitative
    public string MeasurementUnit { get; set; } = string.Empty;
    public decimal TargetValue { get; set; }
    public decimal ActualValue { get; set; }
    public decimal Weight { get; set; } = 100;
    public decimal BaselineValue { get; set; } = 0; // starting value before the goal period
    public decimal AchievementPct { get; set; }
    public string Priority { get; set; } = "Medium"; // High/Medium/Low
    public DateOnly? StartDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public string Status { get; set; } = "Draft"; // Draft/Active/Completed/OnHold/Cancelled
    public bool IsDeleted { get; set; }
    public bool ManagerApproved { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public class GoalProgressUpdate : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid GoalId { get; set; }
    public decimal UpdatedValue { get; set; }
    public string Notes { get; set; } = string.Empty;
    public Guid? UpdatedByUserId { get; set; }
    public string UpdatedByName { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Appraisal Review ───────────────────────────────────────────────────────────

public class AppraisalReview : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CycleId { get; set; }
    public string CycleName { get; set; } = string.Empty;
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    public string DesignationTitle { get; set; } = string.Empty;
    public Guid ScorecardTemplateId { get; set; }
    // Component scores (0-100)
    public decimal KpiScore { get; set; }
    public decimal CompetencyScore { get; set; }
    public decimal AttendanceScore { get; set; }
    public decimal ProductivityScore { get; set; }
    public decimal FeedbackScore { get; set; }
    public decimal DisciplineScore { get; set; }
    public decimal FinalScore { get; set; }
    public string FinalRating { get; set; } = string.Empty;
    // Calibration
    public decimal CalibrationAdjustment { get; set; }
    public string CalibrationNotes { get; set; } = string.Empty;
    // Narrative
    public string SelfAssessmentNotes { get; set; } = string.Empty;
    public string ManagerNotes { get; set; } = string.Empty;
    public string HrNotes { get; set; } = string.Empty;
    // Workflow status
    public string Status { get; set; } = "Pending";
    // Pending/SelfAssessmentDue/SelfAssessmentSubmitted/ManagerReview/ManagerReviewComplete
    // Calibration/FinalApproval/Published/Acknowledged/Appealed/Closed
    public DateTime? SelfAssessmentSubmittedAt { get; set; }
    public DateTime? ManagerReviewedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public bool IsAppealed { get; set; }
    public int? ReviewerManagerId { get; set; }
    public string ReviewerManagerName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

// ── Score Breakdown ────────────────────────────────────────────────────────────

public class AppraisalScoreBreakdown : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ReviewId { get; set; }
    public string Component { get; set; } = string.Empty; // KPI/Competency/Attendance/Productivity/Feedback/Discipline
    public decimal RawScore { get; set; }
    public decimal Weight { get; set; }
    public decimal WeightedScore { get; set; }
    public string Notes { get; set; } = string.Empty;
}

// ── Appraisal Competency Rating ────────────────────────────────────────────────

public class AppraisalCompetencyRating : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ReviewId { get; set; }
    public Guid CompetencyId { get; set; }
    public string CompetencyName { get; set; } = string.Empty;
    public string CompetencyCategory { get; set; } = string.Empty;
    public decimal SelfRating { get; set; }   // 1-5 or 0-100
    public decimal ManagerRating { get; set; }
    public string SelfComments { get; set; } = string.Empty;
    public string ManagerComments { get; set; } = string.Empty;
    public decimal Weight { get; set; } = 100;
}

// ── 360 Feedback ───────────────────────────────────────────────────────────────

public class Feedback360 : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ReviewId { get; set; }
    public int ReviewerEmployeeId { get; set; }
    public string ReviewerName { get; set; } = string.Empty;
    public string ReviewerRole { get; set; } = string.Empty; // Manager/Peer/Subordinate/CrossFunc/Customer
    public bool IsAnonymous { get; set; }
    public decimal Score { get; set; }
    public string Strengths { get; set; } = string.Empty;
    public string Improvements { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
    public DateTime? SubmittedAt { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Calibration ────────────────────────────────────────────────────────────────

public class AppraisalCalibration : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ReviewId { get; set; }
    public Guid CycleId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    public decimal OriginalScore { get; set; }
    public decimal AdjustedScore { get; set; }
    public string AdjustmentReason { get; set; } = string.Empty;
    public string OriginalRating { get; set; } = string.Empty;
    public string AdjustedRating { get; set; } = string.Empty;
    public Guid? CalibratedByUserId { get; set; }
    public string CalibratedByName { get; set; } = string.Empty;
    public DateTime CalibratedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Appraisal Appeal ───────────────────────────────────────────────────────────

public class AppraisalAppeal : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ReviewId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string AppealReason { get; set; } = string.Empty;
    public string EmployeeJustification { get; set; } = string.Empty;
    public string Status { get; set; } = "Submitted"; // Submitted/UnderReview/Upheld/Rejected
    public string HrResponse { get; set; } = string.Empty;
    public Guid? ReviewedByUserId { get; set; }
    public string ReviewedByName { get; set; } = string.Empty;
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
}

// ── Recommendations ────────────────────────────────────────────────────────────

public class IncrementRecommendation : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ReviewId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    public string DesignationTitle { get; set; } = string.Empty;
    public decimal CurrentSalary { get; set; }
    public decimal RecommendedIncrementPct { get; set; }
    public decimal RecommendedIncrementAmount { get; set; }
    public decimal NewSalary { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending"; // Pending/Approved/Rejected/SentToPayroll
    public Guid? RecommendedByUserId { get; set; }
    public string RecommendedByName { get; set; } = string.Empty;
    public Guid? ApprovedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAtUtc { get; set; }
}

public class PromotionRecommendation : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ReviewId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    public string CurrentDesignation { get; set; } = string.Empty;
    public string ProposedDesignation { get; set; } = string.Empty;
    public DateOnly EffectiveDate { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending"; // Pending/Approved/Rejected
    public Guid? RecommendedByUserId { get; set; }
    public string RecommendedByName { get; set; } = string.Empty;
    public Guid? ApprovedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAtUtc { get; set; }
}

public class BonusRecommendation : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ReviewId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    public decimal BonusAmount { get; set; }
    public string BonusType { get; set; } = "Performance"; // Performance/Spot/Annual/Retention
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending"; // Pending/Approved/Rejected/SentToPayroll
    public Guid? RecommendedByUserId { get; set; }
    public string RecommendedByName { get; set; } = string.Empty;
    public Guid? ApprovedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAtUtc { get; set; }
}

// ── Performance Improvement Plan ───────────────────────────────────────────────

public class PerformanceImprovementPlan : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    public Guid? TriggerReviewId { get; set; }
    public string PerformanceGaps { get; set; } = string.Empty;
    public string ImprovementGoals { get; set; } = string.Empty;
    public string SupportPlan { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Status { get; set; } = "Active"; // Active/Improved/Extended/Failed/TerminationRecommended
    public string HrNotes { get; set; } = string.Empty;
    public string ManagerNotes { get; set; } = string.Empty;
    public string EmployeeComments { get; set; } = string.Empty;
    public Guid? InitiatedByUserId { get; set; }
    public string InitiatedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAtUtc { get; set; }
}

public class PIPCheckIn : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PipId { get; set; }
    public DateOnly CheckInDate { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty; // OnTrack/AtRisk/Improved/Deteriorated
    public Guid? CheckedByUserId { get; set; }
    public string CheckedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Probation Review ───────────────────────────────────────────────────────────

public class ProbationReview : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    public string DesignationTitle { get; set; } = string.Empty;
    public DateOnly ProbationStartDate { get; set; }
    public DateOnly ProbationEndDate { get; set; }
    public DateOnly? ReviewDueDate { get; set; }
    public string PerformanceSummary { get; set; } = string.Empty;
    public decimal OverallRating { get; set; }
    public string ManagerRecommendation { get; set; } = string.Empty; // Confirm/Extend/Terminate
    public string ManagerNotes { get; set; } = string.Empty;
    public string HrDecision { get; set; } = string.Empty; // Confirmed/Extended/Terminated
    public string HrNotes { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending"; // Pending/ManagerReviewed/HRApproved/Closed
    public Guid? ReviewedByManagerUserId { get; set; }
    public string ReviewedByManagerName { get; set; } = string.Empty;
    public Guid? ApprovedByHrUserId { get; set; }
    public DateTime? ManagerReviewedAt { get; set; }
    public DateTime? HrApprovedAt { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Continuous Feedback ────────────────────────────────────────────────────────

public class ContinuousFeedback : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public Guid? GivenByUserId { get; set; }
    public string GivenByName { get; set; } = string.Empty;
    public string FeedbackType { get; set; } = "Note"; // Praise/Coaching/Note/Concern/Achievement/Recognition
    public string Content { get; set; } = string.Empty;
    public bool IsPrivate { get; set; } = false;
    public Guid? LinkedReviewId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Performance Audit Log ──────────────────────────────────────────────────────

public class PerformanceAuditLog : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string OldValue { get; set; } = string.Empty;
    public string NewValue { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public Guid? PerformedByUserId { get; set; }
    public string PerformedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
