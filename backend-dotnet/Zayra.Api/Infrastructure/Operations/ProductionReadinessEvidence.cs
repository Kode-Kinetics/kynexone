using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Data;
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

        return new ReadinessEvidence(
            dbProbe.Healthy ? "ready" : "not_ready",
            DateTime.UtcNow,
            new ReadinessDependencies(
                dbProbe,
                RedisDependency(config),
                QiwaDependency(config),
                await SmtpDependencyAsync(db, ct)),
            tenantCount,
            activeTenantCount);
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

        return new TelemetryEvidence(
            "ok",
            DateTime.UtcNow,
            new TelemetryWindow("24h", since),
            new DependencyModes(RedisDependency(config), QiwaDependency(config), await SmtpDependencyAsync(db, ct)),
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
                budgetedMonthlyCost));
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
        catch (Exception ex)
        {
            sw.Stop();
            return new DependencyProbe("error", false, sw.ElapsedMilliseconds, ex.Message);
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
    int ActiveTenants);

public sealed record ReadinessDependencies(
    DependencyProbe Database,
    DependencyMode Redis,
    DependencyMode Qiwa,
    DependencyMode Smtp);

public sealed record DependencyProbe(string Status, bool Healthy, long LatencyMs, string? Error);
public sealed record DependencyMode(string Mode, bool Configured);

public sealed record TelemetryEvidence(
    string Status,
    DateTime Utc,
    TelemetryWindow Window,
    DependencyModes Dependencies,
    GovernanceTelemetry Governance,
    ReportingTelemetry Reporting,
    WorkforcePlanningTelemetry WorkforcePlanning);

public sealed record TelemetryWindow(string Duration, DateTime SinceUtc);
public sealed record DependencyModes(DependencyMode Redis, DependencyMode Qiwa, DependencyMode Smtp);
public sealed record GovernanceTelemetry(int ControlledOverrides24h, DateTime? LatestControlledOverrideAtUtc);

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
