using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Zayra.Api.Infrastructure.Jobs;

/// <summary>
/// F3 — appends <c>FOR UPDATE SKIP LOCKED</c> to a query that opted in with
/// <c>.TagWith(RowLockingInterceptor.ForUpdateSkipLockedTag)</c>.
///
/// <para>WHY AN INTERCEPTOR AND NOT RAW SQL. EF Core 8 has no row-locking operator. The alternatives
/// were <c>FromSqlRaw</c> (banned by <c>BypassLintTests</c> because it bypasses the query filters) or a
/// hand-written SQL string that would also bypass <c>ScopedBypass</c>. Tagging keeps the claim query a
/// normal LINQ query built by <c>ScopedBypass.SystemWide</c> — filtered, ordered and bounded there —
/// and adds only the locking clause, which PostgreSQL accepts after <c>LIMIT</c>.</para>
///
/// <para>SAFETY. The rewrite is keyed on the tag being the FIRST line of the command text, which is
/// where EF emits tags; any other command passes through untouched. A tagged command must run inside
/// a transaction (the row locks are held until commit) — <see cref="BackgroundJobStore"/> does that
/// inside the execution strategy, as <c>ExecutionStrategyLintTests</c> requires.</para>
/// </summary>
public sealed class RowLockingInterceptor : DbCommandInterceptor
{
    public const string ForUpdateSkipLockedTag = "zayra:for-update-skip-locked";
    private const string TagPrefix = "-- " + ForUpdateSkipLockedTag;

    public static readonly RowLockingInterceptor Instance = new();

    private RowLockingInterceptor() { }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Rewrite(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Rewrite(command);
        return ValueTask.FromResult(result);
    }

    internal static void Rewrite(DbCommand command)
    {
        var text = command.CommandText;
        if (!text.StartsWith(TagPrefix, StringComparison.Ordinal)) return;
        if (text.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)) return;
        command.CommandText = text.TrimEnd().TrimEnd(';') + "\nFOR UPDATE SKIP LOCKED";
    }
}
