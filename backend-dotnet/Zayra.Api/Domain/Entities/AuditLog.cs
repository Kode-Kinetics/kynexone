namespace Zayra.Api.Domain.Entities;

public class AuditLog : INullableTenantOwned, ICompanyScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    // Legal-entity dimension: set for company-scoped actions, null for tenant/group-level
    // actions (config semantics — null rows stay visible to every tenant user).
    public Guid? CompanyId { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? Metadata { get; set; }
    public string PreviousHash { get; set; } = string.Empty;
    public string EntryHash { get; set; } = string.Empty;
    public string HashAlgorithm { get; set; } = "SHA-256";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
