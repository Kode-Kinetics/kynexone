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

        var hasFleetSeed = await _db.FleetShipments.AnyAsync(x => x.TenantId == tenantId, ct);
        if (hasFleetSeed)
        {
            await SeedSaudiReadinessAsync(tenantId, slug, employees, null, ct);
            await SeedColdChainAndAssetsAsync(tenantId, slug, null, null, ct);
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
                CustomerVATNumber = $"3{tenantId.ToString("N")[..11]}",
                CustomerCommercialRegistrationNo = $"7{tenantId.ToString("N")[..9]}",
                CustomerNationalAddressBuildingNo = $"{300 + i}",
                CustomerNationalAddressAdditionalNo = $"5{i}",
                CustomerNationalAddressDistrict = i % 2 == 0 ? "Olaya" : "Al Hamra",
                CustomerNationalAddressCity = city,
                CustomerNationalAddressRegion = city == "Riyadh" ? "Riyadh" : city == "Jeddah" ? "Makkah" : "Eastern Province",
                CustomerNationalAddressPostalCode = i % 2 == 0 ? "12211" : "21411",
                CustomerNationalAddressCountry = "Saudi Arabia",
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
        await _db.SaveChangesAsync(ct);

        var stops = new List<ShipmentStop>();
        var pods = new List<ProofOfDelivery>();
        var links = new List<CustomerTrackingLink>();
        var events = new List<ShipmentEvent>();
        var tasks = new List<DriverTask>();
        var carriers = new List<Carrier>();
        var assignments = new List<ShipmentCarrierAssignment>();
        var bookingRequests = new List<BookingRequest>();
        var quoteRequests = new List<QuoteRequest>();

        var carrierNames = new[] { "Rapid Freight", "Desert Haul", "Gulf Connect" };
        for (var i = 0; i < carrierNames.Length; i++)
        {
            var carrier = new Carrier
            {
                TenantId = tenantId,
                Name = carrierNames[i],
                Code = $"CR-{today:yyMMdd}-{i + 1:02}",
                Status = "Active",
                Region = i == 0 ? "Central" : i == 1 ? "Western" : "Eastern",
                ServiceType = i == 0 ? "Road" : i == 1 ? "Cold Chain" : "Express",
                VATNumber = $"3{tenantId.ToString("N")[..11]}",
                CommercialRegistrationNo = $"7{tenantId.ToString("N")[..9]}",
                TransportDocumentNo = $"TD-{today:yyMMdd}-{i + 1:02}",
                PermitNo = $"PERM-{today:yyMMdd}-{i + 1:02}",
                NationalAddressBuildingNo = $"{400 + i}",
                NationalAddressAdditionalNo = $"6{i}",
                District = i == 0 ? "Industrial City" : i == 1 ? "Al Aziziyah" : "Business District",
                City = i == 0 ? "Riyadh" : i == 1 ? "Jeddah" : "Dammam",
                PostalCode = i == 0 ? "12211" : i == 1 ? "21411" : "31411",
                Country = "Saudi Arabia",
                DocumentStatus = "Active",
                ExpiryStatus = i == 2 ? "ExpiringSoon" : "Healthy",
                HijriExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddMonths(9 - i)),
                GregorianExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddMonths(9 - i)),
                OnTimeScore = 92m - i * 2.2m,
                DamageScore = 1.5m + i * 0.4m,
                CostScore = 88m - i * 1.8m,
                Notes = "Seed carrier record for operational demo.",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-12 - i),
            };
            carriers.Add(carrier);
            assignments.Add(new ShipmentCarrierAssignment
            {
                TenantId = tenantId,
                ShipmentId = shipments[i].Id,
                CarrierId = carrier.Id,
                Status = "Assigned",
                QuotedAmount = 1200m + (i * 180m),
                AgreedAmount = 1150m + (i * 170m),
                Notes = "Assigned from carrier management demo.",
                AssignedAtUtc = DateTime.UtcNow.AddHours(-6 - i),
            });
        }

        for (var i = 0; i < 3; i++)
        {
            bookingRequests.Add(new BookingRequest
            {
                TenantId = tenantId,
                RequestNumber = $"BKG-{today:yyMMdd}-{300 + i}",
                CustomerName = customers[i],
                Origin = origins[i],
                Destination = destinations[i],
                Status = i == 0 ? "Open" : "Quoted",
                EstimatedWeightKg = 500m + i * 140m,
                EstimatedVolumeCbm = 2.4m + i * 0.8m,
                RequestedAtUtc = DateTime.UtcNow.AddDays(-2 - i),
                Notes = "Seed booking request.",
            });
            quoteRequests.Add(new QuoteRequest
            {
                TenantId = tenantId,
                QuoteNumber = $"QTE-{today:yyMMdd}-{400 + i}",
                CustomerName = customers[i],
                Origin = origins[i],
                Destination = destinations[i],
                Status = i == 0 ? "Draft" : "Sent",
                EstimatedAmount = 2400m + i * 480m,
                MarginPct = 12m + i * 2m,
                RequestedAtUtc = DateTime.UtcNow.AddDays(-1 - i),
                Notes = "Seed quote request.",
            });
        }

        for (var i = 0; i < shipments.Count; i++)
        {
            var shipment = shipments[i];
            var stop1 = new ShipmentStop
            {
                TenantId = tenantId,
                ShipmentId = shipment.Id,
                StopType = "Pickup",
                SequenceNo = 1,
                LocationName = shipment.Origin,
                ContactName = shipment.CustomerName,
                ContactPhone = "+966500000000",
                AddressLine1 = $"{100 + i} {shipment.Origin} Street",
                City = shipment.City,
                Region = "Saudi Region",
                PostalCode = "11411",
                Country = "Saudi Arabia",
                SaudiNationalAddressBuildingNo = $"{100 + i}",
                SaudiNationalAddressAdditionalNo = $"2{i}",
                SaudiNationalAddressDistrict = "Business District",
                PlannedArrivalAt = DateTime.UtcNow.AddHours(1 + i * 0.5),
                Status = shipment.Status == "Delivered" ? "Completed" : "Planned",
                Notes = "Pickup stop seeded.",
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                UpdatedAt = DateTime.UtcNow,
            };
            var stop2 = new ShipmentStop
            {
                TenantId = tenantId,
                ShipmentId = shipment.Id,
                StopType = "Delivery",
                SequenceNo = 2,
                LocationName = shipment.Destination,
                ContactName = shipment.CustomerName + " Receiver",
                ContactPhone = "+966500000001",
                AddressLine1 = $"{200 + i} {shipment.Destination} Road",
                City = shipment.City,
                Region = "Saudi Region",
                PostalCode = "11511",
                Country = "Saudi Arabia",
                SaudiNationalAddressBuildingNo = $"{200 + i}",
                SaudiNationalAddressAdditionalNo = $"4{i}",
                SaudiNationalAddressDistrict = "Retail Zone",
                PlannedArrivalAt = DateTime.UtcNow.AddHours(4 + i * 0.5),
                Status = shipment.Status == "Delivered" ? "Completed" : "Planned",
                ActualArrivalAt = shipment.Status == "Delivered" ? DateTime.UtcNow.AddHours(-1) : null,
                CompletedAt = shipment.Status == "Delivered" ? DateTime.UtcNow.AddMinutes(-45) : null,
                Notes = "Delivery stop seeded.",
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                UpdatedAt = DateTime.UtcNow,
            };
            stops.Add(stop1);
            stops.Add(stop2);

            if (shipment.Status == "Delivered")
            {
                pods.Add(new ProofOfDelivery
                {
                    TenantId = tenantId,
                    ShipmentId = shipment.Id,
                    StopId = stop2.Id,
                    RecipientName = $"{shipment.CustomerName.Split(' ')[0]} Receiver",
                    RecipientPhone = "+966500000002",
                    SignatureUrl = "https://example.com/signature.png",
                    PhotoUrl = "https://example.com/pod-photo.png",
                    DocumentUrl = "https://example.com/pod.pdf",
                    Notes = "Seed POD verified through customer handoff.",
                    DeliveryCondition = i % 4 == 0 ? "Good" : "Good",
                    CapturedLatitude = 24.71m + i * 0.01m,
                    CapturedLongitude = 46.67m + i * 0.01m,
                    CapturedAt = DateTime.UtcNow.AddHours(-1),
                    VerifiedAt = DateTime.UtcNow.AddMinutes(-20),
                    VerifiedByUserId = null,
                    Status = "Verified",
                    CreatedAt = DateTime.UtcNow.AddHours(-1),
                    UpdatedAt = DateTime.UtcNow,
                });
            }

            links.Add(new CustomerTrackingLink
            {
                TenantId = tenantId,
                ShipmentId = shipment.Id,
                Token = $"trk_{Guid.NewGuid():N}",
                ExpiresAtUtc = DateTime.UtcNow.AddDays(5 + i),
                SharedBy = "Fleet Ops",
                CreatedAtUtc = DateTime.UtcNow.AddHours(-3),
            });

            events.Add(new ShipmentEvent
            {
                TenantId = tenantId,
                ShipmentId = shipment.Id,
                EventType = "ShipmentCreated",
                Message = "Shipment created and queued for planning.",
                ActorName = "Fleet Ops",
                Visibility = "Public",
                OccurredAtUtc = DateTime.UtcNow.AddDays(-2),
                CreatedAtUtc = DateTime.UtcNow.AddDays(-2),
            });
            events.Add(new ShipmentEvent
            {
                TenantId = tenantId,
                ShipmentId = shipment.Id,
                EventType = "ShipmentPlanned",
                Message = "Shipment planned onto operational route.",
                ActorName = "Fleet Ops",
                Visibility = "Public",
                OccurredAtUtc = DateTime.UtcNow.AddDays(-1),
                CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
            });

            tasks.Add(new DriverTask
            {
                TenantId = tenantId,
                ShipmentId = shipment.Id,
                StopId = stop1.Id,
                TaskType = "Pickup",
                Title = $"Pickup {shipment.ShipmentNumber}",
                Description = $"Collect freight from {shipment.Origin}.",
                Status = shipment.Status == "Booked" ? "Open" : "Completed",
                DriverName = shipment.DriverName,
                VehicleNumber = shipment.VehicleNumber,
                DueAtUtc = DateTime.UtcNow.AddHours(2 + i * 0.5),
                CompletedAtUtc = shipment.Status == "Delivered" ? DateTime.UtcNow.AddHours(-2) : null,
                Notes = "Driver task seeded.",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
            });
        }

        _db.Carriers.AddRange(carriers);
        _db.ShipmentCarrierAssignments.AddRange(assignments);
        _db.ShipmentStops.AddRange(stops);
        _db.ProofOfDeliveries.AddRange(pods);
        _db.CustomerTrackingLinks.AddRange(links.Take(3));
        _db.ShipmentEvents.AddRange(events);
        _db.DriverTasks.AddRange(tasks.Take(3));
        _db.BookingRequests.AddRange(bookingRequests);
        _db.QuoteRequests.AddRange(quoteRequests);
        _db.FleetMaintenanceTickets.AddRange(maintenance);
        _db.FleetFuelEvents.AddRange(fuelEvents);
        await _db.SaveChangesAsync(ct);

        await SeedSaudiReadinessAsync(tenantId, slug, employees, shipments, ct);
        await SeedColdChainAndAssetsAsync(tenantId, slug, shipments, vehicles, ct);

        _logger.LogInformation(
            "FleetTmsSeeder: seeded {Shipments} shipments, {Vehicles} vehicles, {TrackingPoints} tracking points, {Maintenance} maintenance tickets, {FuelEvents} fuel events, {Stops} stops, {Pods} PODs, cold-chain telemetry and asset controls for tenant {TenantId} ({Slug}).",
            shipments.Count, vehicles.Count, trackingPoints.Count, maintenance.Count, fuelEvents.Count, stops.Count, pods.Count, tenantId, slug);
    }

    private async Task SeedColdChainAndAssetsAsync(Guid tenantId, string slug, IReadOnlyList<FleetShipment>? shipments, IReadOnlyList<FleetVehicle>? vehicles, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var shipmentList = shipments?.ToList() ?? await _db.FleetShipments.AsNoTracking().Where(x => x.TenantId == tenantId).OrderByDescending(x => x.CreatedAtUtc).Take(6).ToListAsync(ct);
        var vehicleList = vehicles?.ToList() ?? await _db.FleetVehicles.AsNoTracking().Where(x => x.TenantId == tenantId).OrderBy(x => x.VehicleNumber).Take(6).ToListAsync(ct);

        var zones = await _db.TemperatureZones.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync(ct);
        if (zones.Count == 0)
        {
            zones = new List<TemperatureZone>
            {
                new() { TenantId = tenantId, Code = "CHILLED", Name = "Chilled Goods", MinCelsius = 2m, MaxCelsius = 8m, Color = "#38bdf8", Notes = "Milk, produce, pharmaceuticals, and other chilled goods.", CreatedAtUtc = now },
                new() { TenantId = tenantId, Code = "FROZEN", Name = "Frozen Goods", MinCelsius = -25m, MaxCelsius = -10m, Color = "#8b5cf6", Notes = "Frozen inventory with strict temperature control.", CreatedAtUtc = now },
                new() { TenantId = tenantId, Code = "CONTROLLED", Name = "Controlled Ambient", MinCelsius = 15m, MaxCelsius = 25m, Color = "#14b8a6", Notes = "Controlled ambient handling for sensitive freight.", CreatedAtUtc = now },
            };
            _db.TemperatureZones.AddRange(zones);
        }

        var devices = await _db.TemperatureDevices.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync(ct);
        if (devices.Count == 0)
        {
            var devVehicle = vehicleList.FirstOrDefault();
            var devShipment = shipmentList.FirstOrDefault(x => string.Equals(x.Mode, "Refrigerated", StringComparison.OrdinalIgnoreCase));
            devices = new List<TemperatureDevice>
            {
                new()
                {
                    TenantId = tenantId,
                    DeviceCode = $"TDEV-{slug.ToUpperInvariant()}-01",
                    Name = "North Line Sensor",
                    ZoneId = zones.FirstOrDefault(x => x.Code == "CHILLED")?.Id,
                    ShipmentId = devShipment?.Id,
                    VehicleNumber = devVehicle?.VehicleNumber ?? "FLEET-UNIT-01",
                    Status = "Active",
                    LastReportedTemperatureCelsius = 4.2m,
                    BatteryPercent = 87m,
                    LastPingAtUtc = now.AddMinutes(-12),
                    Notes = "Cabin probe for chilled movement.",
                    CreatedAtUtc = now,
                },
                new()
                {
                    TenantId = tenantId,
                    DeviceCode = $"TDEV-{slug.ToUpperInvariant()}-02",
                    Name = "Frozen Bay Sensor",
                    ZoneId = zones.FirstOrDefault(x => x.Code == "FROZEN")?.Id,
                    ShipmentId = shipmentList.Skip(1).FirstOrDefault()?.Id,
                    VehicleNumber = vehicleList.Skip(1).FirstOrDefault()?.VehicleNumber ?? "FLEET-UNIT-02",
                    Status = "Active",
                    LastReportedTemperatureCelsius = -16.8m,
                    BatteryPercent = 72m,
                    LastPingAtUtc = now.AddMinutes(-9),
                    Notes = "Pallet bay probe for frozen freight.",
                    CreatedAtUtc = now,
                },
            };
            _db.TemperatureDevices.AddRange(devices);
        }

        var readings = await _db.TemperatureReadings.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync(ct);
        if (readings.Count == 0)
        {
            var deviceRows = devices.Count > 0 ? devices : await _db.TemperatureDevices.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync(ct);
            var readingSeed = new List<TemperatureReading>();
            for (var i = 0; i < Math.Max(6, deviceRows.Count * 6); i++)
            {
                var device = deviceRows[i % deviceRows.Count];
                var zone = zones.FirstOrDefault(x => x.Id == device.ZoneId) ?? zones[i % zones.Count];
                var breach = i % 7 == 5;
                var value = breach
                    ? zone.MaxCelsius + 2.5m
                    : zone.Code == "FROZEN" ? -17.5m + (i % 3) * 0.4m : zone.Code == "CONTROLLED" ? 20.5m + (i % 3) * 0.3m : 4.0m + (i % 3) * 0.2m;

                readingSeed.Add(new TemperatureReading
                {
                    TenantId = tenantId,
                    DeviceId = device.Id,
                    ShipmentId = device.ShipmentId ?? shipmentList.Skip(i % Math.Max(1, shipmentList.Count)).FirstOrDefault()?.Id,
                    ZoneId = zone.Id,
                    TemperatureCelsius = value,
                    HumidityPercent = 46m + (i % 4) * 3m,
                    Latitude = 24.55m + i * 0.012m,
                    Longitude = 46.65m + i * 0.015m,
                    Source = i % 2 == 0 ? "Sensor" : "Gateway",
                    Status = breach ? "Breach" : "Normal",
                    Notes = breach ? "Temperature moved outside the allowed band." : "In-range telemetry sample.",
                    RecordedAtUtc = now.AddMinutes(-90 + i * 8),
                    CreatedAtUtc = now.AddMinutes(-90 + i * 8),
                });
            }
            readings = readingSeed;
            _db.TemperatureReadings.AddRange(readings);
        }

        var alerts = await _db.TemperatureAlerts.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync(ct);
        if (alerts.Count == 0)
        {
            var breachReadings = readings.Where(x => x.Status == "Breach").Take(3).ToList();
            var alertRows = new List<TemperatureAlert>();
            for (var i = 0; i < breachReadings.Count; i++)
            {
                var reading = breachReadings[i];
                var zone = zones.FirstOrDefault(x => x.Id == reading.ZoneId) ?? zones[0];
                alertRows.Add(new TemperatureAlert
                {
                    TenantId = tenantId,
                    DeviceId = reading.DeviceId,
                    ShipmentId = reading.ShipmentId,
                    ReadingId = reading.Id,
                    AlertType = "TemperatureBreach",
                    Severity = i == 0 ? "Critical" : "High",
                    Status = i == 0 ? "Resolved" : "Open",
                    ThresholdMin = zone.MinCelsius,
                    ThresholdMax = zone.MaxCelsius,
                    MeasuredTemperature = reading.TemperatureCelsius,
                    TriggeredAtUtc = reading.RecordedAtUtc.AddMinutes(2),
                    ResolvedAtUtc = i == 0 ? now.AddMinutes(-18) : null,
                    ResolvedBy = i == 0 ? "Operations Desk" : string.Empty,
                    ResolutionNotes = i == 0 ? "Route was re-iced and device recalibrated." : string.Empty,
                    Notes = "Seed cold-chain alert generated from the demo telemetry stream.",
                });
            }
            alerts = alertRows;
            _db.TemperatureAlerts.AddRange(alerts);
        }

        if (!await _db.ColdChainReports.AnyAsync(x => x.TenantId == tenantId, ct))
        {
            var reportShipments = shipmentList.Take(2).ToList();
            var reportRows = new List<ColdChainReport>();
            foreach (var shipment in reportShipments)
            {
                var shipmentReadings = readings.Where(x => x.ShipmentId == shipment.Id).ToList();
                if (shipmentReadings.Count == 0) continue;
                var breachCount = shipmentReadings.Count(x => x.Status == "Breach");
                reportRows.Add(new ColdChainReport
                {
                    TenantId = tenantId,
                    ShipmentId = shipment.Id,
                    ShipmentNumber = shipment.ShipmentNumber,
                    GeneratedAtUtc = now,
                    CompliancePercent = Math.Round((1m - (breachCount / (decimal)shipmentReadings.Count)) * 100m, 1),
                    MinTemperatureCelsius = shipmentReadings.Min(x => x.TemperatureCelsius),
                    MaxTemperatureCelsius = shipmentReadings.Max(x => x.TemperatureCelsius),
                    TotalReadings = shipmentReadings.Count,
                    BreachCount = breachCount,
                    SummaryJson = $"{{\"shipment\":\"{shipment.ShipmentNumber}\",\"deviceCount\":{shipmentReadings.Select(x => x.DeviceId).Distinct().Count()},\"breachCount\":{breachCount}}}",
                    Notes = "Generated cold-chain compliance view for shipment review.",
                });
            }
            _db.ColdChainReports.AddRange(reportRows);
        }

        if (!await _db.RefrigerationUnitHealthRecords.AnyAsync(x => x.TenantId == tenantId, ct))
        {
            var healthRows = vehicleList.Take(3).Select((vehicle, index) => new RefrigerationUnitHealth
            {
                TenantId = tenantId,
                VehicleNumber = vehicle.VehicleNumber,
                UnitSerial = $"REF-{slug.ToUpperInvariant()}-{index + 1:02}",
                Status = index == 0 ? "ServiceDue" : index == 1 ? "Healthy" : "Monitor",
                CompressorHours = 120m + index * 42m,
                LastServiceAtUtc = now.AddDays(-16 - index * 4),
                NextServiceDueAtUtc = now.AddDays(12 + index * 5),
                TemperatureDeviationCount = index + 1,
                Notes = "Refrigeration unit seeded for preventive monitoring.",
                CreatedAtUtc = now,
            }).ToList();
            _db.RefrigerationUnitHealthRecords.AddRange(healthRows);
        }

        if (!await _db.AssetTypes.AnyAsync(x => x.TenantId == tenantId, ct))
        {
            var createdTypes = new List<AssetType>
            {
                new() { TenantId = tenantId, Code = "PALLET", Name = "Pallet", Description = "Reusable freight pallet", IsReturnable = true, CreatedAtUtc = now },
                new() { TenantId = tenantId, Code = "CRATE", Name = "Crate", Description = "Stackable storage crate", IsReturnable = true, CreatedAtUtc = now },
                new() { TenantId = tenantId, Code = "CAGE", Name = "Security Cage", Description = "Returnable security cage", IsReturnable = true, CreatedAtUtc = now },
            };
            _db.AssetTypes.AddRange(createdTypes);
        }

        var assetTypes = await _db.AssetTypes.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync(ct);
        if (assetTypes.Count == 0)
        {
            assetTypes = _db.AssetTypes.Local.Where(x => x.TenantId == tenantId).ToList();
        }

        var createdAssets = new List<Asset>();
        if (!await _db.Assets.AnyAsync(x => x.TenantId == tenantId, ct))
        {
            var rows = new List<Asset>();
            for (var i = 0; i < 6; i++)
            {
                var assetType = assetTypes[i % assetTypes.Count];
                rows.Add(new Asset
                {
                    TenantId = tenantId,
                    AssetTypeId = assetType.Id,
                    AssetTag = $"AST-{slug.ToUpperInvariant()}-{700 + i}",
                    Name = i % 2 == 0 ? "Cold Chain Pallet" : i % 3 == 0 ? "Security Cage" : "Shipping Crate",
                    Status = i % 4 == 0 ? "Assigned" : "Available",
                    CurrentLocation = shipmentList.Skip(i % Math.Max(1, shipmentList.Count)).FirstOrDefault()?.Destination ?? "Main Warehouse",
                    Condition = i % 5 == 0 ? "Monitor" : "Good",
                    IsReturnable = true,
                    Quantity = 1 + (i % 3),
                    UnitOfMeasure = "Each",
                    Notes = "Seed asset record tied to live logistics usage.",
                    LastSeenAtUtc = now.AddMinutes(-25 - i * 6),
                    CreatedAtUtc = now,
                });
            }
            _db.Assets.AddRange(rows);
            createdAssets = rows;
        }

        var assets = await _db.Assets.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync(ct);
        if (assets.Count == 0)
        {
            assets = createdAssets.Count > 0 ? createdAssets : _db.Assets.Local.Where(x => x.TenantId == tenantId).ToList();
        }
        if (!await _db.AssetAssignments.AnyAsync(x => x.TenantId == tenantId, ct))
        {
            var assignments = new List<AssetAssignment>();
            for (var i = 0; i < Math.Min(4, assets.Count); i++)
            {
                var asset = assets[i];
                var shipment = shipmentList.Count > 0 ? shipmentList[i % shipmentList.Count] : null;
                assignments.Add(new AssetAssignment
                {
                    TenantId = tenantId,
                    AssetId = asset.Id,
                    ShipmentId = shipment?.Id,
                    CarrierId = null,
                    AssigneeType = shipment is null ? "Warehouse" : "Shipment",
                    AssigneeName = shipment is null ? "Main Warehouse" : shipment.ShipmentNumber,
                    Quantity = 1,
                    Status = i % 3 == 0 ? "CheckedOut" : "Assigned",
                    AssignedAtUtc = now.AddHours(-10 - i),
                    ReleasedAtUtc = i % 3 == 0 ? now.AddHours(-2 - i) : null,
                    Notes = "Seed assignment for asset control demo.",
                });
                asset.Status = i % 3 == 0 ? "InUse" : "Assigned";
                asset.CurrentLocation = shipment?.Destination ?? "Main Warehouse";
                asset.LastSeenAtUtc = now.AddMinutes(-15 - i * 5);
                asset.UpdatedAtUtc = now;
            }
            _db.AssetAssignments.AddRange(assignments);
        }

        if (!await _db.AssetEvents.AnyAsync(x => x.TenantId == tenantId, ct))
        {
            var assetEvents = assets.Take(4).Select((asset, index) => new AssetEvent
            {
                TenantId = tenantId,
                AssetId = asset.Id,
                EventType = index % 2 == 0 ? "CheckOut" : "CheckIn",
                Quantity = 1,
                Location = index % 2 == 0 ? "Dispatch Yard" : "Receiving Bay",
                ActorName = "Operations Desk",
                OccurredAtUtc = now.AddHours(-12 + index * 2),
                Notes = "Seed asset event stream.",
            }).ToList();
            _db.AssetEvents.AddRange(assetEvents);
        }

        if (!await _db.BarcodeScanEvents.AnyAsync(x => x.TenantId == tenantId, ct))
        {
            var barcodeScans = assets.Take(3).Select((asset, index) => new BarcodeScanEvent
            {
                TenantId = tenantId,
                AssetId = asset.Id,
                ShipmentId = shipmentList.Count > 0 ? shipmentList[index % shipmentList.Count].Id : null,
                ScannedValue = asset.AssetTag,
                ScannerId = $"SCAN-{slug.ToUpperInvariant()}-{index + 1:02}",
                EventType = "Scan",
                Status = "Captured",
                RecordedAtUtc = now.AddMinutes(-35 - index * 7),
                Notes = "Barcode scan captured in the warehouse.",
            }).ToList();
            _db.BarcodeScanEvents.AddRange(barcodeScans);
        }

        if (!await _db.RfidEvents.AnyAsync(x => x.TenantId == tenantId, ct))
        {
            var rfidEvents = assets.Take(3).Select((asset, index) => new RfidEvent
            {
                TenantId = tenantId,
                AssetId = asset.Id,
                ShipmentId = shipmentList.Count > 0 ? shipmentList[index % shipmentList.Count].Id : null,
                TagId = $"RFID-{slug.ToUpperInvariant()}-{asset.AssetTag}",
                ReaderId = $"RDR-{slug.ToUpperInvariant()}-{index + 1:02}",
                EventType = index % 2 == 0 ? "Read" : "Exit",
                Status = "Captured",
                RecordedAtUtc = now.AddMinutes(-20 - index * 11),
                Notes = "RFID gate event seeded for live operations.",
            }).ToList();
            _db.RfidEvents.AddRange(rfidEvents);
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task SeedSaudiReadinessAsync(Guid tenantId, string slug, IReadOnlyList<Employee> employees, IReadOnlyList<FleetShipment>? shipments, CancellationToken ct)
    {
        if (!await _db.SaudiRegionReferences.AnyAsync(ct))
        {
            _db.SaudiRegionReferences.AddRange(
                new SaudiRegionReference { Code = "Riyadh", NameEn = "Riyadh", NameAr = "الرياض", CountryCode = "SA", CitiesJson = "[\"Riyadh\",\"Diriyah\",\"Al Kharj\",\"Dhurma\"]", SortOrder = 1, IsGccReady = true },
                new SaudiRegionReference { Code = "Makkah", NameEn = "Makkah", NameAr = "مكة المكرمة", CountryCode = "SA", CitiesJson = "[\"Jeddah\",\"Makkah\",\"Taif\",\"Rabigh\"]", SortOrder = 2, IsGccReady = true },
                new SaudiRegionReference { Code = "Eastern", NameEn = "Eastern Province", NameAr = "المنطقة الشرقية", CountryCode = "SA", CitiesJson = "[\"Dammam\",\"Khobar\",\"Dhahran\",\"Jubail\"]", SortOrder = 3, IsGccReady = true },
                new SaudiRegionReference { Code = "Madinah", NameEn = "Madinah", NameAr = "المدينة المنورة", CountryCode = "SA", CitiesJson = "[\"Madinah\",\"Yanbu\",\"AlUla\"]", SortOrder = 4, IsGccReady = true },
                new SaudiRegionReference { Code = "Asir", NameEn = "Asir", NameAr = "عسير", CountryCode = "SA", CitiesJson = "[\"Abha\",\"Khamis Mushait\",\"Bisha\"]", SortOrder = 5, IsGccReady = true }
            );
        }

        if (await _db.FleetReadinessDocuments.AnyAsync(x => x.TenantId == tenantId, ct))
            return;

        var tenantShipments = shipments ?? await _db.FleetShipments.AsNoTracking().Where(x => x.TenantId == tenantId).OrderBy(x => x.CreatedAtUtc).Take(4).ToListAsync(ct);
        var complianceDocs = new List<FleetReadinessDocument>();

        complianceDocs.AddRange(new[]
        {
            new FleetReadinessDocument
            {
                TenantId = tenantId,
                Kind = "Compliance",
                SubjectType = "Branch",
                SubjectId = $"{slug}-riyadh-hq",
                SubjectName = $"{slug.ToUpperInvariant()} Riyadh HQ",
                DocumentType = "Commercial Registration",
                DocumentNumber = $"CR-{slug.ToUpperInvariant()}-001",
                VATNumber = $"3{tenantId.ToString("N")[..11]}",
                CommercialRegistrationNo = $"7{tenantId.ToString("N")[..9]}",
                CountryCode = "SA",
                NationalAddressBuildingNo = "7882",
                NationalAddressAdditionalNo = "1345",
                District = "Olaya",
                City = "Riyadh",
                Region = "Riyadh",
                PostalCode = "12211",
                DocumentStatus = "Active",
                ExpiryStatus = "Healthy",
                IssueDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddYears(-1)),
                GregorianExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddMonths(10)),
                Notes = "Saudi/GCC readiness foundation record for head office compliance.",
            },
            new FleetReadinessDocument
            {
                TenantId = tenantId,
                Kind = "Transport",
                SubjectType = "Carrier",
                SubjectId = "carrier-rapid-freight",
                SubjectName = "Rapid Freight",
                DocumentType = "Transport Permit",
                DocumentNumber = $"TRN-{slug.ToUpperInvariant()}-118",
                TransportDocumentNo = $"TD-{slug.ToUpperInvariant()}-9001",
                PermitNo = $"PERM-{slug.ToUpperInvariant()}-522",
                CountryCode = "SA",
                NationalAddressBuildingNo = "205",
                NationalAddressAdditionalNo = "19",
                District = "Industrial City",
                City = "Jeddah",
                Region = "Makkah",
                PostalCode = "21432",
                DocumentStatus = "Active",
                ExpiryStatus = "Healthy",
                IssueDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddMonths(-4)),
                GregorianExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddMonths(8)),
                Notes = "Transport document ready for cross-border and domestic movement.",
            },
            new FleetReadinessDocument
            {
                TenantId = tenantId,
                Kind = "Compliance",
                SubjectType = "Warehouse",
                SubjectId = $"{slug}-warehouse-east",
                SubjectName = $"{slug.ToUpperInvariant()} Eastern Warehouse",
                DocumentType = "Warehouse Permit",
                DocumentNumber = $"WH-{slug.ToUpperInvariant()}-044",
                VATNumber = $"3{tenantId.ToString("N")[..11]}",
                CommercialRegistrationNo = $"7{tenantId.ToString("N")[..9]}",
                CountryCode = "SA",
                NationalAddressBuildingNo = "12",
                NationalAddressAdditionalNo = "7",
                District = "Al Khalidiyah",
                City = "Dammam",
                Region = "Eastern Province",
                PostalCode = "31411",
                DocumentStatus = "Active",
                ExpiryStatus = "ExpiringSoon",
                IssueDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddYears(-1)),
                GregorianExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(17)),
                Notes = "Warehouse permit used for GCC-ready logistics staging.",
            },
            new FleetReadinessDocument
            {
                TenantId = tenantId,
                Kind = "Driver",
                SubjectType = "Driver",
                SubjectId = employees[0].Id.ToString(),
                SubjectName = employees[0].FullName,
                DocumentType = "Driver License",
                DocumentNumber = $"DL-{slug.ToUpperInvariant()}-{employees[0].EmployeeCode}",
                PermitNo = $"IQAMA-{employees[0].EmployeeCode}",
                CountryCode = employees[0].CountryCode,
                City = "Riyadh",
                Region = "Riyadh",
                PostalCode = "12211",
                DocumentStatus = "Active",
                ExpiryStatus = "Healthy",
                IssueDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddYears(-2)),
                GregorianExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddMonths(14)),
                Notes = "Driver permit and licence readiness record.",
            },
            new FleetReadinessDocument
            {
                TenantId = tenantId,
                Kind = "Driver",
                SubjectType = "Driver",
                SubjectId = employees[Math.Min(1, employees.Count - 1)].Id.ToString(),
                SubjectName = employees[Math.Min(1, employees.Count - 1)].FullName,
                DocumentType = "Work Permit",
                DocumentNumber = $"WP-{slug.ToUpperInvariant()}-{employees[Math.Min(1, employees.Count - 1)].EmployeeCode}",
                PermitNo = $"MOL-{employees[Math.Min(1, employees.Count - 1)].EmployeeCode}",
                CountryCode = employees[Math.Min(1, employees.Count - 1)].CountryCode,
                City = "Jeddah",
                Region = "Makkah",
                PostalCode = "21411",
                DocumentStatus = "Active",
                ExpiryStatus = "Healthy",
                IssueDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddMonths(-8)),
                GregorianExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddMonths(6)),
                Notes = "Driver work permit readiness example.",
            },
            new FleetReadinessDocument
            {
                TenantId = tenantId,
                Kind = "Driver",
                SubjectType = "Driver",
                SubjectId = employees[Math.Min(2, employees.Count - 1)].Id.ToString(),
                SubjectName = employees[Math.Min(2, employees.Count - 1)].FullName,
                DocumentType = "GCC Permit",
                DocumentNumber = $"GCC-{slug.ToUpperInvariant()}-{employees[Math.Min(2, employees.Count - 1)].EmployeeCode}",
                PermitNo = $"GCCP-{employees[Math.Min(2, employees.Count - 1)].EmployeeCode}",
                CountryCode = employees[Math.Min(2, employees.Count - 1)].CountryCode,
                City = "Dammam",
                Region = "Eastern Province",
                PostalCode = "31411",
                DocumentStatus = "Active",
                ExpiryStatus = "ExpiringSoon",
                IssueDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddMonths(-10)),
                GregorianExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(24)),
                Notes = "GCC movement permit example.",
            }
        });

        _db.FleetReadinessDocuments.AddRange(complianceDocs);

        var readyShipments = tenantShipments.Take(2).ToList();
        for (var i = 0; i < readyShipments.Count; i++)
        {
            var shipment = readyShipments[i];
            shipment.IsInvoiceReady = true;
            shipment.InvoiceReadyAtUtc = DateTime.UtcNow.AddHours(-2 - i);
            shipment.InvoiceReadinessNotes = "VAT and CR details are populated for invoice export.";
            shipment.CustomerVATNumber = $"3{tenantId.ToString("N")[..11]}";
            shipment.CustomerCommercialRegistrationNo = $"7{tenantId.ToString("N")[..9]}";
            shipment.CustomerNationalAddressBuildingNo = $"{500 + i}";
            shipment.CustomerNationalAddressAdditionalNo = $"8{i}";
            shipment.CustomerNationalAddressDistrict = "Business District";
            shipment.CustomerNationalAddressCity = i == 0 ? "Riyadh" : "Jeddah";
            shipment.CustomerNationalAddressRegion = i == 0 ? "Riyadh" : "Makkah";
            shipment.CustomerNationalAddressPostalCode = i == 0 ? "12211" : "21411";
            shipment.CustomerNationalAddressCountry = "Saudi Arabia";
            shipment.UpdatedAtUtc = DateTime.UtcNow;
        }

        if (readyShipments.Count > 0)
            _db.FleetShipments.UpdateRange(readyShipments);

        await _db.SaveChangesAsync(ct);
    }
}
