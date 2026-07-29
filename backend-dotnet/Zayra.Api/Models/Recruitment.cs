using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

// ── Stage constants ────────────────────────────────────────────────────────────

public static class RecruitmentStages
{
    public static readonly (string Name, int Order)[] Pipeline =
    [
        ("Applied",    1),
        ("Screening",  2),
        ("Assessment", 3),
        ("Interview",  4),
        ("Offer",      5),
        ("Hired",      6),
    ];

    public static int OrderOf(string stage) =>
        Pipeline.FirstOrDefault(s => s.Name == stage).Order;

    public static string? Next(string stage)
    {
        var idx = Array.FindIndex(Pipeline, s => s.Name == stage);
        return idx >= 0 && idx < Pipeline.Length - 1 ? Pipeline[idx + 1].Name : null;
    }
}

// ── Manpower Requisition ───────────────────────────────────────────────────────

public class ManpowerRequisition : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string RequisitionNumber { get; set; } = string.Empty;       // MRQ-2026-0001
    public Guid? DepartmentId { get; set; }
    public string DepartmentName { get; set; } = string.Empty;
    public Guid? DesignationId { get; set; }
    public string DesignationTitle { get; set; } = string.Empty;
    public int HeadCount { get; set; } = 1;
    public string EmploymentType { get; set; } = "Full-Time";           // Full-Time/Part-Time/Contract/Internship
    public string Priority { get; set; } = "Medium";                    // Low/Medium/High/Critical
    public string Justification { get; set; } = string.Empty;
    public string RequiredSkills { get; set; } = string.Empty;
    public int? MinExperienceYears { get; set; }
    public int? MaxExperienceYears { get; set; }
    public decimal? BudgetFrom { get; set; }
    public decimal? BudgetTo { get; set; }
    public DateOnly? TargetJoiningDate { get; set; }
    public string Status { get; set; } = "Draft";                       // Draft/Submitted/PendingApproval/Approved/Rejected/Converted
    public Guid? RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public int? RequestedByEmployeeId { get; set; }
    public string RejectionReason { get; set; } = string.Empty;
    public Guid? ApprovalRequestId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public DateTime? RejectedAtUtc { get; set; }
}

// ── Job Opening ────────────────────────────────────────────────────────────────

public class JobOpening : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string JobCode { get; set; } = string.Empty;                 // JOB-2026-0001
    public Guid? RequisitionId { get; set; }
    public string Title { get; set; } = string.Empty;
    public Guid? DepartmentId { get; set; }
    public string DepartmentName { get; set; } = string.Empty;
    public Guid? DesignationId { get; set; }
    public string DesignationTitle { get; set; } = string.Empty;
    public string EmploymentType { get; set; } = "Full-Time";
    public int HeadCount { get; set; } = 1;
    public int FilledCount { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Requirements { get; set; } = string.Empty;           // newline-separated
    public string Responsibilities { get; set; } = string.Empty;
    public decimal? SalaryFrom { get; set; }
    public decimal? SalaryTo { get; set; }
    public string Location { get; set; } = string.Empty;
    public string Status { get; set; } = "Open";                       // Open/InProgress/OnHold/Closed/Cancelled
    public Guid? AssignedHrUserId { get; set; }
    public string AssignedHrName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
}

// ── Candidate (Talent Pool) ────────────────────────────────────────────────────

public class Candidate : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Backfilled by CompanyScopeBackfill; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string CurrentJobTitle { get; set; } = string.Empty;
    public string CurrentCompany { get; set; } = string.Empty;
    public decimal TotalExperienceYears { get; set; }
    public string EducationLevel { get; set; } = string.Empty;         // Diploma/Bachelor/Master/PhD
    public string Nationality { get; set; } = string.Empty;
    public string LinkedInUrl { get; set; } = string.Empty;
    public string ResumeUrl { get; set; } = string.Empty;
    public string Source { get; set; } = "Direct";                     // LinkedIn/Referral/JobBoard/Walk-In/Agency/Direct
    public string Status { get; set; } = "Active";                     // Active/Blacklisted
    public string Tags { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

// ── Job Application (Pipeline record) ─────────────────────────────────────────

public class JobApplication : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Stamped from the parent candidate on create; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
    public Guid JobOpeningId { get; set; }
    public string JobTitle { get; set; } = string.Empty;
    public Guid CandidateId { get; set; }
    public string CandidateName { get; set; } = string.Empty;
    public string CandidateEmail { get; set; } = string.Empty;
    public string Stage { get; set; } = "Applied";
    public int StageOrder { get; set; } = 1;
    public string Status { get; set; } = "Active";                     // Active/Rejected/Withdrawn/Hired
    public string RejectionReason { get; set; } = string.Empty;
    public decimal? OfferedSalary { get; set; }
    public DateTime AppliedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StageChangedAtUtc { get; set; }
    public DateTime? HiredAtUtc { get; set; }
    public Guid? OnboardingDraftId { get; set; }
}

// ── Application Event (Timeline) ──────────────────────────────────────────────

public class ApplicationEvent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public string EventType { get; set; } = string.Empty;              // StageAdvanced/Rejected/NoteAdded/InterviewScheduled/FeedbackRecorded/OfferGenerated/OfferSent/OfferAccepted/OfferDeclined/OnboardingStarted
    public string Stage { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public Guid? PerformedByUserId { get; set; }
    public string PerformedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Interview Schedule ─────────────────────────────────────────────────────────

public class InterviewSchedule : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public string InterviewType { get; set; } = "HR Screening";        // HR Screening/Technical/Manager/Panel/Final
    public string InterviewerNames { get; set; } = string.Empty;
    public DateTime ScheduledAt { get; set; }
    public int DurationMinutes { get; set; } = 60;
    public string Mode { get; set; } = "Video";                        // InPerson/Video/Phone
    public string MeetingLink { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Status { get; set; } = "Scheduled";                  // Scheduled/Completed/Cancelled/NoShow
    public int? OverallRating { get; set; }                            // 1-5
    public string Recommendation { get; set; } = string.Empty;         // Proceed/Reject/Hold
    public string FeedbackNotes { get; set; } = string.Empty;
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Offer Letter ───────────────────────────────────────────────────────────────

public class OfferLetter : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Stamped from the parent application on create; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
    public Guid ApplicationId { get; set; }
    public string CandidateName { get; set; } = string.Empty;
    public string OfferedJobTitle { get; set; } = string.Empty;
    public string OfferedDepartment { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public decimal BasicSalary { get; set; }
    public decimal HousingAllowance { get; set; }
    public decimal TransportAllowance { get; set; }
    public decimal OtherAllowances { get; set; }
    public decimal GrossSalary { get; set; }
    public int ProbationMonths { get; set; } = 3;
    public string ContentHtml { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";                      // Draft/Approved/Sent/Accepted/Declined/Expired
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentAtUtc { get; set; }
    public DateTime? ResponseDeadline { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
    public DateTime? DeclinedAtUtc { get; set; }
    public string DeclineReason { get; set; } = string.Empty;
}
