using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

public class SaudiRegionReference
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string CountryCode { get; set; } = "SA";
    public string CitiesJson { get; set; } = "[]";
    public int SortOrder { get; set; }
    public bool IsGccReady { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class FleetReadinessDocument : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Kind { get; set; } = "Compliance"; // Compliance / Transport / Driver
    public string SubjectType { get; set; } = string.Empty; // Branch / Carrier / Shipment / Driver / Vehicle / Customer / Location
    public string SubjectId { get; set; } = string.Empty;
    public string SubjectName { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNumber { get; set; } = string.Empty;
    public string TransportDocumentNo { get; set; } = string.Empty;
    public string PermitNo { get; set; } = string.Empty;
    public string VATNumber { get; set; } = string.Empty;
    public string CommercialRegistrationNo { get; set; } = string.Empty;
    public string CountryCode { get; set; } = "SA";
    public string NationalAddressBuildingNo { get; set; } = string.Empty;
    public string NationalAddressAdditionalNo { get; set; } = string.Empty;
    public string District { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string DocumentStatus { get; set; } = "Active";
    public string ExpiryStatus { get; set; } = "Healthy";
    public DateOnly? IssueDate { get; set; }
    public DateOnly? HijriExpiryDate { get; set; }
    public DateOnly? GregorianExpiryDate { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}
