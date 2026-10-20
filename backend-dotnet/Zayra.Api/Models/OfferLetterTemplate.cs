using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

public class OfferLetterTemplate : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public string Name { get; set; } = "Offer letter";
    public string DesignJson { get; set; } = "{}";
    public int Version { get; set; } = 1;
    public bool Archived { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}
