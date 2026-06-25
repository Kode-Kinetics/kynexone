using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/logistics")]
[Authorize(Roles = "Admin,HR Director,HR Manager,Manager,Supervisor")]
public class LogisticsController : ControllerBase
{
    private readonly ZayraDbContext _db;

    public LogisticsController(ZayraDbContext db)
    {
        _db = db;
    }

    [HttpGet("overview")]
    public async Task<IActionResult> Overview(CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var tenantId = this.GetTenantId()!.Value;

        var orders = _db.DispatchOrders.AsNoTracking().Where(x => x.TenantId == tenantId);
        var routes = _db.DeliveryRoutes.AsNoTracking().Where(x => x.TenantId == tenantId);
        var stops = _db.LastMileStops.AsNoTracking().Where(x => x.TenantId == tenantId);

        var generatedAtUtc = DateTime.UtcNow;
        var activeOrders = await orders.CountAsync(x => x.Status != "Delivered" && x.Status != "Returned", ct);
        var inTransit = await orders.CountAsync(x => x.Status == "Dispatched" || x.Status == "InTransit", ct);
        var deliveredToday = await orders.CountAsync(x => x.Status == "Delivered" && x.DeliveredAtUtc >= DateTime.UtcNow.Date, ct);
        var exceptionOrders = await orders.CountAsync(x => x.Status == "Exception", ct);
        var activeRoutes = await routes.CountAsync(x => x.Status == "Ready" || x.Status == "Active" || x.Status == "Delayed", ct);
        var onTimeRate = await routes
            .Select(x => (decimal?)x.CompletionPercent)
            .AverageAsync(ct) ?? 0m;

        var routeCards = await routes
            .OrderByDescending(x => x.CompletedStops)
            .ThenBy(x => x.RouteCode)
            .Take(4)
            .Select(x => new
            {
                x.Id,
                x.RouteCode,
                x.Hub,
                x.Territory,
                x.DriverName,
                x.VehicleNumber,
                x.Status,
                x.PlannedStops,
                x.CompletedStops,
                x.CompletionPercent,
                x.CurrentStop,
                x.NextStop,
                x.Notes,
            })
            .ToListAsync(ct);

        var orderCards = await orders
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(5)
            .Select(x => new
            {
                x.Id,
                x.OrderNumber,
                x.CustomerName,
                x.City,
                x.Area,
                x.Priority,
                x.Status,
                x.RouteCode,
                x.DriverName,
                x.VehicleNumber,
                x.ItemCount,
                x.OrderValue,
                x.PromisedAtUtc,
                x.DispatchedAtUtc,
                x.DeliveredAtUtc,
                x.DispatchNotes,
            })
            .ToListAsync(ct);

        var alerts = await stops
            .Where(x => (x.Status == "Attempted" || x.Status == "Failed") || !string.IsNullOrWhiteSpace(x.ExceptionReason))
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(4)
            .Select(x => new
            {
                x.OrderNumber,
                x.CustomerName,
                x.RouteCode,
                x.Status,
                x.ExceptionReason,
                x.AttemptCount,
                x.RiderName,
                x.EtaUtc,
            })
            .ToListAsync(ct);

        var liveStops = await stops
            .OrderByDescending(x => x.EtaUtc)
            .Take(6)
            .Select(x => new
            {
                x.Id,
                x.OrderNumber,
                x.CustomerName,
                x.AddressLine,
                x.City,
                x.RouteCode,
                x.Status,
                x.ProofStatus,
                x.RiderName,
                x.TimeWindow,
                x.AttemptCount,
                x.EtaUtc,
            })
            .ToListAsync(ct);

        return Ok(new
        {
            generatedAtUtc,
            summary = new
            {
                activeOrders,
                inTransit,
                deliveredToday,
                exceptionOrders,
                activeRoutes,
                onTimeRate = Math.Round(onTimeRate, 1),
            },
            alerts,
            routeCards,
            orderCards,
            liveStops,
        });
    }

    [HttpGet("orders")]
    public async Task<IActionResult> Orders([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        if (!CanRead()) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var query = _db.DispatchOrders.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    [HttpGet("orders/{id:guid}")]
    public async Task<IActionResult> Order(Guid id, CancellationToken ct = default)
    {
        if (!CanRead()) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var item = await _db.DispatchOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost("orders")]
    public async Task<IActionResult> CreateOrder([FromBody] LogisticsOrderRequest request, CancellationToken ct = default)
    {
        if (!CanWrite()) return Forbid();
        if (string.IsNullOrWhiteSpace(request.OrderNumber)) return BadRequest(new { message = "Order number is required." });
        if (string.IsNullOrWhiteSpace(request.CustomerName)) return BadRequest(new { message = "Customer name is required." });

        var tenantId = this.GetTenantId()!.Value;
        var entity = new DispatchOrder
        {
            TenantId = tenantId,
            OrderNumber = request.OrderNumber.Trim(),
            CustomerName = request.CustomerName.Trim(),
            CustomerSegment = request.CustomerSegment?.Trim() ?? "Retail",
            SalesChannel = request.SalesChannel?.Trim() ?? "Portal",
            City = request.City?.Trim() ?? string.Empty,
            Area = request.Area?.Trim() ?? string.Empty,
            Status = request.Status?.Trim() ?? "Queued",
            Priority = request.Priority?.Trim() ?? "Normal",
            ItemCount = request.ItemCount ?? 1,
            OrderValue = request.OrderValue ?? 0m,
            RouteCode = request.RouteCode?.Trim() ?? string.Empty,
            DriverName = request.DriverName?.Trim() ?? string.Empty,
            VehicleNumber = request.VehicleNumber?.Trim() ?? string.Empty,
            DispatchNotes = request.DispatchNotes?.Trim() ?? string.Empty,
            CreatedAtUtc = DateTime.UtcNow,
            PromisedAtUtc = request.PromisedAtUtc,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        _db.DispatchOrders.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    [HttpPut("orders/{id:guid}")]
    public async Task<IActionResult> UpdateOrder(Guid id, [FromBody] LogisticsOrderRequest request, CancellationToken ct = default)
    {
        if (!CanWrite()) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var entity = await _db.DispatchOrders.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (entity is null) return NotFound();

        entity.CustomerName = request.CustomerName?.Trim() ?? entity.CustomerName;
        entity.CustomerSegment = request.CustomerSegment?.Trim() ?? entity.CustomerSegment;
        entity.SalesChannel = request.SalesChannel?.Trim() ?? entity.SalesChannel;
        entity.City = request.City?.Trim() ?? entity.City;
        entity.Area = request.Area?.Trim() ?? entity.Area;
        entity.Status = request.Status?.Trim() ?? entity.Status;
        entity.Priority = request.Priority?.Trim() ?? entity.Priority;
        entity.ItemCount = request.ItemCount ?? entity.ItemCount;
        entity.OrderValue = request.OrderValue ?? entity.OrderValue;
        entity.RouteCode = request.RouteCode?.Trim() ?? entity.RouteCode;
        entity.DriverName = request.DriverName?.Trim() ?? entity.DriverName;
        entity.VehicleNumber = request.VehicleNumber?.Trim() ?? entity.VehicleNumber;
        entity.DispatchNotes = request.DispatchNotes?.Trim() ?? entity.DispatchNotes;
        entity.PromisedAtUtc = request.PromisedAtUtc ?? entity.PromisedAtUtc;
        entity.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    [HttpGet("routes")]
    public async Task<IActionResult> Routes([FromQuery] string? status, [FromQuery] CancellationToken ct = default)
    {
        if (!CanRead()) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var query = _db.DeliveryRoutes.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);

        var items = await query
            .OrderByDescending(x => x.PlannedForDate)
            .ThenBy(x => x.RouteCode)
            .ToListAsync(ct);

        return Ok(new { items });
    }

    [HttpPost("routes")]
    public async Task<IActionResult> CreateRoute([FromBody] LogisticsRouteRequest request, CancellationToken ct = default)
    {
        if (!CanWrite()) return Forbid();
        if (string.IsNullOrWhiteSpace(request.RouteCode)) return BadRequest(new { message = "Route code is required." });

        var tenantId = this.GetTenantId()!.Value;
        var entity = new DeliveryRoute
        {
            TenantId = tenantId,
            RouteCode = request.RouteCode.Trim(),
            Hub = request.Hub?.Trim() ?? string.Empty,
            Territory = request.Territory?.Trim() ?? string.Empty,
            DriverName = request.DriverName?.Trim() ?? string.Empty,
            VehicleNumber = request.VehicleNumber?.Trim() ?? string.Empty,
            Status = request.Status?.Trim() ?? "Planned",
            PlannedStops = request.PlannedStops ?? 0,
            CompletedStops = request.CompletedStops ?? 0,
            DistanceKm = request.DistanceKm ?? 0m,
            CompletionPercent = request.CompletionPercent ?? 0m,
            CurrentStop = request.CurrentStop?.Trim() ?? string.Empty,
            NextStop = request.NextStop?.Trim() ?? string.Empty,
            PlannedForDate = request.PlannedForDate?.Date ?? DateTime.UtcNow.Date,
            DepartureTimeUtc = request.DepartureTimeUtc ?? DateTime.UtcNow,
            EtaCompleteUtc = request.EtaCompleteUtc,
            Notes = request.Notes?.Trim() ?? string.Empty,
        };

        _db.DeliveryRoutes.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    [HttpPut("routes/{id:guid}")]
    public async Task<IActionResult> UpdateRoute(Guid id, [FromBody] LogisticsRouteRequest request, CancellationToken ct = default)
    {
        if (!CanWrite()) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var entity = await _db.DeliveryRoutes.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (entity is null) return NotFound();

        entity.Hub = request.Hub?.Trim() ?? entity.Hub;
        entity.Territory = request.Territory?.Trim() ?? entity.Territory;
        entity.DriverName = request.DriverName?.Trim() ?? entity.DriverName;
        entity.VehicleNumber = request.VehicleNumber?.Trim() ?? entity.VehicleNumber;
        entity.Status = request.Status?.Trim() ?? entity.Status;
        entity.PlannedStops = request.PlannedStops ?? entity.PlannedStops;
        entity.CompletedStops = request.CompletedStops ?? entity.CompletedStops;
        entity.DistanceKm = request.DistanceKm ?? entity.DistanceKm;
        entity.CompletionPercent = request.CompletionPercent ?? entity.CompletionPercent;
        entity.CurrentStop = request.CurrentStop?.Trim() ?? entity.CurrentStop;
        entity.NextStop = request.NextStop?.Trim() ?? entity.NextStop;
        entity.PlannedForDate = request.PlannedForDate?.Date ?? entity.PlannedForDate;
        entity.DepartureTimeUtc = request.DepartureTimeUtc ?? entity.DepartureTimeUtc;
        entity.EtaCompleteUtc = request.EtaCompleteUtc ?? entity.EtaCompleteUtc;
        entity.Notes = request.Notes?.Trim() ?? entity.Notes;

        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    [HttpGet("routes/{id:guid}/stops")]
    public async Task<IActionResult> RouteStops(Guid id, CancellationToken ct = default)
    {
        if (!CanRead()) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var route = await _db.DeliveryRoutes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (route is null) return NotFound();

        var items = await _db.LastMileStops.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.RouteCode == route.RouteCode)
            .OrderBy(x => x.EtaUtc)
            .ToListAsync(ct);

        return Ok(new { items });
    }

    [HttpGet("last-mile")]
    public async Task<IActionResult> LastMile([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        if (!CanRead()) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var query = _db.LastMileStops.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.EtaUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    [HttpPost("orders/{id:guid}/dispatch")]
    public async Task<IActionResult> DispatchOrder(Guid id, [FromBody] DispatchOrderRequest request, CancellationToken ct)
    {
        if (!CanWrite()) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var order = await _db.DispatchOrders.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (order is null) return NotFound();

        if (order.Status == "Delivered" || order.Status == "Returned")
            return BadRequest(new { message = "Delivered or returned orders cannot be dispatched again." });

        order.Status = "Dispatched";
        order.DriverName = request.DriverName?.Trim() ?? order.DriverName;
        order.VehicleNumber = request.VehicleNumber?.Trim() ?? order.VehicleNumber;
        order.RouteCode = request.RouteCode?.Trim() ?? order.RouteCode;
        order.DispatchNotes = request.Notes?.Trim() ?? order.DispatchNotes;
        order.DispatchedAtUtc = DateTime.UtcNow;
        order.UpdatedAtUtc = DateTime.UtcNow;

        var route = await _db.DeliveryRoutes.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.RouteCode == order.RouteCode, ct);
        if (route is not null && route.Status == "Planned") route.Status = "Ready";

        var stop = await _db.LastMileStops.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.OrderNumber == order.OrderNumber, ct);
        if (stop is not null)
        {
            stop.Status = "OutForDelivery";
            stop.RiderName = order.DriverName;
            stop.UpdatedAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(order);
    }

    [HttpPost("routes/{id:guid}/progress")]
    public async Task<IActionResult> ProgressRoute(Guid id, [FromBody] RouteProgressRequest request, CancellationToken ct)
    {
        if (!CanWrite()) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var route = await _db.DeliveryRoutes.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (route is null) return NotFound();

        route.CompletedStops = Math.Min(route.PlannedStops, route.CompletedStops + Math.Max(1, request.CompletedStopsDelta));
        route.CompletionPercent = route.PlannedStops == 0 ? 0 : Math.Round((route.CompletedStops / (decimal)route.PlannedStops) * 100m, 1);
        route.Status = route.CompletedStops >= route.PlannedStops ? "Closed" : route.CompletedStops > 0 ? "Active" : route.Status;
        route.CurrentStop = request.CurrentStop ?? route.CurrentStop;
        route.NextStop = request.NextStop ?? route.NextStop;
        route.EtaCompleteUtc = request.EtaCompleteUtc ?? route.EtaCompleteUtc;
        route.Notes = request.Notes ?? route.Notes;

        await _db.SaveChangesAsync(ct);
        return Ok(route);
    }

    [HttpPost("stops/{id:guid}/deliver")]
    public async Task<IActionResult> ConfirmDelivery(Guid id, [FromBody] ConfirmDeliveryRequest request, CancellationToken ct)
    {
        if (!CanWrite()) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var stop = await _db.LastMileStops.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (stop is null) return NotFound();

        stop.Status = "Delivered";
        stop.ProofStatus = request.ProofStatus?.Trim() ?? "POD";
        stop.RecipientName = request.RecipientName?.Trim() ?? stop.RecipientName;
        stop.DeliveredAtUtc = DateTime.UtcNow;
        stop.ExceptionReason = request.ExceptionReason?.Trim() ?? string.Empty;
        stop.AttemptCount = Math.Max(stop.AttemptCount, 1);
        stop.UpdatedAtUtc = DateTime.UtcNow;

        var order = await _db.DispatchOrders.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.OrderNumber == stop.OrderNumber, ct);
        if (order is not null)
        {
            order.Status = "Delivered";
            order.DeliveredAtUtc = DateTime.UtcNow;
            order.UpdatedAtUtc = DateTime.UtcNow;
        }

        var route = await _db.DeliveryRoutes.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.RouteCode == stop.RouteCode, ct);
        if (route is not null)
        {
            route.CompletedStops = Math.Min(route.PlannedStops, route.CompletedStops + 1);
            route.CompletionPercent = route.PlannedStops == 0 ? 0 : Math.Round((route.CompletedStops / (decimal)route.PlannedStops) * 100m, 1);
            route.Status = route.CompletedStops >= route.PlannedStops ? "Closed" : "Active";
            route.CurrentStop = stop.CustomerName;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(stop);
    }

    [HttpPost("stops/{id:guid}/attempt")]
    public async Task<IActionResult> RecordAttempt(Guid id, [FromBody] StopAttemptRequest request, CancellationToken ct)
    {
        if (!CanWrite()) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var stop = await _db.LastMileStops.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (stop is null) return NotFound();

        stop.Status = request.Status?.Trim() ?? "Attempted";
        stop.ExceptionReason = request.ExceptionReason?.Trim() ?? "Delivery attempt recorded.";
        stop.AttemptCount = Math.Max(1, stop.AttemptCount + 1);
        stop.ProofStatus = request.ProofStatus?.Trim() ?? stop.ProofStatus;
        stop.EtaUtc = request.NextEtaUtc ?? stop.EtaUtc.AddHours(4);
        stop.UpdatedAtUtc = DateTime.UtcNow;

        var order = await _db.DispatchOrders.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.OrderNumber == stop.OrderNumber, ct);
        if (order is not null)
        {
            order.Status = stop.Status == "Failed" ? "Exception" : "InTransit";
            order.DispatchNotes = request.ExceptionReason?.Trim() ?? order.DispatchNotes;
            order.UpdatedAtUtc = DateTime.UtcNow;
        }

        var route = await _db.DeliveryRoutes.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.RouteCode == stop.RouteCode, ct);
        if (route is not null)
        {
            route.Status = stop.Status == "Failed" ? "Delayed" : route.Status;
            route.CurrentStop = stop.CustomerName;
            route.NextStop = request.NextStop?.Trim() ?? route.NextStop;
            route.Notes = request.ExceptionReason?.Trim() ?? route.Notes;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(stop);
    }

    [HttpPost("stops/{id:guid}/reschedule")]
    public async Task<IActionResult> RescheduleStop(Guid id, [FromBody] StopRescheduleRequest request, CancellationToken ct)
    {
        if (!CanWrite()) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var stop = await _db.LastMileStops.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (stop is null) return NotFound();

        stop.Status = "Rescheduled";
        stop.TimeWindow = string.IsNullOrWhiteSpace(request.TimeWindow) ? stop.TimeWindow : request.TimeWindow.Trim();
        stop.EtaUtc = request.NextEtaUtc ?? stop.EtaUtc.AddHours(6);
        stop.ExceptionReason = request.Reason?.Trim() ?? "Stop rescheduled.";
        stop.UpdatedAtUtc = DateTime.UtcNow;

        var order = await _db.DispatchOrders.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.OrderNumber == stop.OrderNumber, ct);
        if (order is not null)
        {
            order.Status = "Exception";
            order.DispatchNotes = stop.ExceptionReason;
            order.UpdatedAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(stop);
    }

    private bool CanRead() => HasPermission("logistics.read");
    private bool CanWrite() => HasPermission("logistics.write");
    private bool HasPermission(string permission) => User.Claims.Any(x => x.Type == "permission" && string.Equals(x.Value, permission, StringComparison.OrdinalIgnoreCase));
}

public record LogisticsOrderRequest(
    string? OrderNumber,
    string? CustomerName,
    string? CustomerSegment,
    string? SalesChannel,
    string? City,
    string? Area,
    string? Status,
    string? Priority,
    int? ItemCount,
    decimal? OrderValue,
    string? RouteCode,
    string? DriverName,
    string? VehicleNumber,
    string? DispatchNotes,
    DateTime? PromisedAtUtc);

public record LogisticsRouteRequest(
    string? RouteCode,
    string? Hub,
    string? Territory,
    string? DriverName,
    string? VehicleNumber,
    string? Status,
    int? PlannedStops,
    int? CompletedStops,
    decimal? DistanceKm,
    decimal? CompletionPercent,
    string? CurrentStop,
    string? NextStop,
    DateTime? PlannedForDate,
    DateTime? DepartureTimeUtc,
    DateTime? EtaCompleteUtc,
    string? Notes);

public record DispatchOrderRequest(string? RouteCode, string? DriverName, string? VehicleNumber, string? Notes);
public record RouteProgressRequest(int CompletedStopsDelta, string? CurrentStop, string? NextStop, DateTime? EtaCompleteUtc, string? Notes);
public record ConfirmDeliveryRequest(string? RecipientName, string? ProofStatus, string? ExceptionReason);
public record StopAttemptRequest(string? Status, string? ProofStatus, string? ExceptionReason, DateTime? NextEtaUtc, string? NextStop);
public record StopRescheduleRequest(DateTime? NextEtaUtc, string? TimeWindow, string? Reason);
