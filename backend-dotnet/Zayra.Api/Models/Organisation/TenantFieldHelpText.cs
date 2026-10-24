using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// ── Tenant Field Help Text (admin-customizable tooltips) ─────────────────────

public class TenantFieldHelpText : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Stable key identifying the form field, e.g. "employees.joining_date".</summary>
    public string FieldKey { get; set; } = string.Empty;
    /// <summary>Tenant-specific tooltip text shown instead of the built-in default.</summary>
    public string Text { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedBy { get; set; }
}
