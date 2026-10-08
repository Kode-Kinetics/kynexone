using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>
/// Serialises every write of one employee's daily attendance record for one work date (and what is derived with it: the
/// legacy <c>attendance_records</c> row, impacts, exceptions) behind a per-(tenant, employee, work date) advisory lock,
/// and the creation of a tenant's first default attendance policy behind a per-tenant one.
/// <para>Without it, two first-of-day punches racing each read "no daily record yet", each inserted one, and the loser
/// hit the unique index <c>(tenant_id, employee_id, work_date)</c>: an unhandled 23505, answered 500, with the punch's raw
/// event already committed. The same happened between a punch and the day-processing job or device ingest, and between
/// two employees creating the tenant's first <c>DEFAULT</c> policy (unique <c>(tenant_id, code)</c>). Under the lock the
/// second writer waits, then (READ COMMITTED: each statement sees what has committed) finds the first one's row.</para>
/// <para><b>Deadlocks.</b> Every caller takes daily-record locks one at a time in one global order — employee id
/// ascending, then work date ascending — or one employee-day per transaction; a punch takes exactly one; a processing-job
/// item takes at most one employee-month. The policy lock is taken either in its own short transaction (which takes no
/// other lock), or — HR's correction approval only — AFTER that approval's single daily-record lock. Nobody holding the
/// policy lock ever waits for a daily-record lock, so no cycle can form.</para>
/// Transaction-scoped (<c>pg_advisory_xact_lock</c>); on a non-relational provider (tests) the work simply runs.
/// </summary>
public static class AttendanceDailyRecordLock
{
    public static string Key(Guid tenantId, int employeeId, DateOnly workDate) =>
        $"attendance-daily:{tenantId:N}:{employeeId}:{workDate:yyyy-MM-dd}";

    public static string DefaultPolicyKey(Guid tenantId) => $"attendance-default-policy:{tenantId:N}";

    /// <summary>
    /// Runs <paramref name="work"/> (which must save its own changes) under the employee-day lock: inside the caller's
    /// open transaction when there is one (the lock is then held until that transaction ends), otherwise in a new one
    /// under the context's execution strategy.
    /// </summary>
    public static Task RunAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateOnly workDate, Func<Task> work, CancellationToken ct) =>
        RunUnderAsync(db, Key(tenantId, employeeId, workDate), async () => { await work(); return true; }, ct);

    /// <summary>Runs <paramref name="work"/> (which must save its own changes) under the tenant's default-policy lock.</summary>
    public static Task<T> RunForDefaultPolicyAsync<T>(ZayraDbContext db, Guid tenantId, Func<Task<T>> work, CancellationToken ct) =>
        RunUnderAsync(db, DefaultPolicyKey(tenantId), work, ct);

    private static async Task<T> RunUnderAsync<T>(ZayraDbContext db, string lockKey, Func<Task<T>> work, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return await work();
        if (db.Database.CurrentTransaction is not null)
        {
            await AcquireAsync(db, lockKey, ct);
            return await work();
        }
        // A retried attempt starts with a clean change tracker and re-reads everything — unless the caller handed in
        // unsaved changes of its own (they would be lost), in which case the transient failure is surfaced instead.
        var callerHadChanges = db.ChangeTracker.HasChanges();
        var strategy = db.Database.CreateExecutionStrategy();
        var attempt = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0)
            {
                if (callerHadChanges)
                    throw new InvalidOperationException("The attendance write failed on a transient database error and was not retried, because it carried other unsaved changes. Try again.");
                db.ChangeTracker.Clear();
            }
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            await AcquireAsync(db, lockKey, ct);
            var result = await work();
            await tx.CommitAsync(ct);
            return result;
        });
    }

    private static async Task AcquireAsync(ZayraDbContext db, string lockKey, CancellationToken ct)
    {
        if (!(db.Database.ProviderName ?? string.Empty).Contains("Npgsql", StringComparison.OrdinalIgnoreCase)) return;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
    }
}
