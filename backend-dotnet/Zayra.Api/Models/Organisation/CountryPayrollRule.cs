using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// ── Country Payroll Rules (Configurable per country) ─────────────────────────

public class CountryPayrollRule : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public string RuleKey { get; set; } = string.Empty; // eosb_rate, overtime_multiplier_holiday, gratuity_threshold_years, etc.
    public string RuleValue { get; set; } = string.Empty;
    public string DataType { get; set; } = "string"; // string, decimal, int, bool, json
    public string Description { get; set; } = string.Empty;
    public bool IsOverride { get; set; }
    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime? EffectiveTo { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
}
