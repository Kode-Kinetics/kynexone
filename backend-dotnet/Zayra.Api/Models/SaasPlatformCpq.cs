using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

// ── Product Type constants ─────────────────────────────────────────────────────

public static class CatalogItemTypes
{
    public const string BaseProduct   = "BaseProduct";
    public const string Module        = "Module";
    public const string Feature       = "Feature";
    public const string AddOn         = "AddOn";
    public const string PremiumPack   = "PremiumPack";
    public const string Service       = "Service";
    public const string Support       = "Support";
    public const string Integration   = "Integration";
    public const string Usage         = "Usage";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
        { BaseProduct, Module, Feature, AddOn, PremiumPack, Service, Support, Integration, Usage };
}

public static class BillingTypes
{
    public const string FixedMonthly       = "FixedMonthly";
    public const string PerEmployeeMonthly = "PerEmployeeMonthly";
    public const string PerUserMonthly     = "PerUserMonthly";
    public const string PerCompanyMonthly  = "PerCompanyMonthly";
    public const string OneTime            = "OneTime";
    public const string UsageBased        = "UsageBased";
    public const string Included           = "Included";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
        { FixedMonthly, PerEmployeeMonthly, PerUserMonthly, PerCompanyMonthly, OneTime, UsageBased, Included };
}

// ── Product Catalog ─────────────────────────────────────────────────────────

public class ProductCatalogItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string InternalCode { get; set; } = string.Empty;   // SKU
    public string ItemType { get; set; } = CatalogItemTypes.Module;
    public string BillingType { get; set; } = BillingTypes.FixedMonthly;
    public decimal BaseUnitPrice { get; set; }
    public decimal MinimumFee { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public string? DependsOnCodes { get; set; }   // JSON array of InternalCode strings
    public bool RequiresApproval { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsRetired { get; set; }
    public bool IsInternalOnly { get; set; }
    public string? FeatureKey { get; set; }        // maps to FeatureKeys constants
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? CreatedByPlatformUserId { get; set; }
}

// Price tiers / volume pricing
public class ProductPrice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductCatalogItemId { get; set; }
    public int MinQuantity { get; set; } = 1;
    public int? MaxQuantity { get; set; }
    public decimal UnitPrice { get; set; }
    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime? EffectiveTo { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Package Builder ─────────────────────────────────────────────────────────

public class Package
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string InternalCode { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public ICollection<PackageVersion> Versions { get; set; } = new List<PackageVersion>();
}

public class PackageVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PackageId { get; set; }
    public int VersionNumber { get; set; } = 1;
    public string? Notes { get; set; }
    public bool IsCurrent { get; set; } = true;
    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Package? Package { get; set; }
    public ICollection<PackageItem> Items { get; set; } = new List<PackageItem>();
}

public static class PackageInclusionTypes
{
    public const string Included   = "Included";
    public const string Optional   = "Optional";
    public const string Discounted = "Discounted";
}

public class PackageItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PackageVersionId { get; set; }
    public Guid ProductCatalogItemId { get; set; }
    public string InclusionType { get; set; } = PackageInclusionTypes.Included;
    public decimal? DiscountPercent { get; set; }
    public bool IsRequired { get; set; } = true;
    public int SortOrder { get; set; }
    public PackageVersion? PackageVersion { get; set; }
    public ProductCatalogItem? ProductCatalogItem { get; set; }
}

// ── Deal Builder ──────────────────────────────────────────────────────────

public static class DealStatuses
{
    public const string Draft           = "Draft";
    public const string PendingApproval = "PendingApproval";
    public const string Approved        = "Approved";
    public const string Rejected        = "Rejected";
    public const string Activated       = "Activated";
    public const string Cancelled       = "Cancelled";
    public const string Expired         = "Expired";
    public const string Amended         = "Amended";
}

public static class InvoiceDisplayModes
{
    public const string Itemized = "Itemized";
    public const string Package  = "Package";
    public const string Hybrid   = "Hybrid";
}

public class CustomerDeal : INullableTenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }           // null = prospective customer
    public string CompanyName { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string ContactName { get; set; } = string.Empty;

    // Configuration
    public Guid? BaseProductId { get; set; }
    public Guid? PackageVersionId { get; set; }
    public int EmployeeCount { get; set; }
    public int UserCount { get; set; }
    public int CompanyCount { get; set; } = 1;
    public string BillingCycle { get; set; } = "Monthly";
    public string InvoiceDisplayMode { get; set; } = InvoiceDisplayModes.Itemized;
    public string CurrencyCode { get; set; } = "USD";

    // Discounts
    public decimal AnnualDiscountPercent { get; set; }   // auto-applied for annual
    public decimal CustomDiscountPercent { get; set; }
    public string? CustomDiscountReason { get; set; }

    // Status
    public string Status { get; set; } = DealStatuses.Draft;
    public string? RejectionReason { get; set; }
    public string? Notes { get; set; }
    public int QuoteValidityDays { get; set; } = 30;

    // Calculated totals (from PricingEngine; stored for display/audit)
    public decimal CalculatedMRR { get; set; }
    public decimal CalculatedARR { get; set; }
    public decimal CalculatedOneTimeFees { get; set; }
    public decimal CalculatedFirstYearValue { get; set; }

    // Always-stored internal breakdown (never exposed publicly as package only)
    public string? InternalBreakdownJson { get; set; }
    // Customer-facing lines according to InvoiceDisplayMode
    public string? CustomerFacingLinesJson { get; set; }

    public string CreatedByEmail { get; set; } = string.Empty;
    public string? ApprovedByEmail { get; set; }
    public string? ApprovalRequiredReason { get; set; }
    public Guid? PlatformApprovalRequestId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }

    public ICollection<DealLineItem> LineItems { get; set; } = new List<DealLineItem>();
}

public static class DealLineTypes
{
    public const string Module     = "Module";
    public const string Feature    = "Feature";
    public const string AddOn      = "AddOn";
    public const string Service    = "Service";
    public const string OneTimeFee = "OneTimeFee";
    public const string Discount   = "Discount";
    public const string Usage      = "Usage";
}

public class DealLineItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CustomerDealId { get; set; }
    public Guid? ProductCatalogItemId { get; set; }
    public string LineType { get; set; } = DealLineTypes.Module;
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TotalPrice { get; set; }
    public bool IsIncludedInPackage { get; set; }
    public bool IsRecurring { get; set; } = true;
    public int SortOrder { get; set; }
    public string? Notes { get; set; }
    public CustomerDeal? Deal { get; set; }
    public ProductCatalogItem? CatalogItem { get; set; }
}

// ── Quote and Invoice ─────────────────────────────────────────────────────

public static class QuoteTypes
{
    public const string DraftQuote      = "DraftQuote";
    public const string ApprovedQuote   = "ApprovedQuote";
    public const string ProformaInvoice = "ProformaInvoice";
    public const string Amendment       = "Amendment";
}

public static class CpqQuoteStatuses
{
    public const string Draft    = "Draft";
    public const string Sent     = "Sent";
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
    public const string Expired  = "Expired";
}

public class Quote : INullableTenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CustomerDealId { get; set; }
    public Guid? TenantId { get; set; }
    public string QuoteNumber { get; set; } = string.Empty;
    public string QuoteType { get; set; } = QuoteTypes.DraftQuote;
    public string Status { get; set; } = CpqQuoteStatuses.Draft;
    public string CurrencyCode { get; set; } = "USD";
    public string DisplayMode { get; set; } = InvoiceDisplayModes.Itemized;
    public DateOnly? ValidUntilDate { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal GrandTotal { get; set; }
    public decimal MRR { get; set; }
    public decimal ARR { get; set; }
    public decimal OneTimeFees { get; set; }
    public decimal FirstYearValue { get; set; }
    // Always stored: internal itemized breakdown
    public string? InternalBreakdownJson { get; set; }
    // Customer-facing display lines per DisplayMode
    public string? CustomerFacingLinesJson { get; set; }
    public string? Notes { get; set; }
    public string? Terms { get; set; }
    public string CreatedByEmail { get; set; } = string.Empty;
    public string? ApprovedByEmail { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentAtUtc { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
    public ICollection<QuoteLine> Lines { get; set; } = new List<QuoteLine>();
}

public class QuoteLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid QuoteId { get; set; }
    public int DisplayOrder { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public decimal Total { get; set; }
    public bool IsPackageLine { get; set; }
    public bool IsRecurring { get; set; } = true;
    public Quote? Quote { get; set; }
}

// ── Subscription Lines ───────────────────────────────────────────────────

public class SubscriptionLine : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubscriptionId { get; set; }
    public Guid TenantId { get; set; }
    public Guid? ProductCatalogItemId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string BillingType { get; set; } = BillingTypes.FixedMonthly;
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal Total { get; set; }
    public bool IsActive { get; set; } = true;
    public string? FeatureKey { get; set; }
    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime? EffectiveTo { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Subscription Amendments ───────────────────────────────────────────────

public static class AmendmentStatuses
{
    public const string Draft           = "Draft";
    public const string PendingApproval = "PendingApproval";
    public const string Approved        = "Approved";
    public const string Rejected        = "Rejected";
    public const string Activated       = "Activated";
    public const string Cancelled       = "Cancelled";
}

public static class AmendmentTypes
{
    public const string AddModule          = "AddModule";
    public const string RemoveModule       = "RemoveModule";
    public const string ChangeEmployeeLimit = "ChangeEmployeeLimit";
    public const string ChangeBillingCycle = "ChangeBillingCycle";
    public const string ChangePackage      = "ChangePackage";
    public const string AddDiscount        = "AddDiscount";
    public const string Other              = "Other";
}

public class SubscriptionAmendment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OriginalSubscriptionId { get; set; }
    public string Status { get; set; } = AmendmentStatuses.Draft;
    public string AmendmentType { get; set; } = AmendmentTypes.Other;
    public string Description { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public string? OldValueJson { get; set; }
    public string? NewValueJson { get; set; }
    public decimal MonthlyPriceDelta { get; set; }
    public bool RequiresApproval { get; set; }
    public Guid? ApprovalRequestId { get; set; }
    public string CreatedByEmail { get; set; } = string.Empty;
    public string? ApprovedByEmail { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ActivatedAtUtc { get; set; }
}

// ── Tenant Limits ─────────────────────────────────────────────────────────

public static class LimitTypes
{
    public const string Employees     = "Employees";
    public const string Users         = "Users";
    public const string Companies     = "Companies";
    public const string Branches      = "Branches";
    public const string StorageMb     = "StorageMb";
    public const string AiTokens      = "AiTokensMonthly";
    public const string ApiCalls      = "ApiCallsMonthly";
    public const string PayrollRuns   = "PayrollRunsMonthly";
    public const string Workflows     = "Workflows";
    public const string CustomRoles   = "CustomRoles";
}

public class TenantLimit : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string LimitType { get; set; } = string.Empty;
    public long MaxValue { get; set; }              // 0 = unlimited
    public int SoftWarningPercent { get; set; } = 80;
    public int HardLimitPercent { get; set; } = 100;
    public int GracePeriodDays { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

// ── Tenant Usage Snapshot ─────────────────────────────────────────────────

public class TenantUsageSnapshot : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int YearMonth { get; set; }              // YYYYMM
    public long EmployeesActive { get; set; }
    public long UsersActive { get; set; }
    public long CompaniesActive { get; set; }
    public long BranchesActive { get; set; }
    public long StorageUsedMb { get; set; }
    public long AiTokensUsed { get; set; }
    public long ApiCallsCount { get; set; }
    public long PayrollRunsCount { get; set; }
    public long WorkflowsCount { get; set; }
    public long CustomRolesCount { get; set; }
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Internal Invoice Breakdown ────────────────────────────────────────────

public class InternalInvoiceBreakdown : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public Guid TenantId { get; set; }
    public string BreakdownJson { get; set; } = "[]";   // always itemized
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// ── Approval Requests ─────────────────────────────────────────────────────

public static class ApprovalRequestTypes
{
    public const string DiscountAboveThreshold   = "DiscountAboveThreshold";
    public const string FreePremiumFeature        = "FreePremiumFeature";
    public const string SetupFeeWaiver            = "SetupFeeWaiver";
    public const string CustomPrice               = "CustomPrice";
    public const string ManualInvoiceAdjustment   = "ManualInvoiceAdjustment";
    public const string BackdatedPricing          = "BackdatedPricing";
    public const string SubscriptionCancellation  = "SubscriptionCancellation";
    public const string TenantSuspension          = "TenantSuspension";
    public const string SupportSessionSensitive   = "SupportSessionSensitive";
    public const string DealActivation            = "DealActivation";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        DiscountAboveThreshold, FreePremiumFeature, SetupFeeWaiver, CustomPrice,
        ManualInvoiceAdjustment, BackdatedPricing, SubscriptionCancellation,
        TenantSuspension, SupportSessionSensitive, DealActivation
    };
}

public static class ApprovalStatuses
{
    public const string Draft           = "Draft";
    public const string PendingApproval = "PendingApproval";
    public const string Approved        = "Approved";
    public const string Rejected        = "Rejected";
    public const string Expired         = "Expired";
    public const string Cancelled       = "Cancelled";
}

public class PlatformApprovalRequest : INullableTenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? CustomerDealId { get; set; }
    public Guid? SubscriptionAmendmentId { get; set; }
    public string RequestType { get; set; } = string.Empty;
    public string Status { get; set; } = ApprovalStatuses.PendingApproval;
    public string RequestedByEmail { get; set; } = string.Empty;
    public string RequestedAction { get; set; } = string.Empty;
    public string? OldValueJson { get; set; }
    public string? NewValueJson { get; set; }
    public string Reason { get; set; } = string.Empty;
    public decimal? FinancialImpactMonthly { get; set; }
    public string? ApprovedByEmail { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public DateTime? RejectedAtUtc { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddDays(7);
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

// ── Pricing Engine DTOs (not persisted, used in-memory) ───────────────────

/// <summary>Full itemized result from the PricingEngine — always computed, always stored internally.</summary>
public class PricingResult
{
    public List<PricingLineItem> InternalLines { get; set; } = new();
    public List<CustomerFacingLine> CustomerLines { get; set; } = new();
    public decimal GrossMonthly { get; set; }
    public decimal NetMonthly { get; set; }         // after discounts
    public decimal AnnualRecurring { get; set; }
    public decimal OneTimeFees { get; set; }
    public decimal FirstYearContractValue { get; set; }
    public decimal TotalDiscount { get; set; }
    public string CurrencyCode { get; set; } = "USD";
}

public class PricingLineItem
{
    public string InternalCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string BillingType { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public decimal Total { get; set; }
    public bool IsRecurring { get; set; }
    public bool IsOneTime { get; set; }
    public string? FeatureKey { get; set; }
}

public class CustomerFacingLine
{
    public int Order { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Total { get; set; }
    public bool IsPackageLine { get; set; }
    public bool IsRecurring { get; set; } = true;
}

// ── Approval thresholds (configurable) ────────────────────────────────────

public static class ApprovalThresholds
{
    /// <summary>Discount % at or above which an approval request is required.</summary>
    public const decimal DiscountRequiresApprovalAt = 15m;
}
