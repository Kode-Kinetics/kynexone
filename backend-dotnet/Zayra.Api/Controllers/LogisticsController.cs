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

    [HttpGet("routes")]
    public async Task<IActionResult> Routes([FromQuery] string? status, [FromQuery] CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId()!.Value;
        var query = _db.DeliveryRoutes.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);

        var items = await query
            .OrderByDescending(x => x.PlannedForDate)
            .ThenBy(x => x.RouteCode)
            .ToListAsync(ct);

        return Ok(new { items });
    }

    [HttpGet("last-mile")]
    public async Task<IActionResult> LastMile([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
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
        var tenantId = this.GetTenantId()!.Value;
        var order = await _db.DispatchOrders.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (order is null) return NotFound();

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
}

public record DispatchOrderRequest(string? RouteCode, string? DriverName, string? VehicleNumber, string? Notes);
public record RouteProgressRequest(int CompletedStopsDelta, string? CurrentStop, string? NextStop, DateTime? EtaCompleteUtc, string? Notes);
public record ConfirmDeliveryRequest(string? RecipientName, string? ProofStatus, string? ExceptionReason);
