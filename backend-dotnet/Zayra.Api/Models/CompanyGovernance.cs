using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>Lifecycle states shared by company governance policies.</summary>
public static class CompanyPolicyStatuses
{
    public const string Draft = "Draft";
    public const string Active = "Active";
    public const string Archived = "Archived";
}

/// <summary>
/// Per-legal-entity income tax policy. Payroll runs are company-scoped, so tax cannot
/// remain a tenant-wide SystemSettings row: two legal entities in one group may sit in
/// different jurisdictions with different tax treatment.
///
/// Precedence mirrors StatutoryRule: company-specific row (CompanyId set) beats the
/// tenant default row (CompanyId == null); among candidates the newest EffectiveFrom
/// that covers the requested date wins. Resolution: CompanyTaxPolicyResolver.
///
/// CONFIGURABLE POLICY FOUNDATION ONLY — values are customer-entered configuration and
/// carry no claim of statutory/legal correctness; jurisdiction rule packs require legal
/// validation before being treated as authoritative.
/// </summary>
public class CompanyTaxPolicy : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    // Null = tenant-wide default policy; set = legal-entity override (wins).
    public Guid? CompanyId { get; set; }
    // Canonical ISO-2 (validated against IsoReference / CountryCodeStandard).
    public string CountryCode { get; set; } = string.Empty;
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    // Draft | Active | Archived — only Active policies participate in resolution.
    public string Status { get; set; } = CompanyPolicyStatuses.Active;
    // Structured fast-path fields for the current flat-rate model…
    public decimal? IncomeTaxRatePercent { get; set; }
    public bool AppliesToBonus { get; set; } = true;
    // …plus an extensibility bag for bracketed/complex regimes without schema churn.
    public string TaxConfigurationJson { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>
/// Per-legal-entity compliance readiness profile — the enforcement boundary for
/// jurisdiction-specific requirements (statutory fields, document requirements,
/// registration identifiers). Groups may later publish default templates, but the
/// company profile is what enforcement reads.
///
/// CompliancePack references a CountryPack registry key (resolved via
/// country:jurisdiction fallback). RequiredFieldsJson declares which statutory employee
/// fields must fail closed when missing for this entity.
///
/// CONFIGURABLE READINESS PROFILE — country rules are configuration requiring legal
/// validation; nothing here asserts statutory completeness or legal certification.
/// </summary>
public class CompanyComplianceProfile : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    // Null = tenant-wide default template; set = legal-entity profile (wins).
    public Guid? CompanyId { get; set; }
    // Canonical ISO-2 (validated against IsoReference / CountryCodeStandard).
    public string CountryCode { get; set; } = string.Empty;
    // e.g. "KSA-mainland", "UAE-DIFC" — matches the CountryPack jurisdiction keys.
    public string Jurisdiction { get; set; } = string.Empty;
    // CountryPack registry key this profile binds to (e.g. "SAU", "SAU:KSA-mainland").
    public string CompliancePack { get; set; } = string.Empty;
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    // Draft | Active | Archived — only Active profiles participate in resolution.
    public string Status { get; set; } = CompanyPolicyStatuses.Active;
    // JSON array of statutory field requirements, e.g. [{"field":"IqamaNumber","failClosed":true}].
    public string RequiredFieldsJson { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
}
