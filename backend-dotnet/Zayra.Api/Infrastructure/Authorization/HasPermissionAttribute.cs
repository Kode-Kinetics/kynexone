using Microsoft.AspNetCore.Authorization;

namespace Zayra.Api.Infrastructure.Authorization;

/// <summary>
/// Server-side authorization attribute that gates an endpoint on the caller's EFFECTIVE
/// permissions (the <c>permission</c> claims materialized into the JWT at login by
/// <see cref="Zayra.Api.Infrastructure.Auth.AuthService.GetPermissions"/> — role-permission
/// bundles ∪ access-mode perms ∪ Allow-overrides − Deny-overrides). This is what makes a
/// client-created CUSTOM role real: granting a permission to any role (or a per-user Allow
/// override) is enough to reach the endpoint; a Deny override removes the claim at issuance
/// and is therefore denied here with no extra code.
///
/// Semantics are ANY-of (matches the existing imperative <c>HasAnyPermission</c> pattern):
/// the caller needs AT LEAST ONE of the listed keys. Fail-closed — an empty key list or a
/// caller without a matching claim never authorizes (see PermissionAuthorizationHandler).
///
/// Because it derives from <see cref="AuthorizeAttribute"/> it flows through the native
/// authorization pipeline, so an unauthenticated caller gets 401 and an authenticated caller
/// lacking the permission gets 403 — no controller-body changes required.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class HasPermissionAttribute : AuthorizeAttribute
{
    /// <summary>Prefix that marks a synthesized permission policy name (see PermissionPolicyProvider).</summary>
    public const string PolicyPrefix = "perm:";

    /// <summary>Separator between permission keys inside the synthesized policy name.</summary>
    public const char Separator = '|';

    public HasPermissionAttribute(params string[] permissions)
    {
        Permissions = (permissions ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Policy = PolicyPrefix + string.Join(Separator, Permissions);
    }

    /// <summary>The normalized ANY-of permission keys this attribute requires.</summary>
    public IReadOnlyList<string> Permissions { get; }
}
