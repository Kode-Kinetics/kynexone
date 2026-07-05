using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class EmployeeDocument : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope. Backfilled by CompanyScopeBackfill; required for new operational writes.</summary>
    public Guid? CompanyId { get; set; }
    public int? EmployeeId { get; set; }
    public Guid? DraftId { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string DocumentCategory { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string StorageUrl { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public DateOnly? IssueDate { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public DateOnly? RenewalReminderDate { get; set; }
    public string ApprovalStatus { get; set; } = "Pending";
    public int VersionNumber { get; set; } = 1;
    public Guid? UploadedBy { get; set; }
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? VerifiedAtUtc { get; set; }
    public Guid? VerifiedBy { get; set; }
    public DateTime? LastDownloadedAtUtc { get; set; }
    public Guid? LastDownloadedBy { get; set; }
    public string Notes { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
}
