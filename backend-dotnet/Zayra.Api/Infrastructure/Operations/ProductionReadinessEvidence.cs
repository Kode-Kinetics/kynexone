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
        var tenantCounts = dbProbe.Healthy
            ? await db.Tenants.AsNoTracking()
                .GroupBy(_ => 1)
                .Select(g => new { Total = g.Count(), Active = g.Count(x => x.IsActive) })
                .FirstOrDefaultAsync(ct)
            : null;

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
            tenantCounts?.Total ?? 0,
            tenantCounts?.Active ?? 0,
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

    /// <summary>
    /// Migrations this build expects to exist, newest source of truth first.
    /// </summary>
    /// <remarks>
    /// Prefers the assembly's own migration classes. Falls back to the build-time manifest for the
    /// production image, which deletes Migrations/ to keep the Render build under its memory limit
    /// (see <see cref="MigrationManifest"/>). Returns empty only when BOTH are absent — a state the
    /// caller treats as unknown, never as zero.
    /// </remarks>
    internal static IReadOnlyCollection<string> ResolveExpectedMigrations(IEnumerable<string> assemblyMigrations)
    {
        var fromAssembly = assemblyMigrations as IReadOnlyCollection<string> ?? assemblyMigrations.ToList();
        if (fromAssembly.Count > 0) return fromAssembly;
        return MigrationManifest.Ids;
    }

    private static async Task<int> CountPendingMigrationsAsync(ZayraDbContext db, bool dbHealthy, CancellationToken ct)
    {
        if (!dbHealthy) return 0; // DB probe already reports not_ready; don't double-count.
        if (!db.Database.IsRelational()) return 0;
        try
        {
            // DO NOT "simplify" this back to GetPendingMigrationsAsync(). That call is
            // GetMigrations() minus the applied history, and GetMigrations() reads the migration
            // classes compiled into THIS assembly. The production Dockerfile deletes Migrations/
            // before publishing, so in the deployed image that set is empty and the subtraction
            // returns zero pending for every database, unconditionally. This check was a no-op by
            // construction from the day the strip landed: /health/ready reported `ready` with twelve
            // migrations missing, which is how a release was promoted against an un-migrated
            // database. A gate that cannot fail is not a gate.
            var expected = ResolveExpectedMigrations(db.Database.GetMigrations());
            if (expected.Count == 0)
            {
                // Fail closed. We know nothing about what this build should have applied, so we
                // cannot claim parity. -1 is the unknown sentinel; ResolveStatus turns it into
                // not_ready and Render keeps the previous instance serving.
                return -1;
            }

            var applied = await db.Database.GetAppliedMigrationsAsync(ct);
            return expected.Except(applied, StringComparer.Ordinal).Count();
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
        // Readiness is polled continuously by the load balancer. Keep the seven independent queue
        // counters in one database command so the health probe does not compete with user requests.
        var counts = await db.Tenants.AsNoTracking()
            .Select(_ => new
            {
                QiwaPending = db.QiwaSyncLogs.Count(x => x.Status == QiwaSyncLogStatuses.Pending || x.Status == QiwaSyncLogStatuses.Processing),
                QiwaDeadLetter = db.QiwaSyncLogs.Count(x => x.Status == QiwaSyncLogStatuses.DeadLetter),
                NotificationsPending = db.NotificationDeliveries.Count(x => x.Outcome == DeliveryOutcomes.Queued || x.Outcome == DeliveryOutcomes.Sending),
                NotificationsFailed = db.NotificationDeliveries.Count(x => x.Outcome == DeliveryOutcomes.Failed || x.Outcome == DeliveryOutcomes.Unknown),
                ReportsDue = db.ReportSchedules.Count(x => x.IsActive && !x.IsDeleted && (x.NextRunAtUtc == null || x.NextRunAtUtc <= now)),
                ReportsFailed = db.ReportExecutionLogs.Count(x => x.Status == "Failed" && x.CreatedAtUtc >= now.AddHours(-24)),
                ComplianceDue = db.ComplianceReminders.Count(x => x.Status == "Pending" && x.ScheduledAtUtc != null && x.ScheduledAtUtc <= now),
            })
            .FirstOrDefaultAsync(ct);

        return new QueueHealthEvidence(
            true,
            counts?.QiwaPending ?? 0,
            counts?.QiwaDeadLetter ?? 0,
            counts?.NotificationsPending ?? 0,
            counts?.NotificationsFailed ?? 0,
            counts?.ReportsDue ?? 0,
            counts?.ReportsFailed ?? 0,
            counts?.ComplianceDue ?? 0);
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
    /// <summary>
    /// The fleet was NOT MEASURED, because an earlier term — the database probe or migration parity —
    /// already decided the answer (see BuildReadinessAsync). Every count is zero and every worker reads
    /// <c>not_evaluated</c>, because no heartbeat row was read.
    ///
    /// <para>This used to report <c>MissingCount = 6</c> with all six workers <c>"unavailable"</c>.
    /// That is a FABRICATION, and it is indistinguishable from a genuinely dead worker fleet. On
    /// 2026-09-23 three production deploys were investigated as a worker outage on the strength of it,
    /// while the real cause — two migrations absent from the connected database — sat one field away in
    /// the same payload. A readiness probe must never report a measurement it did not take.</para>
    /// </summary>
    public static readonly WorkerFleetReadiness Unavailable = new(
        false, 0, 0, 0, 0, 0,
        ProductionWorkerNames.All.Select(x => new WorkerReadiness(x, "not_evaluated", null, null)).ToList());
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
