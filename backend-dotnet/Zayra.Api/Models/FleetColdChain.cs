using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

public class TemperatureZone : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal MinCelsius { get; set; }
    public decimal MaxCelsius { get; set; }
    public string Color { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public class TemperatureDevice : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string DeviceCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid? ZoneId { get; set; }
    public Guid? ShipmentId { get; set; }
    public string VehicleNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public decimal LastReportedTemperatureCelsius { get; set; }
    public decimal BatteryPercent { get; set; }
    public DateTime? LastPingAtUtc { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public class TemperatureReading : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? ShipmentId { get; set; }
    public Guid? ZoneId { get; set; }
    public decimal TemperatureCelsius { get; set; }
    public decimal? HumidityPercent { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string Source { get; set; } = "Sensor";
    public string Status { get; set; } = "Normal";
    public string Notes { get; set; } = string.Empty;
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class TemperatureAlert : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? ShipmentId { get; set; }
    public Guid ReadingId { get; set; }
    public string AlertType { get; set; } = "TemperatureBreach";
    public string Severity { get; set; } = "High";
    public string Status { get; set; } = "Open";
    public decimal ThresholdMin { get; set; }
    public decimal ThresholdMax { get; set; }
    public decimal MeasuredTemperature { get; set; }
    public DateTime TriggeredAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAtUtc { get; set; }
    public string ResolvedBy { get; set; } = string.Empty;
    public string ResolutionNotes { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public class ColdChainReport : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ShipmentId { get; set; }
    public string ShipmentNumber { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public decimal CompliancePercent { get; set; }
    public decimal MinTemperatureCelsius { get; set; }
    public decimal MaxTemperatureCelsius { get; set; }
    public int TotalReadings { get; set; }
    public int BreachCount { get; set; }
    public string SummaryJson { get; set; } = "{}";
    public string Notes { get; set; } = string.Empty;
}

public class RefrigerationUnitHealth : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string VehicleNumber { get; set; } = string.Empty;
    public string UnitSerial { get; set; } = string.Empty;
    public string Status { get; set; } = "Healthy";
    public decimal CompressorHours { get; set; }
    public DateTime? LastServiceAtUtc { get; set; }
    public DateTime? NextServiceDueAtUtc { get; set; }
    public int TemperatureDeviationCount { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}
