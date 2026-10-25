using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// The installation configuration store (SMTP relay, privileged-MFA enforcement date, maintenance mode).
// It keeps its historical name until WP5 moves it to installation_settings.

// ── Platform Configuration Entries ──────────────────────────────────────────────
// Persistent key-value store for mutable installation settings (e.g. maintenance mode).
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
    /// This installation's identity (a UUID), created once at organisation setup. A licence may be bound to it,
    /// so the client sends it to the vendor to receive a licence that works on this installation only.
    /// </summary>
    public const string InstallationId = "installation.id";
}
