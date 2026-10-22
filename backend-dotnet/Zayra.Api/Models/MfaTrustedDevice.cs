using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// A browser a tenant user chose to trust after completing a two-step code ("Trust this browser for 30 days").
/// While the row is live, the user's next password sign-in from that browser skips the CODE step only: the password,
/// lockout, abuse windows and enrolment rules all still apply (Infrastructure/Auth/MfaTrustedDevices).
///
/// <para>The browser holds a 32-byte random token in the HttpOnly <c>kx_mfa_trust</c> cookie; only its SHA-256
/// (<see cref="TokenHash"/>, 64 hex chars) is stored, so a database read never yields a usable cookie. The row is
/// server-side and revocable: by the user (one browser or all), and automatically whenever the account's password,
/// factor or active state changes (<c>MfaTrustedDevices.StageRevokeAllAsync</c>, called explicitly and from the
/// ZayraDbContext save hook). Platform operators never get one.</para>
/// </summary>
public class MfaTrustedDevice : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Uppercase hex SHA-256 of the raw cookie token. Unique.</summary>
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
    /// <summary>The browser's User-Agent at trust time (truncated to 256), for the "Trusted browsers" list.</summary>
    public string? UserAgent { get; set; }
    public string? IpAddress { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    /// <summary>One of <see cref="MfaTrustRevokeReasons.All"/>; set together with <see cref="RevokedAtUtc"/> (CHECK).</summary>
    public string? RevokedReason { get; set; }
}

/// <summary>Why a trusted browser stopped being trusted. The CHECK on <c>revoked_reason</c> is this list.</summary>
public static class MfaTrustRevokeReasons
{
    public const string UserRevoked = "user_revoked";
    public const string UserRevokedAll = "user_revoked_all";
    public const string PasswordChanged = "password_changed";
    public const string PasswordReset = "password_reset";
    public const string MfaDisabled = "mfa_disabled";
    public const string MfaRecoveryDisabled = "mfa_recovery_disabled";
    public const string MfaResetup = "mfa_resetup";
    public const string UserDeactivated = "user_deactivated";
    public const string UserDeleted = "user_deleted";
    /// <summary>The tenant shortened its trust lifetime and this browser's shorter period had already run out.</summary>
    public const string PolicyShortened = "policy_shortened";

    public static readonly string[] All =
    [
        UserRevoked, UserRevokedAll, PasswordChanged, PasswordReset, MfaDisabled, MfaRecoveryDisabled, MfaResetup,
        UserDeactivated, UserDeleted, PolicyShortened,
    ];
}
