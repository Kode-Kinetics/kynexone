namespace Zayra.Api.Infrastructure.Operations;

/// <summary>What one tick of a lease-guarded periodic sweep actually did.</summary>
public enum SweepOutcome
{
    /// <summary>This instance held the lease for the whole sweep and finished it.</summary>
    Completed,

    /// <summary>Another instance holds the sweep lease; this instance did nothing.</summary>
    Skipped,

    /// <summary>The sweep started, but its lease was lost part-way, so it stopped early.</summary>
    LeaseLost,
}

/// <summary>
/// Turns sweep outcomes into logs and heartbeats, so a skipped or interrupted sweep is never silent
/// and never reported as a success.
///
/// <list type="bullet">
/// <item><see cref="SweepOutcome.Completed"/>: Healthy heartbeat; the consecutive-skip count resets.</item>
/// <item><see cref="SweepOutcome.Skipped"/>: Information log, Warning from the
/// <see cref="WarnAfterConsecutiveSkips"/>th skip in a row (the holder may be stuck), and a
/// <c>Skipped</c> heartbeat with reason <c>lease_held_elsewhere</c>.</item>
/// <item><see cref="SweepOutcome.LeaseLost"/>: Warning log and an <c>Interrupted</c> heartbeat with reason
/// <c>lease_lost</c>; the next tick resumes, because every sweep is idempotent per row.</item>
/// </list>
/// One instance per worker; not thread-safe (a worker runs its ticks one at a time).
/// </summary>
public sealed class SweepOutcomeReporter
{
    public const int WarnAfterConsecutiveSkips = 3;

    private readonly string _workerName;
    private readonly ILogger _log;
    private readonly WorkerHeartbeatReporter? _heartbeat;

    public SweepOutcomeReporter(string workerName, ILogger log, WorkerHeartbeatReporter? heartbeat)
    {
        _workerName = workerName;
        _log = log;
        _heartbeat = heartbeat;
    }

    public int ConsecutiveSkips { get; private set; }

    public async Task ReportAsync(SweepOutcome outcome, CancellationToken ct)
    {
        switch (outcome)
        {
            case SweepOutcome.Completed:
                ConsecutiveSkips = 0;
                if (_heartbeat is not null) await _heartbeat.SucceededAsync(_workerName, ct);
                break;

            case SweepOutcome.Skipped:
                ConsecutiveSkips++;
                if (ConsecutiveSkips >= WarnAfterConsecutiveSkips)
                    _log.LogWarning(
                        "{WorkerName} sweep skipped {ConsecutiveSkips} times in a row: another instance holds the sweep lease. "
                        + "If no instance reports this worker Healthy, the lease holder may be stuck.",
                        _workerName, ConsecutiveSkips);
                else
                    _log.LogInformation(
                        "{WorkerName} sweep skipped: another instance holds the sweep lease ({ConsecutiveSkips} in a row).",
                        _workerName, ConsecutiveSkips);
                if (_heartbeat is not null)
                    await _heartbeat.SkippedAsync(_workerName, Models.WorkerHeartbeatReasons.LeaseHeldElsewhere, ct);
                break;

            case SweepOutcome.LeaseLost:
                ConsecutiveSkips = 0;
                _log.LogWarning(
                    "{WorkerName} sweep stopped part-way: its lease was lost. The work done so far is kept; the next tick resumes.",
                    _workerName);
                if (_heartbeat is not null)
                    await _heartbeat.InterruptedAsync(_workerName, Models.WorkerHeartbeatReasons.LeaseLost, ct);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
        }
    }
}
