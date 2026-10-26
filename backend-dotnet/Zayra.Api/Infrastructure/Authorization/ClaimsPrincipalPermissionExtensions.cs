using System.Security.Claims;

namespace Zayra.Api.Infrastructure.Authorization;

/// <summary>
/// One shared definition of the effective-permission claim check, so the attribute handler
/// and any imperative call site agree byte-for-byte on what "has this permission" means:
/// a <c>permission</c> claim whose value equals the key, case-insensitively.
/// </summary>
public static class ClaimsPrincipalPermissionExtensions
{
    public const string PermissionClaimType = "permission";

    public static bool HasPermission(this ClaimsPrincipal? user, string permission)
    {
        if (user is null || string.IsNullOrWhiteSpace(permission)) return false;
        foreach (var claim in user.Claims)
        {
            if (claim.Type == PermissionClaimType && string.Equals(claim.Value, permission, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>The caller's effective permission keys, read from the validated principal only (never from the request body).</summary>
    public static IReadOnlyCollection<string> EffectivePermissions(this ClaimsPrincipal? user) =>
        user is null
            ? Array.Empty<string>()
            : new HashSet<string>(user.Claims.Where(c => c.Type == PermissionClaimType).Select(c => c.Value), StringComparer.OrdinalIgnoreCase);

    public static bool HasAnyPermission(this ClaimsPrincipal? user, IEnumerable<string> permissions)
    {
        if (user is null || permissions is null) return false;
        foreach (var permission in permissions)
        {
            if (user.HasPermission(permission)) return true;
        }
        return false;
    }
}
