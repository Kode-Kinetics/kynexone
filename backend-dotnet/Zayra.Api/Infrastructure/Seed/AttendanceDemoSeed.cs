using Microsoft.EntityFrameworkCore;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Seed;

/// <summary>
/// WAVE 0 — THE ATTENDANCE MODULE RENDERED AN EMPTY SCREEN FOR EVERY TENANT.
///
/// <para><c>GET /api/attendance</c> → <c>AttendanceService.GetDailyAsync</c> reads
/// <see cref="AttendanceDailyRecord"/>. <c>GET /api/attendance/monthly</c> →
/// <c>GetMonthlyAsync</c> reads the SAME table. Every demo seeder wrote only the legacy
/// <see cref="AttendanceRecord"/> projection, so both endpoints were correct and both were
/// empty — 4,261 legacy rows tenant-wide and 0 rows in the table the API actually reads.</para>
///
/// <para>This type is the single place where demo attendance is manufactured. It exists so the
/// derivation lives ONCE rather than in six seeders, and so that the numbers a demo shows are the
/// numbers the real pipeline would compute. Every field below is derived exactly as
/// <c>AttendanceService.ProcessEmployeeDay</c> derives it; the legacy projection is written exactly
/// as <c>AttendanceService.UpsertLegacyRecord</c> writes it. If you change one, change the other —
/// a seeder whose arithmetic diverges from the pipeline is a demo that lies.</para>
///
/// <para><b>Punches are the input, everything else is output.</b> Seeders used to pick a status
/// ("Present") and an overtime figure out of a random number generator while leaving TimeIn/TimeOut
/// null — a "Present" day with no punches and 3 hours of invented overtime. Here a seeder supplies
/// only the punch times it wants; status, worked minutes, late/early/overtime/undertime and
/// missing-punch all fall out of those punches through the pipeline's own formulas.</para>
///
/// <para><b>Raw events are written too.</b> The punches that justify each day are persisted as
/// <see cref="AttendanceRawEvent"/> rows, so re-running <c>POST /api/attendance/process</c> over a
/// seeded range recomputes the identical daily record instead of wiping the demo to "Absent".</para>
///
/// <para><b>CompanyId is stamped on the legacy row</b> (issue #55). <see cref="AttendanceRecord"/>
/// is <see cref="ICompanyScopedOperational"/>, so a null company makes the row invisible to every
/// company-scoped user — the same poison-default class as the device-ingest defect fixed in #45.
/// 4,036 of the 4,261 seeded rows were born null. The company is taken from the owning employee,
/// exactly as <c>UpsertLegacyRecord</c> now takes it.</para>
/// </summary>
public static class AttendanceDemoSeed
{
    /// <summary>
    /// Why a seeded day was not worked. <c>ProcessEmployeeDay</c> discovers this by querying
    /// LeaveRequests / PublicHolidays / the work week; a seeder already knows its own intent and
    /// states it here. Only <see cref="ApprovedLeave"/> requires the seeder to have actually
    /// created the backing leave request — do not pass it otherwise.
    /// </summary>
    public enum DayContext { WorkingDay, ApprovedLeave, PublicHoliday, RestDay }

    /// <summary>The employee facts a daily record denormalises. Mirrors what
    /// <c>ProcessEmployeeDay</c> copies off the tracked <see cref="Employee"/>.</summary>
    public readonly record struct EmployeeFacts(int Id, string FullName, string Department, string Branch, Guid? CompanyId)
    {
        public static EmployeeFacts From(Employee e) => new(e.Id, e.FullName, e.Department, e.Branch, e.CompanyId);
    }

    /// <summary>
    /// The tenant's active attendance policy, or the identical in-memory default
    /// <c>AttendanceService.DefaultPolicy</c> falls back to. Seeders must derive against the SAME
    /// policy the pipeline would use, or their break/overtime/half-day arithmetic silently diverges.
    /// </summary>
    public static async Task<AttendancePolicy> ResolvePolicyAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
        => await db.AttendancePolicies.AsNoTracking()
               .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.IsActive, ct)
           ?? new AttendancePolicy { TenantId = tenantId, Code = "DEFAULT", Name = "Default attendance policy" };

    /// <summary>
    /// Mirrors <c>AttendanceService.ResolveTenantTimeZoneAsync</c>. Seeder punch times are LOCAL
    /// wall-clock ("08:30 means 08:30 to the employee"); the daily record stores UTC. Getting this
    /// wrong is how a Riyadh tenant ends up with every employee three hours late.
    /// </summary>
    public static async Task<TimeZoneInfo> ResolveTimeZoneAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        var tzId = await db.TenantLocalizationSettings.AsNoTracking()
            .Where(l => l.TenantId == tenantId).Select(l => l.DefaultTimezone).FirstOrDefaultAsync(ct);
        try { return string.IsNullOrWhiteSpace(tzId) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(tzId); }
        catch { return TimeZoneInfo.Utc; }
    }

    private static DateTime ToUtc(DateOnly date, TimeOnly local, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date.ToDateTime(local), DateTimeKind.Unspecified), tz);

    /// <summary>
    /// Adds one employee-day: the raw punches, the <see cref="AttendanceDailyRecord"/> the API reads,
    /// and the legacy <see cref="AttendanceRecord"/> projection. Caller saves.
    /// </summary>
    /// <param name="firstInLocal">Local wall-clock clock-in, or null for a day with no in-punch.</param>
    /// <param name="lastOutLocal">Local wall-clock clock-out, or null for a day with no out-punch.</param>
    /// <param name="shiftStartLocal">
    /// The employee's scheduled local start. Null selects the same 09:00 fallback
    /// <c>ProcessEmployeeDay</c> uses when no <c>ShiftAssignment</c> exists for the date.
    /// </param>
    public static void AddDay(
        ZayraDbContext db,
        Guid tenantId,
        EmployeeFacts employee,
        DateOnly workDate,
        TimeOnly? firstInLocal,
        TimeOnly? lastOutLocal,
        AttendancePolicy policy,
        TimeZoneInfo timeZone,
        DayContext context = DayContext.WorkingDay,
        TimeOnly? shiftStartLocal = null)
    {
        var firstInUtc = firstInLocal is null ? (DateTime?)null : ToUtc(workDate, firstInLocal.Value, timeZone);
        var lastOutUtc = lastOutLocal is null ? (DateTime?)null : ToUtc(workDate, lastOutLocal.Value, timeZone);

        var daily = new AttendanceDailyRecord
        {
            TenantId     = tenantId,
            EmployeeId   = employee.Id,
            EmployeeName = employee.FullName,
            Department   = employee.Department,
            Branch       = employee.Branch,
            WorkDate     = workDate,
            FirstInUtc   = firstInUtc,
            LastOutUtc   = lastOutUtc,
        };

        // ── Verbatim ProcessEmployeeDay arithmetic ────────────────────────────────
        daily.MissingPunch = daily.FirstInUtc is null || daily.LastOutUtc is null;
        daily.BreakMinutes = daily.MissingPunch ? 0 : policy.BreakMinutes;
        daily.TotalWorkedMinutes = daily.FirstInUtc is not null && daily.LastOutUtc is not null
            ? Math.Max(0, (int)(daily.LastOutUtc.Value - daily.FirstInUtc.Value).TotalMinutes - policy.BreakMinutes)
            : 0;

        // No seeder creates ShiftAssignments, so the pipeline's no-shift branch is the honest one:
        // 09:00 LOCAL start, end = start + standard work + break.
        var shiftStart = ToUtc(workDate, shiftStartLocal ?? new TimeOnly(9, 0), timeZone);
        var shiftEnd   = shiftStart.AddMinutes(policy.StandardWorkMinutes + policy.BreakMinutes);

        daily.LateMinutes = daily.FirstInUtc is null
            ? 0 : Math.Max(0, (int)(daily.FirstInUtc.Value - shiftStart).TotalMinutes - policy.GraceMinutes);
        daily.EarlyExitMinutes = daily.LastOutUtc is null
            ? 0 : Math.Max(0, (int)(shiftEnd - daily.LastOutUtc.Value).TotalMinutes - policy.EarlyExitThresholdMinutes);
        daily.OvertimeMinutes  = Math.Max(0, daily.TotalWorkedMinutes - policy.StandardWorkMinutes);
        daily.UndertimeMinutes = Math.Max(0, policy.StandardWorkMinutes - daily.TotalWorkedMinutes);

        if (daily.TotalWorkedMinutes == 0 && context == DayContext.ApprovedLeave)
        {
            daily.Status = "On leave";
            daily.MissingPunch = false;
            daily.LateMinutes = daily.EarlyExitMinutes = daily.UndertimeMinutes = 0;
        }
        else if (daily.TotalWorkedMinutes == 0 && context == DayContext.PublicHoliday)
        {
            daily.Status = "Public holiday";
            daily.MissingPunch = false;
            daily.LateMinutes = daily.EarlyExitMinutes = daily.UndertimeMinutes = 0;
        }
        else if (daily.TotalWorkedMinutes == 0 && context == DayContext.RestDay)
        {
            daily.Status = "Rest day";
            daily.MissingPunch = false;
            daily.LateMinutes = daily.EarlyExitMinutes = daily.UndertimeMinutes = 0;
        }
        else
        {
            daily.Status = daily.TotalWorkedMinutes == 0 ? "Absent"
                : daily.TotalWorkedMinutes < policy.HalfDayThresholdMinutes ? "Half day"
                : daily.LateMinutes > 0 ? "Late" : "Present";
        }

        daily.ProcessedAtUtc = DateTime.UtcNow;
        daily.UpdatedAtUtc   = DateTime.UtcNow;
        db.AttendanceDailyRecords.Add(daily);

        // ── The punches that justify the numbers above ────────────────────────────
        // Persisted so a demo re-process reproduces this exact day rather than erasing it.
        if (firstInUtc is not null) db.AttendanceRawEvents.Add(RawEvent(tenantId, employee.Id, firstInUtc.Value, "In"));
        if (lastOutUtc is not null) db.AttendanceRawEvents.Add(RawEvent(tenantId, employee.Id, lastOutUtc.Value, "Out"));

        // ── Verbatim UpsertLegacyRecord projection ────────────────────────────────
        db.AttendanceRecords.Add(new AttendanceRecord
        {
            TenantId   = tenantId,
            // Issue #55: ICompanyScopedOperational with a null company is invisible to every
            // company-scoped user. Taken from the owning employee, as UpsertLegacyRecord does.
            CompanyId  = employee.CompanyId,
            EmployeeId = employee.Id,
            WorkDate   = workDate,
            TimeIn     = daily.FirstInUtc is null ? null : TimeOnly.FromDateTime(daily.FirstInUtc.Value),
            TimeOut    = daily.LastOutUtc is null ? null : TimeOnly.FromDateTime(daily.LastOutUtc.Value),
            OvertimeHours = Math.Round(daily.OvertimeMinutes / 60m, 2),
            Status     = daily.Status,
            Notes      = daily.MissingPunch ? "Missing punch" : "",
        });
    }

    private static AttendanceRawEvent RawEvent(Guid tenantId, int employeeId, DateTime punchUtc, string direction) => new()
    {
        TenantId           = tenantId,
        EmployeeId         = employeeId,
        Source             = "Demo seed",
        PunchTimestampUtc  = punchUtc,
        PunchDirection     = direction,
        VerificationMethod = "Manual",
        IsProcessed        = true,
    };
}
