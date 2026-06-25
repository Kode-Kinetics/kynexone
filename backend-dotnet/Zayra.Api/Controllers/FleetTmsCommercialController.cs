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
public class FleetTmsCommercialController : ControllerBase
{
    private readonly ZayraDbContext _db;

    public FleetTmsCommercialController(ZayraDbContext db)
    {
        _db = db;
    }

    [HttpGet("carriers")]
    public async Task<IActionResult> GetCarriers(CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var items = await _db.Carriers.AsNoTracking().Where(x => x.TenantId == tenantId).OrderBy(x => x.Name).ToListAsync(ct);
        return Ok(new { items });
    }

    [HttpGet("carriers/{id:guid}")]
    public async Task<IActionResult> GetCarrier(Guid id, CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var carrier = await RequireCarrierAsync(id, ct);
        return carrier is null ? NotFound() : Ok(carrier);
    }

    [HttpPost("carriers")]
    public async Task<IActionResult> CreateCarrier([FromBody] CarrierRequest req, CancellationToken ct)
    {
        if (!CanWrite()) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "Carrier name is required." });
        if (string.IsNullOrWhiteSpace(req.Code)) return BadRequest(new { message = "Carrier code is required." });

        var tenantId = this.GetTenantId()!.Value;
        var entity = new Carrier
        {
            TenantId = tenantId,
            Name = req.Name.Trim(),
            Code = req.Code.Trim(),
            Status = req.Status?.Trim() ?? "Active",
            Region = req.Region?.Trim() ?? string.Empty,
            ServiceType = req.ServiceType?.Trim() ?? string.Empty,
            OnTimeScore = req.OnTimeScore ?? 0,
            DamageScore = req.DamageScore ?? 0,
            CostScore = req.CostScore ?? 0,
            Notes = req.Notes?.Trim() ?? string.Empty,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        _db.Carriers.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    [HttpPut("carriers/{id:guid}")]
    public async Task<IActionResult> UpdateCarrier(Guid id, [FromBody] CarrierRequest req, CancellationToken ct)
    {
        if (!CanWrite()) return Forbid();
        var carrier = await RequireCarrierAsync(id, ct);
        if (carrier is null) return NotFound();

        carrier.Name = req.Name?.Trim() ?? carrier.Name;
        carrier.Code = req.Code?.Trim() ?? carrier.Code;
        carrier.Status = req.Status?.Trim() ?? carrier.Status;
        carrier.Region = req.Region?.Trim() ?? carrier.Region;
        carrier.ServiceType = req.ServiceType?.Trim() ?? carrier.ServiceType;
        carrier.OnTimeScore = req.OnTimeScore ?? carrier.OnTimeScore;
        carrier.DamageScore = req.DamageScore ?? carrier.DamageScore;
        carrier.CostScore = req.CostScore ?? carrier.CostScore;
        carrier.Notes = req.Notes?.Trim() ?? carrier.Notes;
        carrier.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(carrier);
    }

    [HttpPost("shipments/{shipmentId:guid}/carrier")]
    public async Task<IActionResult> AssignCarrier(Guid shipmentId, [FromBody] ShipmentCarrierRequest req, CancellationToken ct)
    {
        if (!CanWrite()) return Forbid();
        if (req.CarrierId == Guid.Empty) return BadRequest(new { message = "Carrier is required." });

        var shipment = await RequireShipmentAsync(shipmentId, ct);
        if (shipment is null) return NotFound();

        var carrier = await RequireCarrierAsync(req.CarrierId, ct);
        if (carrier is null) return NotFound(new { message = "Carrier not found for this tenant." });

        var assignment = new ShipmentCarrierAssignment
        {
            TenantId = shipment.TenantId,
            ShipmentId = shipmentId,
            CarrierId = carrier.Id,
            Status = req.Status?.Trim() ?? "Assigned",
            QuotedAmount = req.QuotedAmount ?? 0,
            AgreedAmount = req.AgreedAmount ?? 0,
            Notes = req.Notes?.Trim() ?? string.Empty,
            AssignedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        shipment.CarrierName = carrier.Name;
        shipment.UpdatedAtUtc = DateTime.UtcNow;

        _db.ShipmentCarrierAssignments.Add(assignment);
        _db.ShipmentEvents.Add(new ShipmentEvent
        {
            TenantId = shipment.TenantId,
            ShipmentId = shipmentId,
            EventType = "CarrierAssigned",
            Message = $"Carrier {carrier.Name} assigned to shipment.",
            ActorName = User.Identity?.Name ?? "system",
            Visibility = "Private",
            OccurredAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(ct);
        return Ok(assignment);
    }

    [HttpGet("booking-requests")]
    public async Task<IActionResult> GetBookingRequests(CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var items = await _db.BookingRequests.AsNoTracking().Where(x => x.TenantId == tenantId).OrderByDescending(x => x.RequestedAtUtc).ToListAsync(ct);
        return Ok(new { items });
    }

    [HttpPost("booking-requests")]
    public async Task<IActionResult> CreateBookingRequest([FromBody] BookingRequestRequest req, CancellationToken ct)
    {
        if (!CanWrite()) return Forbid();
        if (string.IsNullOrWhiteSpace(req.RequestNumber)) return BadRequest(new { message = "Request number is required." });
        if (string.IsNullOrWhiteSpace(req.CustomerName)) return BadRequest(new { message = "Customer name is required." });

        var tenantId = this.GetTenantId()!.Value;
        var entity = new BookingRequest
        {
            TenantId = tenantId,
            RequestNumber = req.RequestNumber.Trim(),
            CustomerName = req.CustomerName.Trim(),
            Origin = req.Origin?.Trim() ?? string.Empty,
            Destination = req.Destination?.Trim() ?? string.Empty,
            Status = req.Status?.Trim() ?? "Open",
            EstimatedWeightKg = req.EstimatedWeightKg ?? 0,
            EstimatedVolumeCbm = req.EstimatedVolumeCbm ?? 0,
            RequestedAtUtc = DateTime.UtcNow,
            Notes = req.Notes?.Trim() ?? string.Empty,
        };

        _db.BookingRequests.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    [HttpGet("quote-requests")]
    public async Task<IActionResult> GetQuoteRequests(CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var tenantId = this.GetTenantId()!.Value;
        var items = await _db.QuoteRequests.AsNoTracking().Where(x => x.TenantId == tenantId).OrderByDescending(x => x.RequestedAtUtc).ToListAsync(ct);
        return Ok(new { items });
    }

    [HttpPost("quote-requests")]
    public async Task<IActionResult> CreateQuoteRequest([FromBody] QuoteRequestRequest req, CancellationToken ct)
    {
        if (!CanWrite()) return Forbid();
        if (string.IsNullOrWhiteSpace(req.QuoteNumber)) return BadRequest(new { message = "Quote number is required." });
        if (string.IsNullOrWhiteSpace(req.CustomerName)) return BadRequest(new { message = "Customer name is required." });

        var tenantId = this.GetTenantId()!.Value;
        var entity = new QuoteRequest
        {
            TenantId = tenantId,
            QuoteNumber = req.QuoteNumber.Trim(),
            CustomerName = req.CustomerName.Trim(),
            Origin = req.Origin?.Trim() ?? string.Empty,
            Destination = req.Destination?.Trim() ?? string.Empty,
            Status = req.Status?.Trim() ?? "Draft",
            EstimatedAmount = req.EstimatedAmount ?? 0,
            MarginPct = req.MarginPct ?? 0,
            RequestedAtUtc = DateTime.UtcNow,
            Notes = req.Notes?.Trim() ?? string.Empty,
        };

        _db.QuoteRequests.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    private async Task<Carrier?> RequireCarrierAsync(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        return await _db.Carriers.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
    }

    private async Task<FleetShipment?> RequireShipmentAsync(Guid shipmentId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        return await _db.FleetShipments.FirstOrDefaultAsync(x => x.Id == shipmentId && x.TenantId == tenantId, ct);
    }

    private bool CanRead() => HasPermission("fleet.read") || HasPermission("logistics.read");
    private bool CanWrite() => HasPermission("fleet.write") || HasPermission("logistics.write");
    private bool HasPermission(string permission) => User.Claims.Any(x => x.Type == "permission" && x.Value == permission);
}

public record CarrierRequest(
    string? Name,
    string? Code,
    string? Status,
    string? Region,
    string? ServiceType,
    decimal? OnTimeScore,
    decimal? DamageScore,
    decimal? CostScore,
    string? Notes);

public record ShipmentCarrierRequest(
    Guid CarrierId,
    decimal? QuotedAmount,
    decimal? AgreedAmount,
    string? Status,
    string? Notes);

public record BookingRequestRequest(
    string? RequestNumber,
    string? CustomerName,
    string? Origin,
    string? Destination,
    string? Status,
    decimal? EstimatedWeightKg,
    decimal? EstimatedVolumeCbm,
    string? Notes);

public record QuoteRequestRequest(
    string? QuoteNumber,
    string? CustomerName,
    string? Origin,
    string? Destination,
    string? Status,
    decimal? EstimatedAmount,
    decimal? MarginPct,
    string? Notes);
