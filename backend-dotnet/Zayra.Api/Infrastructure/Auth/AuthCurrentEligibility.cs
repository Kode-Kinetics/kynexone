using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

internal readonly record struct AuthEligibilityDecision(bool Allowed, string Reason)
{
    public static AuthEligibilityDecision Permit() => new(true, string.Empty);
    public static AuthEligibilityDecision Deny(string reason) => new(false, reason);
}

internal static class AuthCurrentEligibility
{
    public static bool IsEmployeeLifecycleEligible(string? status) =>
        string.Equals(status, EmployeeStatuses.Active, StringComparison.Ordinal)
        || string.Equals(status, EmployeeStatuses.Invited, StringComparison.Ordinal);

    public static AuthEligibilityDecision ForPasswordEntry(
        User? user,
        bool ssoOnly,
        DateTime nowUtc) => Evaluate(user, ssoOnly, policy: null, requireSessionMfa: false, nowUtc);

    public static AuthEligibilityDecision ForSession(
        User? user,
        bool ssoOnly,
        SecuritySetting? policy,
        DateTime nowUtc) => Evaluate(user, ssoOnly, policy, requireSessionMfa: true, nowUtc);

    public static bool IsSsoOnly(User user, TenantIdentityProviderSetting? setting)
    {
        if (user.IdentityProvider.Equals("Local", StringComparison.OrdinalIgnoreCase)) return false;
        if (setting?.EnforceSsoLogin != true) return false;
        var domain = user.Email.Split('@').LastOrDefault() ?? string.Empty;
        var allowed = setting.AllowedDomainsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Length == 0 || allowed.Contains(domain, StringComparer.OrdinalIgnoreCase);
    }

    public static EmployeeUserAccount? PrimaryAccess(User user) =>
        user.EmployeeUserAccounts
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.IsPrimary)
            .ThenByDescending(x => x.CreatedAtUtc)
            .FirstOrDefault();

    private static AuthEligibilityDecision Evaluate(
        User? user,
        bool ssoOnly,
        SecuritySetting? policy,
        bool requireSessionMfa,
        DateTime nowUtc)
    {
        if (user?.Tenant is null) return AuthEligibilityDecision.Deny("user_not_found");
        if (user.IsDeleted) return AuthEligibilityDecision.Deny("user_deleted");
        if (!user.IsActive) return AuthEligibilityDecision.Deny("user_inactive");
        if (!user.Tenant.IsActive) return AuthEligibilityDecision.Deny("tenant_inactive");
        if (!string.Equals(user.Status, "Active", StringComparison.Ordinal))
            return AuthEligibilityDecision.Deny("user_status_blocked");
        if (!user.IsEmailConfirmed) return AuthEligibilityDecision.Deny("email_unconfirmed");
        if ((user.IsLocked && (!user.LockoutEnd.HasValue || user.LockoutEnd > nowUtc))
            || (user.LockoutEnd.HasValue && user.LockoutEnd > nowUtc))
            return AuthEligibilityDecision.Deny("account_locked");

        var primary = PrimaryAccess(user);
        if (string.Equals(user.AccessMode, AccessModes.NoLogin, StringComparison.Ordinal)
            || string.Equals(primary?.AccessMode, AccessModes.NoLogin, StringComparison.Ordinal))
            return AuthEligibilityDecision.Deny("access_mode_no_login");
        if (primary?.RequiresPasswordSetup == true)
            return AuthEligibilityDecision.Deny("requires_password_setup");
        if (user.MustChangePassword)
            return AuthEligibilityDecision.Deny("must_change_password");
        if (ssoOnly)
            return AuthEligibilityDecision.Deny("sso_required");

        if (user.MFAEnabled && string.IsNullOrWhiteSpace(user.MfaSecretEncrypted))
            return AuthEligibilityDecision.Deny("mfa_state_invalid");
        if (requireSessionMfa && policy?.MfaRequired == true
            && (!user.MFAEnabled || string.IsNullOrWhiteSpace(user.MfaSecretEncrypted)))
            return AuthEligibilityDecision.Deny("mfa_enrollment_required");

        return AuthEligibilityDecision.Permit();
    }
}
