using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

// status lifecycle: draft → active ↔ archived (archived = old version superseded by an edit)
// version chain: ParentTemplateId links to the Id of the template this was created from
public class PayslipTemplate : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public int Version { get; set; } = 1;
    public string Status { get; set; } = "draft";         // draft | active | archived
    public string BrandingJson { get; set; } = "{}";     // PayslipBrandingConfig
    public string LayoutJson { get; set; } = "{}";       // PayslipLayoutConfig
    public Guid? ParentTemplateId { get; set; }           // previous version's Id
    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}
