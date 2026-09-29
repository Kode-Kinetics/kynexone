using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Jobs;

public sealed class BackgroundJobOptions
{
    public const string SectionName = "BackgroundJobs";

    /// <summary>Run the worker in this process. Turn off on instances that should only serve HTTP.</summary>
    public bool WorkerEnabled { get; set; } = true;
    /// <summary>Jobs this process runs at once.</summary>
    public int Concurrency { get; set; } = 2;
    /// <summary>
    /// How long a claim is valid without a heartbeat. After this a job whose worker died is reclaimable,
    /// so it bounds how long a crash delays the job. Heartbeats and every committed item renew it.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Idle wait between claim attempts when the queue is empty.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// F3 — claims one job and drives it to a durable outcome. The hosted <see cref="BackgroundJobWorker"/>
/// is a thin loop around <see cref="RunNextAsync"/>; tests drive this class directly with real
/// PostgreSQL and real concurrency.
///
/// <para>OUTCOMES. Succeeded / Cancelled / Failed are written fenced by the lease token; a retryable
/// exception re-queues with back-off (checkpoints kept, so the retry resumes); a graceful shutdown
/// RELEASES the lease without counting the attempt; a lost lease writes nothing at all — the job
/// belongs to someone else now.</para>
/// </summary>
public sealed class BackgroundJobRunner
{
    private readonly IServiceScopeFactory _scopes;
    private readonly BackgroundJobTypeRegistry _registry;
    private readonly BackgroundJobOptions _options;
    private readonly ILogger _log;

    public BackgroundJobRunner(
        IServiceScopeFactory scopes, BackgroundJobTypeRegistry registry, BackgroundJobOptions options,
        ILogger<BackgroundJobRunner> log)
    {
        _scopes = scopes;
        _registry = registry;
        _options = options;
        _log = log;
    }

    /// <summary>
    /// Claims and runs at most one job. Returns false when nothing was claimable.
    /// </summary>
    /// <param name="shutdown">Graceful stop: stop at the next checkpoint and hand the job back.</param>
    /// <param name="abort">Hard stop: abandon the in-flight item (its transaction rolls back).</param>
    public async Task<bool> RunNextAsync(string workerId, CancellationToken shutdown, CancellationToken abort,
        IReadOnlyCollection<string>? onlyTypes = null)
    {
        if (shutdown.IsCancellationRequested) return false;
        ClaimedBackgroundJob? job;
        await using (var claimScope = _scopes.CreateAsyncScope())
        {
            var db = claimScope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            job = await BackgroundJobStore.TryClaimAsync(
                db, onlyTypes ?? _registry.JobTypes, workerId, _options.LeaseDuration, shutdown);
        }
        if (job is null) return false;
        await ExecuteClaimedAsync(job, shutdown, abort);
        return true;
    }

    public async Task ExecuteClaimedAsync(ClaimedBackgroundJob job, CancellationToken shutdown, CancellationToken abort)
    {
        using var jobAbort = CancellationTokenSource.CreateLinkedTokenSource(abort);
        var leaseLost = false;
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = HeartbeatLoopAsync(job, () => { leaseLost = true; jobAbort.Cancel(); }, heartbeatStop.Token);

        // Decide the outcome first, stop the heartbeat, THEN write it — so the final beat can never
        // mistake our own terminal write for a lost lease.
        Func<ZayraDbContext, CancellationToken, Task<bool>>? write = null;
        string label;
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var descriptor = _registry.Get(job.JobType);
            var completed = await BackgroundJobStore.LoadCompletedItemKeysAsync(db, job, jobAbort.Token);
            var handler = (IBackgroundJobHandler)scope.ServiceProvider.GetRequiredService(descriptor.HandlerType);
            var ctx = new JobExecutionContext(job, db, scope.ServiceProvider, completed, _options.LeaseDuration,
                shutdown, jobAbort.Token);

            _log.LogInformation("Job {JobId} ({JobType}) attempt {Attempt} starting; {Done} item(s) already checkpointed.",
                job.Id, job.JobType, job.Attempt, completed.Count);
            await handler.ExecuteAsync(ctx);
            var resultJson = ctx.ResultJson;
            write = (db2, ct) => BackgroundJobStore.CompleteAsync(db2, job, resultJson, ct);
            label = "succeeded";
        }
        catch (BackgroundJobCancelledException)
        {
            write = (db2, ct) => BackgroundJobStore.MarkCancelledAsync(db2, job, ct);
            label = "cancelled";
        }
        catch (BackgroundJobYieldException)
        {
            write = (db2, ct) => BackgroundJobStore.ReleaseAsync(db2, job, ct);
            label = "released (graceful shutdown)";
        }
        catch (BackgroundJobLeaseLostException)
        {
            label = "lease lost — abandoned without writing";
        }
        catch (OperationCanceledException) when (jobAbort.IsCancellationRequested)
        {
            if (leaseLost)
                label = "lease lost — abandoned without writing";
            else
            {
                // Hard stop (shutdown grace expired). The in-flight item rolled back; hand the job back
                // so another instance can resume it now instead of after lease expiry.
                write = (db2, ct) => BackgroundJobStore.ReleaseAsync(db2, job, ct);
                label = "released (hard stop)";
            }
        }
        catch (BackgroundJobPermanentFailureException ex)
        {
            var message = ex.Message;
            write = (db2, ct) => BackgroundJobStore.FailPermanentlyAsync(db2, job, message, ct);
            label = "failed (permanent)";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Job {JobId} ({JobType}) attempt {Attempt} threw.", job.Id, job.JobType, job.Attempt);
            var message = $"{ex.GetType().Name}: {ex.Message}";
            write = (db2, ct) => BackgroundJobStore.FailAttemptAsync(db2, job, message, ct);
            label = "failed attempt";
        }

        heartbeatStop.Cancel();
        try { await heartbeat; } catch (OperationCanceledException) { }
        var outcome = write is null || leaseLost ? label : await FinishAsync(job, write, label);
        _log.LogInformation("Job {JobId} ({JobType}) attempt {Attempt}: {Outcome}.", job.Id, job.JobType, job.Attempt, outcome);
    }

    /// <summary>
    /// Terminal writes use a FRESH scope (the job's own context may hold a broken transaction) and are
    /// not cancellable by shutdown — a few milliseconds to record the outcome beats a job stranded until
    /// its lease expires. A false result means the fence failed: the lease is gone and nothing was written.
    /// </summary>
    private async Task<string> FinishAsync(ClaimedBackgroundJob job, Func<ZayraDbContext, CancellationToken, Task<bool>> write, string label)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            return await write(db, timeout.Token) ? label : label + " — NOT recorded: lease no longer held";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not record outcome '{Outcome}' for job {JobId}; it will be reclaimed when its lease expires.", label, job.Id);
            return label + " — NOT recorded (error)";
        }
    }

    private async Task HeartbeatLoopAsync(ClaimedBackgroundJob job, Action onLeaseLost, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(_options.HeartbeatInterval, stop); }
            catch (OperationCanceledException) { return; }
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
                if (!await BackgroundJobStore.RenewLeaseAsync(db, job, _options.LeaseDuration, stop))
                {
                    if (stop.IsCancellationRequested) return;
                    _log.LogWarning("Job {JobId}: lease lost at heartbeat; aborting this worker's execution.", job.Id);
                    onLeaseLost();
                    return;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // A missed beat is survivable (the lease has slack); a persistent outage ends with the
                // lease expiring, the job being reclaimed elsewhere, and this worker's fence failing.
                _log.LogWarning(ex, "Job {JobId}: heartbeat failed.", job.Id);
            }
        }
    }
}
