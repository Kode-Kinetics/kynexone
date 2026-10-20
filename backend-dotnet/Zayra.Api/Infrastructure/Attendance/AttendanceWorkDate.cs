using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>
/// THE definition of which work date an instant belongs to, shared by every path that writes a daily record: the punch
/// path (<c>AttendanceService.PunchAsync</c>), device ingest (<c>IngestByDeviceKeyAsync</c>), the legacy mobile punch
/// (<c>MobileController.Punch</c>) AND the window of punches a day's recompute counts (<c>WorkDayWindowAsync</c>). The
/// work date is also the <see cref="AttendanceDailyRecordLock"/> key, so a punch dated D must be one D's recompute counts.
///
/// <para><b>The model: consecutive work days partition time.</b> Day D's window is [E(D-1), E(D)), where the boundary
/// E(D) between D and D+1 is ONE instant used as the end of the first and the start of the second, so windows never
/// overlap and never leave a gap, and a punch's date is simply the day whose window contains it. The raw boundary
/// B(D, D+1) is:</para>
/// <list type="number">
///   <item>the local midnight starting D+1, or — when D has a shift — the later of that midnight and the end of D's shift
///     plus <see cref="ShiftPunchToleranceMinutes"/> (an overnight shift ends on D+1, so its clock-out after midnight and
///     a little after the shift end still closes D);</item>
///   <item>only when D+1 has an ASSIGNED shift: pulled earlier to that shift's start minus the tolerance, when that comes
///     first — so D's tolerance never swallows D+1's early clock-in — but never earlier than one minute after the end of
///     D's own shift (so a clock-out at exactly the shift end, or in its final minute, still closes D). An unrostered
///     D+1 does not clip: its notional 09:00 start would leave a 12-hour night 0–60 minutes of slack.</item>
/// </list>
/// <para>E(D) = max(B(D-1, D), B(D, D+1)). A raw boundary can only ever fall behind the one immediately before it (a
/// near-24-hour shift ending late on D, before a just-after-midnight start on D+1); the running maximum keeps the
/// boundaries in order — D's window is then empty — so the days still partition time. Two steps back can never
/// overtake: B(D-2, D-1) is at most D 02:00 and B(D, D+1) at least D+1 minus the tolerance.</para>
/// <para>With no shifts at all every boundary is local midnight: the plain calendar day, exactly as before.</para>
///
/// <para>Before this, the date rule said "previous day if at or before the overnight shift's end" while the window was
/// exactly [shift start, shift end): a clock-out at 06:05 (or a clock-in at 21:45) for a 22:00–06:00 shift fell in no
/// window or on the next day as an out-only day, a clock-out at exactly 06:00 was dated D but not counted by D, and the
/// next day's calendar window overlapped the shift, so a range run counted the 02:00 clock-out on both days.</para>
///
/// <para>The shift assignment is read tenant-wide (<see cref="ScopedBypass.TenantWide{T}"/>): the employee has already
/// been authorised by the calling path, nothing about the shift is returned to the caller, and which day a punch
/// belongs to must not depend on who is asking. <c>ShiftAssignment</c> is company-scoped-operational, so on the
/// anonymous device webhook — empty company scope under Production's strict mode — the filtered read saw no shift at
/// all and every overnight punch fell back to the calendar date.</para>
/// </summary>
public static class AttendanceWorkDate
{
    /// <summary>
    /// How far before its start and after its end a shift still claims a punch: 120 minutes. No tenant, policy or shift
    /// setting exists for this (<c>AttendancePolicy.GraceMinutes</c> is a lateness allowance, not a punch window), so it
    /// is a named constant: wide enough for an early clock-in and a normal overrun, and clipped by the next day's assigned
    /// start so it cannot take the next shift's clock-in. A punch outside it is a correction, as before.
    /// </summary>
    public const int ShiftPunchToleranceMinutes = 120;

    /// <summary>The start assumed for a day with no assigned shift (also <c>ProcessEmployeeDay</c>'s late/early baseline).</summary>
    public static readonly TimeOnly DefaultShiftStart = new(9, 0);

    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(ShiftPunchToleranceMinutes);

    /// <summary>An employee's shift on one date, in local wall-clock times. End &lt;= Start means it ends the next day.</summary>
    public sealed record DayShift(TimeOnly StartTime, TimeOnly EndTime)
    {
        public bool IsOvernight => EndTime <= StartTime;
    }

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

    // ── the pure definition ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The UTC instant a local wall-clock time on <paramref name="date"/> occurs (a DST gap steps forward).</summary>
    private static DateTime LocalToUtc(DateOnly date, TimeOnly time, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), DateTimeKind.Utc);
    }

    /// <summary>The raw boundary B(<paramref name="day"/>, day+1) (see the class summary).</summary>
    public static DateTime BoundaryUtc(DateOnly day, DayShift? shift, DayShift? nextShift, TimeZoneInfo timeZone)
    {
        var next = day.AddDays(1);
        var boundary = TenantTimeZone.LocalDayStartUtc(timeZone, next);
        var floor = TenantTimeZone.LocalDayStartUtc(timeZone, day);
        if (shift is not null)
        {
            var shiftEnd = LocalToUtc(shift.IsOvernight ? next : day, shift.EndTime, timeZone);
            if (shiftEnd + Tolerance > boundary) boundary = shiftEnd + Tolerance;
            floor = shiftEnd + ShiftEndMinute;
        }
        if (nextShift is not null)
        {
            var nextStart = LocalToUtc(next, nextShift.StartTime, timeZone) - Tolerance;
            if (nextStart < boundary) boundary = nextStart > floor ? nextStart : floor;
        }
        return boundary;
    }

    /// <summary>A clock-out at exactly a shift's end (or within its final minute) still closes that shift.</summary>
    private static readonly TimeSpan ShiftEndMinute = TimeSpan.FromMinutes(1);

    /// <summary>E(<paramref name="day"/>): the effective boundary where <paramref name="day"/> ends and day+1 begins.</summary>
    private static DateTime EffectiveBoundaryUtc(DateOnly day, Func<DateOnly, DayShift?> shiftOn, TimeZoneInfo timeZone)
    {
        var previous = BoundaryUtc(day.AddDays(-1), shiftOn(day.AddDays(-1)), shiftOn(day), timeZone);
        var own = BoundaryUtc(day, shiftOn(day), shiftOn(day.AddDays(1)), timeZone);
        return own > previous ? own : previous;
    }

    /// <summary><paramref name="day"/>'s window [StartUtc, EndUtc). Needs the shifts of day-2 .. day+1.</summary>
    public static (DateTime StartUtc, DateTime EndUtc) WindowUtc(DateOnly day, Func<DateOnly, DayShift?> shiftOn, TimeZoneInfo timeZone) =>
        (EffectiveBoundaryUtc(day.AddDays(-1), shiftOn, timeZone), EffectiveBoundaryUtc(day, shiftOn, timeZone));

    /// <summary>
    /// The work date of <paramref name="punchUtc"/>: the day whose window contains it. Needs the shifts of the punch's
    /// local date -4 .. +2: a window can begin up to a day and two hours before its date and end up to two hours before it.
    /// </summary>
    public static DateOnly Resolve(DateTime punchUtc, TimeZoneInfo timeZone, Func<DateOnly, DayShift?> shiftOn)
    {
        var utc = DateTime.SpecifyKind(punchUtc, DateTimeKind.Utc);
        var localDate = TenantTimeZone.LocalDate(timeZone, utc);
        for (var day = localDate.AddDays(-2); day <= localDate.AddDays(1); day = day.AddDays(1))
        {
            var (start, end) = WindowUtc(day, shiftOn, timeZone);
            if (utc >= start && utc < end) return day;
        }
        return localDate; // unreachable while the boundaries partition time; the calendar date is the safe answer
    }

    // ── the reads ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The shifts of the given employees on the given dates, in ONE query (tenant-wide, see the class summary).</summary>
    private static async Task<Dictionary<(int EmployeeId, DateOnly Date), DayShift>> ShiftsAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<int> employeeIds, IReadOnlyCollection<DateOnly> dates, CancellationToken ct)
    {
        // Over-fetches the (employee x date) cross product, which is bounded by the batch.
        var rows = await ScopedBypass.TenantWide(db.ShiftAssignments, tenantId, ShiftReadJustification).AsNoTracking()
            .Where(a => employeeIds.Contains(a.EmployeeId) && dates.Contains(a.AssignedDate))
            .Join(db.ShiftDefinitions.AsNoTracking().Where(d => d.TenantId == tenantId),
                a => a.ShiftDefinitionId, d => d.Id,
                (a, d) => new { a.EmployeeId, a.AssignedDate, d.StartTime, d.EndTime })
            .ToListAsync(ct);
        return rows.GroupBy(r => (r.EmployeeId, r.AssignedDate))
            .ToDictionary(g => g.Key, g => new DayShift(g.First().StartTime, g.First().EndTime));
    }

    /// <summary>One punch's work date (one query).</summary>
    public static async Task<DateOnly> ResolveAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateTime punchUtc, TimeZoneInfo timeZone, CancellationToken ct)
    {
        var dates = await ResolveManyAsync(db, tenantId, [(employeeId, punchUtc)], timeZone, ct);
        return dates[0];
    }

    /// <summary>Each punch's work date, in input order, with ONE shift query for the whole batch (device ingest sends many).</summary>
    public static async Task<IReadOnlyList<DateOnly>> ResolveManyAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyList<(int EmployeeId, DateTime PunchUtc)> punches, TimeZoneInfo timeZone, CancellationToken ct)
    {
        if (punches.Count == 0) return [];
        var employeeIds = punches.Select(p => p.EmployeeId).Distinct().ToList();
        var dates = punches.Select(p => TenantTimeZone.LocalDate(timeZone, p.PunchUtc))
            .SelectMany(d => Enumerable.Range(-4, 7).Select(d.AddDays)).Distinct().ToList();
        var shifts = await ShiftsAsync(db, tenantId, employeeIds, dates, ct);
        return punches.Select(p => Resolve(p.PunchUtc, timeZone,
            d => shifts.TryGetValue((p.EmployeeId, d), out var s) ? s : null)).ToList();
    }

    /// <summary>
    /// Each employee's work date at <paramref name="nowUtc"/> — the day a punch made now is given — and that day's punch
    /// window, with ONE shift query for all of them. What "today" means on a self-service screen: at 01:00 a night worker
    /// is still on the shift that started yesterday evening.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, (DateOnly WorkDate, DateTime StartUtc, DateTime EndUtc)>> TodayManyAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<int> employeeIds, DateTime nowUtc, TimeZoneInfo timeZone, CancellationToken ct)
    {
        if (employeeIds.Count == 0) return new Dictionary<int, (DateOnly, DateTime, DateTime)>();
        // The punch's local date -4 .. +2 covers both the date rule and the resulting day's window (day-2 .. day+1).
        var localDate = TenantTimeZone.LocalDate(timeZone, nowUtc);
        var dates = Enumerable.Range(-4, 7).Select(localDate.AddDays).ToList();
        var shifts = await ShiftsAsync(db, tenantId, employeeIds, dates, ct);
        var result = new Dictionary<int, (DateOnly, DateTime, DateTime)>();
        foreach (var employeeId in employeeIds.Distinct())
        {
            DayShift? On(DateOnly d) => shifts.TryGetValue((employeeId, d), out var s) ? s : null;
            var workDate = Resolve(nowUtc, timeZone, On);
            var (start, end) = WindowUtc(workDate, On, timeZone);
            result[employeeId] = (workDate, start, end);
        }
        return result;
    }

    /// <summary><paramref name="date"/>'s window and its own shift (one query).</summary>
    public static async Task<(DateTime StartUtc, DateTime EndUtc, DayShift? Shift)> WindowAsync(
        ZayraDbContext db, Guid tenantId, int employeeId, DateOnly date, TimeZoneInfo timeZone, CancellationToken ct)
    {
        var shifts = await ShiftsAsync(db, tenantId, [employeeId], [date.AddDays(-2), date.AddDays(-1), date, date.AddDays(1)], ct);
        DayShift? On(DateOnly d) => shifts.TryGetValue((employeeId, d), out var s) ? s : null;
        var (start, end) = WindowUtc(date, On, timeZone);
        return (start, end, On(date));
    }
}
