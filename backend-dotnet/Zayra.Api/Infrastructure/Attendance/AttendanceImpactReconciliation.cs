using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>One attendance day whose pending deductions no longer match what its punches say under today's rules.</summary>
/// <param name="Reason">One of the <see cref="AttendanceImpactReconciliation"/> reason codes.</param>
/// <param name="FromPunches">True: rebuild the day from its punches (the day processor). False: the day has no raw
/// punches (a migration import, or a legacy mobile punch from before it recorded raw events), so only its deductions
/// are re-derived from the record — rebuilding it from punches would wipe it.</param>
public sealed record StaleAttendanceDay(int EmployeeId, DateOnly WorkDate, string Reason, bool FromPunches);

/// <summary>One half-pair day whose late / early-exit minutes are held, not deducted (<see cref="AttendanceDerivedArtifacts.IsHalfPair"/>).</summary>
public sealed record HeldAttendanceDay(int EmployeeId, string EmployeeName, DateOnly WorkDate, int LateMinutes, int EarlyExitMinutes);

/// <summary>
/// The payroll safety net for attendance: before a run consumes the period's pending attendance impacts, are they what
/// the days now imply? Payroll deducts ONLY from <c>attendance_payroll_impacts</c>, so an impact written by an older rule,
/// or left behind by a race or an undone run, is deducted as it stands unless something compares it with the day first.
///
/// <para>A day is stale when, under today's rules:</para>
/// <list type="bullet">
///   <item><see cref="ImpactsWithoutRecord"/>: pending impacts exist, the day has punches, and no attendance record;</item>
///   <item><see cref="DeductionsDiffer"/>: the pending deductions are not what the record implies
///     (<see cref="AttendanceDerivedArtifacts.ImpliedImpacts"/> — for example a late deduction on a half-pair day);</item>
///   <item><see cref="PunchesDiffer"/>: the punches that belong to the day now (<see cref="AttendanceWorkDate"/>, the
///     pairing rule <see cref="AttendanceDerivedArtifacts.PairOf"/>) are not the record's first-in / last-out — for example
///     an Out-only day recorded by the old rule as a zero-length "complete" day with a 480-minute absence;</item>
///   <item><see cref="PunchesMoved"/>: a punch the record was built from now belongs to another day (a time-zone or
///     roster change), or punches already processed into another day now belong to a day with no record;</item>
///   <item><see cref="MinutesDiffer"/>: the record's late / early-exit minutes are not what its punches give under
///     today's rules (<see cref="AttendanceDerivedArtifacts.LateAndEarlyExit"/>, in the tenant's zone and the day's
///     shift) — for example a Saudi tenant's day timed by a stored New York zone, with a false early exit.</item>
/// </list>
/// <para>Paid days are not judged here: a day with a <c>Processed</c> impact, or an employee another non-voided run has
/// already paid this month, is <see cref="AttendanceDerivedArtifacts"/>'s paid-day protection's business (the
/// PaidAttendanceChanged flag). Payroll-locked days and days with no raw punches at all (a migration import, or a legacy
/// mobile punch from before it recorded raw events) are not judged either: recomputing them would wipe what they hold.</para>
/// <para>The answer is fixed by recomputing exactly those days with the day processor
/// (<see cref="AttendanceService.RecomputeDaysAsync"/>), never by a mass rewrite at deploy time.</para>
/// </summary>
public static class AttendanceImpactReconciliation
{
    /// <summary>The blocking payroll error: the run would deduct attendance impacts that no longer match their days.</summary>
    public const string StaleCode = "ERR_ATTENDANCE_IMPACT_STALE";

    /// <summary>The non-blocking payroll warning naming the late / early-exit minutes held on half-pair days.</summary>
    public const string HeldCode = "WARN_MISSING_PUNCH_HELD";

    public const string ImpactsWithoutRecord = "impacts_without_record";
    public const string DeductionsDiffer = "deductions_differ";
    public const string PunchesDiffer = "punches_differ";
    public const string PunchesMoved = "punches_moved";
    public const string MinutesDiffer = "minutes_differ";

    private const string CoverageJustification =
        "Whether another payroll run already paid an employee's month decides whether its attendance days are judged as " +
        "unpaid. It must not depend on the caller's company scope; only the employee ids leave this method.";

    /// <summary>The stale days among <paramref name="employeeIds"/> in [<paramref name="from"/>, <paramref name="to"/>].</summary>
    /// <param name="runId">The run being processed: its own (Draft) slips never make a day paid.</param>
    /// <param name="employeesPerChunk">How many employees are judged per round of queries: a large run is read a
    /// chunk at a time, so no single query or in-memory set grows with the whole population.</param>
    public static async Task<IReadOnlyList<StaleAttendanceDay>> FindStaleDaysAsync(ZayraDbContext db, Guid tenantId,
        IReadOnlyCollection<int> employeeIds, DateOnly from, DateOnly to, Guid? runId, CancellationToken ct, int employeesPerChunk = 200)
    {
        if (employeeIds.Count == 0 || to < from) return [];
        var stale = new List<StaleAttendanceDay>();
        foreach (var chunk in employeeIds.Distinct().Order().Chunk(Math.Max(1, employeesPerChunk)))
            stale.AddRange(await FindStaleChunkAsync(db, tenantId, chunk, from, to, runId, ct));
        return stale;
    }

    private static async Task<List<StaleAttendanceDay>> FindStaleChunkAsync(ZayraDbContext db, Guid tenantId,
        IReadOnlyCollection<int> ids, DateOnly from, DateOnly to, Guid? runId, CancellationToken ct)
    {
        // Only the columns the judgement reads.
        var records = await db.AttendanceDailyRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.EmployeeId) && x.WorkDate >= from && x.WorkDate <= to)
            .Select(x => new AttendanceDailyRecord
            {
                TenantId = x.TenantId, EmployeeId = x.EmployeeId, WorkDate = x.WorkDate, FirstInUtc = x.FirstInUtc,
                LastOutUtc = x.LastOutUtc, MissingPunch = x.MissingPunch, Status = x.Status, LateMinutes = x.LateMinutes,
                EarlyExitMinutes = x.EarlyExitMinutes, OvertimeMinutes = x.OvertimeMinutes,
            })
            .ToListAsync(ct);
        var recordByDay = records.GroupBy(x => (x.EmployeeId, x.WorkDate)).ToDictionary(g => g.Key, g => g.First());
        var impacts = await db.AttendancePayrollImpacts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.EmployeeId) && x.WorkDate >= from && x.WorkDate <= to)
            .Select(x => new AttendancePayrollImpact
            {
                TenantId = x.TenantId, EmployeeId = x.EmployeeId, WorkDate = x.WorkDate, ImpactType = x.ImpactType,
                Minutes = x.Minutes, Status = x.Status,
            })
            .ToListAsync(ct);
        var impactsByDay = impacts.ToLookup(x => (x.EmployeeId, x.WorkDate));

        // Employees another non-voided run that pays recurring salary has already paid in one of these months.
        var months = Enumerable.Range(0, (to.Year - from.Year) * 12 + to.Month - from.Month + 1)
            .Select(i => from.AddMonths(i)).Select(d => d.Year * 100 + d.Month).ToList();
        var paidRuns = ScopedBypass.TenantWide(db.PayrollRuns, tenantId, CoverageJustification)
            .Where(r => r.IncludesRecurringPay && AttendanceDerivedArtifacts.PaidRunStatuses.Contains(r.Status)
                     && months.Contains(r.Year * 100 + r.Month) && (runId == null || r.Id != runId));
        var paidEmployees = (await ScopedBypass.TenantWide(db.PayrollSlips, tenantId, CoverageJustification)
                .Where(s => ids.Contains(s.EmployeeId) && s.Status != "Voided" && paidRuns.Any(r => r.Id == s.RunId))
                .Select(s => s.EmployeeId).Distinct().ToListAsync(ct))
            .ToHashSet();

        var locks = await db.AttendanceLockPeriods.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Status == "Locked" && x.PeriodStart <= to && x.PeriodEnd >= from)
            .Select(x => new { x.PeriodStart, x.PeriodEnd }).ToListAsync(ct);

        // Punches around the period, dated by THE work-date rule. The window is wide enough for any day in the period:
        // a day's window starts at most a day and two hours before its date and ends at most two hours after the next.
        var timeZone = await AttendanceWorkDate.TenantTimeZoneAsync(db, tenantId, ct);
        var windowStart = from.AddDays(-2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var windowEnd = to.AddDays(3).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var events = await db.AttendanceRawEvents.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId != null && ids.Contains(x.EmployeeId!.Value)
                     && x.PunchTimestampUtc >= windowStart && x.PunchTimestampUtc < windowEnd)
            .OrderBy(x => x.PunchTimestampUtc)
            .Select(x => new { EmployeeId = x.EmployeeId!.Value, x.PunchTimestampUtc, x.PunchDirection, x.IsProcessed })
            .ToListAsync(ct);
        var dates = await AttendanceWorkDate.ResolveManyAsync(db, tenantId,
            events.Select(e => (e.EmployeeId, e.PunchTimestampUtc)).ToList(), timeZone, ct);
        var dated = events.Select((e, i) => (Event: e, WorkDate: dates[i])).ToList();
        var eventsByDay = dated.ToLookup(x => (x.Event.EmployeeId, x.WorkDate), x => x.Event);
        var dateOfPunch = dated.GroupBy(x => (x.Event.EmployeeId, x.Event.PunchTimestampUtc))
            .ToDictionary(g => g.Key, g => g.First().WorkDate);

        // What the day processor would give the stored stamps today: the employee's policy, the day's shift, the zone.
        var employees = (await ScopedBypass.NullableTenantWide(db.Employees, tenantId, CoverageJustification).AsNoTracking()
                .Where(e => ids.Contains(e.Id))
                .Select(e => new Employee
                {
                    Id = e.Id, TenantId = e.TenantId, AttendancePolicyCode = e.AttendancePolicyCode,
                    BranchId = e.BranchId, DepartmentId = e.DepartmentId, GradeId = e.GradeId,
                })
                .ToListAsync(ct))
            .ToDictionary(e => e.Id);
        IReadOnlyCollection<AttendancePolicy> policies = await db.AttendancePolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsActive).ToListAsync(ct);
        if (policies.Count == 0) policies = [AttendanceService.DefaultPolicy(tenantId)];
        var recordDates = records.Select(r => r.WorkDate).Distinct().ToList();
        var shifts = recordDates.Count == 0
            ? new Dictionary<(int EmployeeId, DateOnly Date), AttendanceWorkDate.DayShift>()
            : await AttendanceWorkDate.ShiftsOnAsync(db, tenantId, ids, recordDates, ct);

        var days = recordByDay.Keys
            .Concat(impacts.Select(x => (x.EmployeeId, x.WorkDate)))
            .Concat(dated.Where(x => x.WorkDate >= from && x.WorkDate <= to).Select(x => (x.Event.EmployeeId, x.WorkDate)))
            .Distinct()
            .OrderBy(d => d.EmployeeId).ThenBy(d => d.WorkDate);

        var stale = new List<StaleAttendanceDay>();
        foreach (var (employeeId, workDate) in days)
        {
            if (paidEmployees.Contains(employeeId)) continue;
            if (locks.Any(l => l.PeriodStart <= workDate && l.PeriodEnd >= workDate)) continue;
            var dayImpacts = impactsByDay[(employeeId, workDate)].ToList();
            if (dayImpacts.Any(x => x.Status == AttendanceDerivedArtifacts.ProcessedStatus)) continue;
            // Payroll consumes deductions only; overtime payable is claimed through overtime detection.
            var pending = dayImpacts.Where(x => x.ImpactType != AttendanceImpactTypes.OvertimePayable).ToList();
            recordByDay.TryGetValue((employeeId, workDate), out var record);
            var dayEvents = eventsByDay[(employeeId, workDate)].ToList();

            if (record is null)
            {
                // Rebuilt from punches: the processor writes the record the impacts (or the moved punches) belong to.
                // With no record AND no punch there is nothing to judge the impacts against, and nothing to rebuild from.
                if (dayEvents.Count == 0) continue;
                if (pending.Count > 0) stale.Add(new StaleAttendanceDay(employeeId, workDate, ImpactsWithoutRecord, true));
                else if (dayEvents.Any(e => e.IsProcessed)) stale.Add(new StaleAttendanceDay(employeeId, workDate, PunchesMoved, true));
                continue;
            }

            bool MovedAway(DateTime? stamp) => stamp is DateTime at
                && dateOfPunch.TryGetValue((employeeId, at), out var now) && now != workDate;
            if (dayEvents.Count > 0)
            {
                var (firstIn, lastOut) = AttendanceDerivedArtifacts.PairOf(
                    dayEvents.Select(e => (e.PunchTimestampUtc, e.PunchDirection)).ToList());
                if (firstIn != record.FirstInUtc || lastOut != record.LastOutUtc)
                {
                    stale.Add(new StaleAttendanceDay(employeeId, workDate, PunchesDiffer, true));
                    continue;
                }
                // The punches are the day's; are the minutes what they give today? (A record written for an untouched
                // leave / holiday / rest day has no punch, so it never reaches here.)
                if (employees.TryGetValue(employeeId, out var employee))
                {
                    shifts.TryGetValue((employeeId, workDate), out var shift);
                    var (late, early) = AttendanceDerivedArtifacts.LateAndEarlyExit(firstIn, lastOut, workDate,
                        shift?.StartTime, shift?.EndTime, timeZone, AttendanceService.ResolveAttendancePolicy(employee, policies));
                    if (late != record.LateMinutes || early != record.EarlyExitMinutes)
                    {
                        stale.Add(new StaleAttendanceDay(employeeId, workDate, MinutesDiffer, true));
                        continue;
                    }
                }
            }
            else if (MovedAway(record.FirstInUtc) || MovedAway(record.LastOutUtc))
            {
                stale.Add(new StaleAttendanceDay(employeeId, workDate, PunchesMoved, true));
                continue;
            }
            var implied = AttendanceDerivedArtifacts.ImpliedImpacts(record, AttendanceDerivedArtifacts.OrdinaryAbsenceMinutes)
                .Where(x => x.Type != AttendanceImpactTypes.OvertimePayable).ToList();
            if (!SameDeductions(pending, implied))
                stale.Add(new StaleAttendanceDay(employeeId, workDate, DeductionsDiffer, FromPunches: dayEvents.Count > 0));
        }
        return stale;
    }

    /// <summary>
    /// The same money, judged the way payroll reads impacts (PayrollController.Process): an absence is any type containing
    /// "Absence" (older rows say "Absence", today's say "Absence deduction") and is compared by its presence only — its
    /// minutes depend on the day's working-hours baseline (the KSA Ramadan reduction), which the writer resolved; the
    /// short-hours deduction is the sum of the other "deduction" types' minutes.
    /// </summary>
    private static bool SameDeductions(IReadOnlyList<AttendancePayrollImpact> pending, IReadOnlyList<(string Type, int Minutes)> implied)
    {
        static bool IsAbsence(string type) => type.Contains("Absence", StringComparison.OrdinalIgnoreCase);
        static bool IsShortHours(string type) => type.Contains("deduction", StringComparison.OrdinalIgnoreCase) && !IsAbsence(type);
        return pending.Any(x => IsAbsence(x.ImpactType)) == implied.Any(x => IsAbsence(x.Type))
            && pending.Where(x => IsShortHours(x.ImpactType)).Sum(x => x.Minutes) == implied.Where(x => IsShortHours(x.Type)).Sum(x => x.Minutes);
    }

    /// <summary>The half-pair days among <paramref name="employeeIds"/> in the period that hold late or early-exit minutes.</summary>
    public static async Task<IReadOnlyList<HeldAttendanceDay>> FindHeldDaysAsync(ZayraDbContext db, Guid tenantId,
        IReadOnlyCollection<int> employeeIds, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (employeeIds.Count == 0 || to < from) return [];
        var ids = employeeIds.Distinct().ToList();
        return await db.AttendanceDailyRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.EmployeeId) && x.WorkDate >= from && x.WorkDate <= to)
            .Where(AttendanceDerivedArtifacts.IsHalfPairExpression)
            .Where(x => x.LateMinutes > 0 || x.EarlyExitMinutes > 0)
            .OrderBy(x => x.EmployeeId).ThenBy(x => x.WorkDate)
            .Select(x => new HeldAttendanceDay(x.EmployeeId, x.EmployeeName, x.WorkDate, x.LateMinutes, x.EarlyExitMinutes))
            .ToListAsync(ct);
    }
}
