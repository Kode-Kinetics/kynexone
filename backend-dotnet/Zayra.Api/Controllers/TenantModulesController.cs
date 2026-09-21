using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// The tenant administrator's own control over which modules their organisation runs.
///
/// <para>This replaces a switch that could not work: <c>PUT /api/tenant-admin/feature-flags/{key}</c>
/// returned 403 unconditionally, while the Tenant Admin UI rendered toggles for it and swallowed
/// the failure — so an administrator could click a module on or off all day and nothing happened
/// anywhere. Module enablement was, in practice, platform-staff-only.</para>
///
/// <para>Writes here are refused rather than quietly accepted whenever the switch could not be
/// honoured end to end, following the doctrine <c>ApprovalPoliciesController</c> sets out: a
/// configuration endpoint no runtime path reads must answer with a machine-readable code, never
/// 200. Three refusals exist:</para>
/// <list type="bullet">
///   <item><c>400 unknown_module</c> — the key names no module.</item>
///   <item><c>501 module_not_enforceable</c> — the key exists in the historical feature-flag
///   vocabulary but nothing reads it, so storing it would create exactly the dead configuration
///   this endpoint is replacing. The reason names what to switch instead.</item>
///   <item><c>409 module_not_disableable</c> — the module is load-bearing or statutory. The reason
///   is the catalog's, verbatim, so the UI and the API give the administrator the same answer.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/tenant-modules")]
[Authorize]
[HasPermission("security.manage")]
// Module state must never be served from a browser cache — see FeaturesController for the
// failure this prevents.
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class TenantModulesController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly ITenantModuleService _modules;
    private readonly IMemoryCache _cache;
    private readonly IAuditService _audit;

    public TenantModulesController(
        ZayraDbContext db,
        ITenantModuleService modules,
        IMemoryCache cache,
        IAuditService? audit = null)
    {
        _db = db;
        _modules = modules;
        _cache = cache;
        // Optional with concrete fallback (house pattern) so direct constructions keep working.
        _audit = audit ?? new Zayra.Api.Infrastructure.Audit.AuditService(db);
    }

    /// <summary>
    /// Every module, whether it is on, and — when it cannot be switched off — why.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var state = await _modules.GetStateAsync(tenantId.Value, ct);

        var items = ModuleCatalog.All.Select(m =>
        {
            var decision = ModuleCatalog.CanDisable(m, state.CountryCode, k => !state.StoredDisabledKeys.Contains(k));
            return new TenantModuleDto(
                Key: m.Key,
                LabelEn: m.LabelEn,
                LabelAr: m.LabelAr,
                Description: m.Description,
                Enabled: state.IsEnabled(m.Key),
                CanDisable: decision.IsAllowed,
                LockClass: m.Lock.ToString(),
                LockReason: decision.IsAllowed ? null : decision.Reason);
        }).ToList();

        return Ok(new TenantModuleListDto(
            CountryCode: state.CountryCode,
            Modules: items));
    }

    /// <summary>Switch one module on or off.</summary>
    [HttpPut("{moduleKey}")]
    public async Task<IActionResult> SetModule(
        string moduleKey,
        [FromBody] SetTenantModuleRequest req,
        CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        if (ModuleCatalog.NonModuleKeys.TryGetValue(moduleKey, out var notEnforceableReason))
        {
            // Storing this would be configuration with no reader — the exact defect this surface exists to avoid.
            return StatusCode(StatusCodes.Status501NotImplemented, new
            {
                code = "module_not_enforceable",
                module = moduleKey,
                message = notEnforceableReason,
                replacement = "/api/tenant-modules",
            });
        }

        var module = ModuleCatalog.TryGet(moduleKey);
        if (module is null)
        {
            return BadRequest(new
            {
                code = "unknown_module",
                module = moduleKey,
                message = $"'{moduleKey}' is not a configurable module.",
                replacement = "/api/tenant-modules",
            });
        }

        var state = await _modules.GetStateAsync(tenantId.Value, ct);

        if (!req.Enabled)
        {
            var decision = ModuleCatalog.CanDisable(
                module, state.CountryCode, k => !state.StoredDisabledKeys.Contains(k));
            if (!decision.IsAllowed)
            {
                return Conflict(new
                {
                    code = "module_not_disableable",
                    module = module.Key,
                    lockClass = module.Lock.ToString(),
                    message = decision.Reason,
                });
            }
        }

        var wasEnabled = state.IsEnabled(module.Key);

        var flag = await _db.TenantFeatureFlags
            .FirstOrDefaultAsync(f => f.TenantId == tenantId.Value && f.FeatureKey == module.Key, ct);

        if (flag is null)
        {
            flag = new TenantFeatureFlag
            {
                TenantId = tenantId.Value,
                FeatureKey = module.Key,
            };
            _db.TenantFeatureFlags.Add(flag);
        }

        flag.IsEnabled = req.Enabled;
        flag.UpdatedAtUtc = DateTime.UtcNow;
        flag.UpdatedBy = this.GetUserId();

        await _db.SaveChangesAsync(ct);

        // Both caches: the per-tenant module state, and the legacy per-(tenant, key) entries the
        // platform toggle path writes.
        _modules.Invalidate(tenantId.Value);
        Zayra.Api.Infrastructure.Filters.FeatureFlagGuardFilter.InvalidateCache(_cache, tenantId.Value, module.Key);

        if (wasEnabled != req.Enabled)
        {
            await _audit.WriteAsync(
                req.Enabled ? "tenant.module_enabled" : "tenant.module_disabled",
                "TenantFeatureFlag",
                flag.Id.ToString(),
                new RequestContext(
                    HttpContext.Connection.RemoteIpAddress?.ToString(),
                    Request.Headers.UserAgent.ToString(),
                    this.GetUserId(),
                    tenantId),
                JsonSerializer.Serialize(new
                {
                    module = module.Key,
                    before = wasEnabled,
                    after = req.Enabled,
                    lockClass = module.Lock.ToString(),
                }),
                ct);
        }

        var refreshed = await _modules.GetStateAsync(tenantId.Value, ct);
        var refreshedDecision = ModuleCatalog.CanDisable(
            module, refreshed.CountryCode, k => !refreshed.StoredDisabledKeys.Contains(k));

        return Ok(new TenantModuleDto(
            Key: module.Key,
            LabelEn: module.LabelEn,
            LabelAr: module.LabelAr,
            Description: module.Description,
            Enabled: refreshed.IsEnabled(module.Key),
            CanDisable: refreshedDecision.IsAllowed,
            LockClass: module.Lock.ToString(),
            LockReason: refreshedDecision.IsAllowed ? null : refreshedDecision.Reason));
    }
}

public record SetTenantModuleRequest(bool Enabled);

public record TenantModuleDto(
    string Key,
    string LabelEn,
    string LabelAr,
    string Description,
    bool Enabled,
    bool CanDisable,
    string LockClass,
    string? LockReason);

public record TenantModuleListDto(
    string? CountryCode,
    IReadOnlyList<TenantModuleDto> Modules);
