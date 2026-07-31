using Microsoft.AspNetCore.Authorization;

namespace Zayra.Api.Infrastructure.Authorization;

/// <summary>
/// The single source of truth for <c>[HasPermission]</c> enforcement. Reads the same
/// <c>permission</c> claim the 18 existing imperative controllers check, with the same
/// case-insensitive comparison. It resolves nothing at request time — the effective set
/// (role bundles ∪ access-mode ∪ Allow − Deny) is already baked into the claim at token
/// issuance, so a Deny override is honored simply because the claim is absent.
///
/// FAIL-CLOSED: an empty requirement or the absence of any matching claim means
/// <see cref="AuthorizationHandlerContext.Succeed"/> is never called, so the default-deny
/// pipeline yields 403 (401 if unauthenticated).
/// </summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (requirement.Any.Count > 0 && context.User.HasAnyPermission(requirement.Any))
        {
            context.Succeed(requirement);
        }

        // No Succeed() on the miss path → default-deny.
        return Task.CompletedTask;
    }
}
