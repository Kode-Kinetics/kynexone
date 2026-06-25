using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

public interface IFleetTmsSeeder
{
    Task SeedAsync(CancellationToken ct = default);
}

public class FleetTmsSeeder : IFleetTmsSeeder
{
    private readonly ZayraDbContext _db;
    private readonly ILogger<FleetTmsSeeder> _logger;

    public FleetTmsSeeder(ZayraDbContext db, ILogger<FleetTmsSeeder> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var tenants = await _db.Set<Zayra.Api.Domain.Entities.Tenant>()
            .AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        foreach (var tenant in tenants)
            await SeedTenantAsync(tenant.Id, tenant.Slug, ct);
    }

    private async Task SeedTenantAsync(Guid tenantId, string slug, CancellationToken ct)
    {
        if (await _db.FleetShipments.AnyAsync(x => x.TenantId == tenantId, ct)) return;

        var employees = await _db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && (e.Status == "Active" || e.Status == "Confirmed" || e.Status == "Probation"))
            .OrderBy(e => e.EmployeeCode)
            .ToListAsync(ct);

        if (employees.Count == 0)
        {
            _logger.LogInformation("FleetTmsSeeder: tenant {TenantId} ({Slug}) has no active employees — skipping.", tenantId, slug);
            return;
        }

        var today = DateTime.UtcNow.Date;
        var cities = new[] { "Riyadh", "Jeddah", "Dammam", "Khobar" };
        var customers = new[]
        {
            "Almarai Distribution", "Nahdi Logistics", "Tamimi Markets", "Jarir Trade", "Sultan Foods",
            "Noon Fulfilment", "BinDawood Retail", "Al Rajhi Supply", "Gulf Pharma", "Misk Essentials"
        };
        var origins = new[] { "Riyadh DC", "Jeddah Gateway", "Dammam Hub", "Khobar Crossdock" };
        var destinations = new[] { "Central Riyadh", "North Jeddah", "Eastern Province", "Western Retail Loop" };
        var shipmentStatuses = new[] { "Booked", "Planned", "Loaded", "PickedUp", "InTransit", "Delivered", "Exception" };
        var vehicleTypes = new[] { "Van", "Box Truck", "Refrigerated Truck", "Trailer" };
        var maintenanceTypes = new[] { "Service", "Brake Check", "Tyre Replacement", "Cooling Unit" };
        var fuelStations = new[] { "Petromin", "Alyusr", "Aramco Station", "Shell Express" };
        var rng = new Random(tenantId.GetHashCode() & 0x7fffffff);

        var vehicles = new List<FleetVehicle>();
        for (var i = 0; i < 6; i++)
        {
            vehicles.Add(new FleetVehicle
            {
                TenantId = tenantId,
                VehicleNumber = $"FLEET-{today:yyMMdd}-{i + 1:02}",
                PlateNumber = $"KSA-{6000 + i * 17}",
                Type = vehicleTypes[i % vehicleTypes.Length],
                Status = i % 4 == 0 ? "Maintenance" : i % 3 == 0 ? "OnTrip" : "Available",
                DriverName = employees[(i + 1) % employees.Count].FullName,
                CapacityKg = 1200m + (i * 320m),
                CapacityCbm = 6.5m + (i * 1.4m),
                CurrentLoadKg = 280m + (i * 150m),
                FuelLevelPercent = 34m + (i * 8m),
                OdometerKm = 45210m + (i * 2364m),
                HealthStatus = i % 4 == 0 ? "NeedsService" : "Healthy",
                IsRefrigerated = i % 2 == 0,
                TemperatureCelsius = i % 2 == 0 ? 4m + i * 0.5m : null,
                LastKnownLocation = $"{origins[i % origins.Length]} Yard",
                LastPingAtUtc = DateTime.UtcNow.AddMinutes(-20 - i * 7),
                LastServiceAtUtc = DateTime.UtcNow.AddDays(-18 - i * 3),
                NextServiceAtUtc = DateTime.UtcNow.AddDays(10 + i * 6),
                Notes = i % 2 == 0 ? "Cold-chain capable" : "General freight unit",
                CreatedAtUtc = DateTime.UtcNow.AddHours(-10),
            });
        }

        _db.FleetVehicles.AddRange(vehicles);
        await _db.SaveChangesAsync(ct);

        var shipments = new List<FleetShipment>();
        var trackingPoints = new List<FleetTrackingPoint>();
        for (var i = 0; i < 12; i++)
        {
            var vehicle = vehicles[i % vehicles.Count];
            var customer = customers[i % customers.Length];
            var city = cities[i % cities.Length];
            var status = shipmentStatuses[(i + 2) % shipmentStatuses.Length];
            var shipmentNumber = $"SHP-{today:yyMMdd}-{200 + i}";

            shipments.Add(new FleetShipment
            {
                TenantId = tenantId,
                ShipmentNumber = shipmentNumber,
                CustomerName = customer,
                CustomerSegment = i % 3 == 0 ? "Enterprise" : i % 3 == 1 ? "Retail" : "Pharma",
                Origin = origins[i % origins.Length],
                Destination = destinations[i % destinations.Length],
                City = city,
                Status = status,
                Priority = i % 4 == 0 ? "High" : i % 6 == 0 ? "Critical" : "Normal",
                Mode = i % 4 == 0 ? "Refrigerated" : "Road",
                PieceCount = 4 + (i % 5),
                WeightKg = Math.Round(120m + (i * 48.5m), 2),
                VolumeCbm = Math.Round(0.85m + (i * 0.35m), 2),
                DeclaredValue = Math.Round(1800m + (i * 612m), 2),
                CarrierName = "Kynex Logistics",
                DriverName = vehicle.DriverName,
                VehicleNumber = vehicle.VehicleNumber,
                RouteCode = $"RT-{today:yyMMdd}-{(i % 4) + 1:02}",
                PodStatus = status == "Delivered" ? "Signature" : "Pending",
                TemperatureRange = i % 4 == 0 ? "2-8C" : string.Empty,
                Notes = status == "Exception" ? "Rescheduled due to customer unavailable" : "Ready for route assignment",
                CreatedAtUtc = DateTime.UtcNow.AddHours(-12 + i),
                PickupScheduledAtUtc = DateTime.UtcNow.AddHours(1 + (i % 5)),
                PickedUpAtUtc = status is "PickedUp" or "InTransit" or "Delivered" ? DateTime.UtcNow.AddHours(-3 + i * 0.2) : null,
                DeliveredAtUtc = status == "Delivered" ? DateTime.UtcNow.AddHours(-1 + i * 0.1) : null,
                UpdatedAtUtc = DateTime.UtcNow.AddHours(-1),
            });

            trackingPoints.Add(new FleetTrackingPoint
            {
                TenantId = tenantId,
                ShipmentNumber = shipmentNumber,
                VehicleNumber = vehicle.VehicleNumber,
                LocationLabel = $"{city} Corridor",
                Status = status is "Delivered" ? "Delivered" : status is "Exception" ? "Stopped" : "InTransit",
                GeofenceName = $"{city} Service Area",
                AlertType = status == "Exception" ? "DelayRisk" : string.Empty,
                Latitude = 24.5m + (decimal)(rng.NextDouble() * 1.2),
                Longitude = 46.4m + (decimal)(rng.NextDouble() * 1.3),
                SpeedKph = status == "Delivered" ? 0m : Math.Round(38m + (decimal)rng.NextDouble() * 22m, 1),
                RecordedAtUtc = DateTime.UtcNow.AddMinutes(-45 + i * 3),
                EstimatedArrivalUtc = DateTime.UtcNow.AddHours(2 + (i % 4) * 0.5),
                Notes = "Live GPS ping captured from delivery unit.",
            });
        }

        var maintenance = new List<FleetMaintenanceTicket>();
        for (var i = 0; i < 4; i++)
        {
            maintenance.Add(new FleetMaintenanceTicket
            {
                TenantId = tenantId,
                WorkOrderNumber = $"WO-{today:yyMMdd}-{500 + i}",
                VehicleNumber = vehicles[i].VehicleNumber,
                Type = maintenanceTypes[i % maintenanceTypes.Length],
                Status = i % 3 == 0 ? "Open" : i % 3 == 1 ? "InProgress" : "AwaitingParts",
                Priority = i % 2 == 0 ? "High" : "Normal",
                VendorName = i % 2 == 0 ? "National Workshop" : "FleetCare",
                Description = "Preventive maintenance task logged from operational review.",
                EstimatedCost = 420m + (i * 180m),
                ActualCost = 0m,
                DowntimeHours = 4m + i,
                OpenedAtUtc = DateTime.UtcNow.AddDays(-2 - i),
                DueAtUtc = DateTime.UtcNow.AddDays(1 + i),
                Notes = "Service aligned with route demand.",
            });
        }

        var fuelEvents = new List<FleetFuelEvent>();
        for (var i = 0; i < 8; i++)
        {
            fuelEvents.Add(new FleetFuelEvent
            {
                TenantId = tenantId,
                VehicleNumber = vehicles[i % vehicles.Count].VehicleNumber,
                FuelCardNumber = $"CARD-{today:yyMMdd}-{700 + i}",
                StationName = fuelStations[i % fuelStations.Length],
                City = cities[i % cities.Length],
                EventType = "Fuel",
                AnomalyFlag = i % 5 == 0,
                Liters = Math.Round(74m + (i * 8.4m), 2),
                Cost = Math.Round(280m + (i * 66.5m), 2),
                OdometerKm = 45500m + (i * 1900m),
                Notes = i % 5 == 0 ? "High-volume fill flagged for review." : "Normal refuel event.",
                RecordedAtUtc = DateTime.UtcNow.AddHours(-20 + i * 2),
                UpdatedAtUtc = DateTime.UtcNow.AddHours(-1),
            });
        }

        _db.FleetShipments.AddRange(shipments);
        _db.FleetTrackingPoints.AddRange(trackingPoints);
        _db.FleetMaintenanceTickets.AddRange(maintenance);
        _db.FleetFuelEvents.AddRange(fuelEvents);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "FleetTmsSeeder: seeded {Shipments} shipments, {Vehicles} vehicles, {TrackingPoints} tracking points, {Maintenance} maintenance tickets and {FuelEvents} fuel events for tenant {TenantId} ({Slug}).",
            shipments.Count, vehicles.Count, trackingPoints.Count, maintenance.Count, fuelEvents.Count, tenantId, slug);
    }
}
