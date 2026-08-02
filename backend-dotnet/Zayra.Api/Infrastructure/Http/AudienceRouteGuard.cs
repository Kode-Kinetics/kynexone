using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Zayra.Api.Infrastructure.Http;

/// <summary>
/// Per-route audience segregation (defence-in-depth on the read axis).
///
/// Platform-audience tokens are minted only by <c>/api/platform/auth/login</c> and
/// <c>/api/platform/auth/mfa/challenge/verify</c> and are intended solely for platform-admin
/// (<c>/api/platform/*</c>) routes, where they legitimately exercise the cross-tenant read bypass.
/// A platform token presented on a tenant <c>/api/*</c> route is rejected here with a 403 so it can
/// never exercise that bypass against tenant data.
///
/// Impersonation and break-glass tokens carry the TENANT audience (with a <c>tenant_id</c> claim),
/// so platform admins always do cross-tenant work through tenant-scoped tokens — those are never
/// matched by this guard. The logic is a pure static (mirroring <see cref="SecurityHeaders"/>) so it
/// can be unit-tested without booting the host; the middleware body is likewise directly invokable.
/// </summary>
public static class AudienceRouteGuard
{
    /// <summary>Error code returned in the 403 body when a platform token hits a tenant route.</summary>
    public const string ViolationCode = "platform_token_on_tenant_route";

    /// <summary>
    /// True iff an authenticated platform-audience token is being used on a tenant <c>/api/*</c>
    /// route — any <c>/api/*</c> path that is NOT under <c>/api/platform</c>. Segment-aware and
    /// case-insensitive so <c>/API/employees</c> is still caught and <c>/api/platformx</c> is not
    /// wrongly treated as a platform route.
    /// </summary>
    public static bool IsPlatformTokenOnTenantRoute(PathString path, ClaimsPrincipal? user, string platformAudience)
    {
        if (string.IsNullOrEmpty(platformAudience)) return false;
        if (user?.Identity?.IsAuthenticated != true) return false;
        if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.StartsWithSegments("/api/platform", StringComparison.OrdinalIgnoreCase)) return false;
        return user.HasClaim("aud", platformAudience);
    }

    /// <summary>
    /// Middleware body. Registered between <c>UseAuthentication()</c> and <c>UseAuthorization()</c>
    /// so <see cref="HttpContext.User"/> is populated and this runs before endpoint authorization.
    /// On a violation it short-circuits with a 403 in the standard {traceId, code, message} error
    /// shape; otherwise it passes through to the next middleware.
    /// </summary>
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next, string platformAudience)
    {
        if (IsPlatformTokenOnTenantRoute(context.Request.Path, context.User, platformAudience))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                traceId = context.TraceIdentifier,
                code = ViolationCode,
                message = "Platform tokens are not accepted on tenant routes. "
                    + "Use an impersonation or tenant-scoped session for cross-tenant work.",
            });
            return;
        }

        await next(context);
    }
}
