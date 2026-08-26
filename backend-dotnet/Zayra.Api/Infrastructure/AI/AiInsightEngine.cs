using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.AI;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.AI;

/// <summary>
/// Autonomous AI Insight Engine — wakes every hour, scans all active tenants,
/// and materializes proactive anomaly findings into AIInsight rows.
/// Detects: payroll variance, salary outliers, missing salary setup,
/// overtime anomalies, leave balance extremes, headcount spikes,
/// and contract/visa expiry risks.
/// </summary>
public sealed class AiInsightEngine : BackgroundService
{
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AiInsightEngine> _log;
    private readonly WorkerHeartbeatReporter? _heartbeat;

    public AiInsightEngine(IServiceScopeFactory scopeFactory, ILogger<AiInsightEngine> log,
        WorkerHeartbeatReporter? heartbeat = null)
    {
        _scopeFactory = scopeFactory;
        _log = log;
        _heartbeat = heartbeat;
    }

    /// <summary>Exposed for integration tests — runs one analysis cycle synchronously.</summary>
    public Task AnalyzeOnceAsync(CancellationToken ct) => RunAnalysisForAllTenantsAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_heartbeat is not null) await _heartbeat.StartedAsync(ProductionWorkerNames.AiInsights, stoppingToken);
        // stagger startup by 2 minutes so DB migrations have applied
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAnalysisForAllTenantsAsync(stoppingToken);
                if (_heartbeat is not null) await _heartbeat.SucceededAsync(ProductionWorkerNames.AiInsights, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _log.LogError(ex, "AiInsightEngine: unhandled error during analysis cycle");
                if (_heartbeat is not null)
                    try { await _heartbeat.FailedAsync(ProductionWorkerNames.AiInsights, ex, stoppingToken); }
                    catch (Exception heartbeatEx) { _log.LogWarning(heartbeatEx, "Could not persist AI insight worker failure heartbeat."); }
            }

            await Task.Delay(RunInterval, stoppingToken);
        }
    }

    private async Task RunAnalysisForAllTenantsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var llm = scope.ServiceProvider.GetService<ILlmClient>();

        var tenantIds = await db.Tenants
            .AsNoTracking()
            .Where(t => t.IsActive)
            .Select(t => t.Id)
            .ToListAsync(ct);

        _log.LogInformation("AiInsightEngine: scanning {Count} tenants", tenantIds.Count);

        foreach (var tenantId in tenantIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await using var tenantScope = _scopeFactory.CreateAsyncScope();
                var tenantDb = tenantScope.ServiceProvider.GetRequiredService<ZayraDbContext>();
                var tenantLlm = tenantScope.ServiceProvider.GetService<ILlmClient>();
                await AnalyzeTenantAsync(tenantDb, tenantLlm, tenantId, ct);
            }
            catch (Exception ex) { _log.LogWarning(ex, "AiInsightEngine: error for tenant {TenantId}", tenantId); }
        }
    }

    internal async Task AnalyzeTenantAsync(ZayraDbContext db, ILlmClient? llm, Guid tenantId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var insights = new List<AIInsight>();

        // ── 1. Payroll Variance Analysis — per legal entity (Phase 2) ─────────
        // Comparing payroll totals ACROSS companies produces meaningless variance and
        // leaks one company's payroll movement into a sibling's insight feed. Analyze
        // each company's run series independently and stamp the insight's CompanyId.
        var recentRuns = await db.PayrollRuns
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && (r.Status == "Locked" || r.Status == "Approved"))
            .OrderByDescending(r => r.Year).ThenByDescending(r => r.Month)
            .Take(24)
            .ToListAsync(ct);

        foreach (var companySeries in recentRuns.GroupBy(r => r.CompanyId))
        {
            var runs = companySeries.Take(4).ToList();
            if (runs.Count < 2) continue;
            var latest = runs[0];
            var prior3Avg = runs.Skip(1).Average(r => r.TotalNetSalary);
            if (prior3Avg > 0)
            {
                var variancePct = Math.Abs((double)(latest.TotalNetSalary - (decimal)prior3Avg) / (double)prior3Avg) * 100;
                if (variancePct > 10)
                {
                    var direction = latest.TotalNetSalary > (decimal)prior3Avg ? "increased" : "decreased";
                    insights.Add(BuildInsight(tenantId, "Payroll", "PayrollVariance",
                        variancePct > 20 ? "Critical" : "Warning",
                        $"Net payroll {direction} by {variancePct:F1}%",
                        $"Net payroll for {MonthLabel(latest.Month, latest.Year)} is {direction} by {variancePct:F1}% " +
                        $"({latest.TotalNetSalary:N0}) vs. 3-month average ({prior3Avg:N0}). Review for new hires, terminations, or one-time adjustments.",
                        new { LatestRunId = latest.Id, LatestNet = latest.TotalNetSalary, Prior3Avg = prior3Avg, VariancePct = variancePct },
                        companySeries.Key));
                }
            }
        }

        // ── 2. Employees Missing Salary Setup ─────────────────────────────────
        var activeCount = await db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active")
            .CountAsync(ct);

        var assignedCount = await db.EmployeeSalaryStructures
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive && s.EffectiveDate <= DateOnly.FromDateTime(DateTime.UtcNow))
            .Join(db.Employees.Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active"),
                  s => s.EmployeeId, e => e.Id, (s, e) => s.EmployeeId)
            .Distinct()
            .CountAsync(ct);

        var missingSalary = activeCount - assignedCount;
        if (missingSalary > 0 && activeCount > 0)
        {
            var missingPct = missingSalary * 100.0 / activeCount;
            insights.Add(BuildInsight(tenantId, "Payroll", "MissingSalarySetup",
                missingPct > 20 ? "Critical" : "Warning",
                $"{missingSalary} employee(s) without salary assignment",
                $"{missingSalary} of {activeCount} active employees ({missingPct:F0}%) have no active salary structure assigned. " +
                "These employees will be excluded from payroll processing until salary is configured.",
                new { MissingCount = missingSalary, TotalActive = activeCount, MissingPct = missingPct }));
        }

        // ── 3. Overtime Anomaly Detection ─────────────────────────────────────
        if (recentRuns.Count > 0)
        {
            var latestRun = recentRuns[0];
            // Load flat records and aggregate in C# — GroupBy inside EF provider
            // queries is not portable across InMemory, SQLite, and MySQL.
            var rawOvertimeRows = await db.PayrollEarnings
                .AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.PayrollRunId == latestRun.Id && e.Source == "Overtime")
                .Select(e => new { e.EmployeeId, e.Amount })
                .ToListAsync(ct);
            var overtimeEarnings = rawOvertimeRows
                .GroupBy(e => e.EmployeeId)
                .Select(g => new { EmployeeId = g.Key, TotalOvertime = g.Sum(x => x.Amount) })
                .OrderByDescending(x => x.TotalOvertime)
                .Take(5)
                .ToList();

            var basicSalaries = await db.EmployeeSalaryStructures
                .AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.IsActive && s.EffectiveDate <= DateOnly.FromDateTime(DateTime.UtcNow))
                .GroupBy(s => s.EmployeeId)
                .Select(g => (double)g.OrderByDescending(s => s.EffectiveDate).First().BasicSalary)
                .ToListAsync(ct);
            var avgBasic = basicSalaries.Count > 0 ? basicSalaries.Average() : 0.0;

            if (avgBasic > 0)
            {
                var flagged = overtimeEarnings.Where(o => (double)o.TotalOvertime > avgBasic * 0.3).ToList();
                if (flagged.Count > 0)
                {
                    insights.Add(BuildInsight(tenantId, "Payroll", "OvertimeAnomaly",
                        "Warning",
                        $"{flagged.Count} employee(s) with overtime exceeding 30% of average basic",
                        $"{flagged.Count} employee(s) received overtime pay exceeding 30% of the company average basic salary in {MonthLabel(latestRun.Month, latestRun.Year)}. " +
                        "Review for unauthorized overtime, shift planning issues, or data entry errors.",
                        new { RunId = latestRun.Id, FlaggedEmployeeIds = flagged.Select(f => f.EmployeeId), AvgBasic = avgBasic }));
                }
            }
        }

        // ── 4. Leave Balance Extremes ─────────────────────────────────────────
        // Load raw rows first — GroupBy aggregates inside EF provider queries
        // are not portable across providers (InMemory, SQLite, MySQL differ).
        var rawLeaveBalances = await db.EmployeeLeaveBalances
            .AsNoTracking()
            .Where(b => b.TenantId == tenantId)
            .Select(b => new { b.EmployeeId, b.Available })
            .ToListAsync(ct);
        var leaveExtremes = rawLeaveBalances
            .GroupBy(b => b.EmployeeId)
            .Count(g => g.Sum(x => x.Available) > 60);

        if (leaveExtremes > 0)
        {
            insights.Add(BuildInsight(tenantId, "Leave", "LeaveAccumulationRisk",
                "Warning",
                $"{leaveExtremes} employee(s) with 60+ days leave balance",
                $"{leaveExtremes} employee(s) have accumulated more than 60 days of leave balance. " +
                "Large balances represent a financial liability and may indicate low team morale or vacation planning issues. " +
                "Consider enforcing a leave utilization policy.",
                new { EmployeeCount = leaveExtremes, Threshold = 60 }));
        }

        // ── 5. Headcount Change Alert ─────────────────────────────────────────
        var thisMonthJoins = await db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted &&
                        e.CreatedAtUtc >= now.AddDays(-30) && e.Status == "Active")
            .CountAsync(ct);

        var thisMonthTerminations = await db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted &&
                        e.UpdatedAtUtc >= now.AddDays(-30) &&
                        (e.Status == "Offboarded" || e.Status == "Archived"))
            .CountAsync(ct);

        if (thisMonthTerminations > 5 && activeCount > 0)
        {
            var turnoverPct = thisMonthTerminations * 100.0 / activeCount;
            if (turnoverPct > 5)
            {
                insights.Add(BuildInsight(tenantId, "HR", "HeadcountTurnover",
                    turnoverPct > 10 ? "Critical" : "Warning",
                    $"{thisMonthTerminations} termination(s) in the last 30 days ({turnoverPct:F1}%)",
                    $"{thisMonthTerminations} employees left in the last 30 days, representing {turnoverPct:F1}% monthly turnover. " +
                    $"This is above normal thresholds. Review exit interviews and retention strategies. New hires: {thisMonthJoins}.",
                    new { Terminations = thisMonthTerminations, NewJoins = thisMonthJoins, ActiveCount = activeCount, TurnoverPct = turnoverPct }));
            }
        }

        // ── 6. Visa/Iqama Expiry Risk ─────────────────────────────────────────
        var expiryWarningDate = DateOnly.FromDateTime(now.AddDays(60));
        var expiringVisa = await db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active" &&
                        e.VisaExpiryDate.HasValue && e.VisaExpiryDate.Value <= expiryWarningDate)
            .CountAsync(ct);

        if (expiringVisa > 0)
        {
            insights.Add(BuildInsight(tenantId, "Compliance", "VisaExpiryRisk",
                expiringVisa > 5 ? "Critical" : "Warning",
                $"{expiringVisa} employee(s) with visa expiring within 60 days",
                $"{expiringVisa} active employee(s) have visas expiring within the next 60 days. " +
                "Expired visas can result in regulatory fines and operational disruption. " +
                "Initiate renewal processes immediately.",
                new { ExpiringCount = expiringVisa, WindowDays = 60 }));
        }

        // ── 7. Salary Structure Deactivation Risk ─────────────────────────────
        var inactiveStructuresWithAssignments = await db.SalaryStructures
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && !s.IsDeleted && !s.IsActive)
            .Join(db.EmployeeSalaryStructures.Where(e => e.TenantId == tenantId && e.IsActive),
                  s => s.Id, e => e.SalaryStructureId, (s, e) => s.Id)
            .CountAsync(ct);

        if (inactiveStructuresWithAssignments > 0)
        {
            insights.Add(BuildInsight(tenantId, "Payroll", "InactiveSalaryStructure",
                "Warning",
                $"{inactiveStructuresWithAssignments} employee(s) assigned to inactive salary structures",
                $"{inactiveStructuresWithAssignments} employee(s) are assigned to salary structures that have been deactivated. " +
                "These employees will fail payroll validation. Reassign them to active salary structures.",
                new { Count = inactiveStructuresWithAssignments }));
        }

        // ── Save new insights (deduplicate by InsightType within 24h) ─────────
        if (insights.Count == 0) return;

        var cutoff = now.AddHours(-24);
        var existingTypes = await db.AIInsights
            .Where(i => i.TenantId == tenantId && i.CreatedAtUtc >= cutoff)
            .Select(i => i.InsightType)
            .ToListAsync(ct);

        var newInsights = insights
            .Where(i => !existingTypes.Contains(i.InsightType))
            .ToList();

        if (newInsights.Count > 0)
        {
            db.AIInsights.AddRange(newInsights);
            await db.SaveChangesAsync(ct);
            _log.LogInformation("AiInsightEngine: tenant {TenantId} — {Count} new insight(s) saved", tenantId, newInsights.Count);
        }
    }

    private static AIInsight BuildInsight(Guid tenantId, string module, string insightType, string severity,
        string title, string summary, object data, Guid? companyId = null) => new()
    {
        TenantId = tenantId,
        CompanyId = companyId,
        Module = module,
        InsightType = insightType,
        Severity = severity,
        Title = title,
        Summary = summary,
        DataJson = System.Text.Json.JsonSerializer.Serialize(data),
        GeneratedBy = "System",
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static string MonthLabel(int month, int year) =>
        $"{new DateTime(year, month, 1):MMMM yyyy}";
}
