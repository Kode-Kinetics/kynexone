using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// Serializes the money-moving state transitions of a single loan or salary advance, so that two
/// simultaneous approval decisions on the same row cannot both take effect.
///
/// <para><b>The defect this closes.</b> The correctness batch added status guards to
/// <c>LoansController.DecideApproval</c>, <c>LoansController.AddApprovalStep</c> and
/// <c>AdvancesController.Reject</c>. Those are read-then-write guards: they close SEQUENTIAL replay
/// (a second request that arrives after the first has committed) and nothing else. Two requests that
/// arrive at the same instant both read <c>Status == "Pending"</c>, both pass the guard, and both
/// write. For a loan that runs the disbursement block twice: <c>ApprovedAmount</c> and
/// <c>OutstandingBalance</c> are rewritten while <c>TotalRepaid</c> stands (breaking the
/// <c>ApprovedAmount − TotalRepaid − OutstandingBalance == 0</c> invariant the loan audit report
/// reconciles on), <c>DisbursementDate</c> is pushed forward, and <c>GenerateInstallments</c> runs a
/// second time into the unique <c>(TenantId, LoanId, InstallmentNumber)</c> index for an unhandled
/// 500. Only the GL posting was concurrency-safe, and only because the losing insert would have
/// duplicated a row the post-once probe happens to look for — which itself races.</para>
///
/// <para><b>The design.</b> A transaction-scoped PostgreSQL advisory lock keyed on
/// (scope, tenant, aggregate id), taken BEFORE the first read, with the whole unit — transaction,
/// lock, guards, writes, commit — inside the retrying execution strategy. Once the lock is held the
/// existing status guards become sufficient, because the critical section is genuinely serialized:
/// under READ COMMITTED the second decider's statements run against a snapshot taken after the first
/// committed, so it reads the NEW status and its guard refuses with the 409 it was always meant to
/// return. This is verbatim the shape <see cref="Zayra.Api.Infrastructure.Organization.EstablishmentGuardService"/>
/// already uses for concurrent hires against one budgeted slot, and it composes with the
/// compare-and-set discipline in <see cref="Zayra.Api.Infrastructure.Jobs.BackgroundJobStore"/>
/// rather than competing with it.</para>
///
/// <para><b>Retry safety.</b> <c>NpgsqlRetryingExecutionStrategy</c> may re-run the delegate from
/// scratch, so the delegate must be idempotent. It is, by construction:</para>
/// <list type="number">
///   <item>A retry only happens after the transaction ROLLED BACK, so no partial write survives into
///     the next attempt.</item>
///   <item><see cref="Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker.Clear"/> runs at the
///     top of the delegate, so entities added or modified by the abandoned attempt are not re-sent by
///     the next one — without it a retry would try to INSERT the same installment rows twice.</item>
///   <item>Every read happens inside the delegate, after the lock, so the retry re-derives its
///     decision from committed state rather than from a stale in-memory object.</item>
///   <item>The advisory lock is transaction-scoped: the rollback releases it, and the retry takes it
///     again. It can never be leaked or double-held.</item>
/// </list>
/// <para>The one residual is the classic ambiguous commit: if the COMMIT succeeds but the
/// acknowledgement is lost, the strategy re-runs, the re-read finds the loan no longer Pending, and
/// the caller receives 409 for a decision that in fact took effect. That is the safe direction of
/// error — no money moves twice, and the loan reads back Active — and it is the same property the job
/// queue and the establishment guard already have.</para>
/// </summary>
public static class FinanceDecisionSerializer
{
    public const string ScopeLoan = "finance.loan";
    public const string ScopeAdvance = "finance.advance";

    /// <summary>
    /// Runs <paramref name="body"/> as the sole writer of (<paramref name="scope"/>,
    /// <paramref name="tenantId"/>, <paramref name="aggregateId"/>).
    /// </summary>
    public static async Task<T> SerializeAsync<T>(
        ZayraDbContext db, string scope, Guid tenantId, Guid aggregateId,
        Func<Task<T>> body, CancellationToken ct = default)
    {
        // EF InMemory (the unit suite) has neither transactions nor advisory locks. It also has no
        // concurrency to defend against: every InMemory test is single-threaded against one context.
        // Running the body bare keeps those tests byte-identical — the racing proof is the real-Postgres
        // test, which is the only place a row lock means anything anyway.
        if (!db.Database.IsRelational())
            return await body();

        // A caller that has already opened a transaction (a composite write) joins it: taking the lock
        // here is still correct and the outer unit owns the commit.
        if (db.Database.CurrentTransaction is not null)
        {
            await AcquireAsync(db, scope, tenantId, aggregateId, ct);
            return await body();
        }

        // EnableRetryOnFailure forbids a bare BeginTransaction; the whole unit (open → commit) must
        // live inside the strategy delegate, and the delegate must be safe to re-run. See the class
        // remarks for why it is.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await AcquireAsync(db, scope, tenantId, aggregateId, ct);
            var result = await body();
            await tx.CommitAsync(ct);
            return result;
        });
    }

    /// <summary>
    /// Takes the transaction-scoped advisory lock. Released automatically at commit or rollback, so a
    /// crashed request cannot strand a loan.
    /// </summary>
    public static Task AcquireAsync(
        ZayraDbContext db, string scope, Guid tenantId, Guid aggregateId, CancellationToken ct = default)
    {
        if (!db.Database.IsRelational()) return Task.CompletedTask;
        var key = ComputeLockKey(scope, tenantId, aggregateId);
        // Interpolated value binds as a parameter, not as text.
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
    }

    /// <summary>
    /// Stable 64-bit key: first 8 bytes (big-endian) of SHA256(scope ‖ tenant ‖ aggregate). The scope
    /// string keeps the loan and advance key spaces apart, so a loan and an advance that happened to
    /// share an id do not serialize against each other. Public so a caller that must hold several
    /// locks at once can sort the keys before acquiring them and stay deadlock-free.
    /// </summary>
    public static long ComputeLockKey(string scope, Guid tenantId, Guid aggregateId)
    {
        var scopeBytes = System.Text.Encoding.UTF8.GetBytes(scope);
        Span<byte> buffer = stackalloc byte[32 + 64];
        var len = 0;
        tenantId.TryWriteBytes(buffer[..16]); len += 16;
        aggregateId.TryWriteBytes(buffer.Slice(16, 16)); len += 16;
        scopeBytes.AsSpan(0, Math.Min(scopeBytes.Length, 64)).CopyTo(buffer.Slice(32));
        len += Math.Min(scopeBytes.Length, 64);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer[..len], hash);
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(hash[..8]);
    }
}
