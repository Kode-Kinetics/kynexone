using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Zayra.Api.Infrastructure.Authorization;

/// <summary>
/// Materializes <c>perm:</c> authorization policies on demand from the policy name that
/// <see cref="HasPermissionAttribute"/> synthesizes, so no policy has to be pre-registered.
/// Every other policy name — including the "PlatformAdmin" policy and the app-wide
/// <see cref="AuthorizationOptions.FallbackPolicy"/> / default policy configured in
/// <c>AddAuthorization</c> — is delegated unchanged to the framework default provider, so
/// existing authorization behavior is untouched.
/// </summary>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        _fallback = new DefaultAuthorizationPolicyProvider(options);
    }

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!string.IsNullOrEmpty(policyName)
            && policyName.StartsWith(HasPermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            var keys = policyName[HasPermissionAttribute.PolicyPrefix.Length..]
                .Split(HasPermissionAttribute.Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(keys))
                .Build();

            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return _fallback.GetPolicyAsync(policyName);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();
}
