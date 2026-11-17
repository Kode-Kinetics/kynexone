using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// ONE rule for every door that records worked overtime against an employee: the manual request (web, Self-Service and
/// mobile all post to it) and detect-from-attendance. The same hours may be recorded once.
///
/// <para>The doors used to check different things. The manual door refused a window overlapping a live request; the
/// attendance door only skipped a day that already had an ATTENDANCE-sourced request, so hours keyed by hand and then
/// detected from the punches became two requests that could both be approved and paid.</para>
///
/// <para><b>What conflicts.</b> A live overtime request (any status other than <see cref="ClosedRequestStatuses"/>, so
/// pending, approved, paid or converted) of the same employee whose time window overlaps; or, on the same work date,
/// any live request when either side came from attendance, because an attendance-detected request stands for the day's
/// whole measured overtime, not just the window it is stamped with.</para>
///
/// <para><b>Atomicity.</b> The check and the insert run inside one transaction holding
/// <c>pg_advisory_xact_lock(overtime-entry:{tenant}:{employee})</c>. Two doors racing for the same employee serialise;
/// the second one re-reads under READ COMMITTED and sees the first one's row. Lock order: a caller holding several takes
/// them in ascending employee id (detect walks its days in that order); nothing holding this lock waits for any other
/// advisory or row lock, so no cycle can form with the payroll order (payroll.batch-payment → payroll_runs → loans →
/// advances). On a non-PostgreSQL provider (unit tests) the lock is skipped and the work simply runs.</para>
/// </summary>
public static class OvertimeEntryGuard
{
    /// <summary>Overtime request statuses that no longer stand for payable or approvable work.</summary>
    public static readonly string[] ClosedRequestStatuses = { "Rejected", "Cancelled", "Withdrawn" };

    public const string AttendanceSource = "Attendance";

    public static string LockKey(Guid tenantId, int employeeId) => $"overtime-entry:{tenantId:N}:{employeeId}";

    public enum ConflictKind { None, OvertimeRequest }

    /// <summary>
    /// Whether recording these hours would record work that is already recorded (see the class remarks).
    /// <paramref name="wholeDay"/> is true for an attendance-detected entry, which stands for the whole work date.
    /// </summary>
    public static async Task<ConflictKind> FindConflictAsync(
        ZayraDbContext db, Guid tenantId, int employeeId, DateOnly workDate, DateTime startUtc, DateTime endUtc,
        bool wholeDay, CancellationToken ct)
    {
        var requestConflict = await db.OvertimeRequests.AsNoTracking().AnyAsync(x =>
            x.TenantId == tenantId && x.EmployeeId == employeeId && !ClosedRequestStatuses.Contains(x.Status)
            && ((startUtc < x.EndTimeUtc && endUtc > x.StartTimeUtc)
                || (x.WorkDate == workDate && (wholeDay || x.Source == AttendanceSource))), ct);
        return requestConflict ? ConflictKind.OvertimeRequest : ConflictKind.None;
    }

    /// <summary>The refusal every overtime door returns for a conflict, in English and Arabic.</summary>
    public static object Refusal(ConflictKind kind) => new
    {
        code = "overtime_overlaps_existing",
        message = "This overtime request overlaps an existing request.",
        messageAr = "طلب العمل الإضافي هذا يتداخل مع طلب قائم.",
    };

    /// <summary>
    /// Runs <paramref name="work"/> (which must save its own changes) holding the overtime-entry lock of every employee in
    /// <paramref name="employeeIds"/>, taken in ascending id order, in one transaction. Inside an already open transaction
    /// the locks join it; otherwise a new READ COMMITTED transaction is opened under the execution strategy. The work may
    /// return early (a refusal) without saving: the transaction then commits nothing.
    /// </summary>
    public static async Task<T> RunAsync<T>(ZayraDbContext db, Guid tenantId, IEnumerable<int> employeeIds, Func<Task<T>> work, CancellationToken ct)
    {
        var ordered = employeeIds.Distinct().OrderBy(x => x).ToList();
        if (!db.Database.IsRelational()) return await work();
        if (db.Database.CurrentTransaction is not null)
        {
            foreach (var id in ordered) await AcquireAsync(db, tenantId, id, ct);
            return await work();
        }
        var callerHadChanges = db.ChangeTracker.HasChanges();
        var strategy = db.Database.CreateExecutionStrategy();
        var attempt = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0)
            {
                if (callerHadChanges)
                    throw new InvalidOperationException("The overtime entry failed on a transient database error and was not retried, because it carried other unsaved changes. Try again.");
                db.ChangeTracker.Clear();
            }
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            foreach (var id in ordered) await AcquireAsync(db, tenantId, id, ct);
            var result = await work();
            await tx.CommitAsync(ct);
            return result;
        });
    }

    /// <summary>Takes one employee's overtime-entry lock in the caller's open transaction (a no-op off PostgreSQL).</summary>
    public static async Task AcquireAsync(ZayraDbContext db, Guid tenantId, int employeeId, CancellationToken ct)
    {
        if (!(db.Database.ProviderName ?? string.Empty).Contains("Npgsql", StringComparison.OrdinalIgnoreCase)) return;
        var key = LockKey(tenantId, employeeId);
        // A read-side statement: the lock writes no row. Transaction-scoped, released at commit or rollback.
        await db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\" FROM (SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))) AS t").ToListAsync(ct);
    }
}
