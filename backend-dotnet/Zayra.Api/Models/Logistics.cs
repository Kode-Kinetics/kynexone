using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

public class DispatchOrder : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerSegment { get; set; } = string.Empty;
    public string SalesChannel { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Area { get; set; } = string.Empty;
    public string Status { get; set; } = "Queued"; // Queued/Picking/Packed/Dispatched/InTransit/Delivered/Exception/Returned
    public string Priority { get; set; } = "Normal"; // Low/Normal/High/Critical
    public int ItemCount { get; set; }
    public decimal OrderValue { get; set; }
    public string RouteCode { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string VehicleNumber { get; set; } = string.Empty;
    public string DispatchNotes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? PromisedAtUtc { get; set; }
    public DateTime? DispatchedAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class DeliveryRoute : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string RouteCode { get; set; } = string.Empty;
    public string Hub { get; set; } = string.Empty;
    public string Territory { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string VehicleNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "Planned"; // Planned/Ready/Active/Delayed/Closed
    public int PlannedStops { get; set; }
    public int CompletedStops { get; set; }
    public decimal DistanceKm { get; set; }
    public decimal CompletionPercent { get; set; }
    public string CurrentStop { get; set; } = string.Empty;
    public string NextStop { get; set; } = string.Empty;
    public DateTime PlannedForDate { get; set; } = DateTime.UtcNow.Date;
    public DateTime DepartureTimeUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EtaCompleteUtc { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public class LastMileStop : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string RouteCode { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string AddressLine { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Status { get; set; } = "OutForDelivery"; // OutForDelivery/Attempted/Delivered/Rescheduled/Failed
    public string ProofStatus { get; set; } = "None"; // None/POD/OTP/Signature
    public string RecipientName { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public string RiderName { get; set; } = string.Empty;
    public string TimeWindow { get; set; } = string.Empty;
    public DateTime EtaUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DeliveredAtUtc { get; set; }
    public string ExceptionReason { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}
