using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Scope;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Operations;

public static class ProductionReadinessEvidence
{
    private static readonly string[] OpenRequisitionStatuses = ["Draft", "Submitted", "PendingApproval", "Approved"];

    public static async Task<ReadinessEvidence> BuildReadinessAsync(ZayraDbContext db, IConfiguration config, CancellationToken ct)
    {
        var dbProbe = await ProbeDatabaseAsync(db, ct);
        var tenantCount = dbProbe.Healthy ? await db.Tenants.AsNoTracking().CountAsync(ct) : 0;
        var activeTenantCount = dbProbe.Healthy ? await db.Tenants.AsNoTracking().CountAsync(x => x.IsActive, ct) : 0;

        // P0-4: migration-parity gate. A migration-bearing image deployed against a DB that
        // has NOT yet had `dotnet Zayra.Api.dll --migrate` applied would 42703/42P01 tenant-wide.
        // Reporting not_ready here makes /health/ready return 503 so Render refuses to route
        // traffic to the un-migrated instance until the pre-deploy migrate step has run.
        var pendingMigrations = await CountPendingMigrationsAsync(db, dbProbe.Healthy, ct);
        var workers = dbProbe.Healthy && pendingMigrations == 0
            ? EvaluateWorkers(await db.WorkerHeartbeats.AsNoTracking().ToListAsync(ct), DateTime.UtcNow)
            : WorkerFleetReadiness.Unavailable;
        var queues = dbProbe.Healthy && pendingMigrations == 0
            ? await BuildQueueHealthAsync(db, ct)
            : QueueHealthEvidence.Unavailable;

        return new ReadinessEvidence(
            ResolveStatus(dbProbe.Healthy, pendingMigrations, workers.Healthy),
            DateTime.UtcNow,
            new ReadinessDependencies(
                dbProbe,
                RedisDependency(config),
                QiwaDependency(config),
                await SmtpDependencyAsync(db, ct),
                workers),
            tenantCount,
            activeTenantCount,
            pendingMigrations,
            queues);
    }

    /// <summary>Pure status rule: ready iff the DB is reachable AND no migrations are pending.</summary>
    public static string ResolveStatus(bool dbHealthy, int pendingMigrations)
        => dbHealthy && pendingMigrations == 0 ? "ready" : "not_ready";

    public static string ResolveStatus(bool dbHealthy, int pendingMigrations, bool workersHealthy)
        => dbHealthy && pendingMigrations == 0 && workersHealthy ? "ready" : "not_ready";

    public static WorkerFleetReadiness EvaluateWorkers(IReadOnlyCollection<WorkerHeartbeat> rows, DateTime nowUtc)
    {
        var statuses = new List<WorkerReadiness>();
        foreach (var name in ProductionWorkerNames.All)
        {
            var instances = rows.Where(x => x.WorkerName == name)
                .OrderByDescending(x => x.UpdatedAtUtc).ToList();
            var latest = instances.FirstOrDefault();
            if (latest is null)
            {
                statuses.Add(new WorkerReadiness(name, "missing", null, null));
                continue;
            }

            var maxAge = name is ProductionWorkerNames.AiInsights or ProductionWorkerNames.ComplianceReminders
                ? TimeSpan.FromHours(2.5) : TimeSpan.FromMinutes(3);
            var healthy = instances.FirstOrDefault(x => x.Status == WorkerHeartbeatStatuses.Healthy
                && nowUtc - x.UpdatedAtUtc <= maxAge);
            var starting = instances.FirstOrDefault(x => x.Status == WorkerHeartbeatStatuses.Started
                && nowUtc - x.UpdatedAtUtc <= TimeSpan.FromMinutes(5));
            var failed = instances.FirstOrDefault(x => x.Status == WorkerHeartbeatStatuses.Failed
                && nowUtc - x.UpdatedAtUtc <= maxAge);
            var effective = healthy ?? starting ?? failed ?? latest;
            var state = healthy is not null ? "healthy"
                : starting is not null ? "starting"
                : failed is not null ? "failed"
                : "stale";
            statuses.Add(new WorkerReadiness(name, state, effective.LastSucceededAtUtc, effective.UpdatedAtUtc));
        }
        return new WorkerFleetReadiness(
            statuses.All(x => x.Status is "healthy" or "starting"),
            statuses.Count(x => x.Status == "healthy"),
            statuses.Count(x => x.Status == "starting"),
            statuses.Count(x => x.Status == "stale"),
            statuses.Count(x => x.Status == "failed"),
            statuses.Count(x => x.Status == "missing"),
            statuses);
    }

    private static async Task<int> CountPendingMigrationsAsync(ZayraDbContext db, bool dbHealthy, CancellationToken ct)
    {
        if (!dbHealthy) return 0; // DB probe already reports not_ready; don't double-count.
        if (!db.Database.IsRelational()) return 0;
        try
        {
            var pending = await db.Database.GetPendingMigrationsAsync(ct);
            return pending.Count();
        }
        catch
        {
            // A reachable database whose migration state cannot be established is not safe to
            // promote. -1 is an explicit unknown/error sentinel and ResolveStatus fails closed.
            return -1;
        }
    }

    public static async Task<TelemetryEvidence> BuildTelemetryAsync(ZayraDbContext db, IConfiguration config, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-24);
        var reportRuns = await db.ReportExecutionLogs.AsNoTracking()
            .Where(x => x.CreatedAtUtc >= since)
            .Select(x => new { x.Status, x.DurationMs, x.CreatedAtUtc })
            .ToListAsync(ct);

        var failedReportRuns = reportRuns.Count(x => x.Status == "Failed");
        var activeSchedules = await db.ReportSchedules.AsNoTracking()
            .CountAsync(x => x.IsActive && !x.IsDeleted, ct);
        var staleSchedules = await db.ReportSchedules.AsNoTracking()
            .CountAsync(x => x.IsActive && !x.IsDeleted && x.NextRunAtUtc != null && x.NextRunAtUtc < DateTime.UtcNow, ct);

        var activePositions = await db.Positions.AsNoTracking()
            .CountAsync(x => !x.IsDeleted && x.Status != PositionStatuses.Closed, ct);
        var frozenPositions = await db.Positions.AsNoTracking()
            .CountAsync(x => !x.IsDeleted && x.Status == PositionStatuses.Frozen, ct);
        var budgetedMonthlyCost = await db.Positions.AsNoTracking()
            .Where(x => !x.IsDeleted && x.Status != PositionStatuses.Closed)
            .SumAsync(x => (decimal?)x.BudgetedMonthlyCost, ct) ?? 0m;
        var openRequisitionHeadcount = await db.ManpowerRequisitions.AsNoTracking()
            .Where(x => OpenRequisitionStatuses.Contains(x.Status))
            .SumAsync(x => (int?)x.HeadCount, ct) ?? 0;
        var approvedRequisitionHeadcount = await db.ManpowerRequisitions.AsNoTracking()
            .Where(x => x.Status == "Approved")
            .SumAsync(x => (int?)x.HeadCount, ct) ?? 0;
        var controlledOverrides24h = await db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.CreatedAtUtc >= since && x.Action.StartsWith("governance.controlled_override."), ct);
        var latestControlledOverrideAtUtc = await db.AuditLogs.AsNoTracking()
            .Where(x => x.CreatedAtUtc >= since && x.Action.StartsWith("governance.controlled_override."))
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => (DateTime?)x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        var workers = EvaluateWorkers(await db.WorkerHeartbeats.AsNoTracking().ToListAsync(ct), DateTime.UtcNow);
        var queues = await BuildQueueHealthAsync(db, ct);

        return new TelemetryEvidence(
            "ok",
            DateTime.UtcNow,
            new TelemetryWindow("24h", since),
            new DependencyModes(RedisDependency(config), QiwaDependency(config), await SmtpDependencyAsync(db, ct), workers),
            new GovernanceTelemetry(controlledOverrides24h, latestControlledOverrideAtUtc),
            new ReportingTelemetry(
                reportRuns.Count,
                failedReportRuns,
                FailureRate(reportRuns.Count, failedReportRuns),
                Percentile(reportRuns.Select(x => x.DurationMs), 0.95),
                activeSchedules,
                staleSchedules,
                reportRuns.Count == 0 ? null : reportRuns.Max(x => x.CreatedAtUtc)),
            new WorkforcePlanningTelemetry(
                activePositions,
                frozenPositions,
                openRequisitionHeadcount,
                approvedRequisitionHeadcount,
                budgetedMonthlyCost),
            queues);
    }

    private static async Task<QueueHealthEvidence> BuildQueueHealthAsync(ZayraDbContext db, CancellationToken ct)
    {
        // Aggregate operations evidence only: no tenant IDs, recipients, employees, report names,
        // provider messages, or other customer payloads are returned by readiness/telemetry.
        using var systemScope = SystemScopeContext.Begin();
        var now = DateTime.UtcNow;
        return new QueueHealthEvidence(
            true,
            await db.QiwaSyncLogs.CountAsync(x => x.Status == QiwaSyncLogStatuses.Pending || x.Status == QiwaSyncLogStatuses.Processing, ct),
            await db.QiwaSyncLogs.CountAsync(x => x.Status == QiwaSyncLogStatuses.DeadLetter, ct),
            await db.NotificationDeliveries.CountAsync(x => x.Outcome == DeliveryOutcomes.Queued || x.Outcome == DeliveryOutcomes.Sending, ct),
            await db.NotificationDeliveries.CountAsync(x => x.Outcome == DeliveryOutcomes.Failed || x.Outcome == DeliveryOutcomes.Unknown, ct),
            await db.ReportSchedules.CountAsync(x => x.IsActive && !x.IsDeleted && (x.NextRunAtUtc == null || x.NextRunAtUtc <= now), ct),
            await db.ReportExecutionLogs.CountAsync(x => x.Status == "Failed" && x.CreatedAtUtc >= now.AddHours(-24), ct),
            await db.ComplianceReminders.CountAsync(x => x.Status == "Pending" && x.ScheduledAtUtc != null && x.ScheduledAtUtc <= now, ct));
    }

    private static async Task<DependencyProbe> ProbeDatabaseAsync(ZayraDbContext db, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            sw.Stop();
            return new DependencyProbe("ok", true, sw.ElapsedMilliseconds, null);
        }
        catch (Exception)
        {
            sw.Stop();
            // /health/ready is intentionally public for the load balancer. Never echo driver,
            // hostname, database, or credential details from an exception into that response.
            return new DependencyProbe("error", false, sw.ElapsedMilliseconds, "unavailable");
        }
    }

    private static DependencyMode RedisDependency(IConfiguration config)
    {
        var configured = !string.IsNullOrWhiteSpace(config["REDIS_URL"] ?? Environment.GetEnvironmentVariable("REDIS_URL"));
        return new DependencyMode(configured ? "configured" : "fallback_memory", configured);
    }

    private static DependencyMode QiwaDependency(IConfiguration config)
    {
        var live = (config["QIWA_USE_LIVE_ADAPTER"] ?? Environment.GetEnvironmentVariable("QIWA_USE_LIVE_ADAPTER"))
            ?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        return new DependencyMode(live ? "live_adapter" : "sandbox_adapter", live);
    }

    private static async Task<DependencyMode> SmtpDependencyAsync(ZayraDbContext db, CancellationToken ct)
    {
        var configured = await db.SystemSettings.AsNoTracking()
            .AnyAsync(x => x.Category == "Email" && x.SettingKey == "Smtp.Host" && x.SettingValue != "", ct);
        return new DependencyMode(configured ? "configured" : "not_configured", configured);
    }

    private static double FailureRate(int total, int failed) =>
        total == 0 ? 0 : Math.Round(failed * 100.0 / total, 2);

    private static int Percentile(IEnumerable<int> values, double percentile)
    {
        var ordered = values.Where(x => x >= 0).OrderBy(x => x).ToArray();
        if (ordered.Length == 0) return 0;
        var index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }
}

public sealed record ReadinessEvidence(
    string Status,
    DateTime Utc,
    ReadinessDependencies Dependencies,
    int Tenants,
    int ActiveTenants,
    int PendingMigrations,
    QueueHealthEvidence Queues);

public sealed record ReadinessDependencies(
    DependencyProbe Database,
    DependencyMode Redis,
    DependencyMode Qiwa,
    DependencyMode Smtp,
    WorkerFleetReadiness Workers);

public sealed record DependencyProbe(string Status, bool Healthy, long LatencyMs, string? Error);
public sealed record DependencyMode(string Mode, bool Configured);

public sealed record TelemetryEvidence(
    string Status,
    DateTime Utc,
    TelemetryWindow Window,
    DependencyModes Dependencies,
    GovernanceTelemetry Governance,
    ReportingTelemetry Reporting,
    WorkforcePlanningTelemetry WorkforcePlanning,
    QueueHealthEvidence Queues);

public sealed record TelemetryWindow(string Duration, DateTime SinceUtc);
public sealed record DependencyModes(DependencyMode Redis, DependencyMode Qiwa, DependencyMode Smtp, WorkerFleetReadiness Workers);
public sealed record GovernanceTelemetry(int ControlledOverrides24h, DateTime? LatestControlledOverrideAtUtc);

public sealed record WorkerFleetReadiness(
    bool Healthy,
    int HealthyCount,
    int StartingCount,
    int StaleCount,
    int FailedCount,
    int MissingCount,
    IReadOnlyList<WorkerReadiness> Workers)
{
    public static readonly WorkerFleetReadiness Unavailable = new(
        false, 0, 0, 0, 0, ProductionWorkerNames.All.Count,
        ProductionWorkerNames.All.Select(x => new WorkerReadiness(x, "unavailable", null, null)).ToList());
}

public sealed record WorkerReadiness(string Name, string Status, DateTime? LastSucceededAtUtc, DateTime? UpdatedAtUtc);

public sealed record QueueHealthEvidence(
    bool Available,
    int QiwaQueued,
    int QiwaDeadLetter,
    int NotificationsQueued,
    int NotificationsFailed,
    int ReportsDue,
    int ReportsFailed24h,
    int ComplianceRemindersDue)
{
    public static readonly QueueHealthEvidence Unavailable = new(false, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record ReportingTelemetry(
    int ReportRuns24h,
    int FailedReportRuns24h,
    double FailureRatePercent24h,
    int P95DurationMs24h,
    int ActiveSchedules,
    int StaleSchedules,
    DateTime? LastReportRunAtUtc);

public sealed record WorkforcePlanningTelemetry(
    int ActivePositions,
    int FrozenPositions,
    int OpenRequisitionHeadcount,
    int ApprovedRequisitionHeadcount,
    decimal BudgetedMonthlyCost);
