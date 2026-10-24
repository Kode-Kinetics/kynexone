using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// The installation configuration store (SMTP relay, privileged-MFA enforcement date, maintenance mode).
// It keeps its historical name until WP5 moves it to installation_settings.

// ── Platform Configuration Entries ──────────────────────────────────────────────
// Persistent key-value store for mutable platform settings (e.g. maintenance mode).
// Keys are simple ASCII strings; values are free-form strings (booleans as "true"/"false").

public class PlatformConfigEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedByPlatformUserId { get; set; }
}

public static class PlatformConfigKeys
{
    public const string MaintenanceMode    = "maintenance_mode";
    public const string MaintenanceMessage = "maintenance_message";

    /// <summary>
    /// Where a public pricing-quote request is announced: exactly one address. The public wizard
    /// tells the buyer "our team will contact you within 1 business day", so a quote that reaches
    /// nobody is a broken promise. Falls back to Sales:NotificationEmail, then to the platform
    /// relay's From address. Never taken from the request.
    /// </summary>
    public const string SalesNotificationAddress = "sales_notification_address";
}
