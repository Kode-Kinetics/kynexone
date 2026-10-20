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

    public async Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct) =>
        TenantTimeZone.LocalDate(await ZoneAsync(_db, tenantId, ct), _time.GetUtcNow().UtcDateTime);

    /// <summary>
    /// The tenant's time zone by THE rule: what <see cref="TodayAsync"/> uses, and what attendance dates punches by
    /// (<c>AttendanceWorkDate.TenantTimeZoneAsync</c> delegates here). One rule, so a Saudi tenant whose
    /// <c>default_timezone</c> is blank is not on Riyadh time for "today" and on UTC for its punches.
    /// </summary>
    public static async Task<TimeZoneInfo> ZoneAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        var setting = await db.TenantLocalizationSettings.AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .Select(l => new { l.DefaultTimezone, l.CountryCode })
            .FirstOrDefaultAsync(ct);
        return Zone(setting?.CountryCode, setting?.DefaultTimezone);
    }

    /// <summary>The pure rule: Saudi tenants on Riyadh time, everyone else on their stored zone (UTC when unset or unknown).</summary>
    public static TimeZoneInfo Zone(string? countryCode, string? timeZoneId) =>
        IsSaudi(countryCode) ? TenantTimeZone.FromId(RiyadhTimeZoneId) : TenantTimeZone.FromId(timeZoneId);

    /// <summary>The pure rule, exposed for tests: Saudi tenants on Riyadh time, everyone else on their stored zone.</summary>
    public static DateOnly LocalDate(string? countryCode, string? timeZoneId, DateTime utcNow) =>
        TenantTimeZone.LocalDate(Zone(countryCode, timeZoneId), utcNow);

    private static bool IsSaudi(string? countryCode) =>
        countryCode is not null
        && (countryCode.Equals("SA", StringComparison.OrdinalIgnoreCase) || countryCode.Equals("SAU", StringComparison.OrdinalIgnoreCase));
}
