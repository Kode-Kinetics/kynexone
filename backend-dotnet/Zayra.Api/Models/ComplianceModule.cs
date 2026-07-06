using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

// ── Document Type (configurable per tenant) ────────────────────────────────────

public class DocType : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;          // Identity/Visa/Contract/Certificate/Permit/Other
    public bool ExpiryRequired { get; set; }
    public int AlertDaysBeforeExpiry { get; set; } = 30;
    public bool IsMandatory { get; set; }
    public string ApplicableCountries { get; set; } = string.Empty; // CSV: AE,SA,QA
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}

// ── Contract Template ──────────────────────────────────────────────────────────

public class ContractTemplate : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string ContractType { get; set; } = "Employment";       // Employment/Freelance/Internship/Contractor
    public string Language { get; set; } = "en";                   // en/ar/bilingual
    public string ContentHtmlEn { get; set; } = string.Empty;
    public string ContentHtmlAr { get; set; } = string.Empty;
    public string Variables { get; set; } = string.Empty;          // CSV list of merge fields: {{employee_name}},{{start_date}}
    public string CountryCode { get; set; } = "AE";
    public bool IsActive { get; set; } = true;
    public int Version { get; set; } = 1;
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}

// ── Employee Contract ──────────────────────────────────────────────────────────

public class EmployeeContract : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Backfilled by CompanyScopeBackfill; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public Guid? TemplateId { get; set; }
    public string ContractNumber { get; set; } = string.Empty;
    public string ContractType { get; set; } = "Employment";
    public string Status { get; set; } = "Draft";                  // Draft/PendingApproval/Active/Expired/Terminated/Superseded
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }                          // null = indefinite
    public decimal BasicSalary { get; set; }
    public string CurrencyCode { get; set; } = "AED";
    public string ContentHtmlEn { get; set; } = string.Empty;
    public string ContentHtmlAr { get; set; } = string.Empty;
    public string Language { get; set; } = "en";
    public int Version { get; set; } = 1;
    public Guid? PreviousVersionId { get; set; }
    public string SignedByEmployeeName { get; set; } = string.Empty;
    public DateTime? SignedByEmployeeAtUtc { get; set; }
    public string SignedByHrName { get; set; } = string.Empty;
    public DateTime? SignedByHrAtUtc { get; set; }
    public Guid? ApprovalRequestId { get; set; }
    public string FileUrl { get; set; } = string.Empty;
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
}

// ── Compliance Requirement ─────────────────────────────────────────────────────

public class ComplianceRequirement : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid DocTypeId { get; set; }
    public string DocTypeName { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;        // AE/SA/QA/etc.
    public string ApplicableTo { get; set; } = "All";              // All/ByNationality/ByRole/ByEmploymentType
    public string ApplicableValue { get; set; } = string.Empty;    // e.g. "IN" for Indian nationals
    public bool IsMandatory { get; set; } = true;
    public int AlertDaysBeforeExpiry { get; set; } = 30;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Compliance Renewal ─────────────────────────────────────────────────────────

public class ComplianceRenewal : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;       // Visa/Passport/Iqama/WorkPermit/Contract
    public string DocumentNumber { get; set; } = string.Empty;
    public DateOnly ExpiryDate { get; set; }
    public DateOnly? RenewalDate { get; set; }
    public string Status { get; set; } = "Pending";                // Pending/InProgress/Renewed/Exempted/Overdue
    public string AssignedToName { get; set; } = string.Empty;
    public Guid? AssignedToUserId { get; set; }
    public string Notes { get; set; } = string.Empty;
    public Guid? ApprovalRequestId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

// ── Compliance Reminder ────────────────────────────────────────────────────────

public class ComplianceReminder : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string ReminderType { get; set; } = string.Empty;       // VisaExpiry/PassportExpiry/ContractExpiry/DocumentMissing
    public string DocumentType { get; set; } = string.Empty;
    public DateOnly? ExpiryDate { get; set; }
    public string Status { get; set; } = "Pending";                // Pending/Sent/Acknowledged/Dismissed
    public DateTime? ScheduledAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Visa Record ────────────────────────────────────────────────────────────────

public class VisaRecord : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Backfilled by CompanyScopeBackfill; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string VisaType { get; set; } = string.Empty;           // Residence/Visit/Employment/Transit
    public string VisaNumber { get; set; } = string.Empty;
    public string IqamaNumber { get; set; } = string.Empty;        // Saudi Iqama number
    public string EmiratesIdNumber { get; set; } = string.Empty;   // UAE Emirates ID
    public string CountryCode { get; set; } = string.Empty;
    public DateOnly IssueDate { get; set; }
    public DateOnly ExpiryDate { get; set; }
    public string Sponsor { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";                 // Active/Expired/Cancelled/UnderRenewal
    public string FileUrl { get; set; } = string.Empty;
    public Guid? RenewalId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
}

// ── Passport Record ────────────────────────────────────────────────────────────

public class PassportRecord : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Backfilled by CompanyScopeBackfill; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string PassportNumber { get; set; } = string.Empty;
    public string Nationality { get; set; } = string.Empty;
    public string IssuingCountry { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }
    public DateOnly IssueDate { get; set; }
    public DateOnly ExpiryDate { get; set; }
    public string PlaceOfIssue { get; set; } = string.Empty;
    public bool IsHeldByCompany { get; set; }                      // GCC: employer sometimes holds passport
    public DateOnly? ReturnedToEmployeeDate { get; set; }
    public string Status { get; set; } = "Active";                 // Active/Expired/Lost/Surrendered
    public string FileUrl { get; set; } = string.Empty;
    public Guid? RenewalId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
}

// ── Work Permit Record ─────────────────────────────────────────────────────────

public class WorkPermitRecord : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Backfilled by CompanyScopeBackfill; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string PermitNumber { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public string PermitType { get; set; } = string.Empty;         // WorkPermit/LabourCard/NOC/MOLCard
    public DateOnly IssueDate { get; set; }
    public DateOnly ExpiryDate { get; set; }
    public string IssuingAuthority { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";                 // Active/Expired/Cancelled/UnderRenewal
    public string FileUrl { get; set; } = string.Empty;
    public Guid? RenewalId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
}

// ── Compliance Audit Log ───────────────────────────────────────────────────────

public class ComplianceAuditLog : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string EntityType { get; set; } = string.Empty;         // Contract/Visa/Passport/WorkPermit/Document
    public string EntityId { get; set; } = string.Empty;
    public Guid? EmployeeId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string PerformedByName { get; set; } = string.Empty;
    public Guid? PerformedByUserId { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Compliance AI Insight ──────────────────────────────────────────────────────

public class ComplianceAIInsight : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? EmployeeId { get; set; }
    public string InsightType { get; set; } = string.Empty;        // MissingDocument/ExpiryRisk/RenewalAlert/ComplianceGap
    public string Severity { get; set; } = "Medium";               // Low/Medium/High/Critical
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RecommendedAction { get; set; } = string.Empty;
    public bool IsAdvisory { get; set; } = true;
    public bool IsAcknowledged { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAtUtc { get; set; }
}
