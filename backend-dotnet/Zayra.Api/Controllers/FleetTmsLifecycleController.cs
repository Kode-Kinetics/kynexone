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
public class FleetTmsLifecycleController : ControllerBase
{
    private readonly ZayraDbContext _db;

    public FleetTmsLifecycleController(ZayraDbContext db)
    {
        _db = db;
    }

    [HttpGet("shipments/{shipmentId:guid}/stops")]
    public async Task<IActionResult> GetStops(Guid shipmentId, CancellationToken ct)
    {
        var shipment = await RequireShipmentAsync(shipmentId, ct);
        if (shipment is null) return NotFound();

        var items = await _db.ShipmentStops.AsNoTracking()
            .Where(x => x.TenantId == shipment.TenantId && x.ShipmentId == shipmentId)
            .OrderBy(x => x.SequenceNo)
            .ToListAsync(ct);

        return Ok(new { items });
    }

    [HttpPost("shipments/{shipmentId:guid}/stops")]
    public async Task<IActionResult> CreateStop(Guid shipmentId, [FromBody] ShipmentStopRequest req, CancellationToken ct)
    {
        var shipment = await RequireShipmentAsync(shipmentId, ct);
        if (shipment is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.StopType)) return BadRequest(new { message = "Stop type is required." });
        if (req.SequenceNo < 1) return BadRequest(new { message = "Stop sequence is required." });
        if (string.IsNullOrWhiteSpace(req.LocationName)) return BadRequest(new { message = "Location name is required." });

        var entity = new ShipmentStop
        {
            TenantId = shipment.TenantId,
            ShipmentId = shipmentId,
            StopType = req.StopType.Trim(),
            SequenceNo = req.SequenceNo,
            LocationName = req.LocationName.Trim(),
            ContactName = req.ContactName?.Trim() ?? string.Empty,
            ContactPhone = req.ContactPhone?.Trim() ?? string.Empty,
            AddressLine1 = req.AddressLine1?.Trim() ?? string.Empty,
            AddressLine2 = req.AddressLine2?.Trim() ?? string.Empty,
            City = req.City?.Trim() ?? string.Empty,
            Region = req.Region?.Trim() ?? string.Empty,
            PostalCode = req.PostalCode?.Trim() ?? string.Empty,
            Country = req.Country?.Trim() ?? string.Empty,
            SaudiNationalAddressBuildingNo = req.SaudiNationalAddressBuildingNo?.Trim() ?? string.Empty,
            SaudiNationalAddressAdditionalNo = req.SaudiNationalAddressAdditionalNo?.Trim() ?? string.Empty,
            SaudiNationalAddressDistrict = req.SaudiNationalAddressDistrict?.Trim() ?? string.Empty,
            Latitude = req.Latitude,
            Longitude = req.Longitude,
            PlannedArrivalAt = req.PlannedArrivalAt,
            Status = "Planned",
            Notes = req.Notes?.Trim() ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.ShipmentStops.Add(entity);
        _db.ShipmentEvents.Add(new ShipmentEvent
        {
            TenantId = shipment.TenantId,
            ShipmentId = shipmentId,
            EventType = "StopAdded",
            Message = $"Stop {entity.SequenceNo} added for {entity.LocationName}.",
            ActorName = User.Identity?.Name ?? "system",
            Visibility = "Private",
            OccurredAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    [HttpPut("shipments/{shipmentId:guid}/stops/{stopId:guid}")]
    public async Task<IActionResult> UpdateStop(Guid shipmentId, Guid stopId, [FromBody] ShipmentStopRequest req, CancellationToken ct)
    {
        var stop = await RequireStopAsync(shipmentId, stopId, ct);
        if (stop is null) return NotFound();
        if (req.SequenceNo < 1) return BadRequest(new { message = "Stop sequence is required." });
        if (string.IsNullOrWhiteSpace(req.StopType)) return BadRequest(new { message = "Stop type is required." });

        stop.StopType = req.StopType.Trim();
        stop.SequenceNo = req.SequenceNo;
        stop.LocationName = req.LocationName?.Trim() ?? stop.LocationName;
        stop.ContactName = req.ContactName?.Trim() ?? stop.ContactName;
        stop.ContactPhone = req.ContactPhone?.Trim() ?? stop.ContactPhone;
        stop.AddressLine1 = req.AddressLine1?.Trim() ?? stop.AddressLine1;
        stop.AddressLine2 = req.AddressLine2?.Trim() ?? stop.AddressLine2;
        stop.City = req.City?.Trim() ?? stop.City;
        stop.Region = req.Region?.Trim() ?? stop.Region;
        stop.PostalCode = req.PostalCode?.Trim() ?? stop.PostalCode;
        stop.Country = req.Country?.Trim() ?? stop.Country;
        stop.SaudiNationalAddressBuildingNo = req.SaudiNationalAddressBuildingNo?.Trim() ?? stop.SaudiNationalAddressBuildingNo;
        stop.SaudiNationalAddressAdditionalNo = req.SaudiNationalAddressAdditionalNo?.Trim() ?? stop.SaudiNationalAddressAdditionalNo;
        stop.SaudiNationalAddressDistrict = req.SaudiNationalAddressDistrict?.Trim() ?? stop.SaudiNationalAddressDistrict;
        stop.Latitude = req.Latitude ?? stop.Latitude;
        stop.Longitude = req.Longitude ?? stop.Longitude;
        stop.PlannedArrivalAt = req.PlannedArrivalAt;
        stop.Notes = req.Notes?.Trim() ?? stop.Notes;
        stop.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(stop);
    }

    [HttpPost("shipments/{shipmentId:guid}/stops/{stopId:guid}/arrive")]
    public async Task<IActionResult> ArriveAtStop(Guid shipmentId, Guid stopId, [FromBody] ArriveStopRequest req, CancellationToken ct)
    {
        var stop = await RequireStopAsync(shipmentId, stopId, ct);
        if (stop is null) return NotFound();

        stop.Status = "Arrived";
        stop.ActualArrivalAt = DateTime.UtcNow;
        stop.UpdatedAt = DateTime.UtcNow;
        await LogShipmentEventAsync(stop.TenantId, shipmentId, "StopArrived", $"Stop {stop.SequenceNo} arrived at {stop.LocationName}.", "Private", ct);
        await _db.SaveChangesAsync(ct);
        return Ok(stop);
    }

    [HttpPost("shipments/{shipmentId:guid}/stops/{stopId:guid}/complete")]
    public async Task<IActionResult> CompleteStop(Guid shipmentId, Guid stopId, [FromBody] CompleteStopRequest req, CancellationToken ct)
    {
        var stop = await RequireStopAsync(shipmentId, stopId, ct);
        if (stop is null) return NotFound();

        stop.Status = "Completed";
        stop.CompletedAt = DateTime.UtcNow;
        stop.Notes = req.Notes?.Trim() ?? stop.Notes;
        stop.UpdatedAt = DateTime.UtcNow;
        await LogShipmentEventAsync(stop.TenantId, shipmentId, "StopCompleted", $"Stop {stop.SequenceNo} completed at {stop.LocationName}.", "Private", ct);
        await _db.SaveChangesAsync(ct);
        return Ok(stop);
    }

    [HttpGet("shipments/{shipmentId:guid}/pod")]
    public async Task<IActionResult> GetPod(Guid shipmentId, CancellationToken ct)
    {
        var shipment = await RequireShipmentAsync(shipmentId, ct);
        if (shipment is null) return NotFound();
        var items = await _db.ProofOfDeliveries.AsNoTracking().Where(x => x.TenantId == shipment.TenantId && x.ShipmentId == shipmentId).OrderByDescending(x => x.CapturedAt).ToListAsync(ct);
        return Ok(new { items });
    }

    [HttpGet("shipments/{shipmentId:guid}/events")]
    public async Task<IActionResult> GetShipmentEvents(Guid shipmentId, CancellationToken ct)
    {
        var shipment = await RequireShipmentAsync(shipmentId, ct);
        if (shipment is null) return NotFound();

        var items = await _db.ShipmentEvents.AsNoTracking()
            .Where(x => x.TenantId == shipment.TenantId && x.ShipmentId == shipmentId)
            .OrderByDescending(x => x.OccurredAtUtc)
            .ToListAsync(ct);

        return Ok(new { items });
    }

    [HttpGet("shipments/{shipmentId:guid}/tracking-link")]
    public async Task<IActionResult> GetTrackingLinks(Guid shipmentId, CancellationToken ct)
    {
        var shipment = await RequireShipmentAsync(shipmentId, ct);
        if (shipment is null) return NotFound();

        var items = await _db.CustomerTrackingLinks.AsNoTracking()
            .Where(x => x.TenantId == shipment.TenantId && x.ShipmentId == shipmentId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        return Ok(new { items });
    }

    [HttpPost("shipments/{shipmentId:guid}/pod")]
    public async Task<IActionResult> CreatePod(Guid shipmentId, [FromBody] PodRequest req, CancellationToken ct)
    {
        var stop = await RequireStopByIdAsync(shipmentId, req.StopId, ct);
        if (stop is null) return NotFound(new { message = "Stop not found for shipment." });
        if (string.IsNullOrWhiteSpace(req.RecipientName)) return BadRequest(new { message = "Recipient name is required." });
        if (string.IsNullOrWhiteSpace(req.DeliveryCondition)) return BadRequest(new { message = "Delivery condition is required." });

        var entity = new ProofOfDelivery
        {
            TenantId = stop.TenantId,
            ShipmentId = shipmentId,
            StopId = stop.Id,
            CapturedByUserId = this.GetUserId(),
            RecipientName = req.RecipientName.Trim(),
            RecipientPhone = req.RecipientPhone?.Trim() ?? string.Empty,
            SignatureUrl = req.SignatureUrl?.Trim() ?? string.Empty,
            PhotoUrl = req.PhotoUrl?.Trim() ?? string.Empty,
            DocumentUrl = req.DocumentUrl?.Trim() ?? string.Empty,
            Notes = req.Notes?.Trim() ?? string.Empty,
            DeliveryCondition = req.DeliveryCondition.Trim(),
            CapturedLatitude = req.CapturedLatitude,
            CapturedLongitude = req.CapturedLongitude,
            CapturedAt = DateTime.UtcNow,
            Status = "Draft",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.ProofOfDeliveries.Add(entity);
        await LogShipmentEventAsync(stop.TenantId, shipmentId, "PodCreated", $"POD draft created for stop {stop.SequenceNo}.", "Private", ct);
        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    [HttpPut("shipments/{shipmentId:guid}/pod/{podId:guid}")]
    public async Task<IActionResult> UpdatePod(Guid shipmentId, Guid podId, [FromBody] PodRequest req, CancellationToken ct)
    {
        var pod = await RequirePodAsync(shipmentId, podId, ct);
        if (pod is null) return NotFound();
        pod.RecipientName = req.RecipientName?.Trim() ?? pod.RecipientName;
        pod.RecipientPhone = req.RecipientPhone?.Trim() ?? pod.RecipientPhone;
        pod.SignatureUrl = req.SignatureUrl?.Trim() ?? pod.SignatureUrl;
        pod.PhotoUrl = req.PhotoUrl?.Trim() ?? pod.PhotoUrl;
        pod.DocumentUrl = req.DocumentUrl?.Trim() ?? pod.DocumentUrl;
        pod.Notes = req.Notes?.Trim() ?? pod.Notes;
        pod.DeliveryCondition = req.DeliveryCondition?.Trim() ?? pod.DeliveryCondition;
        pod.CapturedLatitude = req.CapturedLatitude ?? pod.CapturedLatitude;
        pod.CapturedLongitude = req.CapturedLongitude ?? pod.CapturedLongitude;
        pod.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(pod);
    }

    [HttpPost("shipments/{shipmentId:guid}/pod/{podId:guid}/submit")]
    public async Task<IActionResult> SubmitPod(Guid shipmentId, Guid podId, CancellationToken ct)
    {
        var pod = await RequirePodAsync(shipmentId, podId, ct);
        if (pod is null) return NotFound();
        if (string.IsNullOrWhiteSpace(pod.RecipientName)) return BadRequest(new { message = "Recipient name is required." });
        if (string.IsNullOrWhiteSpace(pod.DeliveryCondition)) return BadRequest(new { message = "Delivery condition is required." });

        pod.Status = "Submitted";
        pod.UpdatedAt = DateTime.UtcNow;
        await LogShipmentEventAsync(pod.TenantId, shipmentId, "PodSubmitted", "POD submitted for verification.", "Private", ct);
        await _db.SaveChangesAsync(ct);
        return Ok(pod);
    }

    [HttpPost("shipments/{shipmentId:guid}/pod/{podId:guid}/verify")]
    public async Task<IActionResult> VerifyPod(Guid shipmentId, Guid podId, CancellationToken ct)
    {
        var pod = await RequirePodAsync(shipmentId, podId, ct);
        if (pod is null) return NotFound();
        pod.Status = "Verified";
        pod.VerifiedAt = DateTime.UtcNow;
        pod.VerifiedByUserId = this.GetUserId();
        pod.UpdatedAt = DateTime.UtcNow;
        await LogShipmentEventAsync(pod.TenantId, shipmentId, "PodVerified", "POD verified.", "Private", ct);
        await _db.SaveChangesAsync(ct);
        return Ok(pod);
    }

    [HttpPost("shipments/{shipmentId:guid}/pod/{podId:guid}/reject")]
    public async Task<IActionResult> RejectPod(Guid shipmentId, Guid podId, [FromBody] PodRejectRequest req, CancellationToken ct)
    {
        var pod = await RequirePodAsync(shipmentId, podId, ct);
        if (pod is null) return NotFound();
        pod.Status = "Rejected";
        pod.Notes = req.Notes?.Trim() ?? pod.Notes;
        pod.UpdatedAt = DateTime.UtcNow;
        await LogShipmentEventAsync(pod.TenantId, shipmentId, "PodRejected", "POD rejected.", "Private", ct);
        await _db.SaveChangesAsync(ct);
        return Ok(pod);
    }

    [HttpPost("shipments/{shipmentId:guid}/tracking-link")]
    public async Task<IActionResult> CreateTrackingLink(Guid shipmentId, [FromBody] TrackingLinkRequest req, CancellationToken ct)
    {
        var shipment = await RequireShipmentAsync(shipmentId, ct);
        if (shipment is null) return NotFound();
        var token = string.IsNullOrWhiteSpace(req.Token) ? Guid.NewGuid().ToString("N") : req.Token.Trim();
        var link = new CustomerTrackingLink
        {
            TenantId = shipment.TenantId,
            ShipmentId = shipmentId,
            Token = token,
            ExpiresAtUtc = req.ExpiresAtUtc ?? DateTime.UtcNow.AddDays(7),
            SharedBy = User.Identity?.Name ?? "system",
            CreatedAtUtc = DateTime.UtcNow,
        };
        _db.CustomerTrackingLinks.Add(link);
        await LogShipmentEventAsync(shipment.TenantId, shipmentId, "TrackingLinkCreated", "Customer tracking link generated.", "Public", ct);
        await _db.SaveChangesAsync(ct);
        return Ok(link);
    }

    [HttpDelete("shipments/{shipmentId:guid}/tracking-link/{linkId:guid}")]
    public async Task<IActionResult> RevokeTrackingLink(Guid shipmentId, Guid linkId, CancellationToken ct)
    {
        var link = await _db.CustomerTrackingLinks.FirstOrDefaultAsync(x => x.Id == linkId && x.ShipmentId == shipmentId && x.TenantId == this.GetTenantId()!.Value, ct);
        if (link is null) return NotFound();
        link.IsRevoked = true;
        link.RevokedAtUtc = DateTime.UtcNow;
        link.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("driver/tasks")]
    public async Task<IActionResult> GetTasks([FromQuery] string? driverName, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var query = _db.DriverTasks.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(driverName)) query = query.Where(x => x.DriverName == driverName);
        return Ok(new { items = await query.OrderBy(x => x.DueAtUtc).ToListAsync(ct) });
    }

    [HttpGet("driver/tasks/{taskId:guid}")]
    public async Task<IActionResult> GetTask(Guid taskId, CancellationToken ct)
    {
        var task = await RequireTaskAsync(taskId, ct);
        return task is null ? NotFound() : Ok(task);
    }

    [HttpPost("driver/tasks/{taskId:guid}/arrive")]
    public async Task<IActionResult> ArriveTask(Guid taskId, CancellationToken ct)
    {
        var task = await RequireTaskAsync(taskId, ct);
        if (task is null) return NotFound();
        task.Status = "Arrived";
        task.UpdatedAtUtc = DateTime.UtcNow;
        await LogShipmentEventAsync(task.TenantId, task.ShipmentId, "DriverTaskArrived", $"{task.Title} arrived.", "Private", ct);
        await _db.SaveChangesAsync(ct);
        return Ok(task);
    }

    [HttpPost("driver/tasks/{taskId:guid}/complete")]
    public async Task<IActionResult> CompleteTask(Guid taskId, [FromBody] DriverTaskCompleteRequest req, CancellationToken ct)
    {
        var task = await RequireTaskAsync(taskId, ct);
        if (task is null) return NotFound();
        task.Status = "Completed";
        task.CompletedAtUtc = DateTime.UtcNow;
        task.Notes = req.Notes?.Trim() ?? task.Notes;
        task.UpdatedAtUtc = DateTime.UtcNow;
        await LogShipmentEventAsync(task.TenantId, task.ShipmentId, "DriverTaskCompleted", $"{task.Title} completed.", "Private", ct);
        await _db.SaveChangesAsync(ct);
        return Ok(task);
    }

    [HttpPost("driver/tasks/{taskId:guid}/pod")]
    public async Task<IActionResult> TaskPod(Guid taskId, [FromBody] PodRequest req, CancellationToken ct)
    {
        var task = await RequireTaskAsync(taskId, ct);
        if (task is null) return NotFound();
        var pod = await RequireShipmentPodByTaskAsync(task, ct);
        if (pod is null) return NotFound();
        pod.RecipientName = req.RecipientName?.Trim() ?? pod.RecipientName;
        pod.DeliveryCondition = req.DeliveryCondition?.Trim() ?? pod.DeliveryCondition;
        pod.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(pod);
    }

    [HttpPost("shipments/{shipmentId:guid}/mark-invoice-ready")]
    public async Task<IActionResult> MarkInvoiceReady(Guid shipmentId, [FromBody] InvoiceReadyRequest req, CancellationToken ct)
    {
        var shipment = await RequireShipmentAsync(shipmentId, ct);
        if (shipment is null) return NotFound();

        var podOk = await _db.ProofOfDeliveries.AnyAsync(x => x.TenantId == shipment.TenantId && x.ShipmentId == shipmentId && x.Status == "Verified", ct);
        if (!podOk && !req.Override)
            return BadRequest(new { message = "Shipment cannot be invoice-ready until a verified POD exists or an override is granted." });

        shipment.IsInvoiceReady = true;
        shipment.InvoiceReadyAtUtc = DateTime.UtcNow;
        shipment.InvoiceReadinessNotes = req.Notes?.Trim() ?? shipment.InvoiceReadinessNotes;
        shipment.UpdatedAtUtc = DateTime.UtcNow;
        await LogShipmentEventAsync(shipment.TenantId, shipmentId, "InvoiceReady", "Shipment marked invoice-ready.", "Private", ct);
        await _db.SaveChangesAsync(ct);
        return Ok(shipment);
    }

    [HttpGet("shipments/invoice-ready")]
    public async Task<IActionResult> GetInvoiceReady(CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var items = await _db.FleetShipments.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsInvoiceReady).OrderByDescending(x => x.InvoiceReadyAtUtc).ToListAsync(ct);
        return Ok(new { items });
    }

    [AllowAnonymous]
    [HttpGet("/api/public/shipments/track/{token}")]
    public async Task<IActionResult> PublicTrack(string token, CancellationToken ct)
    {
        var resolved = await ResolvePublicShipmentAsync(token, ct);
        return resolved is null ? NotFound() : Ok(resolved.Value.summary);
    }

    [AllowAnonymous]
    [HttpGet("/api/public/shipments/track/{token}/events")]
    public async Task<IActionResult> PublicEvents(string token, CancellationToken ct)
    {
        var resolved = await ResolvePublicShipmentAsync(token, ct);
        if (resolved is null) return NotFound();
        return Ok(new { items = resolved.Value.events });
    }

    [AllowAnonymous]
    [HttpGet("/api/public/shipments/track/{token}/pod")]
    public async Task<IActionResult> PublicPod(string token, CancellationToken ct)
    {
        var resolved = await ResolvePublicShipmentAsync(token, ct);
        return resolved is null ? NotFound() : Ok(new { items = resolved.Value.pod });
    }

    private async Task<FleetShipment?> RequireShipmentAsync(Guid shipmentId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        return await _db.FleetShipments.FirstOrDefaultAsync(x => x.Id == shipmentId && x.TenantId == tenantId, ct);
    }

    private async Task<ShipmentStop?> RequireStopAsync(Guid shipmentId, Guid stopId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        return await _db.ShipmentStops.FirstOrDefaultAsync(x => x.Id == stopId && x.ShipmentId == shipmentId && x.TenantId == tenantId, ct);
    }

    private async Task<ShipmentStop?> RequireStopByIdAsync(Guid shipmentId, Guid stopId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        return await _db.ShipmentStops.FirstOrDefaultAsync(x => x.Id == stopId && x.ShipmentId == shipmentId && x.TenantId == tenantId, ct);
    }

    private async Task<ProofOfDelivery?> RequirePodAsync(Guid shipmentId, Guid podId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        return await _db.ProofOfDeliveries.FirstOrDefaultAsync(x => x.Id == podId && x.ShipmentId == shipmentId && x.TenantId == tenantId, ct);
    }

    private async Task<DriverTask?> RequireTaskAsync(Guid taskId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        return await _db.DriverTasks.FirstOrDefaultAsync(x => x.Id == taskId && x.TenantId == tenantId, ct);
    }

    private async Task<ProofOfDelivery?> RequireShipmentPodByTaskAsync(DriverTask task, CancellationToken ct)
    {
        return await _db.ProofOfDeliveries.FirstOrDefaultAsync(x => x.TenantId == task.TenantId && x.ShipmentId == task.ShipmentId, ct);
    }

    private async Task LogShipmentEventAsync(Guid tenantId, Guid shipmentId, string eventType, string message, string visibility, CancellationToken ct)
    {
        _db.ShipmentEvents.Add(new ShipmentEvent
        {
            TenantId = tenantId,
            ShipmentId = shipmentId,
            EventType = eventType,
            Message = message,
            ActorName = User.Identity?.Name ?? "system",
            Visibility = visibility,
            OccurredAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task<(object summary, object[] events, object[] pod)?> ResolvePublicShipmentAsync(string token, CancellationToken ct)
    {
        var link = await _db.CustomerTrackingLinks.AsNoTracking().FirstOrDefaultAsync(x => x.Token == token, ct);
        if (link is null || link.IsRevoked || link.ExpiresAtUtc < DateTime.UtcNow) return null;
        var shipment = await _db.FleetShipments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == link.ShipmentId && x.TenantId == link.TenantId, ct);
        if (shipment is null) return null;

        var stops = await _db.ShipmentStops.AsNoTracking().Where(x => x.TenantId == link.TenantId && x.ShipmentId == shipment.Id).OrderBy(x => x.SequenceNo).Select(x => new
        {
            x.SequenceNo,
            x.StopType,
            x.LocationName,
            x.City,
            x.Status,
            x.PlannedArrivalAt,
            x.ActualArrivalAt,
            x.CompletedAt,
        }).ToListAsync(ct);

        var events = await _db.ShipmentEvents.AsNoTracking().Where(x => x.TenantId == link.TenantId && x.ShipmentId == shipment.Id && x.Visibility != "Private").OrderByDescending(x => x.OccurredAtUtc).Select(x => new
        {
            x.EventType,
            x.Message,
            x.OccurredAtUtc,
        }).ToListAsync(ct);

        var pod = await _db.ProofOfDeliveries.AsNoTracking().Where(x => x.TenantId == link.TenantId && x.ShipmentId == shipment.Id && x.Status != "Rejected").Select(x => new
        {
            x.RecipientName,
            x.DeliveryCondition,
            x.Status,
            x.CapturedAt,
            x.VerifiedAt,
            x.SignatureUrl,
            x.PhotoUrl,
            x.DocumentUrl,
        }).ToListAsync(ct);

        var summary = new
        {
            shipment.ShipmentNumber,
            shipment.Status,
            shipment.Origin,
            shipment.Destination,
            shipment.PickupScheduledAtUtc,
            shipment.DeliveredAtUtc,
            stops,
            publicEvents = events,
            pod,
        };

        return (summary, events.ToArray(), pod.ToArray());
    }
}

public record ShipmentStopRequest(
    string StopType,
    int SequenceNo,
    string LocationName,
    string? ContactName,
    string? ContactPhone,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? Region,
    string? PostalCode,
    string? Country,
    string? SaudiNationalAddressBuildingNo,
    string? SaudiNationalAddressAdditionalNo,
    string? SaudiNationalAddressDistrict,
    decimal? Latitude,
    decimal? Longitude,
    DateTime PlannedArrivalAt,
    string? Notes);

public record ArriveStopRequest(string? Notes);
public record CompleteStopRequest(string? Notes);
public record PodRequest(
    Guid StopId,
    string? RecipientName,
    string? RecipientPhone,
    string? SignatureUrl,
    string? PhotoUrl,
    string? DocumentUrl,
    string? Notes,
    string? DeliveryCondition,
    decimal? CapturedLatitude,
    decimal? CapturedLongitude);
public record PodRejectRequest(string? Notes);
public record TrackingLinkRequest(string? Token, DateTime? ExpiresAtUtc);
public record DriverTaskCompleteRequest(string? Notes);
public record InvoiceReadyRequest(bool Override, string? Notes);
