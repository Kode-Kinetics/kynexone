namespace Zayra.Api.Infrastructure.Operations;

/// <summary>
/// Graceful drain for rolling, multi-instance deploys.
///
/// <para>On SIGTERM the host fires <see cref="IHostApplicationLifetime.ApplicationStopping"/> and only
/// then stops Kestrel. <see cref="Attach"/> registers a callback on that token which (1) flips
/// <see cref="IsDraining"/>, so <c>/health/ready</c> answers 503 and the load balancer stops routing new
/// requests here, and (2) holds shutdown for <see cref="ReadinessDrainDelay"/> while this instance keeps
/// serving whatever the balancer still sends during its next health-check interval. After that the host
/// stops the server, which refuses new connections and waits for whatever is left of
/// <c>HostOptions.ShutdownTimeout</c> for in-flight requests to finish.</para>
///
/// <para>The callback blocks deliberately: <c>ApplicationLifetime.StopApplication</c> runs Stopping
/// callbacks synchronously under a lock, and <c>Host.StopAsync</c> calls it before stopping any hosted
/// service, so the server cannot stop until the delay has elapsed. <c>/health/live</c> is unaffected:
/// a draining instance is alive and must not be restarted.</para>
///
/// <para>Budget. <c>Host.StopAsync</c> starts the <c>HostOptions.ShutdownTimeout</c> clock BEFORE it raises
/// Stopping, so the drain delay is spent inside that timeout, not on top of it: in-flight requests get
/// <c>ShutdownTimeout - ReadinessDrainDelay</c>. The delay is therefore capped so that at least
/// <see cref="InFlightAllowance"/> is always left for them (or the whole timeout, if it is shorter).
/// Defaults everywhere: delay 0, timeout 30s, so the platform's kill grace period must cover 30s.
/// Configure with <c>Shutdown:ReadinessDrainSeconds</c> and <c>Shutdown:TimeoutSeconds</c>.</para>
/// </summary>
public sealed class ShutdownDrain
{
    // Off by default in every environment: on a single instance with no overlap (Render with a disk
    // today) the delay only adds downtime. Set Shutdown__ReadinessDrainSeconds (e.g. 5) once two or more
    // instances sit behind a balancer.
    public const int DefaultReadinessDrainSeconds = 0;
    public const int DefaultShutdownTimeoutSeconds = 30;

    /// <summary>The part of <see cref="ShutdownTimeout"/> the drain delay may never consume.</summary>
    public static readonly TimeSpan InFlightAllowance = TimeSpan.FromSeconds(10);

    private readonly ILogger<ShutdownDrain> _log;
    private volatile bool _draining;

    public ShutdownDrain(IConfiguration configuration, ILogger<ShutdownDrain> log)
    {
        _log = log;
        var requested = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue("Shutdown:ReadinessDrainSeconds", DefaultReadinessDrainSeconds), 0, 120));
        ReadinessDrainDelay = CapDrainDelay(requested, ShutdownTimeout(configuration));
        if (ReadinessDrainDelay < requested)
            _log.LogWarning(
                "Shutdown:ReadinessDrainSeconds {RequestedSeconds}s capped to {DelaySeconds}s so in-flight requests keep at least {AllowanceSeconds}s of the {TimeoutSeconds}s shutdown timeout.",
                requested.TotalSeconds, ReadinessDrainDelay.TotalSeconds, InFlightAllowance.TotalSeconds,
                ShutdownTimeout(configuration).TotalSeconds);
    }

    /// <summary>The longest drain delay that still leaves in-flight requests their allowance.</summary>
    public static TimeSpan CapDrainDelay(TimeSpan requested, TimeSpan shutdownTimeout)
    {
        var allowance = InFlightAllowance < shutdownTimeout ? InFlightAllowance : shutdownTimeout;
        var max = shutdownTimeout - allowance;
        return requested < TimeSpan.Zero ? TimeSpan.Zero : requested > max ? max : requested;
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
