using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// Surface A — FULL client-CRUD, NON-statutory rate policy for a legal entity.
/// Custom allowance/deduction component rates, company pay parameters, and company-level
/// EOSB parameters where legally permitted (above statutory floor only). Effective-dated and
/// SUPERSEDED on change (never mutated in place) so historical resolution stays deterministic.
///
/// COMPLIANCE BOUNDARY: RateKey MUST resolve to a client-configurable entry in
/// <see cref="ClientRateDefinition"/> (a seeded ALLOW-list). Statutory / Saudization / floored
/// rates are rejected here and can only be touched through Surface B (CompanyStatutoryOverride).
/// </summary>
public class CompanyRatePolicy : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }        // NULL = tenant default; set = company override (wins)
    public string RateKey { get; set; } = "";   // e.g. "allowance.wellness.monthly_amount"
    public string RateCategory { get; set; } = ""; // "Allowance" | "Deduction" | "EOSB" | "PayParameter"
    public string RateValue { get; set; } = "";
    public string DataType { get; set; } = "decimal";
    public string Unit { get; set; } = "";       // "amount" | "percent" | "days" | "multiplier"
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public string Status { get; set; } = CompanyPolicyStatuses.Active;
    public string Notes { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>
/// Surface B — BOUNDED per-company OVERRIDE of a statutory rate. Requires reason text, is
/// effective-dated, visibly flagged, audited, and routed through maker-checker. CompanyId is
/// REQUIRED (a client may override statutory only for its own legal entity — never redefine the
/// platform default, which stays owned by StatutoryRuleSeeder). The seeded statutory default
/// remains the authoritative fallback. Superseded on change (append-only) and archived on revert.
/// </summary>
public class CompanyStatutoryOverride : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }        // REQUIRED at the API boundary (NULL rejected)
    public string CountryCode { get; set; } = "";
    public string Jurisdiction { get; set; } = "";
    public string RuleKey { get; set; } = "";   // MUST reference a known StatutoryRule key
    public string OverrideValue { get; set; } = "";
    public string DataType { get; set; } = "decimal";
    public DateOnly EffectiveFrom { get; set; }         // effective-dated: REQUIRED
    public DateOnly? EffectiveTo { get; set; }
    // Required review/expiry date — a bounded override must not silently outlive its justification.
    public DateOnly? ReviewBy { get; set; }
    public string Reason { get; set; } = "";            // REQUIRED free text
    // Snapshot of the platform default at creation time — evidence + drift detection.
    public string PlatformDefaultAtCreation { get; set; } = "";
    // Draft → PendingApproval → Active → Archived. Only Active participates in resolution.
    public string Status { get; set; } = CompanyPolicyStatuses.Draft;
    public Guid? ApprovalRequestId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>
/// Seeded ALLOW-list of client-configurable (non-statutory) rate keys. This is the compliance
/// guardrail: Surface A writes are validated against this registry (key must exist, value must be
/// the right type and within [Min,Max]). Anything not enumerated here is NOT client-editable —
/// an allow-list, never a denylist, so a new statutory rate can never leak into free CRUD by
/// omission. Seeded as defaults (single declaration in <see cref="StatutoryRateGuard"/>), never
/// the only source — tenants may extend, but statutory keys are refused at seed and write time.
/// </summary>
public class ClientRateDefinition : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string RateKey { get; set; } = "";
    public string RateCategory { get; set; } = "";
    public string DataType { get; set; } = "decimal";
    public string Unit { get; set; } = "";
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public string Description { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
