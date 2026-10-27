using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class CostCenter : ITenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Optional Arabic name. Readers fall back to <see cref="Name"/> when it is empty.</summary>
    public string? NameAr { get; set; }
    public Guid? CompanyId { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Cost-accounting M1: the parent cost centre in the same tenant (a roll-up tree for the small-cell roll-up).
    /// Database-enforced: composite FK (tenant_id, parent_id), no self-parent, no cycle (trigger in M2).</summary>
    public Guid? ParentId { get; set; }
    /// <summary>Cost-accounting M1: the cost centre's functional class — COGS, SELLING, G_AND_A, R_AND_D, OPERATIONS or
    /// CAPITALISABLE. CAPITALISABLE is reserved: storable, but refused for posting in v1. Null = unclassified.</summary>
    public string? FunctionalClass { get; set; }
    /// <summary>Cost-accounting M1: the client's ERP cost-centre value, sent on journal exports when set (else <see cref="Code"/>).
    /// Unique per company among live rows.</summary>
    public string? GlSegment { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
}
