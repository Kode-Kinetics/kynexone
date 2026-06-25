using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/fleet-tms")]
[Authorize(Roles = "Admin,HR Director,HR Manager,Manager,Supervisor")]
public class FleetTmsColdChainController : ControllerBase
{
    private readonly ZayraDbContext _db;

    public FleetTmsColdChainController(ZayraDbContext db) => _db = db;

    [HttpGet("cold-chain/summary")]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var tenantId = RequireTenant();

        var devices = _db.TemperatureDevices.AsNoTracking().Where(x => x.TenantId == tenantId);
        var readings = _db.TemperatureReadings.AsNoTracking().Where(x => x.TenantId == tenantId);
        var alerts = _db.TemperatureAlerts.AsNoTracking().Where(x => x.TenantId == tenantId);
        var reports = _db.ColdChainReports.AsNoTracking().Where(x => x.TenantId == tenantId);
        var zones = _db.TemperatureZones.AsNoTracking().Where(x => x.TenantId == tenantId);

        var activeDevices = await devices.CountAsync(x => x.Status == "Active", ct);
        var openAlerts = await alerts.CountAsync(x => x.Status == "Open" || x.Status == "InReview", ct);
        var readingsToday = await readings.CountAsync(x => x.RecordedAtUtc >= DateTime.UtcNow.Date, ct);
        var breachReadings = await readings.CountAsync(x => x.Status == "Breach", ct);
        var totalReadings = await readings.CountAsync(ct);
        var avgTemp = await readings.Select(x => (decimal?)x.TemperatureCelsius).AverageAsync(ct) ?? 0m;
        var compliance = totalReadings == 0 ? 0m : Math.Round((1m - (breachReadings / (decimal)totalReadings)) * 100m, 1);

        var recentDevices = await devices
            .OrderByDescending(x => x.LastPingAtUtc)
            .Take(6)
            .Select(x => new
            {
                x.Id,
                x.DeviceCode,
                x.Name,
                x.VehicleNumber,
                x.Status,
                x.LastReportedTemperatureCelsius,
                x.BatteryPercent,
                x.LastPingAtUtc,
                x.Notes,
            })
            .ToListAsync(ct);

        var openAlertRows = await alerts
            .Where(x => x.Status != "Resolved")
            .OrderByDescending(x => x.TriggeredAtUtc)
            .Take(6)
            .Select(x => new
            {
                x.Id,
                x.AlertType,
                x.Severity,
                x.Status,
                x.MeasuredTemperature,
                x.ThresholdMin,
                x.ThresholdMax,
                x.TriggeredAtUtc,
                x.ResolutionNotes,
            })
            .ToListAsync(ct);

        var zoneRows = await zones.OrderBy(x => x.Name).Select(x => new
        {
            x.Id,
            x.Code,
            x.Name,
            x.MinCelsius,
            x.MaxCelsius,
            x.Color,
            x.IsActive,
            x.Notes,
        }).ToListAsync(ct);

        var reportRows = await reports
            .OrderByDescending(x => x.GeneratedAtUtc)
            .Take(6)
            .Select(x => new
            {
                x.Id,
                x.ShipmentId,
                x.ShipmentNumber,
                x.GeneratedAtUtc,
                x.CompliancePercent,
                x.MinTemperatureCelsius,
                x.MaxTemperatureCelsius,
                x.TotalReadings,
                x.BreachCount,
                x.SummaryJson,
                x.Notes,
            })
            .ToListAsync(ct);

        return Ok(new
        {
            generatedAtUtc = DateTime.UtcNow,
            summary = new
            {
                activeDevices,
                readingsToday,
                openAlerts,
                totalReadings,
                breachReadings,
                avgTemperatureCelsius = Math.Round(avgTemp, 1),
                compliancePercent = compliance,
            },
            zones = zoneRows,
            devices = recentDevices,
            alerts = openAlertRows,
            reports = reportRows,
        });
    }

    [HttpGet("cold-chain/devices")]
    public async Task<IActionResult> GetDevices(CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var tenantId = RequireTenant();
        var items = await _db.TemperatureDevices.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.LastPingAtUtc)
            .ThenBy(x => x.DeviceCode)
            .Select(x => new
            {
                x.Id,
                x.DeviceCode,
                x.Name,
                x.ZoneId,
                zoneCode = _db.TemperatureZones.Where(z => z.Id == x.ZoneId).Select(z => z.Code).FirstOrDefault(),
                zoneName = _db.TemperatureZones.Where(z => z.Id == x.ZoneId).Select(z => z.Name).FirstOrDefault(),
                x.ShipmentId,
                shipmentNumber = _db.FleetShipments.Where(s => s.Id == x.ShipmentId).Select(s => s.ShipmentNumber).FirstOrDefault(),
                x.VehicleNumber,
                x.Status,
                x.LastReportedTemperatureCelsius,
                x.BatteryPercent,
                x.LastPingAtUtc,
                x.Notes,
            })
            .ToListAsync(ct);
        return Ok(new { items });
    }

    [HttpPost("cold-chain/devices")]
    public async Task<IActionResult> CreateDevice([FromBody] TemperatureDeviceRequest req, CancellationToken ct)
    {
        if (!CanManage()) return Forbid();
        if (string.IsNullOrWhiteSpace(req.DeviceCode)) return BadRequest(new { message = "Device code is required." });
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "Device name is required." });

        var tenantId = RequireTenant();
        var entity = new TemperatureDevice
        {
            TenantId = tenantId,
            DeviceCode = req.DeviceCode.Trim(),
            Name = req.Name.Trim(),
            ZoneId = req.ZoneId,
            ShipmentId = req.ShipmentId,
            VehicleNumber = req.VehicleNumber?.Trim() ?? string.Empty,
            Status = req.Status?.Trim() ?? "Active",
            LastReportedTemperatureCelsius = req.LastReportedTemperatureCelsius ?? 0m,
            BatteryPercent = req.BatteryPercent ?? 0m,
            LastPingAtUtc = req.LastPingAtUtc ?? DateTime.UtcNow,
            Notes = req.Notes?.Trim() ?? string.Empty,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        _db.TemperatureDevices.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    [HttpGet("cold-chain/shipments/{shipmentId:guid}/readings")]
    public async Task<IActionResult> GetShipmentReadings(Guid shipmentId, CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var tenantId = RequireTenant();
        var items = await _db.TemperatureReadings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ShipmentId == shipmentId)
            .OrderByDescending(x => x.RecordedAtUtc)
            .Select(x => new
            {
                x.Id,
                x.DeviceId,
                deviceCode = _db.TemperatureDevices.Where(d => d.Id == x.DeviceId).Select(d => d.DeviceCode).FirstOrDefault(),
                x.ZoneId,
                zoneCode = _db.TemperatureZones.Where(z => z.Id == x.ZoneId).Select(z => z.Code).FirstOrDefault(),
                x.TemperatureCelsius,
                x.HumidityPercent,
                x.Latitude,
                x.Longitude,
                x.Source,
                x.Status,
                x.Notes,
                x.RecordedAtUtc,
                x.CreatedAtUtc,
            })
            .ToListAsync(ct);
        return Ok(new { items });
    }

    [HttpPost("cold-chain/readings")]
    public async Task<IActionResult> CreateReading([FromBody] TemperatureReadingRequest req, CancellationToken ct)
    {
        if (!CanManage()) return Forbid();
        var tenantId = RequireTenant();
        var device = await _db.TemperatureDevices.FirstOrDefaultAsync(x => x.Id == req.DeviceId && x.TenantId == tenantId, ct);
        if (device is null) return NotFound(new { message = "Temperature device not found for this tenant." });

        var zoneId = req.ZoneId ?? device.ZoneId;
        var zone = zoneId.HasValue
            ? await _db.TemperatureZones.FirstOrDefaultAsync(x => x.Id == zoneId.Value && x.TenantId == tenantId, ct)
            : null;

        var entity = new TemperatureReading
        {
            TenantId = tenantId,
            DeviceId = device.Id,
            ShipmentId = req.ShipmentId ?? device.ShipmentId,
            ZoneId = zone?.Id,
            TemperatureCelsius = req.TemperatureCelsius,
            HumidityPercent = req.HumidityPercent,
            Latitude = req.Latitude,
            Longitude = req.Longitude,
            Source = string.IsNullOrWhiteSpace(req.Source) ? "Sensor" : req.Source.Trim(),
            Status = string.IsNullOrWhiteSpace(req.Status) ? "Normal" : req.Status.Trim(),
            Notes = req.Notes?.Trim() ?? string.Empty,
            RecordedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
        };

        device.LastReportedTemperatureCelsius = req.TemperatureCelsius;
        device.BatteryPercent = device.BatteryPercent <= 1 ? 98m : device.BatteryPercent;
        device.LastPingAtUtc = entity.RecordedAtUtc;
        device.ShipmentId = entity.ShipmentId ?? device.ShipmentId;
        device.ZoneId = entity.ZoneId ?? device.ZoneId;
        device.UpdatedAtUtc = DateTime.UtcNow;

        if (zone is not null && (req.TemperatureCelsius < zone.MinCelsius || req.TemperatureCelsius > zone.MaxCelsius))
        {
            entity.Status = "Breach";
            _db.TemperatureAlerts.Add(new TemperatureAlert
            {
                TenantId = tenantId,
                DeviceId = device.Id,
                ShipmentId = entity.ShipmentId,
                ReadingId = entity.Id,
                AlertType = "TemperatureBreach",
                Severity = req.TemperatureCelsius > zone.MaxCelsius + 2 ? "Critical" : "High",
                Status = "Open",
                ThresholdMin = zone.MinCelsius,
                ThresholdMax = zone.MaxCelsius,
                MeasuredTemperature = req.TemperatureCelsius,
                TriggeredAtUtc = entity.RecordedAtUtc,
                Notes = "Breach auto-generated from live temperature reading.",
            });
        }

        _db.TemperatureReadings.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    [HttpGet("cold-chain/alerts")]
    public async Task<IActionResult> GetAlerts([FromQuery] string? status, CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var tenantId = RequireTenant();
        var query = _db.TemperatureAlerts.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);

        var items = await query.OrderByDescending(x => x.TriggeredAtUtc).Select(x => new
        {
            x.Id,
            x.DeviceId,
            deviceCode = _db.TemperatureDevices.Where(d => d.Id == x.DeviceId).Select(d => d.DeviceCode).FirstOrDefault(),
            x.ShipmentId,
            shipmentNumber = _db.FleetShipments.Where(s => s.Id == x.ShipmentId).Select(s => s.ShipmentNumber).FirstOrDefault(),
            x.ReadingId,
            x.AlertType,
            x.Severity,
            x.Status,
            x.ThresholdMin,
            x.ThresholdMax,
            x.MeasuredTemperature,
            x.TriggeredAtUtc,
            x.ResolvedAtUtc,
            x.ResolvedBy,
            x.ResolutionNotes,
            x.Notes,
        }).ToListAsync(ct);
        return Ok(new { items });
    }

    [HttpPost("cold-chain/alerts/{id:guid}/resolve")]
    public async Task<IActionResult> ResolveAlert(Guid id, [FromBody] TemperatureAlertResolveRequest req, CancellationToken ct)
    {
        if (!CanManage()) return Forbid();
        var tenantId = RequireTenant();
        var alert = await _db.TemperatureAlerts.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (alert is null) return NotFound(new { message = "Temperature alert not found for this tenant." });

        alert.Status = "Resolved";
        alert.ResolvedAtUtc = DateTime.UtcNow;
        alert.ResolvedBy = User.Identity?.Name ?? "system";
        alert.ResolutionNotes = req.ResolutionNotes?.Trim() ?? "Resolved by operations.";
        await _db.SaveChangesAsync(ct);
        return Ok(alert);
    }

    [HttpGet("cold-chain/reports/{shipmentId:guid}")]
    public async Task<IActionResult> GetReport(Guid shipmentId, CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var tenantId = RequireTenant();
        var report = await _db.ColdChainReports.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.ShipmentId == shipmentId, ct);
        if (report is not null) return Ok(report);

        var shipment = await _db.FleetShipments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == shipmentId && x.TenantId == tenantId, ct);
        if (shipment is null) return NotFound(new { message = "Shipment not found for this tenant." });

        var readings = await _db.TemperatureReadings.AsNoTracking().Where(x => x.TenantId == tenantId && x.ShipmentId == shipmentId).ToListAsync(ct);
        if (readings.Count == 0) return NotFound(new { message = "No cold-chain readings found for this shipment." });

        var breachCount = readings.Count(x => x.Status == "Breach");
        var entity = new ColdChainReport
        {
            TenantId = tenantId,
            ShipmentId = shipmentId,
            ShipmentNumber = shipment.ShipmentNumber,
            GeneratedAtUtc = DateTime.UtcNow,
            CompliancePercent = Math.Round((1m - (breachCount / (decimal)readings.Count)) * 100m, 1),
            MinTemperatureCelsius = readings.Min(x => x.TemperatureCelsius),
            MaxTemperatureCelsius = readings.Max(x => x.TemperatureCelsius),
            TotalReadings = readings.Count,
            BreachCount = breachCount,
            SummaryJson = $"{{\"shipment\":\"{shipment.ShipmentNumber}\",\"readings\":{readings.Count},\"breachCount\":{breachCount}}}",
            Notes = "Generated on demand from live temperature readings.",
        };
        _db.ColdChainReports.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    private Guid RequireTenant() => Guid.Parse(User.FindFirst("tenant_id")?.Value ?? throw new UnauthorizedAccessException("Tenant claim missing."));
    private bool CanRead() => HasPermission("fleet_tms.cold_chain.view") || HasPermission("fleet.read");
    private bool CanManage() => HasPermission("fleet_tms.cold_chain.manage") || HasPermission("fleet.write");
    private bool HasPermission(string permission) => User.Claims.Any(x => x.Type == "permission" && string.Equals(x.Value, permission, StringComparison.OrdinalIgnoreCase));
}

public record TemperatureDeviceRequest(
    string? DeviceCode,
    string? Name,
    Guid? ZoneId,
    Guid? ShipmentId,
    string? VehicleNumber,
    string? Status,
    decimal? LastReportedTemperatureCelsius,
    decimal? BatteryPercent,
    DateTime? LastPingAtUtc,
    string? Notes);

public record TemperatureReadingRequest(
    Guid DeviceId,
    Guid? ShipmentId,
    Guid? ZoneId,
    decimal TemperatureCelsius,
    decimal? HumidityPercent,
    decimal? Latitude,
    decimal? Longitude,
    string? Source,
    string? Status,
    string? Notes);

public record TemperatureAlertResolveRequest(string? ResolutionNotes);
