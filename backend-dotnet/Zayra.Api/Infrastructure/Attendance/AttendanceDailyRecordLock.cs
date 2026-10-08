using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Attendance;

/// <summary>
/// Serialises every write of one employee's daily attendance record for one work date (and what is derived with it: the
/// legacy <c>attendance_records</c> row, impacts, exceptions) behind a per-(tenant, employee, work date) advisory lock.
/// <para>Without it, two first-of-day punches racing each read "no daily record yet", each inserted one, and the loser
/// hit the unique index <c>(tenant_id, employee_id, work_date)</c>: an unhandled 23505, answered 500, with the punch's raw
/// event already committed. Under the lock the second writer waits, then (READ COMMITTED: each statement sees what has
/// committed) finds the first one's row and updates it.</para>
/// Transaction-scoped (<c>pg_advisory_xact_lock</c>); on a non-relational provider (tests) the work simply runs.
/// </summary>
public static class AttendanceDailyRecordLock
{
    public static string Key(Guid tenantId, int employeeId, DateOnly workDate) =>
        $"attendance-daily:{tenantId:N}:{employeeId}:{workDate:yyyy-MM-dd}";

    /// <summary>
    /// Runs <paramref name="work"/> (which must save its own changes) under the lock: inside the caller's open
    /// transaction when there is one, otherwise in a new one under the context's execution strategy (a retried attempt
    /// starts with a clean change tracker and re-reads everything).
    /// </summary>
    public static async Task RunAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateOnly workDate, Func<Task> work, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            await work();
            return;
        }
        if (db.Database.CurrentTransaction is not null)
        {
            await AcquireAsync(db, tenantId, employeeId, workDate, ct);
            await work();
            return;
        }
        var strategy = db.Database.CreateExecutionStrategy();
        var attempt = 0;
        await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            await AcquireAsync(db, tenantId, employeeId, workDate, ct);
            await work();
            await tx.CommitAsync(ct);
        });
    }

    private static async Task AcquireAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateOnly workDate, CancellationToken ct)
    {
        if (!(db.Database.ProviderName ?? string.Empty).Contains("Npgsql", StringComparison.OrdinalIgnoreCase)) return;
        var lockKey = Key(tenantId, employeeId, workDate);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
    }
}
