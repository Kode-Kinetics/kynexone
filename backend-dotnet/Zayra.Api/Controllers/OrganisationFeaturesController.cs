using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Filters;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// The organisation's opt-in features (<see cref="OptInFeatures"/>): switching one on or off and
/// saving its configuration. This used to be a platform-console action
/// (<c>PUT /api/platform/tenants/{id}/features/{key}</c>); with the platform gone it is the
/// organisation administrator's, behind the same gate as User Management.
/// </summary>
/// <remarks>
/// <para>Every rule the platform enforced is kept. Selfie attendance collects a face image, so it is
/// switched on only with a recorded DPIA sign-off and with this installation's storage on the KSA
/// residency allow-list; the server stamps who enabled it, when, and where storage really is. The punch
/// geofence is refused with an invalid configuration. Switching <c>release_a</c> on installs its
/// renewal approval chains at once.</para>
/// <para>Modules (<see cref="ModuleCatalog"/>) are not served here: they have their own rules at
/// <c>/api/tenant-modules</c>.</para>
/// </remarks>
[ApiController]
[Route("api/tenant-admin/features")]
[HasPermission("security.manage")]
public sealed class OrganisationFeaturesController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ITenantModuleService _modules;

    public OrganisationFeaturesController(ZayraDbContext db, IMemoryCache cache, ITenantModuleService modules)
    {
        _db = db;
        _cache = cache;
        _modules = modules;
    }

    [HttpPut("{featureKey}")]
    public async Task<IActionResult> SetFeature(string featureKey, [FromBody] SetFeatureFlagRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        var userId = this.GetUserId();
        if (tenantId is null || userId is null) return Unauthorized();

        if (!OptInFeatures.IsOptIn(featureKey))
            return BadRequest(new
            {
                code = "unknown_feature",
                feature = featureKey,
                message = $"'{featureKey}' is not an opt-in feature. Modules are switched at /api/tenant-modules.",
            });

        var actorEmail = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct) ?? userId.Value.ToString();

        var flag = await _db.TenantFeatureFlags
            .FirstOrDefaultAsync(f => f.TenantId == tenantId.Value && f.FeatureKey == featureKey, ct);

        // Selfie attendance collects a face image. The two owner decisions — the DPIA sign-off (legal) and the KSA
        // data-residency confirmation (spend) — must be recorded before it can be switched on. Disabling is always
        // allowed (it is the kill switch) and keeps the recorded sign-offs.
        var effectiveConfig = req.ConfigJson;
        if (featureKey == FeatureKeys.SelfieAttendance)
        {
            effectiveConfig ??= flag?.ConfigJson;
            if (req.IsEnabled)
            {
                var (refusal, verdict) = await SelfieEnableRefusalAsync(tenantId.Value, effectiveConfig, ct);
                if (refusal is not null) return refusal;
                // The server stamps who enabled it, when, who confirmed residency and where storage really is (the
                // configured endpoint, the bucket and the region the bucket reports); the request cannot supply any of
                // those. Every policy read compares the stamp with storage now: if storage moves, the feature is off.
                effectiveConfig = SelfieAttendanceConfig.StampServerFields(effectiveConfig, userId.Value,
                    actorEmail, DateTime.UtcNow, verdict!.Location);
            }
        }
        else if (featureKey == FeatureKeys.PunchGeofence && PunchGeofenceConfig.Problems(req.ConfigJson) is { Count: > 0 } problems)
        {
            return BadRequest(new { code = "geofence_config_invalid", message = string.Join(" ", problems), problems });
        }

        if (flag is null)
        {
            flag = new TenantFeatureFlag { TenantId = tenantId.Value, FeatureKey = featureKey };
            _db.TenantFeatureFlags.Add(flag);
        }

        var oldEnabled = flag.IsEnabled;
        flag.IsEnabled = req.IsEnabled;
        flag.ConfigJson = effectiveConfig;
        flag.UpdatedAtUtc = DateTime.UtcNow;
        flag.UpdatedBy = userId.Value;

        object newValues = featureKey == FeatureKeys.SelfieAttendance
            ? new { featureKey, isEnabled = req.IsEnabled, signOffs = SelfieAttendanceConfig.SignOffsForAudit(effectiveConfig), actor = actorEmail }
            : new { featureKey, isEnabled = req.IsEnabled };
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId.Value,
            EntityType = "FeatureFlag",
            EntityId = $"{tenantId.Value}/{featureKey}",
            Action = req.IsEnabled ? "FeatureEnabled" : "FeatureDisabled",
            OldValuesJson = JsonSerializer.Serialize(new { featureKey, isEnabled = oldEnabled }),
            NewValuesJson = JsonSerializer.Serialize(newValues),
            PerformedByName = actorEmail,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""
        });

        await _db.SaveChangesAsync(ct);
        _modules.Invalidate(tenantId.Value);
        FeatureFlagGuardFilter.InvalidateCache(_cache, tenantId.Value, featureKey);
        // Release A: switching release_a on installs the renewal approval chains at once (insert-if-absent), rather
        // than waiting for the next boot's backfill.
        if (req.IsEnabled && featureKey == FeatureKeys.ReleaseA
            && await TenantProvisioningBundle.InstallDefaultApprovalWorkflowsAsync(_db, tenantId.Value, ct) > 0)
            await _db.SaveChangesAsync(ct);
        return Ok(flag);
    }

    /// <summary>
    /// Why selfie attendance may not be switched on with this config, or null. <c>dpia.signedOffBy</c> must be an
    /// existing user of this organisation, <c>dpia.reference</c> in the <c>DPIA-YYYY-NNN</c> format, the sign-off
    /// date neither in the future nor before 2026-01-01, region KSA; and this installation's storage must be on the
    /// KSA residency allow-list.
    /// </summary>
    private async Task<(IActionResult? Refusal, StorageResidencyVerdict? Verdict)> SelfieEnableRefusalAsync(
        Guid tenantId, string? config, CancellationToken ct)
    {
        var missing = SelfieAttendanceConfig.RequestProblems(config, DateTime.UtcNow).ToList();
        if (!missing.Contains("dpia.signedOffBy") && SelfieAttendanceConfig.SignedOffBy(config) is Guid signer
            && !await _db.Users.AsNoTracking().AnyAsync(u => u.Id == signer && u.TenantId == tenantId && !u.IsDeleted, ct))
            missing.Add("dpia.signedOffBy");
        if (missing.Count > 0)
            return (UnprocessableEntity(new
            {
                code = "selfie_signoff_missing",
                message = "Selfie attendance cannot be switched on until the DPIA sign-off is recorded: dpia.signedOffBy "
                          + "must be the id of an existing user of this organisation, dpia.reference must look like DPIA-2026-007, "
                          + "dpia.signedOffAtUtc must be a date from 2026-01-01 up to now, and dataResidency.region must be KSA.",
                missing,
            }), null);

        var residency = await SelfieResidency().CheckAsync(SelfieAttendanceConfig.RequiredRegion, ct);
        if (!residency.Resident)
            return (UnprocessableEntity(new
            {
                code = "selfie_residency_unverified",
                message = "Selfie attendance cannot be switched on: " + residency.Reason
                          + " The installation's storage must be an approved KSA bucket, with both its endpoint and the region the bucket reports listed in Storage:ResidencyAllowList:KSA.",
                storageLocation = residency.ActualLocation,
                bucketRegion = residency.BucketRegion,
            }), null);
        return (null, residency);
    }

    /// <summary>This installation's storage residency (DI); unconfigured, so nothing is resident, when none is registered.</summary>
    private StorageResidency SelfieResidency() =>
        HttpContext?.RequestServices?.GetService(typeof(StorageResidency)) as StorageResidency ?? StorageResidency.Unconfigured;
}
