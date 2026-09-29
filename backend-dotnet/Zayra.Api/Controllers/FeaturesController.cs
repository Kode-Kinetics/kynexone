using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Modules;

namespace Zayra.Api.Controllers;

/// <summary>
/// Read-only module visibility — accessible to any authenticated tenant user, because every user's
/// navigation depends on it.
///
/// <para>Returns the module keys that are <b>effectively</b> off: the tenant's stored flags after
/// <see cref="ModuleCatalog"/>'s locks have been applied. Returning the raw stored rows would let
/// the navigation and the API disagree — a tenant with a stale <c>qiwa_integration=false</c> row
/// would have the Saudization nav hidden while the API (correctly) kept serving it, because
/// Saudization is statutory and cannot be switched off.</para>
///
/// <para>An absent key means the module is enabled. Writes belong to
/// <c>TenantModulesController</c> (<c>/api/tenant-modules</c>), which is permission-gated and
/// audited; this endpoint is deliberately readable by everyone.</para>
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
// Never let a browser cache module state.
//
// Without this the browser reused its first response for the whole session: an administrator
// switched a module off, the write succeeded, the API enforced it — and the navigation and route
// guard kept showing the module because every subsequent fetch was served from the HTTP cache.
// Only visible by driving a real browser; curl bypasses the cache and looked perfect throughout.
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class FeaturesController : ControllerBase
{
    private readonly ITenantModuleService _modules;

    public FeaturesController(ITenantModuleService modules) => _modules = modules;

    [HttpGet("disabled-keys")]
    public async Task<IActionResult> GetDisabledKeys(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var state = await _modules.GetStateAsync(tenantId.Value, ct);
        return Ok(state.EffectiveDisabledKeys.ToList());
    }

    /// <summary>
    /// Which frontend paths belong to which module, and whether that module is on.
    ///
    /// <para>Served rather than duplicated in TypeScript on purpose. The navigation and the route
    /// guard need the same path-to-module mapping the API guard enforces, and a second hand-kept
    /// copy in the frontend is precisely how the backend's own route table came to have seven
    /// prefixes that matched no controller.</para>
    ///
    /// <para>Readable by any authenticated user, because every user's navigation depends on it.
    /// It carries no configuration values — only which modules exist and whether they are on.</para>
    /// </summary>
    [HttpGet("modules")]
    public async Task<IActionResult> GetModules(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var state = await _modules.GetStateAsync(tenantId.Value, ct);

        var modules = ModuleCatalog.All
            .Where(m => m.NavPaths.Count > 0)
            .Select(m => new ModuleNavDto(
                Key: m.Key,
                LabelEn: m.LabelEn,
                NavPaths: m.NavPaths,
                Enabled: state.IsEnabled(m.Key)))
            .ToList();

        return Ok(modules);
    }
}

public record ModuleNavDto(
    string Key,
    string LabelEn,
    IReadOnlyList<string> NavPaths,
    bool Enabled);
