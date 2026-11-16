using Microsoft.EntityFrameworkCore;
using System.Globalization;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// What a NEW hire starts with when the form states nothing. Today: the company's default notice period, which is the existing configurable
/// country rule <c>notice_period_days</c> (seeded per tenant; 60 for Saudi Arabia) for the employing company's country. It is applied at CREATE only:
/// an existing employee with a blank notice period is never back-filled (notice feeds exit notice pay and the final settlement, so a value must be
/// stated by someone, not appear behind HR's back), and once stored a defaulted value is the same number as a typed one.
/// </summary>
public static class EmployeeHireDefaults
{
    public const string NoticeRuleKey = "notice_period_days";
    public const int MaxNoticeDays = 365;

    /// <summary>The notice period in force for <paramref name="countryCode"/> in this tenant, or null when none is set, it is not a whole number of days
    /// from 0 to <see cref="MaxNoticeDays"/>, or no rule is in force. Never guessed.</summary>
    public static async Task<int?> NoticePeriodDaysAsync(ZayraDbContext db, Guid tenantId, string? countryCode, CancellationToken ct, DateTime? asOfUtc = null)
    {
        var iso = (CountryCodeStandard.NormalizeToIso2(countryCode) ?? countryCode ?? string.Empty).Trim().ToUpperInvariant();
        if (iso.Length == 0) return null;
        var now = asOfUtc ?? DateTime.UtcNow;
        var rows = await ScopedBypass.TenantWide(db.CountryPayrollRules, tenantId,
                "The notice-period default for a new hire: the tenant's own country rule, read for the employing company's country. Nothing is written.")
            .AsNoTracking()
            .Where(r => r.RuleKey == NoticeRuleKey && r.EffectiveFrom <= now && (r.EffectiveTo == null || r.EffectiveTo > now))
            .ToListAsync(ct);
        var pick = rows
            .Where(r => string.Equals((CountryCodeStandard.NormalizeToIso2(r.CountryCode) ?? r.CountryCode ?? string.Empty).Trim(), iso, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefault();
        return pick is not null && int.TryParse(pick.RuleValue?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days >= 0 && days <= MaxNoticeDays
            ? days : null;
    }
}
