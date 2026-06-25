using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class Branch : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string AddressLine1 { get; set; } = string.Empty;
    public string AddressLine2 { get; set; } = string.Empty;
    public string VATNumber { get; set; } = string.Empty;
    public string CommercialRegistrationNo { get; set; } = string.Empty;
    public string NationalAddressBuildingNo { get; set; } = string.Empty;
    public string NationalAddressAdditionalNo { get; set; } = string.Empty;
    public string NationalAddressDistrict { get; set; } = string.Empty;
    public string NationalAddressRegion { get; set; } = string.Empty;
    public string NationalAddressPostalCode { get; set; } = string.Empty;
    public string NationalAddressCountry { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "America/New_York";
    public string LaborOfficeCode { get; set; } = string.Empty;
    public bool IsHeadOffice { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
}
