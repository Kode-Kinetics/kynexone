using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

// ── Tenant Localization ───────────────────────────────────────────────────────

public class TenantLocalizationSetting : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string DefaultLanguage { get; set; } = "en"; // en, ar
    public bool RtlEnabled { get; set; }
    public string CalendarSystem { get; set; } = "Gregorian"; // Gregorian, Hijri
    public string DefaultTimezone { get; set; } = "America/New_York";
    public string DateFormat { get; set; } = "MM/DD/YYYY";
    public string CurrencyCode { get; set; } = "USD";
    public string CountryCode { get; set; } = "US";
    public string WeekStartDay { get; set; } = "Monday";
    public string WorkWeek { get; set; } = "Mon-Fri"; // Sun-Thu, Mon-Fri, Mon-Sat
    public bool HijriDatesEnabled { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
