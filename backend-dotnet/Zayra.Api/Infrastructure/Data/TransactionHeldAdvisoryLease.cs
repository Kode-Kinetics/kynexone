using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Zayra.Api.Infrastructure.Data;

/// <summary>
/// A PostgreSQL advisory lock that must outlive many short transactions (a long import that saves
/// progress as it goes), held in a way that survives a transaction-mode connection pooler.
///
/// <para>Production reaches Neon through its PgBouncer pooler in transaction mode: every autocommit
/// statement and every transaction may run on a different server session. A SESSION advisory lock
/// (<c>pg_advisory_lock</c>) is therefore not owned by the caller at all — it stays on whichever
/// pooled server session ran it, the later <c>pg_advisory_unlock</c> usually runs elsewhere and is
/// a no-op, and other requests that draw that session inherit the lock.</para>
///
/// <para>This lease instead opens its own connection, begins a transaction and takes
/// <c>pg_advisory_xact_lock</c> in it. The pooler pins one server session from BEGIN to the end of
/// the transaction, so the lock is held exactly as long as the lease, and disposing the lease rolls
/// the transaction back, which releases it. If the lease connection dies, Postgres aborts the
/// transaction and the lock is released rather than leaked. Callers doing long work should call
/// <see cref="EnsureHeldAsync"/> between steps: it keeps the lease transaction from sitting idle and
/// fails loudly if the lease has been lost.</para>
///
/// <para>The work itself runs on the caller's own DbContext connection, not on the lease
/// connection. Mutual exclusion is only between lease holders for the same key.</para>
/// </summary>
internal sealed class TransactionHeldAdvisoryLease : IAsyncDisposable
{
    private static readonly TransactionHeldAdvisoryLease None = new(null, null);

    private readonly NpgsqlConnection? _connection;
    private readonly NpgsqlTransaction? _transaction;
    private bool _disposed;

    private TransactionHeldAdvisoryLease(NpgsqlConnection? connection, NpgsqlTransaction? transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    /// <summary>True when a real lock is held (PostgreSQL); false for other providers.</summary>
    public bool IsHeld => _transaction is not null;

    /// <summary>
    /// Blocks until the lock for <paramref name="key"/> is acquired (bounded by the command timeout).
    /// A no-op lease is returned for non-PostgreSQL providers.
    /// </summary>
    public static async Task<TransactionHeldAdvisoryLease> AcquireAsync(DbContext db, long key, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return None;

        var source = (NpgsqlConnection)db.Database.GetDbConnection();
        var commandTimeout = db.Database.GetCommandTimeout();
        // Same retry policy as every other database call; each attempt starts from a fresh
        // connection, and a failed attempt disposes its own.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // Clone keeps the credentials (and the client-side pool) even after the source
            // connection has been opened and its ConnectionString no longer shows the password.
            var connection = (NpgsqlConnection)((ICloneable)source).Clone();
            try
            {
                await connection.OpenAsync(ct);
                var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                await using (var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
                {
                    if (commandTimeout is { } seconds) cmd.CommandTimeout = seconds;
                    cmd.Parameters.AddWithValue("key", key);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                return new TransactionHeldAdvisoryLease(connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        });
    }

    /// <summary>
    /// Proves the lease transaction is still alive (and stops it counting as idle). Throws
    /// <see cref="InvalidOperationException"/> if the lease has been lost, so the caller stops
    /// before doing unserialised work.
    /// </summary>
    public async Task EnsureHeldAsync(CancellationToken ct)
    {
        if (_transaction is null || _connection is null) return;
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT 1", _connection, _transaction);
            await cmd.ExecuteScalarAsync(ct);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "The advisory lease protecting this operation was lost; stopping before any unserialised work.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed || _connection is null) return;
        _disposed = true;
        try
        {
            // Rolling back ends the transaction and releases the xact lock. If the connection is
            // already broken the server has aborted the transaction itself.
            if (_transaction is not null) await _transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            // Nothing to release on a dead connection; disposing below discards it.
        }
        finally
        {
            if (_transaction is not null) await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
