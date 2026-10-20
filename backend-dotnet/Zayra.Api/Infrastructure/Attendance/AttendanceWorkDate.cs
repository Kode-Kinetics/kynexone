using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>
/// THE rule for which work date a punch belongs to, shared by every path that writes a daily record: the punch path
/// (<c>AttendanceService.PunchAsync</c>), device ingest (<c>IngestByDeviceKeyAsync</c>) and the legacy mobile punch
/// (<c>MobileController.Punch</c>). The work date is also the <see cref="AttendanceDailyRecordLock"/> key, so two paths
/// that disagree about it recompute different days under different locks.
///
/// <para>The rule: the tenant-local calendar date of the punch, except that a punch at or before the end of an
/// OVERNIGHT shift (end &lt;= start) assigned to the employee on the previous local date belongs to that previous date
/// — a night worker's 02:00 clock-out closes yesterday's shift, it does not open today.</para>
///
/// <para>Before this was shared, device ingest used the plain local date and the legacy mobile punch the UTC date.</para>
///
/// <para>The shift assignment is read tenant-wide (<see cref="ScopedBypass.TenantWide{T}"/>): the employee has already
/// been authorised by the calling path, nothing about the shift is returned to the caller, and which day a punch
/// belongs to must not depend on who is asking. <c>ShiftAssignment</c> is company-scoped-operational, so on the
/// anonymous device webhook — empty company scope under Production's strict mode — the filtered read saw no shift at
/// all and every overnight punch fell back to the calendar date.</para>
/// </summary>
public static class AttendanceWorkDate
{
    private const string ShiftReadJustification =
        "An employee's own roster decides which work date a punch belongs to. The employee is already authorised by the " +
        "calling path (or resolved from the authenticated device's tenant on the anonymous webhook); no shift data is " +
        "returned, and the answer must not depend on the caller's company scope.";

    /// <summary>The tenant's attendance time zone: its stored default, failing open to UTC (see <see cref="TenantTimeZone"/>).</summary>
    public static async Task<TimeZoneInfo> TenantTimeZoneAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        var tzId = await db.TenantLocalizationSettings.AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .Select(l => l.DefaultTimezone)
            .FirstOrDefaultAsync(ct);
        return TenantTimeZone.FromId(tzId);
    }

    /// <summary>The pure rule, given the end of the overnight shift assigned on the punch's previous local date (if any).</summary>
    public static DateOnly Resolve(DateTime punchUtc, TimeZoneInfo timeZone, TimeOnly? previousDayOvernightShiftEnd)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(punchUtc, DateTimeKind.Utc), timeZone);
        var localDate = DateOnly.FromDateTime(local);
        return previousDayOvernightShiftEnd is { } end && TimeOnly.FromDateTime(local) <= end
            ? localDate.AddDays(-1)
            : localDate;
    }

    /// <summary>The previous local date the rule looks up for <paramref name="punchUtc"/>.</summary>
    private static DateOnly PreviousLocalDate(DateTime punchUtc, TimeZoneInfo timeZone) =>
        TenantTimeZone.LocalDate(timeZone, punchUtc).AddDays(-1);

    /// <summary>One punch's work date (one query).</summary>
    public static async Task<DateOnly> ResolveAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateTime punchUtc, TimeZoneInfo timeZone, CancellationToken ct)
    {
        var dates = await ResolveManyAsync(db, tenantId, [(employeeId, punchUtc)], timeZone, ct);
        return dates[0];
    }

    /// <summary>
    /// Each punch's work date, in input order, with ONE shift query for the whole batch (device ingest sends many).
    /// </summary>
    public static async Task<IReadOnlyList<DateOnly>> ResolveManyAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyList<(int EmployeeId, DateTime PunchUtc)> punches, TimeZoneInfo timeZone, CancellationToken ct)
    {
        if (punches.Count == 0) return [];
        var employeeIds = punches.Select(p => p.EmployeeId).Distinct().ToList();
        var previousDates = punches.Select(p => PreviousLocalDate(p.PunchUtc, timeZone)).Distinct().ToList();
        // Over-fetches the (employee x date) cross product, which is bounded by the batch and filtered exactly below.
        var overnight = await ScopedBypass.TenantWide(db.ShiftAssignments, tenantId, ShiftReadJustification).AsNoTracking()
            .Where(a => employeeIds.Contains(a.EmployeeId) && previousDates.Contains(a.AssignedDate))
            .Join(db.ShiftDefinitions.AsNoTracking().Where(d => d.TenantId == tenantId),
                a => a.ShiftDefinitionId, d => d.Id,
                (a, d) => new { a.EmployeeId, a.AssignedDate, d.StartTime, d.EndTime })
            .Where(s => s.EndTime <= s.StartTime)
            .ToListAsync(ct);
        var endByEmployeeDay = overnight
            .GroupBy(s => (s.EmployeeId, s.AssignedDate))
            .ToDictionary(g => g.Key, g => g.First().EndTime);

        return punches.Select(p =>
        {
            var previous = PreviousLocalDate(p.PunchUtc, timeZone);
            TimeOnly? end = endByEmployeeDay.TryGetValue((p.EmployeeId, previous), out var e) ? e : null;
            return Resolve(p.PunchUtc, timeZone, end);
        }).ToList();
    }

    /// <summary>The employee's shift on <paramref name="date"/> (start/end local times), read tenant-wide as above.</summary>
    public static async Task<(TimeOnly StartTime, TimeOnly EndTime)?> ShiftOnAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateOnly date, CancellationToken ct)
    {
        var row = await ScopedBypass.TenantWide(db.ShiftAssignments, tenantId, ShiftReadJustification).AsNoTracking()
            .Where(a => a.EmployeeId == employeeId && a.AssignedDate == date)
            .Join(db.ShiftDefinitions.AsNoTracking().Where(d => d.TenantId == tenantId),
                a => a.ShiftDefinitionId, d => d.Id, (a, d) => new { d.StartTime, d.EndTime })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.StartTime, row.EndTime);
    }
}
