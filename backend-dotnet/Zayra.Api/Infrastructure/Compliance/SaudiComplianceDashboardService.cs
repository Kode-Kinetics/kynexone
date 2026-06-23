using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Compliance;

/// <summary>
/// Computes the Saudi regulatory compliance Command Center (QIWA + WPS + GOSI) for a
/// tenant.  All figures are derived from live DB records — nothing is hard-coded.
/// All queries are tenant-scoped.  No sensitive identifiers (IBAN, GosiReference,
/// Iqama, NationalId) appear in any response payload — only counts and codes.
/// </summary>
public sealed class SaudiComplianceDashboardService
{
    private readonly ZayraDbContext _db;

    public SaudiComplianceDashboardService(ZayraDbContext db) => _db = db;

    public async Task<SaudiComplianceDashboard> BuildAsync(Guid tenantId, CancellationToken ct)
    {
        var evaluatedAt = DateTime.UtcNow;

        var qiwa = await BuildQiwaAsync(tenantId, ct);
        var wps  = await BuildWpsAsync(tenantId, ct);
        var gosi = await BuildGosiAsync(tenantId, ct);

        var overallScore  = ComputeComplianceScore(qiwa, wps, gosi);
        var actionItems   = BuildActionItems(qiwa, wps, gosi, evaluatedAt);

        var urgentCount   = actionItems.Count(a => a.Severity is "Critical" or "High");

        // Enumerate which Saudi modules are actively enabled for this tenant.
        var enabledModules = new List<string>();
        if (qiwa.FeatureEnabled)    enabledModules.Add("QIWA");
        enabledModules.Add("WPS");   // WPS is always structurally active when Payroll is on
        enabledModules.Add("GOSI");

        var overall = new OverallSection(overallScore, urgentCount, evaluatedAt, enabledModules);
        return new SaudiComplianceDashboard(overall, qiwa, wps, gosi, actionItems);
    }

    // ── Module builders ───────────────────────────────────────────────────────

    private async Task<QiwaDashboardSection> BuildQiwaAsync(Guid tenantId, CancellationToken ct)
    {
        var featureEnabled      = await IsFeatureEnabledAsync(tenantId, FeatureKeys.QiwaIntegration, ct);
        var credentialConfigured = await _db.QiwaApiCredentials.AsNoTracking()
            .AnyAsync(c => c.TenantId == tenantId, ct);

        var connection = await _db.QiwaTenantConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);

        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active")
            .ToListAsync(ct);

        var blocked = new List<BlockedEmployee>();
        foreach (var e in employees)
        {
            var missing = QiwaIntegrationService.MissingQiwaFields(e);
            if (missing.Count > 0)
                blocked.Add(new BlockedEmployee(e.Id, e.EmployeeCode, e.FullName, missing));
        }

        var total   = employees.Count;
        var ready   = total - blocked.Count;
        var percent = total == 0 ? 0 : Math.Round(ready * 100.0 / total, 1);

        var failedCount = await _db.QiwaSyncLogs
            .CountAsync(l => l.TenantId == tenantId &&
                             (l.Status == QiwaSyncLogStatuses.Failed || l.Status == QiwaSyncLogStatuses.DeadLetter), ct);

        var lastSuccess = await _db.QiwaSyncLogs
            .Where(l => l.TenantId == tenantId && l.Status == QiwaSyncLogStatuses.Success)
            .OrderByDescending(l => l.CompletedAtUtc)
            .Select(l => l.CompletedAtUtc)
            .FirstOrDefaultAsync(ct);

        return new QiwaDashboardSection(
            featureEnabled, credentialConfigured,
            connection?.Status ?? "NotConfigured",
            connection?.LastConnectedAtUtc,
            total, ready, blocked.Count, percent,
            failedCount, lastSuccess, blocked);
    }

    private async Task<WpsDashboardSection> BuildWpsAsync(Guid tenantId, CancellationToken ct)
    {
        var lastRun = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.Year).ThenByDescending(r => r.Month)
            .FirstOrDefaultAsync(ct);

        var issues = new List<string>();
        string? lastRunStatus = lastRun?.Status;
        string? lastRunPeriod = lastRun is null ? null : $"{lastRun.Year}-{lastRun.Month:D2}";

        var pendingApprovals = await _db.PayrollRuns
            .CountAsync(r => r.TenantId == tenantId
                          && r.Status != "Locked" && r.Status != "Paid" && r.Status != "Draft", ct);

        var missingIbanCount = 0;
        if (lastRun is not null)
        {
            if (lastRun.Status != "Locked" && lastRun.Status != "Paid")
                issues.Add($"Latest payroll run ({lastRunPeriod}) is not yet approved/locked.");

            var profiles = await _db.EmployeePayrollProfiles.AsNoTracking()
                .Where(p => p.TenantId == tenantId && !p.IsDeleted)
                .ToListAsync(ct);
            missingIbanCount = profiles.Count(p => !IbanValidator.IsValid(p.Iban));
            if (missingIbanCount > 0)
                issues.Add($"{missingIbanCount} employee(s) missing or invalid IBAN for WPS payment.");
        }

        // WPS export history from SIF file batches.
        var lastBatch = await _db.WPSFileBatches.AsNoTracking()
            .Where(b => b.TenantId == tenantId)
            .OrderByDescending(b => b.CreatedAtUtc)
            .Select(b => new { b.CreatedAtUtc, b.Status, b.EmployeeCount })
            .FirstOrDefaultAsync(ct);

        var exportHistoryCount = await _db.WPSFileBatches
            .CountAsync(b => b.TenantId == tenantId, ct);

        return new WpsDashboardSection(
            lastRunStatus, lastRunPeriod, pendingApprovals,
            missingIbanCount, exportHistoryCount,
            lastBatch?.CreatedAtUtc, lastBatch?.Status,
            issues);
    }

    private async Task<GosiDashboardSection> BuildGosiAsync(Guid tenantId, CancellationToken ct)
    {
        var company = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);

        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active")
            .ToListAsync(ct);

        var salaries = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .ToListAsync(ct);

        // IgnoreQueryFilters is intentional: same as GosiReadinessReportService — Guid.Empty
        // platform defaults are invisible through the global tenant filter. Scope is re-applied
        // explicitly: own-tenant overrides + Guid.Empty defaults only.
        var rules = await _db.GosiContributionRules
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => (r.TenantId == Guid.Empty || r.TenantId == tenantId) && r.IsActive)
            .ToListAsync(ct);

        var periodDate   = DateOnly.FromDateTime(DateTime.UtcNow);
        var missingRef   = employees.Count(e => string.IsNullOrWhiteSpace(e.GosiReference));
        var missingEmpId = string.IsNullOrWhiteSpace(company?.GosiEmployerId) ? employees.Count : 0;

        // Run readiness validator for every active employee.
        var reports = employees.Select(e =>
        {
            var salary = salaries
                .Where(s => s.EmployeeId == e.Id && s.EffectiveDate <= periodDate)
                .OrderByDescending(s => s.EffectiveDate)
                .FirstOrDefault();

            var applicable = GosiCalculationService.SelectActiveRules(
                GosiCalculationService.DeriveClassification(e.Nationality),
                rules, periodDate, tenantId);

            return GosiReadinessValidator.Validate(e, salary?.BasicSalary, applicable);
        }).ToList();

        var readyCount      = reports.Count(r => r.IsReady);
        var blockedCount    = reports.Count(r => !r.IsReady);
        var warningCount    = reports.Count(r => r.WarningCount > 0);
        var readinessPct    = employees.Count == 0 ? 100.0 : Math.Round(readyCount * 100.0 / employees.Count, 1);
        var gccCount        = reports.Count(r => r.Classification == GosiClassifications.GCC);

        // Blocked employee list: only codes + blocking issue codes, no sensitive values.
        var blockedEmployees = reports
            .Where(r => !r.IsReady)
            .Join(employees, r => r.EmployeeId, e => e.Id, (r, e) => new GosiBlockedEmployee(
                r.EmployeeId,
                r.EmployeeCode,
                e.FullName,
                r.BlockingIssues.Select(i => i.Code).ToList()))
            .ToList();

        // Variance count from the most recent completed payroll run.
        var varianceCount = 0;
        var lastRun = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && (r.Status == "Locked" || r.Status == "Paid"))
            .OrderByDescending(r => r.Year).ThenByDescending(r => r.Month)
            .FirstOrDefaultAsync(ct);

        if (lastRun is not null)
        {
            var lastRunDate = new DateOnly(lastRun.Year, lastRun.Month,
                DateTime.DaysInMonth(lastRun.Year, lastRun.Month));

            var deductions = await _db.PayrollDeductions.AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.PayrollRunId == lastRun.Id
                            && d.Source == "Statutory" && !d.IsEmployerContribution)
                .ToListAsync(ct);

            var empIds = deductions.Select(d => d.EmployeeId).Distinct().ToList();
            foreach (var empId in empIds)
            {
                var emp  = employees.FirstOrDefault(e => e.Id == empId);
                if (emp is null) continue;
                var sal  = salaries
                    .Where(s => s.EmployeeId == empId && s.EffectiveDate <= lastRunDate)
                    .OrderByDescending(s => s.EffectiveDate)
                    .FirstOrDefault();
                var expected = GosiCalculationService.Calculate(
                    emp.Nationality, sal?.BasicSalary ?? 0m, rules, lastRunDate, tenantId);
                var actual = deductions
                    .Where(d => d.EmployeeId == empId
                             && d.ComponentCode.EndsWith("-EE"))
                    .Sum(d => d.Amount);
                if (Math.Abs(actual - expected.EmployeeTotal) > 0.01m)
                    varianceCount++;
            }
        }

        var warnings = new List<string>();
        if (missingRef > 0)
            warnings.Add($"{missingRef} employee(s) missing GOSI reference number.");
        if (string.IsNullOrWhiteSpace(company?.GosiEmployerId))
            warnings.Add("Company GOSI employer ID is not set.");
        if (gccCount > 0)
            warnings.Add($"{gccCount} GCC employee(s) — contribution rates pending legal confirmation.");

        return new GosiDashboardSection(
            missingRef, missingEmpId,
            readyCount, blockedCount, warningCount, readinessPct,
            gccCount, varianceCount, warnings, blockedEmployees);
    }

    // ── Score ─────────────────────────────────────────────────────────────────

    private static int ComputeComplianceScore(
        QiwaDashboardSection qiwa, WpsDashboardSection wps, GosiDashboardSection gosi)
    {
        // QIWA weight: 30% when the feature is enabled; skip (treat as 100%) otherwise.
        double qiwaScore = qiwa.FeatureEnabled ? qiwa.ReadinessPercent : 100.0;

        // WPS weight: 35% — deduct 25 per blocking issue; deduct 3 per missing IBAN (max 25).
        double wpsScore = 100.0 - (wps.BlockingIssues.Count * 25.0);
        if (wps.MissingIbanCount > 0) wpsScore -= Math.Min(25.0, wps.MissingIbanCount * 3.0);
        wpsScore = Math.Max(0, wpsScore);

        // GOSI weight: 35% — based on readiness percent; 100% if no employees.
        double gosiScore = (gosi.ReadyCount + gosi.BlockedCount) > 0
            ? gosi.ReadinessPercent
            : 100.0;

        return (int)Math.Round((qiwaScore * 0.30) + (wpsScore * 0.35) + (gosiScore * 0.35));
    }

    // ── Action items ──────────────────────────────────────────────────────────

    private static List<ComplianceActionItem> BuildActionItems(
        QiwaDashboardSection qiwa, WpsDashboardSection wps, GosiDashboardSection gosi,
        DateTime evaluatedAt)
    {
        var items = new List<ComplianceActionItem>();

        // QIWA actions
        if (!qiwa.FeatureEnabled)
        {
            items.Add(new(
                "qiwa_feature_disabled",
                "Medium", "QIWA",
                "QIWA integration is not enabled",
                "The QIWA integration module is not active for this tenant. Enable it to begin syncing employees with the Ministry of Human Resources.",
                0,
                "Contact your platform administrator to activate the QIWA module.",
                "/saudi-compliance?tab=configure&section=qiwa",
                "compliance.read", true, evaluatedAt));
        }
        else if (!qiwa.CredentialConfigured)
        {
            items.Add(new(
                "qiwa_credentials_missing",
                "High", "QIWA",
                "QIWA API credentials are not configured",
                "OAuth2 client credentials have not been saved. Employee sync cannot proceed until credentials are provided.",
                0,
                "Go to Saudi Compliance → Configure → QIWA and enter your Client ID and Secret from the Qiwa Developer Portal.",
                "/saudi-compliance?tab=configure&section=qiwa",
                "compliance.read", true, evaluatedAt));
        }
        else if (qiwa.ConnectionStatus is "NotConfigured")
        {
            items.Add(new(
                "qiwa_not_configured",
                "High", "QIWA",
                "QIWA connection is not configured",
                "Establishment ID has not been set. QIWA sync requires a valid MOL-issued establishment number.",
                0,
                "Navigate to Saudi Compliance → Configure → QIWA and enter the Establishment ID.",
                "/saudi-compliance?tab=configure&section=qiwa",
                "compliance.read", true, evaluatedAt));
        }
        else if (qiwa.ConnectionStatus is "Error" or "ApiError" or "ConfigurationError")
        {
            items.Add(new(
                "qiwa_connection_error",
                "Critical", "QIWA",
                "QIWA connection is in an error state",
                $"The QIWA integration has encountered an error (status: {qiwa.ConnectionStatus}). Sync will be suspended until this is resolved.",
                0,
                "Review the QIWA configuration for credential or establishment ID issues.",
                "/saudi-compliance?tab=configure&section=qiwa",
                "compliance.read", true, evaluatedAt));
        }

        if (qiwa.BlockedFromSync > 0)
        {
            var severity = qiwa.BlockedFromSync >= 10 ? "Critical" : "High";
            items.Add(new(
                "qiwa_blocked_employees",
                severity, "QIWA",
                $"{qiwa.BlockedFromSync} employee(s) blocked from QIWA sync",
                "These employees are missing required fields (e.g. National ID, Date of Birth, Job Title) and cannot be submitted to QIWA.",
                qiwa.BlockedFromSync,
                "Open the People module and complete the missing QIWA fields for each blocked employee.",
                "/people?filter=qiwa_blocked",
                "qiwa.read", true, evaluatedAt));
        }

        if (qiwa.FailedSyncCount > 0)
        {
            items.Add(new(
                "qiwa_failed_syncs",
                "Medium", "QIWA",
                $"{qiwa.FailedSyncCount} QIWA sync attempt(s) failed or dead-lettered",
                "One or more recent employee sync operations to QIWA did not complete successfully. This may indicate a credential or API issue.",
                qiwa.FailedSyncCount,
                "Review sync logs and retry failed records. Check credentials if the failure rate is high.",
                "/saudi-compliance",
                "qiwa.read", true, evaluatedAt));
        }

        // WPS actions
        if (wps.MissingIbanCount > 0)
        {
            var severity = wps.MissingIbanCount >= 5 ? "Critical" : "High";
            items.Add(new(
                "wps_missing_iban",
                severity, "WPS",
                $"{wps.MissingIbanCount} employee(s) missing or invalid IBAN",
                "Saudi WPS requires a valid Saudi IBAN (SA + 22 digits) for every employee. Payroll cannot be disbursed via WPS for these employees.",
                wps.MissingIbanCount,
                "Go to People → Employee → Payroll Profile → Payment Details and add the correct IBAN.",
                "/people?filter=missing_iban",
                "payroll.read", true, evaluatedAt));
        }

        if (wps.PendingApprovals > 0)
        {
            items.Add(new(
                "wps_pending_approvals",
                "Medium", "WPS",
                $"{wps.PendingApprovals} payroll run(s) awaiting approval",
                "Payroll runs that are not yet Locked or Paid have not been submitted through WPS. Delays beyond the 10th of the month may result in non-compliance.",
                wps.PendingApprovals,
                "Navigate to Payroll and lock or approve the pending runs.",
                "/payroll",
                "payroll.read", true, evaluatedAt));
        }

        // GOSI actions
        if (string.IsNullOrEmpty(gosi.Warnings.FirstOrDefault(w => w.Contains("employer ID"))) is false
            || gosi.EmployeesMissingGosiEmployerId > 0)
        {
            items.Add(new(
                "gosi_missing_employer_id",
                "High", "GOSI",
                "GOSI employer ID is not set",
                "The company's GOSI employer ID is required for contribution filing. Without it, GOSI deductions cannot be attributed correctly.",
                0,
                "Go to Saudi Compliance → Configure → GOSI and enter the GOSI Employer ID.",
                "/saudi-compliance?tab=configure&section=gosi",
                "compliance.read", true, evaluatedAt));
        }

        if (gosi.BlockedCount > 0)
        {
            var severity = gosi.BlockedCount >= 5 ? "Critical" : "High";
            items.Add(new(
                "gosi_blocked_employees",
                severity, "GOSI",
                $"{gosi.BlockedCount} employee(s) blocked from GOSI calculation",
                "These employees are missing a GOSI reference number or basic salary and will be excluded from GOSI contribution deductions.",
                gosi.BlockedCount,
                "Open the People module and ensure each employee has a GOSI Reference and an active salary structure.",
                "/people?filter=missing_gosi_ref",
                "compliance.read", true, evaluatedAt));
        }

        if (gosi.GccEmployeeCount > 0)
        {
            items.Add(new(
                "gosi_gcc_pending_confirmation",
                "Medium", "GOSI",
                $"{gosi.GccEmployeeCount} GCC employee(s) — contribution rates pending legal confirmation",
                "GCC national contribution rates are seeded at the Saudi baseline pending bilateral treaty verification. Confirm applicable rates with your legal team before payroll processing.",
                gosi.GccEmployeeCount,
                "Review GCC employee GOSI rates under Compliance → GOSI Contribution Rules and update if required.",
                "/gosi/contribution-rules",
                "compliance.read", true, evaluatedAt));
        }

        if (gosi.VarianceCount > 0)
        {
            items.Add(new(
                "gosi_variance_detected",
                "High", "GOSI",
                $"{gosi.VarianceCount} GOSI variance(s) detected in last payroll run",
                "Actual GOSI deductions in the most recent run differ from expected rule-based amounts. This may indicate a rule change that was not applied retroactively.",
                gosi.VarianceCount,
                "Run the GOSI variance report for the most recent payroll period and investigate discrepancies.",
                "/gosi/variance",
                "payroll.read", true, evaluatedAt));
        }

        // Sort: Critical → High → Medium → Low
        return items.OrderBy(a => a.Severity switch
        {
            "Critical" => 0, "High" => 1, "Medium" => 2, _ => 3
        }).ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<bool> IsFeatureEnabledAsync(Guid tenantId, string featureKey, CancellationToken ct)
    {
        // Absent flag = feature enabled by default (matches HasAnyGatingFeatureAsync pattern).
        var flag = await _db.TenantFeatureFlags.AsNoTracking()
            .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.FeatureKey == featureKey, ct);
        return flag?.IsEnabled ?? true;
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record SaudiComplianceDashboard(
    OverallSection Overall,
    QiwaDashboardSection Qiwa,
    WpsDashboardSection Wps,
    GosiDashboardSection Gosi,
    IReadOnlyList<ComplianceActionItem> ActionItems);

public record OverallSection(
    int ComplianceScore,
    int UrgentActionCount,
    DateTime LastEvaluatedAt,
    IReadOnlyList<string> EnabledModules);

public record QiwaDashboardSection(
    bool FeatureEnabled,
    bool CredentialConfigured,
    string ConnectionStatus,
    DateTime? LastConnectedAt,
    int TotalEmployees,
    int ReadyForSync,
    int BlockedFromSync,
    double ReadinessPercent,
    int FailedSyncCount,
    DateTime? LastSuccessfulSync,
    IReadOnlyList<BlockedEmployee> BlockedEmployees);

public record BlockedEmployee(int EmployeeId, string EmployeeCode, string FullName, IReadOnlyList<string> MissingFields);

public record WpsDashboardSection(
    string? LastRunStatus,
    string? LastRunPeriod,
    int PendingApprovals,
    int MissingIbanCount,
    int ExportHistoryCount,
    DateTime? LastExportDate,
    string? LastExportStatus,
    IReadOnlyList<string> BlockingIssues);

public record GosiDashboardSection(
    int EmployeesMissingGosiRef,
    int EmployeesMissingGosiEmployerId,
    int ReadyCount,
    int BlockedCount,
    int WarningCount,
    double ReadinessPercent,
    int GccEmployeeCount,
    int VarianceCount,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<GosiBlockedEmployee> BlockedEmployees);

public record GosiBlockedEmployee(int EmployeeId, string EmployeeCode, string FullName, IReadOnlyList<string> BlockingIssueCodes);

public record ComplianceActionItem(
    string Id,
    string Severity,
    string Module,
    string Title,
    string Description,
    int AffectedCount,
    string RecommendedAction,
    string? Route,
    string PermissionRequired,
    bool CanAct,
    DateTime EvaluatedAt);
