using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// tenant_subscriptions: no reader since WP3 (the signed licence holds the limits); kept mapped only until WP5
// drops the table. tenant_feature_flags: what the organisation has switched on, capped by the licence
// (TenantModuleService); it becomes organisation_modules in WP5.

// ── Tenant Subscription ───────────────────────────────────────────────────────

public class TenantSubscription : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Plan { get; set; } = "Starter"; // Starter, Growth, Enterprise
    public string Status { get; set; } = "Active"; // see SubscriptionStatuses constants
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAtUtc { get; set; }
    public int MaxEmployees { get; set; } = 50;
    public int MaxUsers { get; set; } = 10;
    public int MaxCompanies { get; set; } = 1;
    public int MaxAdminUsers { get; set; } = 10;
    public string BillingEmail { get; set; } = string.Empty;
    public string BillingCycle { get; set; } = "Monthly"; // Monthly, Annual
    public decimal MonthlyAmount { get; set; }
    public string CurrencyCode { get; set; } = "USD";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

// ── Tenant Feature Flags ──────────────────────────────────────────────────────

public class TenantFeatureFlag : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string FeatureKey { get; set; } = string.Empty; // ai_assistant, mobile_app, wps_export, etc.
    public bool IsEnabled { get; set; }
    public string? ConfigJson { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedBy { get; set; }
}
