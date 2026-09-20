using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Jobs;

/// <summary>
/// F3 — what a handler sees while it runs, and THE CHECKPOINT CONTRACT.
///
/// <para><see cref="RunItemAsync"/> is the only way a handler makes durable progress. For one item it
/// runs, in ONE database transaction inside the execution strategy:</para>
/// <list type="number">
///   <item><b>Fence</b> — <c>UPDATE background_jobs SET progress+1, lease renewed WHERE id AND lease_token
///     = mine AND status = Running AND cancel not requested</c>. Zero rows means the lease was taken
///     over (this worker stalled and was reclaimed) or a cancel arrived: the transaction is abandoned
///     before the item touches anything. The UPDATE also row-locks the job, so a reclaimer's
///     <c>SKIP LOCKED</c> scan cannot take the job while an item is mid-flight.</item>
///   <item><b>Apply</b> — the handler's delegate stages its writes on <see cref="Db"/>.</item>
///   <item><b>Checkpoint</b> — a <see cref="BackgroundJobItem"/> row for the item key (unique per job).</item>
///   <item><b>Commit.</b> All three land, or none do.</item>
/// </list>
/// <para>So after any crash, the committed checkpoints are exactly the committed items. A resumed attempt
/// skips them (<see cref="IsItemCompleted"/>), and even a zombie that raced past the in-memory check
/// would be stopped by the fence and, failing that, by the unique index.</para>
/// </summary>
public sealed class JobExecutionContext
{
    private readonly ClaimedBackgroundJob _job;
    private readonly HashSet<string> _completed;
    private readonly TimeSpan _leaseDuration;
    private readonly CancellationToken _shutdown;

    internal JobExecutionContext(
        ClaimedBackgroundJob job, ZayraDbContext db, IServiceProvider services,
        IEnumerable<string> completedItemKeys, TimeSpan leaseDuration,
        CancellationToken shutdown, CancellationToken abort)
    {
        _job = job;
        Db = db;
        Services = services;
        _completed = new HashSet<string>(completedItemKeys, StringComparer.Ordinal);
        _leaseDuration = leaseDuration;
        _shutdown = shutdown;
        AbortToken = abort;
    }

    public Guid JobId => _job.Id;
    public Guid TenantId => _job.TenantId;
    public string JobType => _job.JobType;
    public int Attempt => _job.Attempt;
    public Guid? CreatedByUserId => _job.CreatedByUserId;
    public string PayloadJson => _job.PayloadJson;

    /// <summary>The job's DbContext (the same instance the job's DI scope hands to scoped services).</summary>
    public ZayraDbContext Db { get; }
    public IServiceProvider Services { get; }

    /// <summary>
    /// Fires only on a HARD stop — the host's shutdown grace period ran out, or the lease was lost. Pass it
    /// to reads outside items. It is deliberately NOT the graceful-shutdown signal: a graceful stop lets
    /// the in-flight item reach its checkpoint and then yields at the boundary.
    /// </summary>
    public CancellationToken AbortToken { get; }

    public IReadOnlyCollection<string> CompletedItemKeys => _completed;
    public bool IsItemCompleted(string itemKey) => _completed.Contains(itemKey);

    /// <summary>Items applied during THIS attempt (for tests and result summaries).</summary>
    public int AppliedThisAttempt { get; private set; }

    internal string? ResultJson { get; private set; }

    public T GetPayload<T>() =>
        JsonSerializer.Deserialize<T>(_job.PayloadJson)
        ?? throw new BackgroundJobPermanentFailureException($"Job {_job.Id} has an empty or invalid payload.");

    /// <summary>Stored on the job when it succeeds.</summary>
    public void SetResult(object result) => ResultJson = JsonSerializer.Serialize(result);

    public async Task SetTotalAsync(int total, string? message = null)
    {
        if (!await BackgroundJobStore.SetProgressTotalAsync(Db, _job, total, message, AbortToken))
            await ThrowForFailedFenceAsync();
    }

    /// <summary>
    /// Applies one item exactly once. Returns false (and does nothing) if the item was already
    /// checkpointed by an earlier attempt. Throws <see cref="BackgroundJobCancelledException"/> /
    /// <see cref="BackgroundJobYieldException"/> at the boundary BEFORE the item when a cancel or a
    /// graceful shutdown is pending, and <see cref="BackgroundJobLeaseLostException"/> if another worker
    /// has taken the job over.
    /// </summary>
    /// <param name="itemKey">Deterministic across attempts, e.g. <c>employee:1042</c>. Max 200 chars.</param>
    /// <param name="apply">Stages writes on <see cref="Db"/>. Must not open its own transaction. May be
    /// re-invoked from scratch after a transient database fault.</param>
    /// <param name="itemResult">Optional small result stored on the checkpoint row.</param>
    /// <param name="countsTowardProgress">False for bookkeeping steps (a final audit entry, say) that must
    /// also happen exactly once but are not part of <c>ProgressTotal</c>.</param>
    public Task<bool> RunItemAsync(string itemKey, Func<CancellationToken, Task> apply, Func<object?>? itemResult = null,
        bool countsTowardProgress = true) =>
        RunItemCoreAsync(itemKey, apply, itemResult, countsTowardProgress, terminal: false);

    /// <summary>
    /// W2-A — the item a <see cref="IBackgroundJobTerminalHook"/> runs in. Identical to
    /// <see cref="RunItemAsync"/> (one transaction: fence, apply, checkpoint) except that the fence checks the
    /// lease ONLY — a cancel request is exactly when a cancel hook must run — and a graceful shutdown does not
    /// stop it: it is the last write of a job that is ending anyway, and yielding here would strand the
    /// domain state the hook exists to repair.
    /// </summary>
    internal Task<bool> RunTerminalItemAsync(string itemKey, Func<CancellationToken, Task> apply) =>
        RunItemCoreAsync(itemKey, apply, null, countsTowardProgress: false, terminal: true);

    /// <summary>Item results committed by earlier items of this job, keyed by item key (W2-A: pinned inputs).</summary>
    public async Task<IReadOnlyDictionary<string, string?>> LoadItemResultsAsync(string? keyPrefix = null)
    {
        var q = ScopedBypass.TenantWide(Db.BackgroundJobItems, _job.TenantId,
                "Job worker reads the checkpoints of a job it holds the lease on; tenant from the claimed row.")
            .AsNoTracking()
            .Where(i => i.JobId == _job.Id);
        if (keyPrefix is not null) q = q.Where(i => i.ItemKey.StartsWith(keyPrefix));
        return (await q.Select(i => new { i.ItemKey, i.ResultJson }).ToListAsync(AbortToken))
            .ToDictionary(i => i.ItemKey, i => i.ResultJson, StringComparer.Ordinal);
    }

    private async Task<bool> RunItemCoreAsync(string itemKey, Func<CancellationToken, Task> apply, Func<object?>? itemResult,
        bool countsTowardProgress, bool terminal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemKey);
        if (itemKey.Length > 200) throw new ArgumentException("Item keys are limited to 200 characters.", nameof(itemKey));
        if (_completed.Contains(itemKey)) return false;
        if (!terminal && _shutdown.IsCancellationRequested) throw new BackgroundJobYieldException();
        AbortToken.ThrowIfCancellationRequested();

        var strategy = Db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                Db.ChangeTracker.Clear();
                await using var tx = await Db.Database.BeginTransactionAsync(AbortToken);
                var now = DateTime.UtcNow;
                var leaseUntil = now + _leaseDuration;
                var increment = countsTowardProgress ? 1 : 0;
                var fence = BackgroundJobStore.Leased(Db, _job);
                if (!terminal) fence = fence.Where(j => j.CancelRequestedAtUtc == null);
                var fenced = await fence
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.ProgressCompleted, j => j.ProgressCompleted + increment)
                        .SetProperty(j => j.LeaseExpiresAtUtc, leaseUntil)
                        .SetProperty(j => j.HeartbeatAtUtc, now)
                        .SetProperty(j => j.UpdatedAtUtc, now), AbortToken);
                if (fenced != 1)
                {
                    await tx.RollbackAsync(AbortToken);
                    if (terminal) throw new BackgroundJobLeaseLostException(_job.Id);
                    await ThrowForFailedFenceAsync();
                }

                await apply(AbortToken);

                var result = itemResult?.Invoke();
                Db.BackgroundJobItems.Add(new BackgroundJobItem
                {
                    TenantId = _job.TenantId,
                    JobId = _job.Id,
                    ItemKey = itemKey,
                    Attempt = _job.Attempt,
                    ResultJson = result is null ? null : JsonSerializer.Serialize(result),
                });
                await Db.SaveChangesAsync(AbortToken);
                await tx.CommitAsync(AbortToken);
            });
        }
        catch (DbUpdateException ex) when (BackgroundJobStore.IsUniqueViolation(ex, "ux_background_job_items_"))
        {
            // The checkpoint already exists: a commit whose acknowledgement was lost and then retried by
            // the execution strategy, or a racing zombie. Either way this attempt's transaction rolled back
            // and the item is applied exactly once — by whoever committed the checkpoint.
            Db.ChangeTracker.Clear();
            _completed.Add(itemKey);
            return false;
        }
        finally
        {
            Db.ChangeTracker.Clear();
        }

        _completed.Add(itemKey);
        AppliedThisAttempt++;
        return true;
    }

    /// <summary>The fence matched no row. Work out why — cancel, or lease lost — and throw accordingly.</summary>
    private async Task ThrowForFailedFenceAsync()
    {
        var state = await ScopedBypass.TenantWide(Db.BackgroundJobs, _job.TenantId,
                "Job worker diagnoses a failed lease fence on the job it held; tenant from the claimed row.")
            .AsNoTracking()
            .Where(j => j.Id == _job.Id)
            .Select(j => new { j.LeaseToken, j.Status, j.CancelRequestedAtUtc })
            .FirstOrDefaultAsync(AbortToken);
        if (state is not null && state.LeaseToken == _job.LeaseToken
            && state.Status == BackgroundJobStatuses.Running && state.CancelRequestedAtUtc is not null)
            throw new BackgroundJobCancelledException();
        throw new BackgroundJobLeaseLostException(_job.Id);
    }
}
