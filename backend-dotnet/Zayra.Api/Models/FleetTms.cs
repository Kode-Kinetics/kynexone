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
    public bool IsInvoiceReady { get; set; }
    public DateTime? InvoiceReadyAtUtc { get; set; }
    public string InvoiceReadinessNotes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? PickupScheduledAtUtc { get; set; }
    public DateTime? PickedUpAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class ShipmentStop : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ShipmentId { get; set; }
    public string StopType { get; set; } = "Pickup";
    public int SequenceNo { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public string ContactName { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string AddressLine1 { get; set; } = string.Empty;
    public string AddressLine2 { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string SaudiNationalAddressBuildingNo { get; set; } = string.Empty;
    public string SaudiNationalAddressAdditionalNo { get; set; } = string.Empty;
    public string SaudiNationalAddressDistrict { get; set; } = string.Empty;
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public DateTime PlannedArrivalAt { get; set; }
    public DateTime? ActualArrivalAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string Status { get; set; } = "Planned";
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ProofOfDelivery : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ShipmentId { get; set; }
    public Guid StopId { get; set; }
    public Guid? CapturedByUserId { get; set; }
    public Guid? DriverId { get; set; }
    public Guid? VehicleId { get; set; }
    public string RecipientName { get; set; } = string.Empty;
    public string RecipientPhone { get; set; } = string.Empty;
    public string SignatureUrl { get; set; } = string.Empty;
    public string PhotoUrl { get; set; } = string.Empty;
    public string DocumentUrl { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string DeliveryCondition { get; set; } = "Good";
    public decimal? CapturedLatitude { get; set; }
    public decimal? CapturedLongitude { get; set; }
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    public DateTime? VerifiedAt { get; set; }
    public Guid? VerifiedByUserId { get; set; }
    public string Status { get; set; } = "Draft";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class CustomerTrackingLink : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ShipmentId { get; set; }
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddDays(7);
    public bool IsRevoked { get; set; }
    public string SharedBy { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class ShipmentEvent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ShipmentId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string ActorName { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public string Visibility { get; set; } = "Private";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public class DriverTask : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ShipmentId { get; set; }
    public Guid? StopId { get; set; }
    public string TaskType { get; set; } = "Pickup";
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = "Open";
    public string DriverName { get; set; } = string.Empty;
    public string VehicleNumber { get; set; } = string.Empty;
    public DateTime DueAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public class Carrier : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string Region { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
    public decimal OnTimeScore { get; set; }
    public decimal DamageScore { get; set; }
    public decimal CostScore { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public class CarrierContact : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CarrierId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public class CarrierPerformanceScore : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CarrierId { get; set; }
    public decimal OnTimePct { get; set; }
    public decimal DamagePct { get; set; }
    public decimal AcceptancePct { get; set; }
    public decimal OverallScore { get; set; }
    public DateTime ScoredAtUtc { get; set; } = DateTime.UtcNow;
    public string Notes { get; set; } = string.Empty;
}

public class ShipmentCarrierAssignment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ShipmentId { get; set; }
    public Guid CarrierId { get; set; }
    public string Status { get; set; } = "Assigned";
    public decimal QuotedAmount { get; set; }
    public decimal AgreedAmount { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime AssignedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public class BookingRequest : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string RequestNumber { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public string Status { get; set; } = "Open";
    public decimal EstimatedWeightKg { get; set; }
    public decimal EstimatedVolumeCbm { get; set; }
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public string Notes { get; set; } = string.Empty;
}

public class QuoteRequest : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string QuoteNumber { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";
    public decimal EstimatedAmount { get; set; }
    public decimal MarginPct { get; set; }
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public string Notes { get; set; } = string.Empty;
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
