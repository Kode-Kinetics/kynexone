using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

// ── Workforce Plan ─────────────────────────────────────────────────────────────

public class WorkforcePlan : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string PlanCode { get; set; } = string.Empty;           // WFP-2026-001
    public int PlanYear { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public Guid? DepartmentId { get; set; }
    public string DepartmentName { get; set; } = string.Empty;
    public int CurrentHeadcount { get; set; }
    public int PlannedHeadcount { get; set; }
    public int GapCount { get; set; }                               // Planned - Current
    public decimal BudgetAllocated { get; set; }
    public decimal BudgetUtilized { get; set; }
    public string CurrencyCode { get; set; } = "AED";
    public string Status { get; set; } = "Draft";                  // Draft/Approved/InProgress/Closed
    public string Notes { get; set; } = string.Empty;
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public Guid? ApprovalRequestId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
}

// ── Candidate Document ─────────────────────────────────────────────────────────

public class CandidateDocument : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CandidateId { get; set; }
    public Guid? ApplicationId { get; set; }
    public string DocumentType { get; set; } = string.Empty;       // Resume/CoverLetter/Portfolio/Certificate/ID/Other
    public string FileName { get; set; } = string.Empty;
    public string FileUrl { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public string UploadedByName { get; set; } = string.Empty;
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}

// ── Interview Feedback ─────────────────────────────────────────────────────────

public class InterviewFeedback : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid InterviewScheduleId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid? InterviewerUserId { get; set; }
    public string InterviewerName { get; set; } = string.Empty;
    public string InterviewerRole { get; set; } = string.Empty;    // HR/Technical/Manager/Panel
    public int CommunicationScore { get; set; }                    // 1-5
    public int TechnicalScore { get; set; }
    public int CultureFitScore { get; set; }
    public int ProblemSolvingScore { get; set; }
    public int LeadershipScore { get; set; }
    public int OverallScore { get; set; }                          // 1-5
    public string Strengths { get; set; } = string.Empty;
    public string Concerns { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;    // StrongHire/Hire/Hold/Reject
    public DateTime SubmittedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Assessment Template ────────────────────────────────────────────────────────

public class AssessmentTemplate : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AssessmentType { get; set; } = "MCQ";            // MCQ/Essay/Coding/Mixed
    public int DurationMinutes { get; set; } = 60;
    public int PassingScore { get; set; } = 70;                    // percentage
    public int TotalMarks { get; set; }
    public bool IsRandomized { get; set; }
    public string Audience { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}

// ── Assessment Question ────────────────────────────────────────────────────────

public class AssessmentQuestion : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid TemplateId { get; set; }
    public int OrderIndex { get; set; }
    public string QuestionType { get; set; } = "MCQ";              // MCQ/Essay/TrueFalse
    public string QuestionText { get; set; } = string.Empty;
    public string OptionsJson { get; set; } = "[]";                // JSON array of option strings
    public string CorrectAnswer { get; set; } = string.Empty;
    public int Marks { get; set; } = 1;
    public string Difficulty { get; set; } = "Medium";             // Easy/Medium/Hard
    public string SkillTag { get; set; } = string.Empty;
}

// ── Candidate Assessment (sent to candidate) ──────────────────────────────────

public class CandidateAssessment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid CandidateId { get; set; }
    public Guid TemplateId { get; set; }
    public string TemplateName { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";                // Pending/Sent/InProgress/Completed/Expired
    public string InvitationToken { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime? SentAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public int? ScoreObtained { get; set; }
    public int? TotalMarks { get; set; }
    public decimal? ScorePercentage { get; set; }
    public bool? Passed { get; set; }
    public string ResultJson { get; set; } = "{}";
    public Guid? AssignedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Offer Approval ─────────────────────────────────────────────────────────────

public class OfferApproval : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OfferLetterId { get; set; }
    public Guid ApplicationId { get; set; }
    public int StepOrder { get; set; }
    public string ApproverName { get; set; } = string.Empty;
    public Guid? ApproverUserId { get; set; }
    public string ApproverRole { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";                // Pending/Approved/Rejected/Delegated
    public string Comments { get; set; } = string.Empty;
    public DateTime? DecidedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Onboarding Checklist (Template) ───────────────────────────────────────────

public class OnboardingChecklist : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ApplicableTo { get; set; } = "All";              // All/ByDepartment/ByRole
    public Guid? DepartmentId { get; set; }
    public string DepartmentName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}

public class OnboardingChecklistTemplateTask : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ChecklistId { get; set; }
    public string TaskTitle { get; set; } = string.Empty;
    public string TaskDescription { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public string AssignedToName { get; set; } = string.Empty;
    public Guid? AssignedToUserId { get; set; }
    public int DueOffsetDays { get; set; }
    public int OrderIndex { get; set; }
    public bool IsMandatory { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

// ── Onboarding Task (instance) ────────────────────────────────────────────────

public class OnboardingTask : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? ChecklistId { get; set; }
    public Guid? EmployeeId { get; set; }
    public Guid? ApplicationId { get; set; }                       // Pre-joining linkage
    public string TaskTitle { get; set; } = string.Empty;
    public string TaskDescription { get; set; } = string.Empty;
    public string Category { get; set; } = "General";             // Document/IT/Training/Policy/Access/General
    public string AssignedToName { get; set; } = string.Empty;
    public Guid? AssignedToUserId { get; set; }
    public string Status { get; set; } = "Pending";               // Pending/InProgress/Completed/Skipped/Blocked
    public DateOnly? DueDate { get; set; }
    public DateOnly? CompletedDate { get; set; }
    public string Notes { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
    public bool IsMandatory { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

// ── Recruitment Audit Log ──────────────────────────────────────────────────────

public class RecruitmentAuditLog : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string EntityType { get; set; } = string.Empty;        // Application/Offer/Interview/Assessment/Onboarding
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string PerformedByName { get; set; } = string.Empty;
    public Guid? PerformedByUserId { get; set; }
    public string OldValuesJson { get; set; } = "{}";
    public string NewValuesJson { get; set; } = "{}";
    public string IpAddress { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
