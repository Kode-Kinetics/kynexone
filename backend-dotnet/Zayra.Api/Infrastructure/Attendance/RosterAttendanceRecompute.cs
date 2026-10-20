using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Jobs;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>
/// A roster change re-dates punches: a punch's work date depends on the shifts around it (AttendanceWorkDate), read
/// when the day is recomputed. Until someone ran a range run, a night worker given a night shift kept an In-only
/// evening and an Out-only morning, and the stale-attendance check then blocked payroll on them. Every assignment
/// change now queues the existing <c>attendance.process</c> job for the employee's days around it (D-1..D+1), so the
/// punches move to the right day on their own.
///
/// <para>Only days up to the tenant's today (a future day has no punch, and processing it would open an Absent day), and
/// only months payroll has not paid (a paid day is never rewritten anyway; the stale check and the PaidAttendanceChanged
/// flag handle those). Idempotent: the job's key is the employee and range, so the same change queued twice, or two
/// changes covering the same days, share one live job.</para>
/// </summary>
public static class RosterAttendanceRecompute
{
    /// <returns>How many jobs were queued (an already-live job for the same days is not counted again).</returns>
    public static async Task<int> EnqueueAsync(ZayraDbContext db, BackgroundJobStore? jobs, Guid tenantId,
        IEnumerable<(int EmployeeId, DateOnly Date)> changedDays, Guid? requestedBy, CancellationToken ct)
    {
        if (jobs is null) return 0;
        var today = Zayra.Api.Application.Attendance.TenantTimeZone.LocalDate(await TenantClock.ZoneAsync(db, tenantId, ct), DateTime.UtcNow);
        var queued = 0;
        foreach (var employee in changedDays.GroupBy(d => d.EmployeeId))
        {
            foreach (var (from, to) in Ranges(employee.Select(d => d.Date), today))
            {
                var days = new List<DateOnly>();
                for (var d = from; d <= to; d = d.AddDays(1))
                    if (!await AttendanceDerivedArtifacts.IsCoveredByPayrollAsync(db, tenantId, employee.Key, d, ct)) days.Add(d);
                if (days.Count == 0) continue;
                var payload = new AttendanceProcessingJobPayload(days.Min(), days.Max(), employee.Key, GroupScope: true,
                    CompanyIds: Array.Empty<Guid>(), RequestedByUserId: requestedBy, IpAddress: null, UserAgent: "roster-change");
                var result = await jobs.EnqueueAsync(tenantId, AttendanceProcessingJobHandler.JobType,
                    AttendanceProcessingJobHandler.DefaultIdempotencyKey(payload), payload, requestedBy, ct);
                if (result.Created) queued++;
            }
        }
        return queued;
    }

    /// <summary>Each changed date widened to D-1..D+1, clipped to today, merged where they touch.</summary>
    private static IEnumerable<(DateOnly From, DateOnly To)> Ranges(IEnumerable<DateOnly> dates, DateOnly today)
    {
        DateOnly? from = null, to = null;
        foreach (var d in dates.Distinct().Order())
        {
            var lo = d.AddDays(-1);
            var hi = d.AddDays(1) > today ? today : d.AddDays(1);
            if (hi < lo) continue;
            if (from is null) { from = lo; to = hi; continue; }
            if (lo <= to!.Value.AddDays(1)) { if (hi > to) to = hi; continue; }
            yield return (from.Value, to.Value);
            from = lo; to = hi;
        }
        if (from is not null) yield return (from.Value, to!.Value);
    }
}
