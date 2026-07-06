using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

// ── AI Model Configuration ────────────────────────────────────────────────────

public class AIModelConfig : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string ModelName { get; set; } = string.Empty; // e.g. claude-sonnet-4-6
    public string Provider { get; set; } = "Anthropic";
    public string UseCase { get; set; } = string.Empty; // hr_assistant, resume_screening, payroll_validation
    public bool IsActive { get; set; } = true;
    public string ConfigJson { get; set; } = "{}"; // temperature, max_tokens, system prompt overrides
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

// ── AI Insight (generic cross-module) ────────────────────────────────────────

public class AIInsight : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    // Legal-entity dimension: company-specific insights (e.g. per-company payroll
    // variance) are stamped; tenant-level insights stay null (visible to everyone).
    public Guid? CompanyId { get; set; }
    public string Module { get; set; } = string.Empty; // Attendance, Leave, Payroll, Recruitment, HR
    public string InsightType { get; set; } = string.Empty; // AbsenteeismPattern, ChurnRisk, PayrollVariance, etc.
    public string Severity { get; set; } = "Info"; // Info, Warning, Critical
    public int? EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string DataJson { get; set; } = "{}";
    public string GeneratedBy { get; set; } = "System"; // System, AI
    public bool IsAcknowledged { get; set; }
    public Guid? AcknowledgedBy { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── AI Recommendation ─────────────────────────────────────────────────────────

public class AIRecommendation : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? AIInsightId { get; set; }
    public string Module { get; set; } = string.Empty;
    public string RecommendationType { get; set; } = string.Empty;
    public int? EmployeeId { get; set; }
    public string RecommendationText { get; set; } = string.Empty;
    public string ActionLabel { get; set; } = string.Empty; // e.g. "Schedule Meeting", "Review Payslip"
    public string ActionRoute { get; set; } = string.Empty; // frontend route
    public string Priority { get; set; } = "Normal"; // Low, Normal, High, Urgent
    public string Status { get; set; } = "Pending"; // Pending, Actioned, Dismissed
    public bool IsAdvisoryOnly { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ActionedAtUtc { get; set; }
    public Guid? ActionedBy { get; set; }
}

// ── AI HR Assistant Query Log ─────────────────────────────────────────────────

public class AIHRQueryLog : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public int? EmployeeId { get; set; }
    public string UserRole { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string LoggedPrompt { get; set; } = string.Empty;
    public string PromptHash { get; set; } = string.Empty;
    public string PromptSummary { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;
    public string IntentClassified { get; set; } = string.Empty; // leave_inquiry, headcount, payroll_summary
    public string Module { get; set; } = string.Empty;
    public bool WasBlocked { get; set; }
    public string BlockedReason { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ResponseStatus { get; set; } = string.Empty;
    public bool HumanReviewRequired { get; set; }
    public int TokensUsed { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int ResponseTimeMs { get; set; }
    public bool IsAdvisoryLabelShown { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── AI HR Assistant Query Cache ───────────────────────────────────────────────

public class AIHRQueryCache : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string CacheKey { get; set; } = string.Empty;
    public string QueryHash { get; set; } = string.Empty;
    public string NormalizedQuery { get; set; } = string.Empty;
    public string IntentClassified { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public int? EmployeeId { get; set; }
    public string UserRoleSignature { get; set; } = string.Empty;
    public string PermissionSignature { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ResponseStatus { get; set; } = string.Empty;
    public bool HumanReviewRequired { get; set; }
    public bool IsAdvisoryLabelShown { get; set; } = true;
    public int TokensUsed { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int ResponseTimeMs { get; set; }
    public int HitCount { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastHitAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddMinutes(5);
}

// ── Resume Screening ──────────────────────────────────────────────────────────

public class ResumeParseResult : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CandidateId { get; set; }
    public Guid? JobApplicationId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string StorageUrl { get; set; } = string.Empty;
    public string ParsedTextJson { get; set; } = "{}"; // extracted fields: name, email, skills, experience years, education
    public string RawText { get; set; } = string.Empty;
    public string ParseStatus { get; set; } = "Pending"; // Pending, Parsed, Failed
    public string ParsedBy { get; set; } = "AI";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ParsedAtUtc { get; set; }
}

public class CandidateAIScore : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CandidateId { get; set; }
    public Guid? JobApplicationId { get; set; }
    public Guid? JobOpeningId { get; set; }
    public Guid? ResumeParseResultId { get; set; }
    public decimal OverallScore { get; set; }
    public decimal SkillMatchScore { get; set; }
    public decimal ExperienceScore { get; set; }
    public decimal EducationScore { get; set; }
    public string SkillMatchDetails { get; set; } = string.Empty;
    public string Strengths { get; set; } = string.Empty;
    public string Concerns { get; set; } = string.Empty;
    public string Recommendation { get; set; } = "Review"; // Shortlist, Review, Reject
    public bool IsAdvisoryOnly { get; set; } = true;
    public string GeneratedBy { get; set; } = "AI";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Payroll AI Validation ─────────────────────────────────────────────────────

public class PayrollAIValidationResult : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PayrollRunId { get; set; }
    public int? EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string ValidationType { get; set; } = string.Empty; // SalaryVariance, MissingComponent, OvertimeAnomaly, WPSFlag
    public string Severity { get; set; } = "Info"; // Info, Warning, Critical
    public string Message { get; set; } = string.Empty;
    public string DataJson { get; set; } = "{}";
    public bool IsResolved { get; set; }
    public Guid? ResolvedBy { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public string ResolutionNote { get; set; } = string.Empty;
    public bool IsAdvisoryOnly { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Employee Risk Intelligence ────────────────────────────────────────────────

public class EmployeeRiskScore : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    public decimal ChurnRiskScore { get; set; } // 0-100
    public decimal BurnoutRiskScore { get; set; } // 0-100
    public decimal PerformanceDeclineScore { get; set; } // 0-100
    public string OverallRiskLevel { get; set; } = "Low"; // Low, Medium, High, Critical
    public string RiskFactorsJson { get; set; } = "[]"; // list of contributing factors
    public string Recommendations { get; set; } = string.Empty;
    public bool IsAdvisoryOnly { get; set; } = true;
    public DateTime ComputedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? AcknowledgedAtUtc { get; set; }
    public Guid? AcknowledgedBy { get; set; }
}

public class EmployeeChurnPrediction : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public decimal ChurnProbability { get; set; } // 0.0 - 1.0
    public string TimeHorizon { get; set; } = "90days"; // 30days, 90days, 180days
    public string[] ContributingFactors { get; set; } = [];
    public string ModelVersion { get; set; } = "v1";
    public bool IsAdvisoryOnly { get; set; } = true;
    public DateTime ComputedAtUtc { get; set; } = DateTime.UtcNow;
}

public class BurnoutRiskSignal : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int EmployeeId { get; set; }
    public string SignalType { get; set; } = string.Empty; // ExcessiveOvertime, HighAbsenteeism, LowSentiment, NoPTO
    public string SignalValue { get; set; } = string.Empty;
    public string Severity { get; set; } = "Low";
    public DateOnly DetectedDate { get; set; }
    public bool IsAcknowledged { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
