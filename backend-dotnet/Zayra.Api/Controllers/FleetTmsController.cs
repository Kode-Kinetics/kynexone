using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/fleet")]
[Authorize(Roles = "Admin,HR Director,HR Manager,Manager,Supervisor")]
public class FleetTmsController : ControllerBase
{
    private readonly ZayraDbContext _db;

    public FleetTmsController(ZayraDbContext db)
    {
        _db = db;
    }

    [HttpGet("overview")]
    public async Task<IActionResult> Overview(CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;

        var shipments = _db.FleetShipments.AsNoTracking().Where(x => x.TenantId == tenantId);
        var vehicles = _db.FleetVehicles.AsNoTracking().Where(x => x.TenantId == tenantId);
        var maintenance = _db.FleetMaintenanceTickets.AsNoTracking().Where(x => x.TenantId == tenantId);
        var fuel = _db.FleetFuelEvents.AsNoTracking().Where(x => x.TenantId == tenantId);
        var tracking = _db.FleetTrackingPoints.AsNoTracking().Where(x => x.TenantId == tenantId);

        var generatedAtUtc = DateTime.UtcNow;
        var activeShipments = await shipments.CountAsync(x => x.Status != "Delivered" && x.Status != "Cancelled", ct);
        var enRoute = await shipments.CountAsync(x => x.Status == "PickedUp" || x.Status == "InTransit" || x.Status == "Loaded", ct);
        var deliveredToday = await shipments.CountAsync(x => x.Status == "Delivered" && x.DeliveredAtUtc >= DateTime.UtcNow.Date, ct);
        var activeVehicles = await vehicles.CountAsync(x => x.Status == "Available" || x.Status == "OnTrip" || x.Status == "Maintenance", ct);
        var onTripVehicles = await vehicles.CountAsync(x => x.Status == "OnTrip", ct);
        var openMaintenance = await maintenance.CountAsync(x => x.Status == "Open" || x.Status == "InProgress" || x.Status == "AwaitingParts", ct);
        var fuelAlerts = await fuel.CountAsync(x => x.AnomalyFlag, ct);
        var trackingEvents = await tracking.CountAsync(ct);
        var avgFuelLevel = await vehicles.Select(x => (decimal?)x.FuelLevelPercent).AverageAsync(ct) ?? 0m;
        var deliveredRate = activeShipments + deliveredToday == 0 ? 0m : Math.Round(deliveredToday / (decimal)(activeShipments + deliveredToday) * 100m, 1);

        var shipmentCards = await shipments
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(6)
            .Select(x => new
            {
                x.Id,
                x.ShipmentNumber,
                x.CustomerName,
                x.Origin,
                x.Destination,
                x.City,
                x.Status,
                x.Priority,
                x.Mode,
                x.PieceCount,
                x.WeightKg,
                x.VolumeCbm,
                x.DriverName,
                x.VehicleNumber,
                x.RouteCode,
                x.PodStatus,
                x.PickupScheduledAtUtc,
                x.PickedUpAtUtc,
                x.DeliveredAtUtc,
                x.TemperatureRange,
                x.Notes,
            })
            .ToListAsync(ct);

        var vehicleCards = await vehicles
            .OrderByDescending(x => x.LastPingAtUtc)
            .ThenBy(x => x.VehicleNumber)
            .Take(6)
            .Select(x => new
            {
                x.Id,
                x.VehicleNumber,
                x.PlateNumber,
                x.Type,
                x.Status,
                x.DriverName,
                x.CapacityKg,
                x.CapacityCbm,
                x.CurrentLoadKg,
                x.FuelLevelPercent,
                x.OdometerKm,
                x.HealthStatus,
                x.IsRefrigerated,
                x.TemperatureCelsius,
                x.LastKnownLocation,
                x.LastPingAtUtc,
                x.LastServiceAtUtc,
                x.NextServiceAtUtc,
                x.Notes,
            })
            .ToListAsync(ct);

        var trackingCards = await tracking
            .OrderByDescending(x => x.RecordedAtUtc)
            .Take(6)
            .Select(x => new
            {
                x.Id,
                x.ShipmentNumber,
                x.VehicleNumber,
                x.LocationLabel,
                x.Status,
                x.GeofenceName,
                x.AlertType,
                x.Latitude,
                x.Longitude,
                x.SpeedKph,
                x.RecordedAtUtc,
                x.EstimatedArrivalUtc,
                x.Notes,
            })
            .ToListAsync(ct);

        var maintenanceCards = await maintenance
            .OrderByDescending(x => x.OpenedAtUtc)
            .Take(6)
            .Select(x => new
            {
                x.Id,
                x.WorkOrderNumber,
                x.VehicleNumber,
                x.Type,
                x.Status,
                x.Priority,
                x.VendorName,
                x.Description,
                x.EstimatedCost,
                x.ActualCost,
                x.DowntimeHours,
                x.OpenedAtUtc,
                x.DueAtUtc,
                x.ClosedAtUtc,
                x.Notes,
            })
            .ToListAsync(ct);

        var fuelCards = await fuel
            .OrderByDescending(x => x.RecordedAtUtc)
            .Take(6)
            .Select(x => new
            {
                x.Id,
                x.VehicleNumber,
                x.FuelCardNumber,
                x.StationName,
                x.City,
                x.EventType,
                x.AnomalyFlag,
                x.Liters,
                x.Cost,
                x.OdometerKm,
                x.Notes,
                x.RecordedAtUtc,
            })
            .ToListAsync(ct);

        var loadPlanCards = await shipments
            .GroupBy(x => x.RouteCode)
            .Select(g => new
            {
                RouteCode = g.Key,
                ShipmentCount = g.Count(),
                TotalWeightKg = g.Sum(x => x.WeightKg),
                TotalVolumeCbm = g.Sum(x => x.VolumeCbm),
                HighPriority = g.Count(x => x.Priority == "High" || x.Priority == "Critical"),
                Delivered = g.Count(x => x.Status == "Delivered"),
            })
            .OrderByDescending(x => x.ShipmentCount)
            .Take(4)
            .ToListAsync(ct);

        return Ok(new
        {
            generatedAtUtc,
            summary = new
            {
                activeShipments,
                enRoute,
                deliveredToday,
                activeVehicles,
                onTripVehicles,
                openMaintenance,
                fuelAlerts,
                trackingEvents,
                avgFuelLevel = Math.Round(avgFuelLevel, 1),
                deliveredRate,
            },
            shipmentCards,
            vehicleCards,
            trackingCards,
            maintenanceCards,
            fuelCards,
            loadPlanCards,
        });
    }

    [HttpGet("shipments")]
    public async Task<IActionResult> Shipments([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId()!.Value;
        var query = _db.FleetShipments.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { total, page, pageSize, items });
    }

    [HttpGet("vehicles")]
    public async Task<IActionResult> Vehicles([FromQuery] string? status, CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId()!.Value;
        var query = _db.FleetVehicles.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);

        var items = await query.OrderBy(x => x.VehicleNumber).ToListAsync(ct);
        return Ok(new { items });
    }

    [HttpGet("tracking")]
    public async Task<IActionResult> Tracking([FromQuery] string? shipmentNumber, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId()!.Value;
        var query = _db.FleetTrackingPoints.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(shipmentNumber)) query = query.Where(x => x.ShipmentNumber == shipmentNumber);

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.RecordedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { total, page, pageSize, items });
    }

    [HttpGet("maintenance")]
    public async Task<IActionResult> Maintenance([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId()!.Value;
        var query = _db.FleetMaintenanceTickets.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.OpenedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { total, page, pageSize, items });
    }

    [HttpGet("fuel")]
    public async Task<IActionResult> Fuel([FromQuery] bool? anomaliesOnly, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId()!.Value;
        var query = _db.FleetFuelEvents.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (anomaliesOnly == true) query = query.Where(x => x.AnomalyFlag);

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.RecordedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { total, page, pageSize, items });
    }

    [HttpPost("shipments/{id:guid}/dispatch")]
    public async Task<IActionResult> DispatchShipment(Guid id, [FromBody] DispatchShipmentRequest request, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var shipment = await _db.FleetShipments.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (shipment is null) return NotFound();

        shipment.Status = "InTransit";
        shipment.DriverName = request.DriverName?.Trim() ?? shipment.DriverName;
        shipment.VehicleNumber = request.VehicleNumber?.Trim() ?? shipment.VehicleNumber;
        shipment.RouteCode = request.RouteCode?.Trim() ?? shipment.RouteCode;
        shipment.Notes = request.Notes?.Trim() ?? shipment.Notes;
        shipment.PickedUpAtUtc = DateTime.UtcNow;
        shipment.UpdatedAtUtc = DateTime.UtcNow;

        var vehicle = await _db.FleetVehicles.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.VehicleNumber == shipment.VehicleNumber, ct);
        if (vehicle is not null)
        {
            vehicle.Status = "OnTrip";
            vehicle.DriverName = shipment.DriverName;
            vehicle.CurrentLoadKg = Math.Min(vehicle.CapacityKg, vehicle.CurrentLoadKg + shipment.WeightKg);
            vehicle.LastKnownLocation = shipment.Origin;
            vehicle.LastPingAtUtc = DateTime.UtcNow;
            vehicle.UpdatedAtUtc = DateTime.UtcNow;
        }

        _db.FleetTrackingPoints.Add(new FleetTrackingPoint
        {
            TenantId = tenantId,
            ShipmentNumber = shipment.ShipmentNumber,
            VehicleNumber = shipment.VehicleNumber,
            LocationLabel = shipment.Origin,
            Status = "PickedUp",
            GeofenceName = "Origin Hub",
            Latitude = 24.7136m,
            Longitude = 46.6753m,
            SpeedKph = 0m,
            RecordedAtUtc = DateTime.UtcNow,
            EstimatedArrivalUtc = DateTime.UtcNow.AddHours(3),
            Notes = "Shipment dispatched from the command center.",
        });

        await _db.SaveChangesAsync(ct);
        return Ok(shipment);
    }

    [HttpPost("vehicles/{id:guid}/service")]
    public async Task<IActionResult> ServiceVehicle(Guid id, [FromBody] VehicleServiceRequest request, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var vehicle = await _db.FleetVehicles.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (vehicle is null) return NotFound();

        vehicle.Status = request.Status?.Trim() ?? "Maintenance";
        vehicle.HealthStatus = request.HealthStatus?.Trim() ?? vehicle.HealthStatus;
        vehicle.LastServiceAtUtc = DateTime.UtcNow;
        vehicle.NextServiceAtUtc = request.NextServiceAtUtc ?? vehicle.NextServiceAtUtc;
        vehicle.Notes = request.Notes?.Trim() ?? vehicle.Notes;
        vehicle.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(vehicle);
    }

    [HttpPost("maintenance/{id:guid}/close")]
    public async Task<IActionResult> CloseMaintenance(Guid id, [FromBody] CloseMaintenanceRequest request, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var ticket = await _db.FleetMaintenanceTickets.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (ticket is null) return NotFound();

        ticket.Status = request.Status?.Trim() ?? "Closed";
        ticket.ClosedAtUtc = DateTime.UtcNow;
        ticket.ActualCost = request.ActualCost ?? ticket.ActualCost;
        ticket.Notes = request.Notes?.Trim() ?? ticket.Notes;
        ticket.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(ticket);
    }

    [HttpPost("fuel/{id:guid}/flag")]
    public async Task<IActionResult> FlagFuelEvent(Guid id, [FromBody] FlagFuelEventRequest request, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var eventRow = await _db.FleetFuelEvents.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (eventRow is null) return NotFound();

        eventRow.AnomalyFlag = request.AnomalyFlag;
        eventRow.Notes = request.Notes?.Trim() ?? eventRow.Notes;
        eventRow.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(eventRow);
    }
}

public record DispatchShipmentRequest(string? VehicleNumber, string? DriverName, string? RouteCode, string? Notes);
public record VehicleServiceRequest(string? Status, string? HealthStatus, DateTime? NextServiceAtUtc, string? Notes);
public record CloseMaintenanceRequest(string? Status, decimal? ActualCost, string? Notes);
public record FlagFuelEventRequest(bool AnomalyFlag, string? Notes);
