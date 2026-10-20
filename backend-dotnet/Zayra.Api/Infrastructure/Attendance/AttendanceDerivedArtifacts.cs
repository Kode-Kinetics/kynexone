using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>
/// The ONE writer of the rows that a settled attendance day implies.
///
/// <para>One attendance day is read by three different surfaces from three different tables:</para>
/// <list type="bullet">
///   <item><c>attendance_daily_records</c> — the Attendance grid</item>
///   <item><c>attendance_records</c> — the legacy row the executive dashboard SUMS for its
///     overtime-hours tile and thresholds for its churn heuristic</item>
///   <item><c>attendance_payroll_impacts</c> — the ONLY table the payroll run reads for
///     attendance-driven money (overtime payable, late / early-exit / absence deductions)</item>
/// </list>
///
/// <para>The processor wrote all three; <c>MigrationImportController</c> wrote only the first. An
/// imported month therefore showed its overtime on the grid, read ZERO on the dashboard, and was
/// never paid — and its lateness and absences were never deducted. Making payroll read the other
/// copy instead would have spread the duplication further, so both doors now call this one writer
/// and produce the same state by construction.</para>
///
/// <para>Static and <c>ZayraDbContext</c>-only on purpose: the migration importer must be able to
/// call it without taking a dependency on <c>AttendanceService</c>, whose constructor pulls in
/// notifications and an HTTP client factory that an import has no use for.</para>
/// </summary>
public static class AttendanceDerivedArtifacts
{
    /// <summary>What one absent day costs when no jurisdiction rule reduces it.</summary>
    public const int OrdinaryAbsenceMinutes = 480;

    /// <summary>The attendance exception raised when a paid day's recomputed impacts differ from what payroll paid.</summary>
    public const string PaidAttendanceChanged = "PaidAttendanceChanged";

    /// <summary>
    /// Writes both derived representations for a settled daily record.
    /// </summary>
    /// <param name="employeeCompanyId">
    /// The owning employee's company, from the caller's already-resolved entity. Null is legitimate
    /// (an employee with no company, or a tenant with no company dimension yet) and passes through
    /// unchanged — this method must not invent a company it was not given.
    /// </param>
    /// <param name="absenceMinutes">What one absent day costs in minutes: 480 on an ordinary day, or
    /// the reduced KSA Art. 98 Ramadan baseline (360) on a Ramadan day.</param>
    public static async Task SyncAsync(
        ZayraDbContext db, Guid tenantId, Guid? employeeCompanyId,
        AttendanceDailyRecord daily, int absenceMinutes, CancellationToken ct)
    {
        await UpsertLegacyRecordAsync(db, tenantId, employeeCompanyId, daily, ct);
        await UpsertImpactsAsync(db, tenantId, daily, absenceMinutes, ct);
    }

    /// <summary>
    /// The legacy <c>attendance_records</c> row — what the executive dashboard sums.
    /// </summary>
    /// <remarks>
    /// WAVE 1 B1 (round 2) — <c>AttendanceRecord</c> is <c>ICompanyScopedOperational</c>, so a row
    /// whose <c>CompanyId</c> is null is invisible to every company-scoped user. The company is
    /// HANDED IN by the caller, taken from the <c>Employee</c> it already holds; there is
    /// deliberately no employee query in scope here to re-introduce that bug with.
    /// </remarks>
    public static async Task UpsertLegacyRecordAsync(
        ZayraDbContext db, Guid tenantId, Guid? employeeCompanyId,
        AttendanceDailyRecord daily, CancellationToken ct)
    {
        var record = await db.AttendanceRecords.FirstOrDefaultAsync(
            x => x.TenantId == tenantId && x.EmployeeId == daily.EmployeeId && x.WorkDate == daily.WorkDate, ct);
        if (record is null)
        {
            record = new AttendanceRecord { TenantId = tenantId, EmployeeId = daily.EmployeeId, WorkDate = daily.WorkDate };
            db.AttendanceRecords.Add(record);
        }
        // ??= not =: an existing row's company is never reassigned here. EnforceCompanyScopeOnWritesAsync
        // throws company_reassignment_blocked on exactly that, and repairing a null is the only
        // transition this path is allowed to make.
        record.CompanyId ??= employeeCompanyId;
        record.TimeIn = daily.FirstInUtc is null ? null : TimeOnly.FromDateTime(daily.FirstInUtc.Value);
        record.TimeOut = daily.LastOutUtc is null ? null : TimeOnly.FromDateTime(daily.LastOutUtc.Value);
        record.OvertimeHours = Math.Round(daily.OvertimeMinutes / 60m, 2);
        record.Status = daily.Status;
        record.Notes = daily.MissingPunch ? "Missing punch" : "";
    }

    /// <summary>The impact status payroll stamps on what a run consumed (PayrollController; the void restores the prior one).</summary>
    public const string ProcessedStatus = "Processed";

    /// <summary>
    /// The payroll impacts for the day. Pending impacts are replaced wholesale rather than added to, so re-processing —
    /// and re-importing — is idempotent and never doubles what payroll owes.
    ///
    /// <para><b>A <c>Processed</c> impact is never deleted, replaced or duplicated.</b> It is what a payroll run consumed,
    /// and the run's witness row (<c>payroll_run_consumptions</c>) points at its id — the void/reopen restore finds it
    /// there. This used to replace every impact whatever its status, and since nothing writes
    /// <c>AttendanceLockPeriods</c> (so no period is ever locked), any HR process run over a paid period deleted the
    /// paid impacts and raised fresh pending ones that an off-cycle run would deduct again.</para>
    ///
    /// <para>On a day with any <c>Processed</c> impact (a paid day): the processed impacts stay exactly as paid; no new
    /// deduction is raised for that day (an off-cycle run would take it); and when the recomputed deductions differ from
    /// what was paid, a <see cref="PaidAttendanceChanged"/> attendance exception lists the differences for HR to correct
    /// with a payroll adjustment. Overtime payable, which payroll never consumes (it is claimed through overtime
    /// detection), is still recomputed like any pending impact unless it was itself processed.</para>
    /// </summary>
    /// <param name="absenceMinutes">What one absent day costs in minutes: 480 on an ordinary day, or the
    /// reduced KSA Art. 98 Ramadan baseline (360) on a Ramadan day. Deducting a full 480 for a 6-hour
    /// Ramadan day over-deducts by a third. See the call site for why the ordinary-day literal stays.</param>
    public static async Task UpsertImpactsAsync(ZayraDbContext db, Guid tenantId, AttendanceDailyRecord daily, int absenceMinutes, CancellationToken ct)
    {
        var existing = await db.AttendancePayrollImpacts
            .Where(x => x.TenantId == tenantId && x.EmployeeId == daily.EmployeeId && x.WorkDate == daily.WorkDate)
            .ToListAsync(ct);
        var paid = existing.Where(x => x.Status == ProcessedStatus).ToList();
        db.AttendancePayrollImpacts.RemoveRange(existing.Where(x => x.Status != ProcessedStatus));

        var recomputed = new List<(string Type, int Minutes)>();
        if (daily.LateMinutes > 0) recomputed.Add((AttendanceImpactTypes.LateDeduction, daily.LateMinutes));
        if (daily.EarlyExitMinutes > 0) recomputed.Add((AttendanceImpactTypes.EarlyExitDeduction, daily.EarlyExitMinutes));
        if (daily.Status == "Absent") recomputed.Add((AttendanceImpactTypes.AbsenceDeduction, absenceMinutes > 0 ? absenceMinutes : OrdinaryAbsenceMinutes));
        if (daily.OvertimeMinutes > 0) recomputed.Add((AttendanceImpactTypes.OvertimePayable, daily.OvertimeMinutes));

        var dayIsPaid = paid.Count > 0;
        bool Frozen(string type) => paid.Any(p => p.ImpactType == type) || (dayIsPaid && type != AttendanceImpactTypes.OvertimePayable);
        foreach (var (type, minutes) in recomputed.Where(r => !Frozen(r.Type)))
            db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact
            {
                TenantId = tenantId, EmployeeId = daily.EmployeeId, WorkDate = daily.WorkDate,
                ImpactType = type, Minutes = minutes, DailyRecordId = daily.Id,
            });
        if (!dayIsPaid) return;

        // What payroll paid vs what the day now says, for every type the paid day froze.
        var differences = paid.Select(p => p.ImpactType).Concat(recomputed.Select(r => r.Type)).Distinct()
            .Where(Frozen)
            .Select(type => (Type: type,
                Paid: paid.Where(p => p.ImpactType == type).Sum(p => p.Minutes),
                Now: recomputed.Where(r => r.Type == type).Sum(r => r.Minutes)))
            .Where(d => d.Paid != d.Now)
            .OrderBy(d => d.Type, StringComparer.Ordinal)
            .ToList();
        if (differences.Count == 0) return;

        var details = "Attendance for a paid day changed after payroll: "
            + string.Join("; ", differences.Select(d => $"{d.Type} paid {d.Paid} min, recomputed {d.Now} min"))
            + ". The paid impacts were left as paid and no new deduction was raised; correct the difference with a payroll adjustment.";
        var open = await db.AttendanceExceptions.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == daily.EmployeeId
            && x.WorkDate == daily.WorkDate && x.ExceptionType == PaidAttendanceChanged && !x.IsResolved, ct);
        if (open is not null) { open.Details = details; return; }
        db.AttendanceExceptions.Add(new AttendanceException
        {
            TenantId = tenantId, EmployeeId = daily.EmployeeId, DailyRecordId = daily.Id, WorkDate = daily.WorkDate,
            ExceptionType = PaidAttendanceChanged, Severity = "High", Details = details,
        });
    }
}
