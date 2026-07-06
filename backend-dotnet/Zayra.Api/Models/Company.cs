using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public static class CompanyApprovalStatuses
{
    public const string Active = "Active";
    public const string Draft = "Draft";
    public const string PendingActivation = "PendingActivation";
}

public class Company : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string LegalNameEn { get; set; } = string.Empty;
    public string LegalNameAr { get; set; } = string.Empty;
    public string TradeName { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public string Jurisdiction { get; set; } = string.Empty;
    public string RegistrationNumber { get; set; } = string.Empty;
    public string TaxNumber { get; set; } = string.Empty;
    public string WpsEmployerId { get; set; } = string.Empty;
    public string GosiEmployerId { get; set; } = string.Empty;
    public string QiwaEstablishmentId { get; set; } = string.Empty;
    public string DefaultCurrency { get; set; } = "USD";
    public bool IsActive { get; set; } = true;
    // Company lifecycle under group governance: Active | Draft | PendingActivation.
    // Draft is produced by the GroupDraftPlatformApproval creation mode and becomes
    // Active via the platform approval endpoint.
    public string ApprovalStatus { get; set; } = CompanyApprovalStatuses.Active;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
}
