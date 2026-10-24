using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// ── MFA Challenge Token ───────────────────────────────────────────────────────

/// <summary>Short-lived single-use token issued after password validates but before TOTP is checked.
/// Stored as a SHA-256 hash. Raw value is returned to the client as the MFA challenge token.</summary>
public class MfaChallengeToken : INullableTenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Set for tenant users.</summary>
    public Guid? UserId { get; set; }
    /// <summary>Set for platform admins.</summary>
    public Guid? PlatformUserId { get; set; }
    /// <summary>TenantId of the user, null for platform admins.</summary>
    public Guid? TenantId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public string CreatedByIp { get; set; } = string.Empty;
    /// <summary>Set when the token is consumed. Null = still valid.</summary>
    public DateTime? UsedAtUtc { get; set; }
    /// <summary>
    /// Wrong-TOTP attempts against this challenge. The challenge is consumed once this reaches
    /// <see cref="MaxAttempts"/>, so a single challenge token cannot be used to brute-force the
    /// 6-digit code within its TTL (MFA-bypass hardening).
    /// </summary>
    public int FailedAttempts { get; set; }
    public const int MaxAttempts = 5;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsValid => UsedAtUtc is null && DateTime.UtcNow < ExpiresAtUtc;
}
