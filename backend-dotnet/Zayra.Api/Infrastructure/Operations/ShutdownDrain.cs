namespace Zayra.Api.Infrastructure.Operations;

/// <summary>
/// Graceful drain for rolling, multi-instance deploys.
///
/// <para>On SIGTERM the host fires <see cref="IHostApplicationLifetime.ApplicationStopping"/> and only
/// then stops Kestrel. <see cref="Attach"/> registers a callback on that token which (1) flips
/// <see cref="IsDraining"/>, so <c>/health/ready</c> answers 503 and the load balancer stops routing new
/// requests here, and (2) holds shutdown for <see cref="ReadinessDrainDelay"/> while this instance keeps
/// serving whatever the balancer still sends during its next health-check interval. After that the host
/// stops the server, which refuses new connections and waits up to <c>HostOptions.ShutdownTimeout</c> for
/// in-flight requests to finish.</para>
///
/// <para>The callback blocks deliberately: <c>ApplicationLifetime.StopApplication</c> runs Stopping
/// callbacks synchronously under a lock, and <c>Host.StopAsync</c> calls it before stopping any hosted
/// service, so the server cannot stop until the delay has elapsed. <c>/health/live</c> is unaffected:
/// a draining instance is alive and must not be restarted.</para>
///
/// <para>Budget: the platform's kill grace period must cover delay + ShutdownTimeout (defaults 5s + 30s; the delay defaults to 0 in Development).
/// Both are configurable: <c>Shutdown:ReadinessDrainSeconds</c> and <c>Shutdown:TimeoutSeconds</c>.</para>
/// </summary>
public sealed class ShutdownDrain
{
    // Off by default: on a single instance with no overlap (Render with a disk today) the delay only adds
    // downtime. Set Shutdown__ReadinessDrainSeconds (e.g. 5) once two or more instances sit behind a balancer.
    public const int DefaultReadinessDrainSeconds = 0;
    public const int DefaultShutdownTimeoutSeconds = 30;

    private readonly ILogger<ShutdownDrain> _log;
    private volatile bool _draining;

    public ShutdownDrain(IConfiguration configuration, IHostEnvironment environment, ILogger<ShutdownDrain> log)
    {
        _log = log;
        // Development (local runs, the in-process test host) has no balancer to drain from; waiting
        // there only slows every Ctrl+C and every test-host dispose.
        var seconds = configuration.GetValue("Shutdown:ReadinessDrainSeconds", DefaultReadinessDrainSeconds);
        ReadinessDrainDelay = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 120));
    }

    public TimeSpan ReadinessDrainDelay { get; }

    /// <summary>True from the moment shutdown begins; <c>/health/ready</c> reports 503 while it is.</summary>
    public bool IsDraining => _draining;

    public static TimeSpan ShutdownTimeout(IConfiguration configuration) =>
        TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue("Shutdown:TimeoutSeconds", DefaultShutdownTimeoutSeconds), 1, 300));

    public void Attach(IHostApplicationLifetime lifetime) =>
        lifetime.ApplicationStopping.Register(BeginDrain);

    internal void BeginDrain()
    {
        if (_draining) return;
        _draining = true;
        _log.LogInformation(
            "Shutdown requested: /health/ready now reports 503 (draining). Serving for {DelaySeconds}s more before the server stops accepting connections.",
            ReadinessDrainDelay.TotalSeconds);
        if (ReadinessDrainDelay > TimeSpan.Zero) Thread.Sleep(ReadinessDrainDelay);
    }
}
