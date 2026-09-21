using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Zayra.Api.Infrastructure.Modules;

namespace Zayra.Api.Infrastructure.Filters;

/// <summary>
/// Blocks access to a module's API when the tenant has that module switched off.
///
/// <para>Runs globally (registered in <c>Program.cs</c>) and self-selects by URL prefix. The
/// prefixes are no longer kept here: they come from <see cref="ModuleCatalog"/>, which is also
/// what the tenant-facing module API, the notification dispatcher, the dashboard and the frontend
/// route guard read. Previously this file held two hand-maintained string arrays that had drifted
/// — seven of the nineteen mapped prefixes matched no controller at all, so the <c>finance</c> and
/// <c>wps_export</c> flags appeared switchable while gating nothing.</para>
///
/// <para>Defaults, stated deliberately because the layers used to disagree:
/// an unclassified route is <b>allowed</b> (a new controller is never accidentally unreachable in
/// production; <c>ModuleCatalogCoverageTests</c> is what keeps that set empty), and a module with
/// no stored row is <b>enabled</b>. Only an explicit, permitted <c>false</c> blocks.</para>
///
/// <para>A module the tenant is not permitted to disable is never blocked here even if a stale
/// <c>false</c> row exists for it — see <see cref="TenantModuleService"/>.</para>
/// </summary>
public class FeatureFlagGuardFilter : IAsyncActionFilter
{
    private readonly ITenantModuleService _modules;
    private readonly ILogger<FeatureFlagGuardFilter> _log;

    public FeatureFlagGuardFilter(ITenantModuleService modules, ILogger<FeatureFlagGuardFilter> log)
    {
        _modules = modules;
        _log = log;
    }

    /// <summary>
    /// Call this whenever a tenant module is toggled so the cached state is evicted immediately.
    /// The <paramref name="featureKey"/> is accepted for call-site compatibility; the cache is
    /// held per tenant, so the whole tenant's state is dropped.
    /// </summary>
    public static void InvalidateCache(IMemoryCache cache, Guid tenantId, string featureKey)
        => TenantModuleService.Invalidate(cache, tenantId);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;

        var module = ModuleCatalog.ResolveApiPath(path);
        if (module is null)
        {
            // Route not classified by the catalog — not a gated module, allow.
            await next();
            return;
        }

        if (module.Lock == ModuleLock.Core)
        {
            // Core modules are never gated. Resolving them explicitly (rather than through an
            // allow-list that had to be kept in sync by hand) is what removed the drift.
            await next();
            return;
        }

        var tenantClaim = context.HttpContext.User.FindFirstValue("tenant_id");
        if (!Guid.TryParse(tenantClaim, out var tenantId))
        {
            // No tenant context (unauthenticated or platform admin JWT) — let auth handle it.
            await next();
            return;
        }

        var state = await _modules.GetStateAsync(tenantId, context.HttpContext.RequestAborted);
        if (state.IsEnabled(module.Key))
        {
            await next();
            return;
        }

        _log.LogWarning(
            "FeatureFlagGuard blocked request. Tenant={TenantId} Feature={FeatureKey} Path={Path} IP={IP} UserAgent={UserAgent}",
            tenantId,
            module.Key,
            path,
            context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            context.HttpContext.Request.Headers.UserAgent.ToString());

        context.Result = new ObjectResult(new
        {
            error = "feature_not_enabled",
            feature = module.Key,
            message = $"The '{module.LabelEn}' module is not enabled for your account. "
                      + "An administrator can switch it on under Tenant Admin → Modules."
        })
        { StatusCode = StatusCodes.Status403Forbidden };
    }
}
