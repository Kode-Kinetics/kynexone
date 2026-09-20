using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Timesheets;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Timesheets;

/// <summary>
/// W2-G — tenant timesheet settings, stored as <see cref="SystemSetting"/> rows under
/// Category = "Timesheets". Missing rows read as the defaults below, so a tenant provisioned
/// before this module works without any migration of settings data.
/// </summary>
public sealed class TimesheetSettingsService : ITimesheetSettingsService
{
    public const string KeyPeriodType = "PeriodType";
    public const string KeyWeekStartDay = "WeekStartDay";
    public const string KeyTolerance = "OverAllocationToleranceMinutes";
    public const string KeyBlocksSubmit = "OverAllocationBlocksSubmit";
    public const string KeyStandardDailyMinutes = "StandardDailyMinutes";

    public static readonly TimesheetSettingsDto Defaults = new(
        TimesheetPeriodTypes.Weekly, DayOfWeek.Sunday, OverAllocationToleranceMinutes: 0, OverAllocationBlocksSubmit: false, StandardDailyMinutes: 480);

    private readonly ZayraDbContext _db;
    public TimesheetSettingsService(ZayraDbContext db) => _db = db;

    public async Task<TimesheetSettingsDto> GetAsync(Guid tenantId, CancellationToken ct)
    {
        var rows = await _db.SystemSettings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Category == TimesheetConstants.SettingsCategory)
            .Select(x => new { x.SettingKey, x.SettingValue })
            .ToListAsync(ct);
        var map = rows.ToDictionary(x => x.SettingKey, x => x.SettingValue, StringComparer.OrdinalIgnoreCase);
        return Parse(map);
    }

    internal static TimesheetSettingsDto Parse(IReadOnlyDictionary<string, string> map)
    {
        var periodType = map.TryGetValue(KeyPeriodType, out var p) && string.Equals(p, TimesheetPeriodTypes.Monthly, StringComparison.OrdinalIgnoreCase)
            ? TimesheetPeriodTypes.Monthly : TimesheetPeriodTypes.Weekly;
        var weekStart = map.TryGetValue(KeyWeekStartDay, out var w) && Enum.TryParse<DayOfWeek>(w, true, out var day) ? day : Defaults.WeekStartDay;
        var tolerance = map.TryGetValue(KeyTolerance, out var t) && int.TryParse(t, out var tol) ? Math.Clamp(tol, 0, 1440) : Defaults.OverAllocationToleranceMinutes;
        var blocks = map.TryGetValue(KeyBlocksSubmit, out var b) && bool.TryParse(b, out var bl) ? bl : Defaults.OverAllocationBlocksSubmit;
        var daily = map.TryGetValue(KeyStandardDailyMinutes, out var d) && int.TryParse(d, out var dm) ? Math.Clamp(dm, 60, 1440) : Defaults.StandardDailyMinutes;
        return new TimesheetSettingsDto(periodType, weekStart, tolerance, blocks, daily);
    }

    public async Task<TimesheetSettingsDto> UpdateAsync(Guid tenantId, TimesheetSettingsRequest request, Guid? userId, CancellationToken ct)
    {
        var current = await GetAsync(tenantId, ct);
        var periodType = request.PeriodType is null ? current.PeriodType
            : string.Equals(request.PeriodType, TimesheetPeriodTypes.Monthly, StringComparison.OrdinalIgnoreCase) ? TimesheetPeriodTypes.Monthly
            : string.Equals(request.PeriodType, TimesheetPeriodTypes.Weekly, StringComparison.OrdinalIgnoreCase) ? TimesheetPeriodTypes.Weekly
            : throw new TimesheetValidationException("invalid_period_type", "Period type must be Weekly or Monthly.");
        var next = new TimesheetSettingsDto(
            periodType,
            request.WeekStartDay ?? current.WeekStartDay,
            Math.Clamp(request.OverAllocationToleranceMinutes ?? current.OverAllocationToleranceMinutes, 0, 1440),
            request.OverAllocationBlocksSubmit ?? current.OverAllocationBlocksSubmit,
            Math.Clamp(request.StandardDailyMinutes ?? current.StandardDailyMinutes, 60, 1440));

        var rows = await _db.SystemSettings
            .Where(x => x.TenantId == tenantId && x.Category == TimesheetConstants.SettingsCategory)
            .ToListAsync(ct);
        Upsert(rows, tenantId, KeyPeriodType, next.PeriodType, "string", "Timesheet period: Weekly or Monthly", userId);
        Upsert(rows, tenantId, KeyWeekStartDay, next.WeekStartDay.ToString(), "string", "First day of a weekly timesheet period", userId);
        Upsert(rows, tenantId, KeyTolerance, next.OverAllocationToleranceMinutes.ToString(), "int", "Minutes logged above attendance before a day is flagged", userId);
        Upsert(rows, tenantId, KeyBlocksSubmit, next.OverAllocationBlocksSubmit ? "true" : "false", "bool", "Whether an over-allocated day blocks submission", userId);
        Upsert(rows, tenantId, KeyStandardDailyMinutes, next.StandardDailyMinutes.ToString(), "int", "Available minutes per working day (utilisation)", userId);
        await _db.SaveChangesAsync(ct);
        return next;
    }

    private void Upsert(List<SystemSetting> rows, Guid tenantId, string key, string value, string dataType, string description, Guid? userId)
    {
        var row = rows.FirstOrDefault(r => string.Equals(r.SettingKey, key, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            row = new SystemSetting { TenantId = tenantId, Category = TimesheetConstants.SettingsCategory, SettingKey = key, DataType = dataType, Description = description };
            _db.SystemSettings.Add(row);
            rows.Add(row);
        }
        row.SettingValue = value;
        row.UpdatedAtUtc = DateTime.UtcNow;
        row.UpdatedBy = userId;
    }
}

/// <summary>Period arithmetic shared by the service, the reports and the tests.</summary>
public static class TimesheetPeriods
{
    public static (DateOnly Start, DateOnly End) For(DateOnly date, string periodType, DayOfWeek weekStart)
    {
        if (string.Equals(periodType, TimesheetPeriodTypes.Monthly, StringComparison.OrdinalIgnoreCase))
        {
            var start = new DateOnly(date.Year, date.Month, 1);
            return (start, start.AddMonths(1).AddDays(-1));
        }
        var back = ((int)date.DayOfWeek - (int)weekStart + 7) % 7;
        var ws = date.AddDays(-back);
        return (ws, ws.AddDays(6));
    }

    public static (DateOnly Start, DateOnly End) Previous(DateOnly periodStart, string periodType)
        => string.Equals(periodType, TimesheetPeriodTypes.Monthly, StringComparison.OrdinalIgnoreCase)
            ? (periodStart.AddMonths(-1), periodStart.AddDays(-1))
            : (periodStart.AddDays(-7), periodStart.AddDays(-1));

    public static (DateOnly Start, DateOnly End) Next(DateOnly periodEnd, string periodType)
    {
        var start = periodEnd.AddDays(1);
        return string.Equals(periodType, TimesheetPeriodTypes.Monthly, StringComparison.OrdinalIgnoreCase)
            ? (start, start.AddMonths(1).AddDays(-1))
            : (start, start.AddDays(6));
    }

    public static string Label(DateOnly start, DateOnly end, string periodType)
        => string.Equals(periodType, TimesheetPeriodTypes.Monthly, StringComparison.OrdinalIgnoreCase)
            ? start.ToString("MMMM yyyy")
            : $"{start:dd MMM} – {end:dd MMM yyyy}";

    public static IEnumerable<DateOnly> Days(DateOnly start, DateOnly end)
    {
        for (var d = start; d <= end; d = d.AddDays(1)) yield return d;
    }
}
