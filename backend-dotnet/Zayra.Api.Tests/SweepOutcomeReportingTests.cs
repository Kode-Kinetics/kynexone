using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A lease-guarded sweep that did not run (skipped) or did not finish (lease lost) must be visible —
/// logged above Debug and written to the worker heartbeat — and must never be reported as Healthy.
/// The lease mechanics themselves are covered against real PostgreSQL in
/// <see cref="SingletonWorkerLeasePostgresTests"/>.
/// </summary>
public sealed class SweepOutcomeReportingTests : IDisposable
{
    private const string Worker = ProductionWorkerNames.ComplianceReminders;
    private readonly ServiceProvider _services;
    private readonly ListLogger _log = new();

    public SweepOutcomeReportingTests()
    {
        var name = Guid.NewGuid().ToString();
        _services = new ServiceCollection()
            .AddDbContext<ZayraDbContext>(o => o.UseInMemoryDatabase(name))
            .BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    private SweepOutcomeReporter Reporter(out WorkerHeartbeatReporter heartbeat)
    {
        heartbeat = new WorkerHeartbeatReporter(_services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WorkerHeartbeatReporter>.Instance);
        return new SweepOutcomeReporter(Worker, _log, heartbeat);
    }

    private async Task<WorkerHeartbeat> RowAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ZayraDbContext>().WorkerHeartbeats.AsNoTracking()
            .SingleAsync(x => x.WorkerName == Worker);
    }

    [Fact]
    public async Task ASkippedSweep_IsLoggedAtInformation_AndWrittenAsSkipped_NotHealthy()
    {
        var reporter = Reporter(out _);

        await reporter.ReportAsync(SweepOutcome.Skipped, CancellationToken.None);

        _log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Message.Contains("skipped"));
        var row = await RowAsync();
        row.Status.Should().Be(WorkerHeartbeatStatuses.Skipped);
        row.LastErrorCode.Should().Be(WorkerHeartbeatReasons.LeaseHeldElsewhere);
        row.LastSucceededAtUtc.Should().BeNull("a skip is not a success of this instance");
    }

    [Fact]
    public async Task TheThirdConsecutiveSkip_IsAWarning_AndASuccessResetsTheCount()
    {
        var reporter = Reporter(out _);

        await reporter.ReportAsync(SweepOutcome.Skipped, CancellationToken.None);
        await reporter.ReportAsync(SweepOutcome.Skipped, CancellationToken.None);
        _log.Entries.Should().OnlyContain(e => e.Level == LogLevel.Information);

        await reporter.ReportAsync(SweepOutcome.Skipped, CancellationToken.None);
        reporter.ConsecutiveSkips.Should().Be(3);
        _log.Entries[^1].Level.Should().Be(LogLevel.Warning);
        _log.Entries[^1].Message.Should().Contain("3 times in a row");

        await reporter.ReportAsync(SweepOutcome.Completed, CancellationToken.None);
        reporter.ConsecutiveSkips.Should().Be(0);
        (await RowAsync()).Status.Should().Be(WorkerHeartbeatStatuses.Healthy);

        _log.Entries.Clear();
        await reporter.ReportAsync(SweepOutcome.Skipped, CancellationToken.None);
        _log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information, "the run of skips starts again");
    }

    [Fact]
    public async Task ALostLease_IsNeverReportedAsHealthy()
    {
        var reporter = Reporter(out _);
        await reporter.ReportAsync(SweepOutcome.Completed, CancellationToken.None);
        var lastSuccess = (await RowAsync()).LastSucceededAtUtc;

        await reporter.ReportAsync(SweepOutcome.LeaseLost, CancellationToken.None);

        var row = await RowAsync();
        row.Status.Should().Be(WorkerHeartbeatStatuses.Interrupted);
        row.LastErrorCode.Should().Be(WorkerHeartbeatReasons.LeaseLost);
        row.LastSucceededAtUtc.Should().Be(lastSuccess, "the interrupted sweep did not succeed");
        _log.Entries[^1].Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void Readiness_ShowsSkippedAndInterruptedWithTheirReason_WithoutFailingTheInstance()
    {
        var now = DateTime.UtcNow;
        var rows = ProductionWorkerNames.All.Select(name => new WorkerHeartbeat
        {
            WorkerName = name, InstanceId = "a", Status = WorkerHeartbeatStatuses.Healthy,
            LastSucceededAtUtc = now, UpdatedAtUtc = now,
        }).ToList();
        rows.RemoveAll(r => r.WorkerName is ProductionWorkerNames.ComplianceReminders or ProductionWorkerNames.AiInsights);
        rows.Add(new WorkerHeartbeat
        {
            WorkerName = ProductionWorkerNames.ComplianceReminders, InstanceId = "a",
            Status = WorkerHeartbeatStatuses.Skipped, LastErrorCode = WorkerHeartbeatReasons.LeaseHeldElsewhere, UpdatedAtUtc = now,
        });
        rows.Add(new WorkerHeartbeat
        {
            WorkerName = ProductionWorkerNames.AiInsights, InstanceId = "a",
            Status = WorkerHeartbeatStatuses.Interrupted, LastErrorCode = WorkerHeartbeatReasons.LeaseLost, UpdatedAtUtc = now,
        });

        var fleet = ProductionReadinessEvidence.EvaluateWorkers(rows, now);

        fleet.Healthy.Should().BeTrue("/health/ready is the platform health check; a sweep running elsewhere is no reason to pull this instance");
        fleet.SkippedCount.Should().Be(1);
        fleet.InterruptedCount.Should().Be(1);
        fleet.Workers.Single(w => w.Name == ProductionWorkerNames.ComplianceReminders)
            .Should().Match<WorkerReadiness>(w => w.Status == "skipped" && w.Reason == WorkerHeartbeatReasons.LeaseHeldElsewhere);
        fleet.Workers.Single(w => w.Name == ProductionWorkerNames.AiInsights)
            .Should().Match<WorkerReadiness>(w => w.Status == "interrupted" && w.Reason == WorkerHeartbeatReasons.LeaseLost);
    }

    [Fact]
    public void Readiness_PrefersAnotherInstancesHealthyRowOverThisInstancesSkip()
    {
        var now = DateTime.UtcNow;
        var rows = ProductionWorkerNames.All.Select(name => new WorkerHeartbeat
        {
            WorkerName = name, InstanceId = "holder", Status = WorkerHeartbeatStatuses.Healthy,
            LastSucceededAtUtc = now.AddMinutes(-10), UpdatedAtUtc = now.AddMinutes(-10),
        }).ToList();
        rows.Add(new WorkerHeartbeat
        {
            WorkerName = ProductionWorkerNames.ComplianceReminders, InstanceId = "skipper",
            Status = WorkerHeartbeatStatuses.Skipped, LastErrorCode = WorkerHeartbeatReasons.LeaseHeldElsewhere, UpdatedAtUtc = now,
        });

        var fleet = ProductionReadinessEvidence.EvaluateWorkers(rows, now);

        fleet.Workers.Single(w => w.Name == ProductionWorkerNames.ComplianceReminders).Status.Should().Be("healthy");
        fleet.SkippedCount.Should().Be(0);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
