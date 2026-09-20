using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Jobs;

/// <summary>A job as seen by the worker that just claimed it. <see cref="LeaseToken"/> is the fence.</summary>
/// <param name="TerminalOnly">W2-A — null for a normal claim. <c>Cancelled</c> / <c>Failed</c> when the job is
/// being ENDED and was claimed only so its <see cref="IBackgroundJobTerminalHook"/> can run first (a started
/// job cancelled while queued for a retry, or one whose attempts ran out through lease expiry). The runner
/// does not execute the handler for such a claim.</param>
/// <param name="TerminalError">The failure message that goes with <c>TerminalOnly = Failed</c>.</param>
public sealed record ClaimedBackgroundJob(
    Guid Id, Guid TenantId, string JobType, string PayloadJson, int Attempt, int MaxAttempts,
    Guid LeaseToken, Guid? CreatedByUserId, string? TerminalOnly = null, string? TerminalError = null);

public sealed record BackgroundJobEnqueueResult(BackgroundJob Job, bool Created);

/// <summary>What an enqueue endpoint returns (202): the job id, and whether an existing job was returned.</summary>
public sealed record BackgroundJobEnqueueResponse(Guid JobId, string Status, bool Deduplicated, string StatusUrl);

public enum BackgroundJobCancelOutcome { NotFound, Cancelled, CancelRequested, AlreadyFinished }

/// <summary>
/// F3 — every read and write of <c>background_jobs</c>.
///
/// <para>TWO AUDIENCES, TWO TENANCY MODES.</para>
/// <list type="bullet">
///   <item><b>Request path</b> (<see cref="EnqueueAsync"/>, <see cref="GetAsync"/>, <see cref="ListAsync"/>,
///     <see cref="RequestCancelAsync"/>): runs under the caller's principal, so the reflection-applied
///     tenant filter already pins every query to the caller's tenant; the explicit <c>TenantId</c>
///     predicate is defence in depth. No bypass of any kind.</item>
///   <item><b>Worker path</b> (static members): runs with no request principal. Discovery of claimable
///     work is inherently cross-tenant and goes through <see cref="ScopedBypass.SystemWide{T,TKey}"/>
///     (bounded, ordered). Every subsequent write is tenant-pinned through
///     <see cref="ScopedBypass.TenantWide{T}"/> using the tenant read from the claimed row, AND fenced
///     by the lease token.</item>
/// </list>
/// </summary>
public sealed class BackgroundJobStore
{
    private readonly ZayraDbContext _db;
    private readonly BackgroundJobTypeRegistry _registry;

    public BackgroundJobStore(ZayraDbContext db, BackgroundJobTypeRegistry registry)
    {
        _db = db;
        _registry = registry;
    }

    // ═════════════════════════════ Request path ═════════════════════════════

    /// <summary>
    /// Idempotent enqueue. If a job already holds (tenant, type, key) — live, or succeeded under
    /// Forever retention — that job is returned and nothing is inserted. Concurrent callers racing on
    /// the same key are serialised by the partial unique indexes: the loser's insert fails with 23505
    /// and it returns the winner's row.
    /// </summary>
    public async Task<BackgroundJobEnqueueResult> EnqueueAsync(
        Guid tenantId, string jobType, string idempotencyKey, object payload, Guid? createdByUserId,
        CancellationToken ct)
    {
        var descriptor = _registry.Get(jobType);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            throw new ArgumentException("An idempotency key (1..200 chars) is required.", nameof(idempotencyKey));
        var payloadJson = payload as string ?? JsonSerializer.Serialize(payload);

        for (var round = 0; round < 3; round++)
        {
            var existing = await FindKeyHolderAsync(tenantId, jobType, idempotencyKey, ct);
            if (existing is not null) return new BackgroundJobEnqueueResult(existing, false);

            var job = new BackgroundJob
            {
                TenantId = tenantId,
                JobType = jobType,
                IdempotencyKey = idempotencyKey,
                KeyRetention = descriptor.KeyRetention,
                MaxAttempts = descriptor.MaxAttempts,
                PayloadJson = payloadJson,
                CreatedByUserId = createdByUserId,
                RunAfterUtc = DateTime.UtcNow,
            };
            _db.BackgroundJobs.Add(job);
            try
            {
                await _db.SaveChangesAsync(ct);
                return new BackgroundJobEnqueueResult(job, true);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex, "ux_background_jobs_"))
            {
                // Lost the race to a concurrent enqueue of the same key: return the winner.
                _db.Entry(job).State = EntityState.Detached;
            }
        }
        throw new InvalidOperationException(
            $"Could not enqueue or locate background job {jobType}/{idempotencyKey} after 3 attempts.");
    }

    private Task<BackgroundJob?> FindKeyHolderAsync(Guid tenantId, string jobType, string key, CancellationToken ct) =>
        _db.BackgroundJobs.AsNoTracking()
            .Where(j => j.TenantId == tenantId && j.JobType == jobType && j.IdempotencyKey == key
                        && (j.Status == BackgroundJobStatuses.Queued || j.Status == BackgroundJobStatuses.Running
                            || (j.KeyRetention == BackgroundJobKeyRetention.Forever
                                && j.Status == BackgroundJobStatuses.Succeeded)))
            .OrderByDescending(j => j.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

    public Task<BackgroundJob?> GetAsync(Guid tenantId, Guid jobId, CancellationToken ct) =>
        _db.BackgroundJobs.AsNoTracking().FirstOrDefaultAsync(j => j.TenantId == tenantId && j.Id == jobId, ct);

    public async Task<(IReadOnlyList<BackgroundJob> Items, int Total)> ListAsync(
        Guid tenantId, IReadOnlyCollection<string> visibleTypes, Guid? onlyCreatedBy, string? status,
        string? jobType, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var types = visibleTypes.ToList();
        var q = _db.BackgroundJobs.AsNoTracking()
            .Where(j => j.TenantId == tenantId && types.Contains(j.JobType));
        if (onlyCreatedBy is Guid uid) q = q.Where(j => j.CreatedByUserId == uid);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(j => j.Status == status);
        if (!string.IsNullOrWhiteSpace(jobType)) q = q.Where(j => j.JobType == jobType);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(j => j.CreatedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    /// <summary>
    /// A Queued job is cancelled immediately (it never runs). A Running job is flagged; its worker
    /// observes the flag at the next checkpoint boundary — the item in flight completes or rolls back
    /// atomically, never half-applies — and marks the job Cancelled.
    /// </summary>
    public async Task<BackgroundJobCancelOutcome> RequestCancelAsync(Guid tenantId, Guid jobId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // W2-A — a Queued job that has ALREADY RUN (it is waiting out a retry back-off, or was handed back by
        // a stopping worker) may have committed domain state, and if its type has a terminal hook that state
        // must be repaired before the job is recorded Cancelled. Flag it and make it due now; a worker claims
        // it to run the hook only (TryClaimAsync), then marks it Cancelled. A job that never started has
        // nothing to undo and is cancelled on the spot, exactly as before.
        var hooked = _registry.HookedJobTypes.ToList();
        if (hooked.Count > 0)
        {
            var flaggedStarted = await _db.BackgroundJobs
                .Where(j => j.TenantId == tenantId && j.Id == jobId && j.Status == BackgroundJobStatuses.Queued
                            && j.StartedAtUtc != null && hooked.Contains(j.JobType))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.CancelRequestedAtUtc, j => j.CancelRequestedAtUtc ?? now)
                    .SetProperty(j => j.RunAfterUtc, now)
                    .SetProperty(j => j.UpdatedAtUtc, now), ct);
            if (flaggedStarted == 1) return BackgroundJobCancelOutcome.CancelRequested;
        }

        var cancelledQueued = await _db.BackgroundJobs
            .Where(j => j.TenantId == tenantId && j.Id == jobId && j.Status == BackgroundJobStatuses.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, BackgroundJobStatuses.Cancelled)
                .SetProperty(j => j.CancelRequestedAtUtc, now)
                .SetProperty(j => j.CompletedAtUtc, now)
                .SetProperty(j => j.UpdatedAtUtc, now), ct);
        if (cancelledQueued == 1) return BackgroundJobCancelOutcome.Cancelled;

        var flagged = await _db.BackgroundJobs
            .Where(j => j.TenantId == tenantId && j.Id == jobId && j.Status == BackgroundJobStatuses.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.CancelRequestedAtUtc, j => j.CancelRequestedAtUtc ?? now)
                .SetProperty(j => j.UpdatedAtUtc, now), ct);
        if (flagged == 1) return BackgroundJobCancelOutcome.CancelRequested;

        var exists = await _db.BackgroundJobs.AnyAsync(j => j.TenantId == tenantId && j.Id == jobId, ct);
        return exists ? BackgroundJobCancelOutcome.AlreadyFinished : BackgroundJobCancelOutcome.NotFound;
    }

    // ═════════════════════════════ Worker path ═════════════════════════════
    // No request principal: tenant is taken from the claimed row, never from a caller.

    private const string ClaimJustification =
        "Job worker claims the next due or lease-expired job across tenants; no request principal exists. " +
        "Bounded to one row, ordered by due time, row-locked with SKIP LOCKED.";
    private const string FenceJustification =
        "Job worker writes to a job it has claimed; tenant re-applied from the claimed row and every write " +
        "is additionally fenced by the lease token.";

    /// <summary>
    /// Claims at most one job of <paramref name="jobTypes"/>: a Queued job whose back-off has elapsed, or
    /// a Running job whose lease has EXPIRED (its worker died or stalled).
    ///
    /// <para>TWO INDEPENDENT GUARANTEES against a double claim, in one transaction inside the execution
    /// strategy:</para>
    /// <list type="number">
    ///   <item><c>SELECT … FOR UPDATE SKIP LOCKED</c> (via <see cref="RowLockingInterceptor"/>): two
    ///     instances polling at the same instant lock DIFFERENT rows, so they do not even contend.</item>
    ///   <item>The claim itself is a compare-and-set <c>UPDATE … WHERE status = observed AND lease_token =
    ///     observed</c>. Under READ COMMITTED PostgreSQL re-checks that predicate against the latest row
    ///     version, so of two racing claimers exactly one matches. This makes correctness independent of
    ///     the interceptor being registered on a given DbContext — the lock is an optimisation, the CAS
    ///     is the rule.</item>
    /// </list>
    /// <para>A fresh lease token is minted on every claim; the previous holder's token stops matching,
    /// which is what fences a reclaimed zombie out of every later write.</para>
    /// </summary>
    /// <param name="hookedTypes">W2-A — types with an <see cref="IBackgroundJobTerminalHook"/>. A started job of
    /// such a type that must END (cancel requested, or attempts exhausted) is claimed with
    /// <see cref="ClaimedBackgroundJob.TerminalOnly"/> set, so its hook runs before the terminal status is
    /// written. Null or empty keeps F3's original behaviour: the status is written directly.</param>
    public static async Task<ClaimedBackgroundJob?> TryClaimAsync(
        ZayraDbContext db, IReadOnlyCollection<string> jobTypes, string leaseOwner, TimeSpan leaseDuration,
        CancellationToken ct, IReadOnlyCollection<string>? hookedTypes = null)
    {
        if (jobTypes.Count == 0) return null;
        var types = jobTypes.ToList();
        var hooked = hookedTypes is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(hookedTypes, StringComparer.Ordinal);
        var owner = Truncate(leaseOwner, 300);
        // A reclaimed job that has exhausted its attempts is failed here and the scan continues, so a
        // poison job cannot block the head of the queue. A lost CAS race also continues. Bounded.
        for (var scan = 0; scan < 10; scan++)
        {
            var strategy = db.Database.CreateExecutionStrategy();
            var outcome = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var now = DateTime.UtcNow;
                var candidate = (await ScopedBypass.SystemWide(db.BackgroundJobs, 1, ClaimJustification,
                        j => types.Contains(j.JobType)
                             && ((j.Status == BackgroundJobStatuses.Queued && j.RunAfterUtc <= now)
                                 || (j.Status == BackgroundJobStatuses.Running && j.LeaseExpiresAtUtc < now)),
                        j => j.RunAfterUtc)
                    .TagWith(RowLockingInterceptor.ForUpdateSkipLockedTag)
                    .AsNoTracking()
                    .ToListAsync(ct)).FirstOrDefault();
                if (candidate is null)
                {
                    await tx.CommitAsync(ct);
                    return (Claimed: (ClaimedBackgroundJob?)null, Continue: false);
                }

                var observedStatus = candidate.Status;
                var observedToken = candidate.LeaseToken;
                var reclaim = observedStatus == BackgroundJobStatuses.Running;
                // Compare-and-set on exactly the state we observed.
                var cas = ScopedBypass.TenantWide(db.BackgroundJobs, candidate.TenantId, FenceJustification)
                    .Where(j => j.Id == candidate.Id && j.Status == observedStatus && j.LeaseToken == observedToken
                                && (observedStatus == BackgroundJobStatuses.Queued || j.LeaseExpiresAtUtc < now));

                // W2-A — a started job of a hooked type is never ended behind its handler's back: claim it so
                // the runner can run the terminal hook, THEN record the outcome. (Queued + cancel flag is the
                // state RequestCancelAsync leaves a started hooked job in.)
                var mustEnd = candidate.CancelRequestedAtUtc is not null
                    ? BackgroundJobStatuses.Cancelled
                    : reclaim && candidate.AttemptCount >= candidate.MaxAttempts ? BackgroundJobStatuses.Failed : null;
                if (mustEnd is not null && hooked.Contains(candidate.JobType) && candidate.StartedAtUtc is not null)
                {
                    var hookToken = Guid.NewGuid();
                    var hookLease = now + leaseDuration;
                    var terminalError = mustEnd == BackgroundJobStatuses.Failed
                        ? Truncate($"Lease expired after {candidate.AttemptCount} attempt(s) without the job completing; " +
                                   "the worker most likely crashed while running it." +
                                   (candidate.LastError is null ? "" : " Last error: " + candidate.LastError), 4000)
                        : null;
                    var took = await cas.ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Status, BackgroundJobStatuses.Running)
                        .SetProperty(j => j.LeaseOwner, owner)
                        .SetProperty(j => j.LeaseToken, hookToken)
                        .SetProperty(j => j.LeaseExpiresAtUtc, hookLease)
                        .SetProperty(j => j.HeartbeatAtUtc, now)
                        .SetProperty(j => j.ProgressMessage, mustEnd == BackgroundJobStatuses.Cancelled
                            ? "Cancelling: undoing the work of earlier attempts."
                            : "Failing: undoing the work of earlier attempts.")
                        .SetProperty(j => j.UpdatedAtUtc, now), ct);
                    await tx.CommitAsync(ct);
                    if (took != 1) return (Claimed: (ClaimedBackgroundJob?)null, Continue: true);
                    return (Claimed: new ClaimedBackgroundJob(candidate.Id, candidate.TenantId, candidate.JobType,
                        candidate.PayloadJson, candidate.AttemptCount, candidate.MaxAttempts, hookToken,
                        candidate.CreatedByUserId, mustEnd, terminalError), Continue: false);
                }

                if (reclaim && candidate.CancelRequestedAtUtc is not null)
                {
                    await cas.ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Status, BackgroundJobStatuses.Cancelled)
                        .SetProperty(j => j.CompletedAtUtc, now)
                        .SetProperty(j => j.UpdatedAtUtc, now)
                        .SetProperty(j => j.LeaseToken, (Guid?)null)
                        .SetProperty(j => j.LeaseOwner, (string?)null)
                        .SetProperty(j => j.LeaseExpiresAtUtc, (DateTime?)null), ct);
                    await tx.CommitAsync(ct);
                    return (Claimed: (ClaimedBackgroundJob?)null, Continue: true);
                }
                if (candidate.AttemptCount >= candidate.MaxAttempts)
                {
                    var error = Truncate(
                        $"Lease expired after {candidate.AttemptCount} attempt(s) without the job completing; " +
                        "the worker most likely crashed while running it." +
                        (candidate.LastError is null ? "" : " Last error: " + candidate.LastError), 4000);
                    await cas.ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Status, BackgroundJobStatuses.Failed)
                        .SetProperty(j => j.LastError, error)
                        .SetProperty(j => j.CompletedAtUtc, now)
                        .SetProperty(j => j.UpdatedAtUtc, now)
                        .SetProperty(j => j.LeaseToken, (Guid?)null)
                        .SetProperty(j => j.LeaseOwner, (string?)null)
                        .SetProperty(j => j.LeaseExpiresAtUtc, (DateTime?)null), ct);
                    await tx.CommitAsync(ct);
                    return (Claimed: (ClaimedBackgroundJob?)null, Continue: true);
                }

                var token = Guid.NewGuid();
                var attempt = candidate.AttemptCount + 1;
                var leaseUntil = now + leaseDuration;
                var message = reclaim
                    ? $"Resumed on attempt {attempt} after the previous worker's lease expired."
                    : candidate.ProgressMessage;
                var won = await cas.ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, BackgroundJobStatuses.Running)
                    .SetProperty(j => j.AttemptCount, attempt)
                    .SetProperty(j => j.LeaseOwner, owner)
                    .SetProperty(j => j.LeaseToken, token)
                    .SetProperty(j => j.LeaseExpiresAtUtc, leaseUntil)
                    .SetProperty(j => j.HeartbeatAtUtc, now)
                    .SetProperty(j => j.StartedAtUtc, j => j.StartedAtUtc ?? now)
                    .SetProperty(j => j.ProgressMessage, message)
                    .SetProperty(j => j.UpdatedAtUtc, now), ct);
                await tx.CommitAsync(ct);
                if (won != 1) return (Claimed: (ClaimedBackgroundJob?)null, Continue: true);
                return (Claimed: new ClaimedBackgroundJob(candidate.Id, candidate.TenantId, candidate.JobType,
                    candidate.PayloadJson, attempt, candidate.MaxAttempts, token, candidate.CreatedByUserId),
                    Continue: false);
            });
            if (outcome.Claimed is not null || !outcome.Continue) return outcome.Claimed;
        }
        return null;
    }

    internal static IQueryable<BackgroundJob> Leased(ZayraDbContext db, ClaimedBackgroundJob job) =>
        ScopedBypass.TenantWide(db.BackgroundJobs, job.TenantId, FenceJustification)
            .Where(j => j.Id == job.Id && j.LeaseToken == job.LeaseToken && j.Status == BackgroundJobStatuses.Running);

    /// <summary>Extends the lease. False means the lease is gone — another worker owns the job.</summary>
    public static async Task<bool> RenewLeaseAsync(
        ZayraDbContext db, ClaimedBackgroundJob job, TimeSpan leaseDuration, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var n = await Leased(db, job).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.LeaseExpiresAtUtc, now + leaseDuration)
            .SetProperty(j => j.HeartbeatAtUtc, now), ct);
        return n == 1;
    }

    public static async Task<bool> SetProgressTotalAsync(
        ZayraDbContext db, ClaimedBackgroundJob job, int total, string? message, CancellationToken ct)
    {
        var msg = message is null ? null : Truncate(message, 500);
        var n = await Leased(db, job).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.ProgressTotal, total)
            .SetProperty(j => j.ProgressMessage, j => msg ?? j.ProgressMessage)
            .SetProperty(j => j.UpdatedAtUtc, DateTime.UtcNow), ct);
        return n == 1;
    }

    public static Task<bool> CompleteAsync(ZayraDbContext db, ClaimedBackgroundJob job, string? resultJson, CancellationToken ct) =>
        TerminateAsync(db, job, BackgroundJobStatuses.Succeeded, null, resultJson, ct);

    public static Task<bool> MarkCancelledAsync(ZayraDbContext db, ClaimedBackgroundJob job, CancellationToken ct) =>
        TerminateAsync(db, job, BackgroundJobStatuses.Cancelled, null, null, ct);

    /// <summary>W2-A — Cancelled, with a note (a terminal hook that failed) recorded in LastError.</summary>
    public static Task<bool> MarkCancelledAsync(ZayraDbContext db, ClaimedBackgroundJob job, CancellationToken ct, string note) =>
        TerminateAsync(db, job, BackgroundJobStatuses.Cancelled, note, null, ct);

    public static Task<bool> FailPermanentlyAsync(ZayraDbContext db, ClaimedBackgroundJob job, string error, CancellationToken ct) =>
        TerminateAsync(db, job, BackgroundJobStatuses.Failed, error, null, ct);

    private static async Task<bool> TerminateAsync(
        ZayraDbContext db, ClaimedBackgroundJob job, string status, string? error, string? resultJson, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var err = error is null ? null : Truncate(error, 4000);
        var n = await Leased(db, job).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, status)
            .SetProperty(j => j.CompletedAtUtc, now)
            .SetProperty(j => j.UpdatedAtUtc, now)
            .SetProperty(j => j.LastError, j => err ?? j.LastError)
            .SetProperty(j => j.ResultJson, j => resultJson ?? j.ResultJson)
            .SetProperty(j => j.LeaseToken, (Guid?)null)
            .SetProperty(j => j.LeaseOwner, (string?)null)
            .SetProperty(j => j.LeaseExpiresAtUtc, (DateTime?)null), ct);
        return n == 1;
    }

    /// <summary>
    /// A retryable failure: back to Queued with exponential back-off, or Failed when attempts are
    /// exhausted. Checkpoints are kept, so the retry resumes rather than restarts.
    /// </summary>
    public static async Task<bool> FailAttemptAsync(
        ZayraDbContext db, ClaimedBackgroundJob job, string error, CancellationToken ct)
    {
        if (job.Attempt >= job.MaxAttempts)
            return await FailPermanentlyAsync(db, job,
                $"Failed after {job.Attempt} attempt(s). Last error: {error}", ct);
        var now = DateTime.UtcNow;
        var backoff = TimeSpan.FromSeconds(Math.Min(900, 15 * Math.Pow(2, job.Attempt - 1)));
        var err = Truncate(error, 4000);
        var n = await Leased(db, job).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, BackgroundJobStatuses.Queued)
            .SetProperty(j => j.RunAfterUtc, now + backoff)
            .SetProperty(j => j.LastError, err)
            .SetProperty(j => j.UpdatedAtUtc, now)
            .SetProperty(j => j.LeaseToken, (Guid?)null)
            .SetProperty(j => j.LeaseOwner, (string?)null)
            .SetProperty(j => j.LeaseExpiresAtUtc, (DateTime?)null), ct);
        return n == 1;
    }

    /// <summary>
    /// Graceful hand-back on shutdown: the job returns to Queued, immediately claimable by another
    /// instance, and the attempt is NOT counted — a deploy is not the job's fault.
    /// </summary>
    public static async Task<bool> ReleaseAsync(ZayraDbContext db, ClaimedBackgroundJob job, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var n = await Leased(db, job).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, BackgroundJobStatuses.Queued)
            .SetProperty(j => j.RunAfterUtc, now)
            .SetProperty(j => j.AttemptCount, j => j.AttemptCount > 0 ? j.AttemptCount - 1 : 0)
            .SetProperty(j => j.ProgressMessage, "Released by a stopping worker; will resume from its last checkpoint.")
            .SetProperty(j => j.UpdatedAtUtc, now)
            .SetProperty(j => j.LeaseToken, (Guid?)null)
            .SetProperty(j => j.LeaseOwner, (string?)null)
            .SetProperty(j => j.LeaseExpiresAtUtc, (DateTime?)null), ct);
        return n == 1;
    }

    public static Task<List<string>> LoadCompletedItemKeysAsync(ZayraDbContext db, ClaimedBackgroundJob job, CancellationToken ct) =>
        ScopedBypass.TenantWide(db.BackgroundJobItems, job.TenantId,
                "Job worker loads the checkpoints of a job it holds the lease on; tenant from the claimed row.")
            .Where(i => i.JobId == job.Id)
            .Select(i => i.ItemKey)
            .ToListAsync(ct);

    internal static bool IsUniqueViolation(DbUpdateException ex, string constraintPrefix) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && (pg.ConstraintName?.StartsWith(constraintPrefix, StringComparison.Ordinal) ?? false);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
