using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Jobs;

/// <summary>
/// F3 — the hosted loop that runs durable jobs. Safe to run on every instance: claiming is
/// <c>FOR UPDATE SKIP LOCKED</c> + a fenced lease, so N instances (including old and new during a
/// deploy cutover) share the queue without ever running one job twice.
///
/// <para>GRACEFUL SHUTDOWN (SIGTERM). The host cancels <c>stoppingToken</c>: every slot stops claiming;
/// a slot mid-job lets its in-flight item commit its checkpoint, observes the stop at the boundary, and
/// RELEASES the lease (job back to Queued, attempt not counted) so the next instance resumes it at once.
/// If the host's shutdown timeout expires first, <see cref="StopAsync"/> fires the abort token: the
/// in-flight item's transaction rolls back (nothing half-applied) and the job is released best-effort —
/// failing that, its lease simply expires and it is reclaimed.</para>
/// </summary>
public sealed class BackgroundJobWorker : BackgroundService
{
    private static readonly TimeSpan HeartbeatReportInterval = TimeSpan.FromSeconds(60);

    private readonly BackgroundJobRunner _runner;
    private readonly BackgroundJobOptions _options;
    private readonly ILogger<BackgroundJobWorker> _log;
    private readonly WorkerHeartbeatReporter? _heartbeat;
    private readonly CancellationTokenSource _abort = new();
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private DateTime _lastReportUtc = DateTime.MinValue;

    public BackgroundJobWorker(BackgroundJobRunner runner, BackgroundJobOptions options,
        ILogger<BackgroundJobWorker> log, WorkerHeartbeatReporter? heartbeat = null)
    {
        _runner = runner;
        _options = options;
        _log = log;
        _heartbeat = heartbeat;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WorkerEnabled)
        {
            _log.LogInformation("BackgroundJobWorker disabled on this instance (BackgroundJobs:WorkerEnabled=false).");
            return;
        }
        _log.LogInformation("BackgroundJobWorker {WorkerId} started (concurrency={Concurrency}, lease={Lease}s).",
            _workerId, _options.Concurrency, _options.LeaseDuration.TotalSeconds);
        if (_heartbeat is not null) await _heartbeat.StartedAsync(ProductionWorkerNames.BackgroundJobs, stoppingToken);

        var slots = Enumerable.Range(0, Math.Max(1, _options.Concurrency))
            .Select(i => SlotLoopAsync($"{_workerId}#{i}", stoppingToken))
            .ToList();
        await Task.WhenAll(slots);
        _log.LogInformation("BackgroundJobWorker {WorkerId} stopped.", _workerId);
    }

    private async Task SlotLoopAsync(string slotId, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                worked = await _runner.RunNextAsync(slotId, stoppingToken, _abort.Token);
                await ReportHealthyAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "BackgroundJobWorker slot {Slot} iteration failed.", slotId);
                if (_heartbeat is not null)
                    try { await _heartbeat.FailedAsync(ProductionWorkerNames.BackgroundJobs, ex, stoppingToken); }
                    catch (Exception hbEx) { _log.LogWarning(hbEx, "Could not persist background-job worker failure heartbeat."); }
            }
            if (worked) continue; // drain: claim again immediately while there is work
            try { await Task.Delay(_options.PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReportHealthyAsync(CancellationToken ct)
    {
        if (_heartbeat is null || DateTime.UtcNow - _lastReportUtc < HeartbeatReportInterval) return;
        _lastReportUtc = DateTime.UtcNow;
        await _heartbeat.SucceededAsync(ProductionWorkerNames.BackgroundJobs, ct);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // cancellationToken fires when the host's shutdown timeout is exhausted: escalate to a hard stop.
        using var escalate = cancellationToken.Register(() => _abort.Cancel());
        await base.StopAsync(cancellationToken);
        // base.StopAsync stops waiting the moment the timeout fires; give the aborted slots a brief,
        // bounded moment to write their best-effort lease release before the host tears down DI.
        if (ExecuteTask is { IsCompleted: false } running)
            await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    public override void Dispose()
    {
        _abort.Dispose();
        base.Dispose();
    }
}
