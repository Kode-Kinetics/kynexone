using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// Runs a user-permission-graph read inside one explicit transaction so that split queries
/// (AsSplitQuery) observe one coherent graph.
///
/// <para>Why split at all: the auth graph includes roles→role-permissions, overrides, employee
/// accounts and entity grants. As ONE query the result set is their PRODUCT (3 roles × 150
/// permissions × overrides × grants), and that burst OOM-killed the 512 MB API instance
/// 12 times on 2026-09-20/21. Split queries make the cost the SUM of the collections.</para>
///
/// <para>Security contract: a split query is several statements. Outside a transaction, under
/// READ COMMITTED, each could observe a different committed state and assemble an authorization
/// graph that never existed. So AsSplitQuery is allowed ONLY when the query is deterministic
/// (keyed by primary key or a unique key) AND runs inside an explicit transaction: either the
/// caller's, which holds the row-lock anchors (user FOR UPDATE / tenant FOR SHARE), or the
/// non-locking snapshot this helper opens (REPEATABLE READ on PostgreSQL, SERIALIZABLE
/// elsewhere) — the same isolation TenantSessionSecurity uses.</para>
/// </summary>
public static class AuthGraphSnapshot
{
    public static IsolationLevel SnapshotIsolation(DatabaseFacade database) =>
        database.IsNpgsql() ? IsolationLevel.RepeatableRead : IsolationLevel.Serializable;

    public static async Task<T> ReadAsync<T>(
        ZayraDbContext db,
        Func<CancellationToken, Task<T>> read,
        CancellationToken ct)
    {
        // Non-relational providers ignore query splitting; an ambient transaction is the
        // caller's anchored unit of work, and the read must join it rather than nest.
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
            return await read(ct);

        // Production enables a retrying execution strategy, so the transaction lives inside it.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(SnapshotIsolation(db.Database), ct);
            var result = await read(ct);
            await transaction.CommitAsync(ct);
            return result;
        });
    }
}
