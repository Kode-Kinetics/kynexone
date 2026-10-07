using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Zayra.Api.Infrastructure.Data;

/// <summary>
/// Refuses to run <c>pg_advisory_xact_lock</c> / <c>pg_try_advisory_xact_lock</c> outside a
/// transaction. In autocommit the lock is released the instant the statement ends, so the caller
/// would believe it is serialised while holding nothing — a silent no-op that no test against an
/// idle database would ever notice. Every advisory lock in Zayra.Api is transaction-scoped
/// (session locks are banned by <c>SessionAdvisoryLockRatchetTests</c>), so one guard here covers
/// every site that goes through EF. <see cref="TransactionHeldAdvisoryLease"/> uses its own raw
/// connection and always runs inside its own transaction.
/// </summary>
public sealed class AdvisoryXactLockGuardInterceptor : DbCommandInterceptor
{
    public static readonly AdvisoryXactLockGuardInterceptor Instance = new();

    private AdvisoryXactLockGuardInterceptor() { }

    internal static void Check(DbCommand command)
    {
        if (command.Transaction is null
            && command.CommandText.Contains("advisory_xact_lock", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "A transaction-scoped advisory lock was requested outside a transaction; it would be released " +
                "immediately and serialise nothing. Take it inside the operation's execution-strategy transaction.");
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Check(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }
}
