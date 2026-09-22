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

    /// <summary>The UTC instant at which the tenant-local <paramref name="localDate"/> begins.</summary>
    public static DateTime LocalDayStartUtc(TimeZoneInfo timeZone, DateOnly localDate)
    {
        var localMidnight = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // A DST transition at midnight can make local 00:00 not exist; step forward until it does.
        while (timeZone.IsInvalidTime(localMidnight)) localMidnight = localMidnight.AddMinutes(30);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(localMidnight, timeZone), DateTimeKind.Utc);
    }
}
