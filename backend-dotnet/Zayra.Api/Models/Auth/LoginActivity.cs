using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// ── Login Activity ────────────────────────────────────────────────────────────

public class LoginActivity : INullableTenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? UserId { get; set; }
    /// <summary>Email used in the login attempt. NEVER store passwords or tokens.</summary>
    public string? EmailAttempted { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? FailureReason { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}

public static class LoginEventTypes
{
    public const string LoginSuccess             = "login_success";
    public const string LoginFailed              = "login_failed";
    public const string LoginBlockedLockout      = "login_blocked_lockout";
    public const string AccountLocked            = "account_locked";
    public const string PasswordResetRequested   = "password_reset_requested";
    public const string PasswordResetCompleted   = "password_reset_completed";
    public const string MfaReset                 = "mfa_reset";
    public const string SessionRevoked           = "session_revoked";
    public const string PlatformLoginSuccess     = "platform_login_success";
    public const string PlatformLoginFailed      = "platform_login_failed";
}
