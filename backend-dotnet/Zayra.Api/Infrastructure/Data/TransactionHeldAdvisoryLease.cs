using System.Data;
using System.Globalization;
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
/// <c>pg_try_advisory_xact_lock</c> in it. The pooler pins one server session from BEGIN to the end
/// of the transaction, so the lock is held exactly as long as the lease, and disposing the lease
/// rolls the transaction back, which releases it. If the lease connection dies, Postgres aborts the
/// transaction and the lock is released rather than leaked.</para>
///
/// <para>Lifetime. Right after taking the lock the lease runs
/// <c>SET LOCAL idle_in_transaction_session_timeout</c> (<see cref="DefaultIdleCeiling"/>), a hard
/// ceiling that overrides any role default, and a background keepalive re-issues that same
/// <c>SET LOCAL</c> every <see cref="DefaultKeepaliveInterval"/> so a long section never trips it.
/// The keepalive is deliberately a utility statement, not a query: it reads nothing and takes no
/// snapshot, and because it replaces the extended-protocol unnamed portal it also drops the
/// snapshot the lock query's portal would otherwise pin until the transaction ends. The lease never
/// writes, so it is never assigned a transaction id. Net effect: the lease does not hold back the
/// xmin horizon (vacuum) however long it lives. If the process stalls past the ceiling, Postgres
/// ends the lease and the lock is freed; <see cref="EnsureHeldAsync"/> then fails, so the caller
/// stops before unserialised work.</para>
///
/// <para>This file is the one sanctioned use of a raw <see cref="NpgsqlConnection"/> in Zayra.Api
/// (<c>RawNpgsqlUsageRatchetTests</c>). It bypasses the EF interceptors, which is safe only because
/// it touches no table: it runs the lock query and the <c>SET LOCAL</c>, nothing else.
/// The work itself runs on the caller's own DbContext connection. Mutual exclusion is only between
/// lease holders for the same key.</para>
/// </summary>
internal sealed class TransactionHeldAdvisoryLease : IAsyncDisposable
{
    internal static readonly TimeSpan DefaultIdleCeiling = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan DefaultKeepaliveInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Absolute lease age. Past it the keepalive stops renewing and ends the lease transaction, which
    /// releases the package lock, so a stuck-but-alive import cannot hold it forever. The import's
    /// next <see cref="EnsureHeldAsync"/> fails, the batch is left for resume, and a resume can take
    /// the lock.
    /// </summary>
    internal static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(4);

    /// <param name="IdleCeiling">Hard <c>idle_in_transaction_session_timeout</c> for the lease transaction.</param>
    /// <param name="KeepaliveInterval">Keepalive period; <see cref="Timeout.InfiniteTimeSpan"/> disables it (tests only).</param>
    internal sealed record LeaseOptions(TimeSpan IdleCeiling, TimeSpan KeepaliveInterval)
    {
        public static readonly LeaseOptions Default = new(DefaultIdleCeiling, DefaultKeepaliveInterval);

        public TimeSpan MaxAge { get; init; } = DefaultMaxAge;

        /// <summary>Test seam: runs before each keepalive renewal (used to inject a faulting ping).</summary>
        internal Func<Task>? BeforeKeepalive { get; init; }
    }

    private static readonly TransactionHeldAdvisoryLease None = new(null, null, string.Empty);

    private readonly NpgsqlConnection? _connection;
    private readonly NpgsqlTransaction? _transaction;
    private readonly string _keepaliveSql;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task _keepalive = Task.CompletedTask;
    private long _acquiredAt;
    private TimeSpan _maxAge = DefaultMaxAge;
    private Func<Task>? _beforeKeepalive;
    private volatile bool _lost;
    private bool _disposed;

    private TransactionHeldAdvisoryLease(NpgsqlConnection? connection, NpgsqlTransaction? transaction, string keepaliveSql)
    {
        _connection = connection;
        _transaction = transaction;
        _keepaliveSql = keepaliveSql;
    }

    // SET takes no bind parameters; the value is a formatted integer, so nothing is interpolated
    // from outside.
    private static string IdleCeilingSql(TimeSpan ceiling) =>
        "SET LOCAL idle_in_transaction_session_timeout = "
        + ((long)ceiling.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

    /// <summary>True while a real lock is held (PostgreSQL); false for other providers or once lost.</summary>
    public bool IsHeld => _transaction is not null && !_lost;

    /// <summary>True once the lease is known to be gone (connection ended, idle ceiling, keepalive failure).</summary>
    public bool IsLost => _lost;

    /// <summary>
    /// Takes the lock for <paramref name="key"/> without waiting. Returns null if another holder has
    /// it, and a no-op lease for non-PostgreSQL providers.
    /// </summary>
    public static Task<TransactionHeldAdvisoryLease?> TryAcquireAsync(DbContext db, long key, CancellationToken ct) =>
        TryAcquireAsync(db, key, LeaseOptions.Default, ct);

    internal static async Task<TransactionHeldAdvisoryLease?> TryAcquireAsync(
        DbContext db, long key, LeaseOptions options, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return None;
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException(
                "Acquire the advisory lease before opening a transaction on the DbContext. The lease lives on its own " +
                "connection: the caller's transaction neither sees nor owns it, and the retrying execution strategy " +
                "cannot run inside a user-initiated transaction.");

        var source = (NpgsqlConnection)db.Database.GetDbConnection();
        var commandTimeout = db.Database.GetCommandTimeout();
        var keepaliveSql = IdleCeilingSql(options.IdleCeiling);
        // Same retry policy as every other database call; each attempt starts from a fresh
        // connection, and a failed attempt disposes its own.
        var strategy = db.Database.CreateExecutionStrategy();
        var lease = await strategy.ExecuteAsync(async () =>
        {
            // Clone keeps the credentials (and the client-side pool) even after the source
            // connection has been opened and its ConnectionString no longer shows the password.
            var connection = (NpgsqlConnection)((ICloneable)source).Clone();
            try
            {
                await connection.OpenAsync(ct);
                var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                bool acquired;
                await using (var cmd = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", connection, transaction))
                {
                    if (commandTimeout is { } seconds) cmd.CommandTimeout = seconds;
                    cmd.Parameters.AddWithValue("key", key);
                    acquired = (bool)(await cmd.ExecuteScalarAsync(ct))!;
                }
                if (!acquired)
                {
                    await transaction.RollbackAsync(ct);
                    await connection.DisposeAsync();
                    return (TransactionHeldAdvisoryLease?)null;
                }
                // A hard ceiling for this transaction only, overriding whatever the role or database
                // sets. Also replaces the lock query's portal, releasing its snapshot.
                await using (var cmd = new NpgsqlCommand(keepaliveSql, connection, transaction))
                    await cmd.ExecuteNonQueryAsync(ct);
                return new TransactionHeldAdvisoryLease(connection, transaction, keepaliveSql);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        });
        if (lease is not null)
        {
            lease._acquiredAt = System.Diagnostics.Stopwatch.GetTimestamp();
            lease._maxAge = options.MaxAge;
            lease._beforeKeepalive = options.BeforeKeepalive;
            if (options.KeepaliveInterval != Timeout.InfiniteTimeSpan)
                lease._keepalive = lease.KeepaliveLoopAsync(options.KeepaliveInterval);
        }
        return lease;
    }

    /// <summary>
    /// Proves the lease transaction is still alive. Throws <see cref="InvalidOperationException"/> if
    /// the lease has been lost, so the caller stops before doing unserialised work.
    /// </summary>
    public async Task EnsureHeldAsync(CancellationToken ct)
    {
        if (_transaction is null) return;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPastMaxAge) _lost = true;
        if (!await PingAsync(ct))
            throw new InvalidOperationException(
                "The advisory lease protecting this operation was lost; stopping before any unserialised work.");
    }

    /// <summary>
    /// Non-throwing <see cref="EnsureHeldAsync"/>: pings the lease transaction and returns whether it is
    /// still alive. For a sweep to check at its end, so a lease that died after the last per-row
    /// <see cref="IsLost"/> check (the keepalive only notices every 30s) is not reported as a clean run.
    /// Always true for the no-op lease of a non-PostgreSQL provider.
    /// </summary>
    public async Task<bool> IsStillHeldAsync(CancellationToken ct)
    {
        if (_transaction is null) return true;
        if (_disposed) return false;
        if (IsPastMaxAge) _lost = true;
        return await PingAsync(ct);
    }

    private bool IsPastMaxAge => System.Diagnostics.Stopwatch.GetElapsedTime(_acquiredAt) >= _maxAge;

    /// <summary>Never faults: any failure marks the lease lost, and DisposeAsync still rolls back.</summary>
    private async Task KeepaliveLoopAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                if (IsPastMaxAge)
                {
                    _lost = true;
                    await EndTransactionAsync();
                    return;
                }
                if (_beforeKeepalive is not null) await _beforeKeepalive();
                if (!await PingAsync(CancellationToken.None)) return;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Disposed.
        }
        catch (Exception)
        {
            // Anything PingAsync did not classify: the lease can no longer be trusted.
            _lost = true;
        }
    }

    /// <summary>Rolls the lease transaction back (releasing the lock); safe to call more than once.</summary>
    private async Task EndTransactionAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_transaction is not null) await _transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // Already completed, or the connection is dead: either way nothing is held.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Re-issues the <c>SET LOCAL</c> ceiling on the lease transaction: no data, no snapshot.</summary>
    private async Task<bool> PingAsync(CancellationToken ct)
    {
        if (_lost) return false;
        await _gate.WaitAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand(_keepaliveSql, _connection, _transaction);
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            _lost = true;
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed || _connection is null) return;
        _disposed = true;
        _stop.Cancel();
        try
        {
            await _keepalive;
        }
        catch (Exception)
        {
            // The loop catches everything itself; this is belt and braces so that nothing it could
            // ever throw skips the rollback below.
        }
        try
        {
            // Rolling back ends the transaction and releases the xact lock. If the connection is
            // already broken the server has aborted the transaction itself.
            await EndTransactionAsync();
        }
        finally
        {
            try
            {
                if (_transaction is not null) await _transaction.DisposeAsync();
            }
            catch (Exception)
            {
                // Disposing the connection below discards whatever is left.
            }
            await _connection.DisposeAsync();
            _gate.Dispose();
            _stop.Dispose();
        }
    }
}
