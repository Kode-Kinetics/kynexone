using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>
/// The attendance side of a payroll lock. Nothing used to write it: <c>AttendanceLockPeriods</c> stayed empty and
/// <c>AttendanceDailyRecord.IsPayrollLocked</c> stayed false, so the processor's lock checks, the correction refusal and
/// leave's [FLAG-PAYROLL] path never fired, and a locked (paid, posted) month's days could be rewritten by a range run,
/// a correction or a late punch.
///
/// <para><b>Per employee, not per tenant.</b> A payroll run locks ONE company's period. Locking the whole tenant's month
/// would freeze every other company's attendance until its own payroll ran. So the lock is the run's employees' daily
/// records (<c>IsPayrollLocked</c>), plus one <c>AttendanceLockPeriod</c> row recording which run locked which period
/// (<see cref="RunLockType"/>), which the tenant-wide checks ignore. A void releases both; a reopen cannot reach a
/// locked run, but releases anything left for it all the same.</para>
/// </summary>
public static class AttendancePayrollLock
{
    /// <summary>The lock period row's <c>LockType</c> for a run: "PayrollRun:{id}". A tenant-wide lock is any other type.</summary>
    public static string RunLockType(Guid runId) => $"PayrollRun:{runId}";

    public const string RunLockPrefix = "PayrollRun:";

    private const string Justification =
        "Locking or releasing a payroll run's attendance is a system step over the run's whole population, whatever the " +
        "caller's company scope; the employees are the run's own slips and nothing is returned.";

    /// <summary>Locks the run's employees' attendance days in the run's month. Idempotent.</summary>
    public static async Task LockAsync(ZayraDbContext db, Guid tenantId, Guid runId, int year, int month, Guid? userId, CancellationToken ct)
    {
        var (from, to) = Period(year, month);
        var employeeIds = await RunEmployeesAsync(db, tenantId, runId, ct);
        if (employeeIds.Count > 0)
            await db.AttendanceDailyRecords
                .Where(d => d.TenantId == tenantId && employeeIds.Contains(d.EmployeeId) && d.WorkDate >= from && d.WorkDate <= to && !d.IsPayrollLocked)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsPayrollLocked, true), ct);
        var lockType = RunLockType(runId);
        if (!await db.AttendanceLockPeriods.AnyAsync(x => x.TenantId == tenantId && x.LockType == lockType, ct))
            db.AttendanceLockPeriods.Add(new AttendanceLockPeriod
            {
                TenantId = tenantId, PeriodStart = from, PeriodEnd = to, LockType = lockType, Status = "Locked", LockedByUserId = userId,
            });
    }

    /// <summary>
    /// Releases what <see cref="LockAsync"/> locked for the run: its lock period row, and the lock on its employees' days —
    /// except for employees another still-locked run of the same month also covers.
    /// </summary>
    public static async Task ReleaseAsync(ZayraDbContext db, Guid tenantId, Guid runId, int year, int month, CancellationToken ct)
    {
        var (from, to) = Period(year, month);
        var lockType = RunLockType(runId);
        await db.AttendanceLockPeriods.Where(x => x.TenantId == tenantId && x.LockType == lockType).ExecuteDeleteAsync(ct);
        var employeeIds = await RunEmployeesAsync(db, tenantId, runId, ct);
        if (employeeIds.Count == 0) return;
        var stillLockedRuns = ScopedBypass.TenantWide(db.PayrollRuns, tenantId, Justification)
            .Where(r => r.Id != runId && r.Year == year && r.Month == month && (r.Status == "Locked" || r.Status == "Paid"));
        var stillLocked = await ScopedBypass.TenantWide(db.PayrollSlips, tenantId, Justification)
            .Where(s => employeeIds.Contains(s.EmployeeId) && s.Status != "Voided" && stillLockedRuns.Any(r => r.Id == s.RunId))
            .Select(s => s.EmployeeId).Distinct().ToListAsync(ct);
        var release = employeeIds.Except(stillLocked).ToList();
        if (release.Count > 0)
            await db.AttendanceDailyRecords
                .Where(d => d.TenantId == tenantId && release.Contains(d.EmployeeId) && d.WorkDate >= from && d.WorkDate <= to && d.IsPayrollLocked)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsPayrollLocked, false), ct);
    }

    /// <summary>
    /// Whether the employee's day may not be rewritten: a tenant-wide lock period covers it (anything but a run's own
    /// row), or the day itself is locked by a payroll run.
    /// </summary>
    public static async Task<bool> IsLockedAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateOnly date, CancellationToken ct) =>
        await IsTenantLockedAsync(db, tenantId, date, date, ct)
        || await db.AttendanceDailyRecords.AnyAsync(d => d.TenantId == tenantId && d.EmployeeId == employeeId && d.WorkDate == date && d.IsPayrollLocked, ct);

    /// <summary>A tenant-wide lock period overlapping [from, to] (a run's own row does not lock the tenant).</summary>
    public static Task<bool> IsTenantLockedAsync(ZayraDbContext db, Guid tenantId, DateOnly from, DateOnly to, CancellationToken ct) =>
        db.AttendanceLockPeriods.AnyAsync(x => x.TenantId == tenantId && x.Status == "Locked" && x.PeriodStart <= to && x.PeriodEnd >= from
            && !x.LockType.StartsWith(RunLockPrefix), ct);

    private static (DateOnly From, DateOnly To) Period(int year, int month)
    {
        var from = new DateOnly(year, month, 1);
        return (from, from.AddMonths(1).AddDays(-1));
    }

    private static Task<List<int>> RunEmployeesAsync(ZayraDbContext db, Guid tenantId, Guid runId, CancellationToken ct) =>
        ScopedBypass.TenantWide(db.PayrollSlips, tenantId, Justification)
            .Where(s => s.RunId == runId).Select(s => s.EmployeeId).Distinct().ToListAsync(ct);
}
