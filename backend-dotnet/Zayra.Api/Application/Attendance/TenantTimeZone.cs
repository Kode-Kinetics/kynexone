namespace Zayra.Api.Application.Attendance;

/// <summary>
/// THE rule for turning a tenant's stored <c>TenantLocalizationSetting.DefaultTimezone</c> into a
/// <see cref="TimeZoneInfo"/>, and for asking "what calendar day is it for this tenant right now".
///
/// <para>Extracted from <c>AttendanceService.ResolveTenantTimeZoneAsync</c> so the attendance
/// processor (which decides which <c>WorkDate</c> a punch lands on) and the dashboard (which asks
/// how many people are present on "today") cannot disagree about the day. Before this, the dashboard
/// used the UTC date: for a Riyadh tenant (UTC+3) every punch between 00:00 and 03:00 local was
/// written to a WorkDate the dashboard did not consider "today" yet, and a UTC-negative tenant saw
/// yesterday's attendance as today's for part of the evening.</para>
///
/// <para>Fails OPEN to UTC on an unset or unrecognised id, exactly as the processor always has, so a
/// typo in settings degrades to the previous behaviour rather than throwing.</para>
/// </summary>
public static class TenantTimeZone
{
    public static TimeZoneInfo FromId(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch { return TimeZoneInfo.Utc; }
    }

    /// <summary>The tenant-local calendar date at the UTC instant <paramref name="utcNow"/>.</summary>
    public static DateOnly LocalDate(TimeZoneInfo timeZone, DateTime utcNow) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), timeZone));

    /// <summary>
    /// The inclusive [first, last] calendar-day window of a named month. A month is a calendar fact,
    /// not an instant, so this takes no timezone: September 2026 is 1–30 September in every zone.
    ///
    /// <para>It exists because the leave calendar used to derive its window by serialising two local
    /// <c>Date</c> objects through <c>toISOString()</c> in the browser. For any UTC-positive tenant —
    /// i.e. every GCC tenant, AST +3 and GST +4 — local 1 Sept 00:00 became <c>2026-08-31T21:00Z</c>,
    /// so the fetched range ran 31 Aug → 29 Sept: <b>the last day of every month was never fetched</b>
    /// and the previous month's last day leaked in, while the grid keyed its cells off LOCAL
    /// components. That is the one-day shift users saw. Naming the month instead of serialising an
    /// instant makes the shift structurally impossible rather than fixed by convention.</para>
    /// </summary>
    public static (DateOnly From, DateOnly To) MonthWindow(int year, int month)
    {
        if (year < 1 || year > 9999) throw new ArgumentOutOfRangeException(nameof(year));
        if (month < 1 || month > 12) throw new ArgumentOutOfRangeException(nameof(month));
        var from = new DateOnly(year, month, 1);
        return (from, new DateOnly(year, month, DateTime.DaysInMonth(year, month)));
    }

    /// <summary>
    /// The month window the tenant is currently IN — "currently" resolved in the tenant's own zone,
    /// not in UTC. For a Riyadh tenant at 2026-10-01 01:00 local (2026-09-30 22:00 UTC) this is
    /// October; the UTC reading it replaces said September.
    /// </summary>
    public static (DateOnly From, DateOnly To) CurrentMonthWindow(TimeZoneInfo timeZone, DateTime utcNow)
    {
        var today = LocalDate(timeZone, utcNow);
        return MonthWindow(today.Year, today.Month);
    }

    /// <summary>The UTC instant at which the tenant-local <paramref name="localDate"/> begins.</summary>
    public static DateTime LocalDayStartUtc(TimeZoneInfo timeZone, DateOnly localDate)
    {
        var localMidnight = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // A DST transition at midnight can make local 00:00 not exist; step forward until it does.
        while (timeZone.IsInvalidTime(localMidnight)) localMidnight = localMidnight.AddMinutes(30);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(localMidnight, timeZone), DateTimeKind.Utc);
    }
}
