using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

public interface ILogisticsSeeder
{
    Task SeedAsync(CancellationToken ct = default);
}

public class LogisticsSeeder : ILogisticsSeeder
{
    private readonly ZayraDbContext _db;
    private readonly ILogger<LogisticsSeeder> _logger;

    public LogisticsSeeder(ZayraDbContext db, ILogger<LogisticsSeeder> logger)
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
        if (await _db.DispatchOrders.AnyAsync(x => x.TenantId == tenantId, ct)) return;

        var employees = await _db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && (e.Status == "Active" || e.Status == "Confirmed" || e.Status == "Probation"))
            .OrderBy(e => e.EmployeeCode)
            .ToListAsync(ct);

        if (employees.Count == 0)
        {
            _logger.LogInformation("LogisticsSeeder: tenant {TenantId} ({Slug}) has no active employees — skipping.", tenantId, slug);
            return;
        }

        var hubNames = new[] { "North Hub", "Central Hub", "South Hub" };
        var territories = new[] { "City Core", "Business District", "Residential Loop", "Industrial Edge" };
        var channels = new[] { "Portal", "WhatsApp", "Partner API", "Sales Desk" };
        var customers = new[]
        {
            "Al Noor Pharmacy", "Sama Clinic", "Zenith Traders", "Oasis Mart", "ByteWave Store",
            "Capital Coffee", "Misk Home", "Nova Supplies", "Royal Beauty", "Urban Fit"
        };
        var cities = new[] { "Riyadh", "Jeddah", "Dammam", "Khobar" };
        var orderStatuses = new[] { "Queued", "Picking", "Packed", "Dispatched", "InTransit", "Delivered", "Exception" };
        var routeStatuses = new[] { "Ready", "Active", "Delayed", "Closed" };
        var proofStatuses = new[] { "None", "OTP", "Signature", "POD" };

        var rng = new Random(tenantId.GetHashCode() & 0x7fffffff);
        var today = DateTime.UtcNow.Date;
        var routes = new List<DeliveryRoute>();
        for (var i = 0; i < 4; i++)
        {
            var completed = 8 + i * 2;
            var planned = completed + 4 + (i % 2);
            routes.Add(new DeliveryRoute
            {
                TenantId = tenantId,
                RouteCode = $"RT-{today:yyMMdd}-{i + 1:02}",
                Hub = hubNames[i % hubNames.Length],
                Territory = territories[i % territories.Length],
                DriverName = employees[(i + 1) % employees.Count].FullName,
                VehicleNumber = $"VAN-{(i + 3) * 14}",
                Status = routeStatuses[i % routeStatuses.Length],
                PlannedStops = planned,
                CompletedStops = completed,
                DistanceKm = Math.Round(38.5m + (i * 8.4m), 1),
                CompletionPercent = Math.Round((completed / (decimal)planned) * 100m, 1),
                CurrentStop = $"{customers[(i + 2) % customers.Length]} · {cities[i % cities.Length]}",
                NextStop = $"{customers[(i + 3) % customers.Length]} · {cities[(i + 1) % cities.Length]}",
                PlannedForDate = today,
                DepartureTimeUtc = DateTime.UtcNow.AddHours(-4).AddMinutes(i * 9),
                EtaCompleteUtc = DateTime.UtcNow.AddHours(2).AddMinutes(i * 18),
                Notes = i == 2 ? "Traffic expected after 5 PM on eastern belt" : "All parcels scanned at hub departure",
            });
        }

        _db.DeliveryRoutes.AddRange(routes);
        await _db.SaveChangesAsync(ct);

        var orders = new List<DispatchOrder>();
        var stops = new List<LastMileStop>();
        for (var i = 0; i < 12; i++)
        {
            var route = routes[i % routes.Count];
            var customer = customers[i % customers.Length];
            var city = cities[i % cities.Length];
            var status = orderStatuses[(i + 2) % orderStatuses.Length];
            var promised = DateTime.UtcNow.AddHours(2 + (i % 5));
            var driver = route.DriverName;
            var vehicle = route.VehicleNumber;
            var orderNumber = $"ORD-{today:yyMMdd}-{100 + i}";

            orders.Add(new DispatchOrder
            {
                TenantId = tenantId,
                OrderNumber = orderNumber,
                CustomerName = customer,
                CustomerSegment = i % 3 == 0 ? "Enterprise" : i % 3 == 1 ? "Retail" : "SME",
                SalesChannel = channels[i % channels.Length],
                City = city,
                Area = territories[i % territories.Length],
                Status = status,
                Priority = i % 4 == 0 ? "High" : i % 6 == 0 ? "Critical" : "Normal",
                ItemCount = 2 + (i % 5),
                OrderValue = Math.Round(180m + (i * 67.5m), 2),
                RouteCode = route.RouteCode,
                DriverName = driver,
                VehicleNumber = vehicle,
                DispatchNotes = status is "Exception"
                    ? "Customer unreachable at the first attempt"
                    : "Packed, scanned, and ready for route assignment",
                CreatedAtUtc = DateTime.UtcNow.AddHours(-8 + i),
                PromisedAtUtc = promised,
                DispatchedAtUtc = status is "Dispatched" or "InTransit" or "Delivered" ? DateTime.UtcNow.AddHours(-2 + (i * 0.2)) : null,
                DeliveredAtUtc = status == "Delivered" ? DateTime.UtcNow.AddHours(-1 + (i * 0.1)) : null,
                UpdatedAtUtc = DateTime.UtcNow.AddHours(-1),
            });

            stops.Add(new LastMileStop
            {
                TenantId = tenantId,
                OrderNumber = orderNumber,
                RouteCode = route.RouteCode,
                CustomerName = customer,
                AddressLine = $"{100 + i} {city} Business Road",
                City = city,
                Status = status is "Delivered" ? "Delivered" : status is "Exception" ? "Attempted" : "OutForDelivery",
                ProofStatus = status == "Delivered" ? proofStatuses[(i + 1) % proofStatuses.Length] : "None",
                RecipientName = status == "Delivered" ? $"{customer.Split(' ')[0]} Receiver" : string.Empty,
                AttemptCount = status == "Exception" ? 2 : status == "Delivered" ? 1 : 0,
                RiderName = driver,
                TimeWindow = i % 2 == 0 ? "10:00-13:00" : "14:00-18:00",
                EtaUtc = DateTime.UtcNow.AddHours(1 + (i * 0.25)),
                DeliveredAtUtc = status == "Delivered" ? DateTime.UtcNow.AddMinutes(-(30 + i * 5)) : null,
                ExceptionReason = status == "Exception" ? "Customer requested reschedule" : string.Empty,
                CreatedAtUtc = DateTime.UtcNow.AddHours(-6 + i * 0.5),
                UpdatedAtUtc = DateTime.UtcNow.AddHours(-1),
            });
        }

        _db.DispatchOrders.AddRange(orders);
        _db.LastMileStops.AddRange(stops);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "LogisticsSeeder: seeded {Orders} dispatch orders, {Routes} routes and {Stops} last-mile stops for tenant {TenantId} ({Slug}).",
            orders.Count, routes.Count, stops.Count, tenantId, slug);
    }
}
