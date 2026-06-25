using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

public class AssetType : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsReturnable { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public class Asset : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AssetTypeId { get; set; }
    public string AssetTag { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Available";
    public string CurrentLocation { get; set; } = string.Empty;
    public string Condition { get; set; } = "Good";
    public bool IsReturnable { get; set; } = true;
    public decimal Quantity { get; set; } = 1;
    public string UnitOfMeasure { get; set; } = "Each";
    public string Notes { get; set; } = string.Empty;
    public DateTime? LastSeenAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public class AssetAssignment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AssetId { get; set; }
    public Guid? ShipmentId { get; set; }
    public Guid? CarrierId { get; set; }
    public string AssigneeType { get; set; } = "Shipment";
    public string AssigneeName { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public string Status { get; set; } = "Assigned";
    public DateTime AssignedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReleasedAtUtc { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public class AssetEvent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AssetId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public string Location { get; set; } = string.Empty;
    public string ActorName { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public string Notes { get; set; } = string.Empty;
}

public class BarcodeScanEvent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? ShipmentId { get; set; }
    public string ScannedValue { get; set; } = string.Empty;
    public string ScannerId { get; set; } = string.Empty;
    public string EventType { get; set; } = "Scan";
    public string Status { get; set; } = "Captured";
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
    public string Notes { get; set; } = string.Empty;
}

public class RfidEvent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? ShipmentId { get; set; }
    public string TagId { get; set; } = string.Empty;
    public string ReaderId { get; set; } = string.Empty;
    public string EventType { get; set; } = "Read";
    public string Status { get; set; } = "Captured";
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
    public string Notes { get; set; } = string.Empty;
}
