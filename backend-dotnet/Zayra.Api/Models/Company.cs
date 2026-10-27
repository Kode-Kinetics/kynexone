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
    /// <summary>The company code this legal entity carries in the client's ERP (SAP BUKRS, Oracle balancing
    /// segment, NetSuite subsidiary, D365 legal entity…). Stamped on every journal export line; when null the
    /// export falls back to RegistrationNumber, then a short id.</summary>
    public string? ErpCompanyCode { get; set; }
    public string TaxNumber { get; set; } = string.Empty;
    public string WpsEmployerId { get; set; } = string.Empty;
    public string GosiEmployerId { get; set; } = string.Empty;
    public string QiwaEstablishmentId { get; set; } = string.Empty;
    /// <summary>The establishment's Unified National Number ("700 number", SA: 10 digits, shown in Qiwa).
    /// Optional; null = not recorded. Captured by the create-company wizard.</summary>
    public string? UnifiedNationalNumber { get; set; }
    public string DefaultCurrency { get; set; } = "USD";
    /// <summary>Cost-accounting M1: the costing switch. While false nothing is stamped or enforced, so journals and exports
    /// are byte-identical to before the feature.</summary>
    public bool CostAccountingEnabled { get; set; }
    /// <summary>Cost-accounting M1: the company's fallback cost centre (and home of the rounding line). Must belong to this company.</summary>
    public Guid? DefaultCostCenterId { get; set; }
    /// <summary>Cost-accounting M1: minimum employees per cost centre before it is rolled up to its parent in a run. 0 = off.</summary>
    public short CostCentreSmallCellMin { get; set; }
    // Company email domain (e.g. "acme.sa") used to auto-derive employee work emails. Empty = not set;
    // employees then fall back to manual work-email entry (never blocks create/import). Format-validated
    // (host domain, lowercased) at the company-form boundary; sets the HR string only — no mailbox is
    // provisioned. See WorkEmailPatterns for how the local part is formed.
    public string EmailDomain { get; set; } = string.Empty;
    // How the local part of a derived work email is formed from the employee name (data, not hard-coded):
    // first.last | flast | first | first_last. Default first.last. Per-company (subsumes per-tenant in a
    // Group tenant). Vocabulary lives in WorkEmailPatterns.
    public string WorkEmailPattern { get; set; } = WorkEmailPatterns.FirstLast;
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
