using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// Maintenance mode for the organisation's administrators (WP8): see whether it is on, and switch it. This route is
/// exempt from <see cref="MaintenanceModeGate"/>, so the administrator who switched it on can switch it off.
/// </summary>
[ApiController]
[Route("api/organisation-admin/maintenance")]
[HasPermission("security.manage")]
public sealed class OrganisationMaintenanceController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IMaintenanceState _maintenance;

    public OrganisationMaintenanceController(ZayraDbContext db, IMaintenanceState maintenance)
    {
        _db = db;
        _maintenance = maintenance;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var status = await MaintenanceState.ReadAsync(_db, ct);
        return Ok(new { on = status.On, message = status.Message });
    }

    [HttpPut]
    public async Task<IActionResult> Set([FromBody] SetMaintenanceRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (req.Message is { Length: > 500 })
            return BadRequest(new { error = "maintenance_message_too_long", message = "Keep the maintenance message under 500 characters." });

        await _maintenance.SetAsync(_db, req.On, req.Message, this.GetUserId(), ct);
        _db.AdminAuditLogs.Add(new AdminAuditLog
        {
            TenantId = tenantId.Value,
            EntityType = "Installation",
            EntityId = "maintenance",
            Action = req.On ? "MaintenanceOn" : "MaintenanceOff",
            NewValuesJson = JsonSerializer.Serialize(new { on = req.On, message = req.Message }),
            PerformedByName = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? this.GetUserId()?.ToString() ?? "organisation_admin",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
        });
        await _db.SaveChangesAsync(ct);

        var status = await MaintenanceState.ReadAsync(_db, ct);
        return Ok(new { on = status.On, message = status.Message });
    }
}

public sealed record SetMaintenanceRequest(bool On, string? Message);
