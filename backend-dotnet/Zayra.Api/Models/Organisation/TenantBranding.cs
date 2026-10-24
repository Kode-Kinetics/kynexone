using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// ── Tenant Branding ───────────────────────────────────────────────────────────

public class TenantBranding : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string LogoUrl { get; set; } = string.Empty;
    public string PrimaryColor { get; set; } = "#2563EB";
    public string AccentColor { get; set; } = "#7C3AED";
    public string CompanyNameEn { get; set; } = string.Empty;
    public string CompanyNameAr { get; set; } = string.Empty;
    public string PortalTitle { get; set; } = string.Empty;
    public string FaviconUrl { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
