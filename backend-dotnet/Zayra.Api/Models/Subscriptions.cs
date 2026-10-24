using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// BRIDGE until WP3: the limit and module readers still read these rows. The signed licence replaces
// tenant_subscriptions and tenant_feature_flags, and AiPlanLimits becomes a licence field.

// ── Subscription status constants ─────────────────────────────────────────────

public static class SubscriptionStatuses
{
    public const string Trial          = "Trial";
    public const string Active         = "Active";
    public const string PastDue        = "PastDue";
    public const string Suspended      = "Suspended";
    public const string Cancelled      = "Cancelled";
    public const string ManualContract = "ManualContract";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Trial, Active, PastDue, Suspended, Cancelled, ManualContract
    };
}

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

public static class AiPlanLimits
{
    // Monthly token limits per plan. 0 = unlimited.
    public static long GetMonthlyTokenLimit(string plan) => plan switch
    {
        "Starter"    => 50_000,
        "Growth"     => 200_000,
        "Enterprise" => 0,
        _            => 50_000
    };
}
