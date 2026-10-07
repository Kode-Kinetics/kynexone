using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Operations;

/// <summary>
/// "Only one instance runs this tick" for the periodic in-process sweeps that are not driven by
/// <c>background_jobs</c> (loan lifecycle, compliance reminders, AI insights). Every API instance
/// hosts every worker; once the service runs on more than one instance — or during a deploy, when the
/// old and new instance overlap — two copies of the same sweep would otherwise run at once.
///
/// <para>Built on <see cref="TransactionHeldAdvisoryLease"/>: a transaction-scoped advisory lock held
/// on the lease's own connection, which survives Neon's transaction-mode pooler and is released the
/// moment the tick ends or the connection dies. Session advisory locks are banned
/// (<c>SessionAdvisoryLockRatchetTests</c>). Non-blocking: an instance that loses the race skips the
/// tick, it does not queue behind the winner.</para>
///
/// <para>The lease prevents CONCURRENT ticks. Ticks that run one after the other on different
/// instances are each made safe by the sweep's own state (a reminder is no longer Pending, an insight
/// type already exists inside its 24h window, a loan snapshot is already current).</para>
/// </summary>
internal static class SingletonWorkerLease
{
    /// <summary>Stable 64-bit key: first 8 bytes (big-endian) of SHA256("singleton-worker:" + name).</summary>
    public static long LockKey(string workerName)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"singleton-worker:{workerName}"));
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(digest.AsSpan(0, 8));
    }

    /// <summary>
    /// Takes the cluster-wide lease for <paramref name="workerName"/> without waiting. Null means
    /// another instance is running this sweep right now; the caller skips the tick. On providers other
    /// than PostgreSQL (unit tests) a no-op lease is returned, so the tick always runs.
    /// </summary>
    public static Task<TransactionHeldAdvisoryLease?> TryAcquireAsync(DbContext db, string workerName, CancellationToken ct) =>
        TransactionHeldAdvisoryLease.TryAcquireAsync(db, LockKey(workerName), ct);
}

/// <summary>Lease names for sweeps that report no heartbeat of their own.</summary>
internal static class SingletonWorkerNames
{
    public const string LoanLifecycle = "loan-lifecycle";
}
