using Microsoft.EntityFrameworkCore;
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
        UpsertImpacts(db, tenantId, daily, absenceMinutes);
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

    /// <summary>
    /// The payroll impacts for the day. Replaces the day's impacts wholesale rather than adding to
    /// them, so re-processing — and re-importing — is idempotent and never doubles what payroll owes.
    /// </summary>
    /// <param name="absenceMinutes">What one absent day costs in minutes: 480 on an ordinary day, or the
    /// reduced KSA Art. 98 Ramadan baseline (360) on a Ramadan day. Deducting a full 480 for a 6-hour
    /// Ramadan day over-deducts by a third. See the call site for why the ordinary-day literal stays.</param>
    public static void UpsertImpacts(ZayraDbContext db, Guid tenantId, AttendanceDailyRecord daily, int absenceMinutes)
    {
        var existing = db.AttendancePayrollImpacts.Where(x => x.TenantId == tenantId && x.EmployeeId == daily.EmployeeId && x.WorkDate == daily.WorkDate);
        db.AttendancePayrollImpacts.RemoveRange(existing);
        if (daily.LateMinutes > 0) db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact { TenantId = tenantId, EmployeeId = daily.EmployeeId, WorkDate = daily.WorkDate, ImpactType = "Late deduction", Minutes = daily.LateMinutes, DailyRecordId = daily.Id });
        if (daily.EarlyExitMinutes > 0) db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact { TenantId = tenantId, EmployeeId = daily.EmployeeId, WorkDate = daily.WorkDate, ImpactType = "Early exit deduction", Minutes = daily.EarlyExitMinutes, DailyRecordId = daily.Id });
        if (daily.Status == "Absent") db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact { TenantId = tenantId, EmployeeId = daily.EmployeeId, WorkDate = daily.WorkDate, ImpactType = "Absence deduction", Minutes = absenceMinutes > 0 ? absenceMinutes : OrdinaryAbsenceMinutes, DailyRecordId = daily.Id });
        if (daily.OvertimeMinutes > 0) db.AttendancePayrollImpacts.Add(new AttendancePayrollImpact { TenantId = tenantId, EmployeeId = daily.EmployeeId, WorkDate = daily.WorkDate, ImpactType = "Overtime payable", Minutes = daily.OvertimeMinutes, DailyRecordId = daily.Id });
    }
}
