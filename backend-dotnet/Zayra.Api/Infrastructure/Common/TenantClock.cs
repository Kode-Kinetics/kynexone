using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Common;

/// <summary>
/// <see cref="ITenantClock"/> over <c>tenant_localization_settings</c>. The rule is the one
/// <c>EffectiveChangeJobHandler.TenantLocalDateAsync</c> applies (stored timezone through
/// <see cref="TenantTimeZone"/>), with one addition: a Saudi tenant is always on Asia/Riyadh.
/// </summary>
public sealed class TenantClock : ITenantClock
{
    public const string RiyadhTimeZoneId = "Asia/Riyadh";

    private readonly ZayraDbContext _db;
    private readonly TimeProvider _time;

    public TenantClock(ZayraDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct)
    {
        var setting = await _db.TenantLocalizationSettings.AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .Select(l => new { l.DefaultTimezone, l.CountryCode })
            .FirstOrDefaultAsync(ct);
        return LocalDate(setting?.CountryCode, setting?.DefaultTimezone, _time.GetUtcNow().UtcDateTime);
    }

    /// <summary>The pure rule, exposed for tests: Saudi tenants on Riyadh time, everyone else on their stored zone.</summary>
    public static DateOnly LocalDate(string? countryCode, string? timeZoneId, DateTime utcNow)
    {
        var zone = IsSaudi(countryCode)
            ? TenantTimeZone.FromId(RiyadhTimeZoneId)
            : TenantTimeZone.FromId(timeZoneId);
        return TenantTimeZone.LocalDate(zone, utcNow);
    }

    private static bool IsSaudi(string? countryCode) =>
        countryCode is not null
        && (countryCode.Equals("SA", StringComparison.OrdinalIgnoreCase) || countryCode.Equals("SAU", StringComparison.OrdinalIgnoreCase));
}
