namespace Zayra.Api.Application.Common;

/// <summary>
/// "What calendar day is it for this tenant." Every Release A date rule (renewal deadlines, freeze dates,
/// the Qiwa respond-by date, "effective from today or later") reads today from here, never from
/// <c>DateTime.UtcNow</c>: for a Riyadh tenant 00:00–03:00 local is still yesterday in UTC, and a notice date
/// computed in UTC is a day early or late for three hours every night.
///
/// <para>Saudi tenants always use <c>Asia/Riyadh</c> (one zone, no daylight saving), whatever the stored
/// timezone says — statutory dates are Saudi dates. Other tenants use their stored timezone through
/// <c>TenantTimeZone</c>, which fails open to UTC exactly as attendance does.</para>
/// </summary>
public interface ITenantClock
{
    Task<DateOnly> TodayAsync(Guid tenantId, CancellationToken ct);
}
