using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using System.Data;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Setup;

/// <summary>
/// The tenant setup lock: <c>pg_advisory_xact_lock</c> on SHA-256("SETUP:" + tenant id), held until the
/// surrounding transaction commits or rolls back. ONE key, shared by every writer that creates a company or
/// applies setup for a tenant — Setup Studio's Apply, the company form (<c>CompanyEndpoints.CreateAsync</c>)
/// and the create-company wizard's commit — so the plan's company allowance, the CR/name uniqueness check and
/// the setup assistant's composite-scope policies are each read and written by one writer at a time.
///
/// <para>Writes no row and cannot widen tenant or company scope (the key is derived from the authenticated
/// tenant). Must run inside the operation's execution-strategy transaction:
/// <c>AdvisoryXactLockGuardInterceptor</c> refuses it otherwise. A no-op on a non-relational provider.</para>
/// </summary>
public static class SetupTenantLock
{
    /// <summary>The 64-bit advisory key for <paramref name="tenantId"/>. Unchanged from the original inline
    /// derivation in SetupAssistantController, so existing and new writers contend on the same lock.</summary>
    public static long KeyFor(Guid tenantId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"SETUP:{tenantId:N}"));
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(hash.AsSpan(0, 8));
    }

    public static async Task AcquireAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return;
        var key = KeyFor(tenantId);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
    }

    /// <summary>
    /// Runs an in-use check and the write it guards as ONE unit: a Serializable transaction holding the
    /// tenant setup lock, so two setup writers cannot both pass the check, and a concurrent payroll write
    /// that would change the check's answer makes one side fail instead of both succeeding.
    /// A plain call on a non-relational provider.
    /// </summary>
    public static Task<T> InLockedTransactionAsync<T>(ZayraDbContext db, Guid tenantId, Func<Task<T>> work, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return work();
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await AcquireAsync(db, tenantId, ct);
            var result = await work();
            await tx.CommitAsync(ct);
            return result;
        });
    }
}
