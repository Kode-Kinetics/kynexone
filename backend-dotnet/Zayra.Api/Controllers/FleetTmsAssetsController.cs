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
public class FleetTmsAssetsController : ControllerBase
{
    private readonly ZayraDbContext _db;

    public FleetTmsAssetsController(ZayraDbContext db) => _db = db;

    [HttpGet("assets/types")]
    public async Task<IActionResult> GetAssetTypes(CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var tenantId = RequireTenant();
        var items = await _db.AssetTypes.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);
        return Ok(new { items });
    }

    [HttpPost("assets/types")]
    public async Task<IActionResult> CreateAssetType([FromBody] AssetTypeRequest req, CancellationToken ct)
    {
        if (!CanManage()) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Code)) return BadRequest(new { message = "Asset type code is required." });
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "Asset type name is required." });

        var tenantId = RequireTenant();
        var entity = new AssetType
        {
            TenantId = tenantId,
            Code = req.Code.Trim(),
            Name = req.Name.Trim(),
            Description = req.Description?.Trim() ?? string.Empty,
            IsReturnable = req.IsReturnable ?? true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        _db.AssetTypes.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    [HttpGet("assets")]
    public async Task<IActionResult> GetAssets(CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var tenantId = RequireTenant();

        var items = await _db.Assets.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.LastSeenAtUtc)
            .ThenBy(x => x.AssetTag)
            .Select(x => new
            {
                x.Id,
                x.AssetTypeId,
                assetTypeCode = _db.AssetTypes.Where(t => t.Id == x.AssetTypeId).Select(t => t.Code).FirstOrDefault(),
                assetTypeName = _db.AssetTypes.Where(t => t.Id == x.AssetTypeId).Select(t => t.Name).FirstOrDefault(),
                x.AssetTag,
                x.Name,
                x.Status,
                x.CurrentLocation,
                x.Condition,
                x.IsReturnable,
                x.Quantity,
                x.UnitOfMeasure,
                x.Notes,
                x.LastSeenAtUtc,
                createdAtUtc = x.CreatedAtUtc,
                assignmentCount = _db.AssetAssignments.Count(a => a.AssetId == x.Id && a.TenantId == tenantId),
            })
            .ToListAsync(ct);

        return Ok(new { items });
    }

    [HttpGet("assets/{id:guid}")]
    public async Task<IActionResult> GetAsset(Guid id, CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var asset = await RequireAssetAsync(id, ct);
        if (asset is null) return NotFound();

        var tenantId = RequireTenant();
        var assignments = await _db.AssetAssignments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.AssetId == id)
            .OrderByDescending(x => x.AssignedAtUtc)
            .Select(x => new
            {
                x.Id,
                x.AssetId,
                x.ShipmentId,
                shipmentNumber = _db.FleetShipments.Where(s => s.Id == x.ShipmentId).Select(s => s.ShipmentNumber).FirstOrDefault(),
                x.CarrierId,
                carrierName = _db.Carriers.Where(c => c.Id == x.CarrierId).Select(c => c.Name).FirstOrDefault(),
                x.AssigneeType,
                x.AssigneeName,
                x.Quantity,
                x.Status,
                x.AssignedAtUtc,
                x.ReleasedAtUtc,
                x.Notes,
            })
            .ToListAsync(ct);

        var events = await LoadEventsAsync(tenantId, id, ct);
        return Ok(new { asset, assignments, events });
    }

    [HttpPost("assets")]
    public async Task<IActionResult> CreateAsset([FromBody] AssetRequest req, CancellationToken ct)
    {
        if (!CanManage()) return Forbid();
        if (string.IsNullOrWhiteSpace(req.AssetTag)) return BadRequest(new { message = "Asset tag is required." });
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "Asset name is required." });
        if (req.AssetTypeId == Guid.Empty) return BadRequest(new { message = "Asset type is required." });

        var tenantId = RequireTenant();
        var assetType = await _db.AssetTypes.FirstOrDefaultAsync(x => x.Id == req.AssetTypeId && x.TenantId == tenantId, ct);
        if (assetType is null) return NotFound(new { message = "Asset type not found for this tenant." });

        var entity = new Asset
        {
            TenantId = tenantId,
            AssetTypeId = assetType.Id,
            AssetTag = req.AssetTag.Trim(),
            Name = req.Name.Trim(),
            Status = req.Status?.Trim() ?? "Available",
            CurrentLocation = req.CurrentLocation?.Trim() ?? string.Empty,
            Condition = req.Condition?.Trim() ?? "Good",
            IsReturnable = req.IsReturnable ?? true,
            Quantity = req.Quantity ?? 1,
            UnitOfMeasure = req.UnitOfMeasure?.Trim() ?? "Each",
            Notes = req.Notes?.Trim() ?? string.Empty,
            LastSeenAtUtc = req.LastSeenAtUtc,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        _db.Assets.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    [HttpPut("assets/{id:guid}")]
    public async Task<IActionResult> UpdateAsset(Guid id, [FromBody] AssetRequest req, CancellationToken ct)
    {
        if (!CanManage()) return Forbid();
        var asset = await RequireAssetAsync(id, ct);
        if (asset is null) return NotFound();

        var tenantId = RequireTenant();
        if (req.AssetTypeId != Guid.Empty)
        {
            var assetType = await _db.AssetTypes.FirstOrDefaultAsync(x => x.Id == req.AssetTypeId && x.TenantId == tenantId, ct);
            if (assetType is null) return NotFound(new { message = "Asset type not found for this tenant." });
            asset.AssetTypeId = assetType.Id;
        }

        asset.AssetTag = req.AssetTag?.Trim() ?? asset.AssetTag;
        asset.Name = req.Name?.Trim() ?? asset.Name;
        asset.Status = req.Status?.Trim() ?? asset.Status;
        asset.CurrentLocation = req.CurrentLocation?.Trim() ?? asset.CurrentLocation;
        asset.Condition = req.Condition?.Trim() ?? asset.Condition;
        asset.IsReturnable = req.IsReturnable ?? asset.IsReturnable;
        asset.Quantity = req.Quantity ?? asset.Quantity;
        asset.UnitOfMeasure = req.UnitOfMeasure?.Trim() ?? asset.UnitOfMeasure;
        asset.Notes = req.Notes?.Trim() ?? asset.Notes;
        asset.LastSeenAtUtc = req.LastSeenAtUtc ?? asset.LastSeenAtUtc;
        asset.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(asset);
    }

    [HttpPost("assets/{id:guid}/assign")]
    public async Task<IActionResult> AssignAsset(Guid id, [FromBody] AssetAssignmentRequest req, CancellationToken ct)
    {
        if (!CanManage()) return Forbid();
        var tenantId = RequireTenant();
        var asset = await RequireAssetAsync(id, ct);
        if (asset is null) return NotFound();

        var entity = new AssetAssignment
        {
            TenantId = tenantId,
            AssetId = id,
            ShipmentId = req.ShipmentId,
            CarrierId = req.CarrierId,
            AssigneeType = string.IsNullOrWhiteSpace(req.AssigneeType) ? (req.ShipmentId.HasValue ? "Shipment" : "Warehouse") : req.AssigneeType.Trim(),
            AssigneeName = req.AssigneeName?.Trim() ?? (req.ShipmentId.HasValue ? req.ShipmentId.Value.ToString() : "Warehouse"),
            Quantity = req.Quantity ?? asset.Quantity,
            Status = req.Status?.Trim() ?? "Assigned",
            AssignedAtUtc = DateTime.UtcNow,
            ReleasedAtUtc = req.ReleasedAtUtc,
            Notes = req.Notes?.Trim() ?? string.Empty,
        };

        asset.Status = "Assigned";
        asset.CurrentLocation = req.CurrentLocation?.Trim() ?? asset.CurrentLocation;
        asset.LastSeenAtUtc = DateTime.UtcNow;
        asset.UpdatedAtUtc = DateTime.UtcNow;

        _db.AssetAssignments.Add(entity);
        _db.AssetEvents.Add(new AssetEvent
        {
            TenantId = tenantId,
            AssetId = id,
            EventType = "Assigned",
            Quantity = entity.Quantity,
            Location = asset.CurrentLocation,
            ActorName = User.Identity?.Name ?? "system",
            OccurredAtUtc = DateTime.UtcNow,
            Notes = entity.Notes,
        });

        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    [HttpPost("assets/{id:guid}/check-in")]
    public async Task<IActionResult> CheckInAsset(Guid id, [FromBody] AssetMovementRequest req, CancellationToken ct)
    {
        if (!CanManage()) return Forbid();
        var tenantId = RequireTenant();
        var asset = await RequireAssetAsync(id, ct);
        if (asset is null) return NotFound();

        asset.Status = "Available";
        asset.CurrentLocation = req.Location?.Trim() ?? asset.CurrentLocation;
        asset.Condition = req.Condition?.Trim() ?? asset.Condition;
        asset.LastSeenAtUtc = DateTime.UtcNow;
        asset.UpdatedAtUtc = DateTime.UtcNow;

        var activeAssignment = await _db.AssetAssignments
            .Where(x => x.TenantId == tenantId && x.AssetId == id && x.ReleasedAtUtc == null)
            .OrderByDescending(x => x.AssignedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (activeAssignment is not null)
        {
            activeAssignment.Status = "Returned";
            activeAssignment.ReleasedAtUtc = DateTime.UtcNow;
            activeAssignment.Notes = string.IsNullOrWhiteSpace(req.Notes) ? activeAssignment.Notes : req.Notes.Trim();
        }

        _db.AssetEvents.Add(new AssetEvent
        {
            TenantId = tenantId,
            AssetId = id,
            EventType = "CheckIn",
            Quantity = asset.Quantity,
            Location = asset.CurrentLocation,
            ActorName = User.Identity?.Name ?? "system",
            OccurredAtUtc = DateTime.UtcNow,
            Notes = req.Notes?.Trim() ?? "Asset checked back into inventory.",
        });

        await _db.SaveChangesAsync(ct);
        return Ok(asset);
    }

    [HttpPost("assets/{id:guid}/check-out")]
    public async Task<IActionResult> CheckOutAsset(Guid id, [FromBody] AssetMovementRequest req, CancellationToken ct)
    {
        if (!CanManage()) return Forbid();
        var tenantId = RequireTenant();
        var asset = await RequireAssetAsync(id, ct);
        if (asset is null) return NotFound();

        asset.Status = "InUse";
        asset.CurrentLocation = req.Location?.Trim() ?? asset.CurrentLocation;
        asset.Condition = req.Condition?.Trim() ?? asset.Condition;
        asset.LastSeenAtUtc = DateTime.UtcNow;
        asset.UpdatedAtUtc = DateTime.UtcNow;

        var assignment = new AssetAssignment
        {
            TenantId = tenantId,
            AssetId = id,
            ShipmentId = req.ShipmentId,
            CarrierId = req.CarrierId,
            AssigneeType = string.IsNullOrWhiteSpace(req.AssigneeType) ? (req.ShipmentId.HasValue ? "Shipment" : "Dispatch") : req.AssigneeType.Trim(),
            AssigneeName = req.AssigneeName?.Trim() ?? (req.ShipmentId.HasValue ? req.ShipmentId.Value.ToString() : "Dispatch"),
            Quantity = req.Quantity ?? asset.Quantity,
            Status = "CheckedOut",
            AssignedAtUtc = DateTime.UtcNow,
            Notes = req.Notes?.Trim() ?? "Asset checked out.",
        };

        _db.AssetAssignments.Add(assignment);
        _db.AssetEvents.Add(new AssetEvent
        {
            TenantId = tenantId,
            AssetId = id,
            EventType = "CheckOut",
            Quantity = assignment.Quantity,
            Location = asset.CurrentLocation,
            ActorName = User.Identity?.Name ?? "system",
            OccurredAtUtc = DateTime.UtcNow,
            Notes = assignment.Notes,
        });

        await _db.SaveChangesAsync(ct);
        return Ok(asset);
    }

    [HttpGet("assets/{id:guid}/events")]
    public async Task<IActionResult> GetEvents(Guid id, CancellationToken ct)
    {
        if (!CanRead()) return Forbid();
        var tenantId = RequireTenant();
        if (await RequireAssetAsync(id, ct) is null) return NotFound();
        var items = await LoadEventsAsync(tenantId, id, ct);
        return Ok(new { items });
    }

    [HttpPost("assets/scan")]
    public async Task<IActionResult> ScanAsset([FromBody] AssetScanRequest req, CancellationToken ct)
    {
        if (!CanManage()) return Forbid();
        var tenantId = RequireTenant();

        object result;
        var isRfid = string.Equals(req.Kind, "RFID", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(req.TagId);
        if (isRfid)
        {
            var entity = new RfidEvent
            {
                TenantId = tenantId,
                AssetId = req.AssetId,
                ShipmentId = req.ShipmentId,
                TagId = req.TagId?.Trim() ?? req.ScannedValue?.Trim() ?? string.Empty,
                ReaderId = req.ReaderId?.Trim() ?? "RFID-GATE",
                EventType = req.EventType?.Trim() ?? "Read",
                Status = req.Status?.Trim() ?? "Captured",
                RecordedAtUtc = DateTime.UtcNow,
                Notes = req.Notes?.Trim() ?? string.Empty,
            };
            _db.RfidEvents.Add(entity);
            result = entity;
        }
        else
        {
            var entity = new BarcodeScanEvent
            {
                TenantId = tenantId,
                AssetId = req.AssetId,
                ShipmentId = req.ShipmentId,
                ScannedValue = req.ScannedValue?.Trim() ?? string.Empty,
                ScannerId = req.ScannerId?.Trim() ?? "BARCODE-SCAN",
                EventType = req.EventType?.Trim() ?? "Scan",
                Status = req.Status?.Trim() ?? "Captured",
                RecordedAtUtc = DateTime.UtcNow,
                Notes = req.Notes?.Trim() ?? string.Empty,
            };
            _db.BarcodeScanEvents.Add(entity);
            result = entity;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(result);
    }

    private async Task<List<object>> LoadEventsAsync(Guid tenantId, Guid assetId, CancellationToken ct)
    {
        var assetEvents = await _db.AssetEvents.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.AssetId == assetId)
            .Select(x => new
            {
                id = x.Id,
                type = "AssetEvent",
                x.EventType,
                x.Quantity,
                location = x.Location,
                actorName = x.ActorName,
                occurredAtUtc = x.OccurredAtUtc,
                notes = x.Notes,
            })
            .ToListAsync(ct);

        var barcodeEvents = await _db.BarcodeScanEvents.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.AssetId == assetId)
            .Select(x => new
            {
                id = x.Id,
                type = "BarcodeScan",
                x.EventType,
                quantity = 1m,
                location = x.ScannerId,
                actorName = x.ScannerId,
                occurredAtUtc = x.RecordedAtUtc,
                notes = x.Notes,
            })
            .ToListAsync(ct);

        var rfidEvents = await _db.RfidEvents.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.AssetId == assetId)
            .Select(x => new
            {
                id = x.Id,
                type = "RfidEvent",
                x.EventType,
                quantity = 1m,
                location = x.ReaderId,
                actorName = x.ReaderId,
                occurredAtUtc = x.RecordedAtUtc,
                notes = x.Notes,
            })
            .ToListAsync(ct);

        var assignments = await _db.AssetAssignments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.AssetId == assetId)
            .Select(x => new
            {
                id = x.Id,
                type = "Assignment",
                eventType = x.Status,
                x.AssigneeType,
                x.AssigneeName,
                x.Quantity,
                location = x.AssigneeName,
                actorName = x.AssigneeType,
                occurredAtUtc = x.AssignedAtUtc,
                x.ReleasedAtUtc,
                notes = x.Notes,
            })
            .ToListAsync(ct);

        return assetEvents.Cast<object>()
            .Concat(barcodeEvents.Cast<object>())
            .Concat(rfidEvents.Cast<object>())
            .Concat(assignments.Cast<object>())
            .ToList();
    }

    private async Task<Asset?> RequireAssetAsync(Guid id, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        return await _db.Assets.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
    }

    private Guid RequireTenant() => Guid.Parse(User.FindFirst("tenant_id")?.Value ?? throw new UnauthorizedAccessException("Tenant claim missing."));
    private bool CanRead() => HasPermission("fleet_tms.assets.view") || HasPermission("fleet.read");
    private bool CanManage() => HasPermission("fleet_tms.assets.manage") || HasPermission("fleet.write");
    private bool HasPermission(string permission) => User.Claims.Any(x => x.Type == "permission" && string.Equals(x.Value, permission, StringComparison.OrdinalIgnoreCase));
}

public record AssetRequest(
    Guid AssetTypeId,
    string? AssetTag,
    string? Name,
    string? Status,
    string? CurrentLocation,
    string? Condition,
    bool? IsReturnable,
    decimal? Quantity,
    string? UnitOfMeasure,
    string? Notes,
    DateTime? LastSeenAtUtc);

public record AssetTypeRequest(
    string? Code,
    string? Name,
    string? Description,
    bool? IsReturnable);

public record AssetAssignmentRequest(
    Guid? ShipmentId,
    Guid? CarrierId,
    string? AssigneeType,
    string? AssigneeName,
    decimal? Quantity,
    string? Status,
    string? CurrentLocation,
    DateTime? ReleasedAtUtc,
    string? Notes);

public record AssetMovementRequest(
    string? Location,
    string? Condition,
    string? Notes,
    Guid? ShipmentId,
    Guid? CarrierId,
    string? AssigneeType,
    string? AssigneeName,
    decimal? Quantity);

public record AssetScanRequest(
    string? Kind,
    Guid? AssetId,
    Guid? ShipmentId,
    string? ScannedValue,
    string? TagId,
    string? ScannerId,
    string? ReaderId,
    string? EventType,
    string? Status,
    string? Notes);
