using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Organization;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/tenant-hr-config")]
[Authorize]
public class TenantHrConfigController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;

    public TenantHrConfigController(ZayraDbContext db, IAuditService? audit = null)
    {
        _db = db;
        // Optional with concrete fallback (house pattern) so direct constructions keep working.
        _audit = audit ?? new Zayra.Api.Infrastructure.Audit.AuditService(db);
    }

    [HttpGet]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var config = await _db.TenantHrConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId.Value, ct);

        if (config is null)
        {
            // Return safe defaults without persisting
            return Ok(TenantHrConfigDto.From(new TenantHrConfig { TenantId = tenantId.Value }));
        }

        return Ok(TenantHrConfigDto.From(config));
    }

    [HttpPut]
    [HasPermission("organization.write")]
    public async Task<IActionResult> Upsert([FromBody] TenantHrConfigRequest req, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var config = await _db.TenantHrConfigs
            .FirstOrDefaultAsync(x => x.TenantId == tenantId.Value, ct);

        if (config is null)
        {
            config = new TenantHrConfig { TenantId = tenantId.Value };
            _db.TenantHrConfigs.Add(config);
        }

        // ── Establishment enforcement mode: FIELD-LEVEL control, never the generic role gate ──
        // Setting the mode to Off neutralizes every staffing budget in the tenant — strictly more
        // powerful than editing one budget row, so a CHANGE requires the same permission that
        // moves budget walls (403, never silently ignored), a mandatory reason, and an audit.
        // Omitting the field (null) keeps the stored value — legacy clients cannot reset it.
        string? modeBefore = null, modeAfter = null;
        if (req.EstablishmentEnforcementMode is not null)
        {
            var requested = req.EstablishmentEnforcementMode.Trim();
            if (requested is not (EstablishmentGuardService.ModeOff or EstablishmentGuardService.ModeAdvisory or EstablishmentGuardService.ModeEnforced))
                return BadRequest(new { message = "EstablishmentEnforcementMode must be Off, Advisory, or Enforced." });
            if (!string.Equals(config.EstablishmentEnforcementMode, requested, StringComparison.Ordinal))
            {
                if (!User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, EstablishmentHttp.WritePermission, StringComparison.OrdinalIgnoreCase)))
                    return Forbid();
                if (string.IsNullOrWhiteSpace(req.EstablishmentModeChangeReason))
                    return BadRequest(new { message = "A reason is required when changing the establishment enforcement mode." });
                modeBefore = config.EstablishmentEnforcementMode;
                modeAfter = requested;
                config.EstablishmentEnforcementMode = requested;
            }
        }

        // The eleven approval-routing / import / hierarchy / payroll switches are retired (2026-10-25): nothing ever read them.
        // A request that still carries them is ignored; the columns stay for one release so a rollback works.
        config.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        if (modeAfter is not null)
        {
            await _audit.WriteAsync("establishment.enforcement_mode_changed", "TenantHrConfig", config.Id.ToString(),
                new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), tenantId),
                JsonSerializer.Serialize(new { before = modeBefore, after = modeAfter, reason = req.EstablishmentModeChangeReason }), ct);
        }
        return Ok(TenantHrConfigDto.From(config));
    }
}

/// <summary>
/// The tenant HR settings that are still live. Unknown properties are ignored on deserialisation, so a client that still
/// sends the retired switches is not refused. Omitting a field keeps the stored value.
/// </summary>
public record TenantHrConfigRequest(
    string? EstablishmentEnforcementMode = null,
    string? EstablishmentModeChangeReason = null);

/// <summary>What GET and PUT return: the live settings only (never the entity, so a retired column cannot leak back).</summary>
public record TenantHrConfigDto(string EstablishmentEnforcementMode, int EngagementMinCohortSize, int EngagementFreeTextRetentionMonths, DateTime UpdatedAtUtc)
{
    public static TenantHrConfigDto From(TenantHrConfig c) =>
        new(c.EstablishmentEnforcementMode, c.EngagementMinCohortSize, c.EngagementFreeTextRetentionMonths, c.UpdatedAtUtc);
}
