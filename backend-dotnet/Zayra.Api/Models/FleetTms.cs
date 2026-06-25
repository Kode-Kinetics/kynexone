using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

public class FleetShipment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string ShipmentNumber { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerSegment { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Status { get; set; } = "Booked";
    public string Priority { get; set; } = "Normal";
    public string Mode { get; set; } = "Road";
    public int PieceCount { get; set; }
    public decimal WeightKg { get; set; }
    public decimal VolumeCbm { get; set; }
    public decimal DeclaredValue { get; set; }
    public string CarrierName { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string VehicleNumber { get; set; } = string.Empty;
    public string RouteCode { get; set; } = string.Empty;
    public string PodStatus { get; set; } = "Pending";
    public string TemperatureRange { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? PickupScheduledAtUtc { get; set; }
    public DateTime? PickedUpAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class FleetVehicle : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string VehicleNumber { get; set; } = string.Empty;
    public string PlateNumber { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = "Available";
    public string DriverName { get; set; } = string.Empty;
    public decimal CapacityKg { get; set; }
    public decimal CapacityCbm { get; set; }
    public decimal CurrentLoadKg { get; set; }
    public decimal FuelLevelPercent { get; set; }
    public decimal OdometerKm { get; set; }
    public string HealthStatus { get; set; } = "Healthy";
    public bool IsRefrigerated { get; set; }
    public decimal? TemperatureCelsius { get; set; }
    public string LastKnownLocation { get; set; } = string.Empty;
    public DateTime? LastPingAtUtc { get; set; }
    public DateTime? LastServiceAtUtc { get; set; }
    public DateTime? NextServiceAtUtc { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public class FleetTrackingPoint : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string ShipmentNumber { get; set; } = string.Empty;
    public string VehicleNumber { get; set; } = string.Empty;
    public string LocationLabel { get; set; } = string.Empty;
    public string Status { get; set; } = "InTransit";
    public string GeofenceName { get; set; } = string.Empty;
    public string AlertType { get; set; } = string.Empty;
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public decimal SpeedKph { get; set; }
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EstimatedArrivalUtc { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public class FleetMaintenanceTicket : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string WorkOrderNumber { get; set; } = string.Empty;
    public string VehicleNumber { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = "Open";
    public string Priority { get; set; } = "Normal";
    public string VendorName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal EstimatedCost { get; set; }
    public decimal ActualCost { get; set; }
    public decimal DowntimeHours { get; set; }
    public DateTime OpenedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DueAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime? UpdatedAtUtc { get; set; }
}

public class FleetFuelEvent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string VehicleNumber { get; set; } = string.Empty;
    public string FuelCardNumber { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string EventType { get; set; } = "Fuel";
    public bool AnomalyFlag { get; set; }
    public decimal Liters { get; set; }
    public decimal Cost { get; set; }
    public decimal OdometerKm { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}
