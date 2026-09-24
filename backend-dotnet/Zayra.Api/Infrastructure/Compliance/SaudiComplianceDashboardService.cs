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
    private readonly GosiReconciliationService _reconciliation;

    public SaudiComplianceDashboardService(ZayraDbContext db, GosiReconciliationService reconciliation)
    {
        _db = db;
        _reconciliation = reconciliation;
    }

    public async Task<SaudiComplianceDashboard> BuildAsync(Guid tenantId, CancellationToken ct)
    {
        var evaluatedAt = DateTime.UtcNow;

        var qiwa = await BuildQiwaAsync(tenantId, ct);
        var wps  = await BuildWpsAsync(tenantId, ct);
        var gosi = await BuildGosiAsync(tenantId, ct);

        var (overallScore, scoreBreakdown) = ComputeComplianceScore(qiwa, wps, gosi);
        var actionItems   = BuildActionItems(qiwa, wps, gosi, evaluatedAt);

        var urgentCount   = actionItems.Count(a => a.Severity is "Critical" or "High");

        // Enumerate which Saudi modules are actively enabled for this tenant.
        var enabledModules = new List<string>();
        if (qiwa.FeatureEnabled)    enabledModules.Add("QIWA");
        enabledModules.Add("WPS");   // WPS is always structurally active when Payroll is on
        enabledModules.Add("GOSI");

        var overall = new OverallSection(overallScore, urgentCount, evaluatedAt, enabledModules, scoreBreakdown);
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
        // A readiness percentage with nothing to be ready is undefined, not 0% and not 100%.
        // null travels to the UI, which renders "Not configured" instead of a coloured bar that
        // claims a state the tenant has not reached yet.
        double? percent = total == 0 ? null : Math.Round(ready * 100.0 / total, 1);

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
                issues.Add($"{Plural(missingIbanCount, "employee has", "employees have")} a missing or invalid IBAN for WPS payment.");
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
            .Where(s => s.TenantId == tenantId && s.IsActive)
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
        // EmployeesMissingGosiEmployerId counts AFFECTED EMPLOYEES, so it is 0 for a tenant with no
        // employees — even when the employer ID is missing. The card was reading that count as
        // "is it set?" and printing a confident "Set" directly above "Company GOSI employer ID is
        // not set". The boolean below is the actual answer to the question the card asks.
        var missingEmpId = string.IsNullOrWhiteSpace(company?.GosiEmployerId) ? employees.Count : 0;
        var employerIdConfigured = !string.IsNullOrWhiteSpace(company?.GosiEmployerId);

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
        // Was `employees.Count == 0 ? 100.0`, which put a full green "100% Readiness" bar directly
        // above the tenant's own "Company GOSI employer ID is not set" warning. Zero ready out of
        // zero employees is undefined — the UI shows "Not configured" for null.
        double? readinessPct = employees.Count == 0 ? null : Math.Round(readyCount * 100.0 / employees.Count, 1);
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
        // POD-A1: unified onto the SAME reconciliation engine the GOSI endpoints use, so this count
        // agrees with GosiController.variance-report (one GOSI truth). "expected" is recomputed via the
        // run's country pack + covered-wage base — NOT the old GosiCalculationService basic-only path,
        // which used a different base and rate store and flagged 100% of Saudi employees.
        var varianceCount = 0;
        var lastRun = await _db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && (r.Status == "Locked" || r.Status == "Paid"))
            .OrderByDescending(r => r.Year).ThenByDescending(r => r.Month)
            .FirstOrDefaultAsync(ct);

        if (lastRun is not null)
        {
            var recon = await _reconciliation.ReconcileAsync(tenantId, lastRun, ct);
            varianceCount = recon.VarianceCount;
        }

        var warnings = new List<string>();
        if (missingRef > 0)
            warnings.Add($"{Plural(missingRef, "employee is", "employees are")} missing a GOSI reference number.");
        if (string.IsNullOrWhiteSpace(company?.GosiEmployerId))
            warnings.Add("Company GOSI employer ID is not set.");
        if (gccCount > 0)
            warnings.Add($"{Plural(gccCount, "GCC employee", "GCC employees")} — contribution rates pending legal confirmation.");

        return new GosiDashboardSection(
            missingRef, missingEmpId, employerIdConfigured,
            readyCount, blockedCount, warningCount, readinessPct,
            gccCount, varianceCount, warnings, blockedEmployees);
    }

    // ── Score ─────────────────────────────────────────────────────────────────

    private const double QiwaWeight = 0.30;
    private const double WpsWeight  = 0.35;
    private const double GosiWeight = 0.35;

    /// <summary>
    /// The compliance score and the working behind it. The arithmetic is unchanged — what is new is
    /// that every component now carries its weight, its own sub-score and a plain-language basis, so
    /// a customer can see exactly how the headline number was reached instead of being handed an
    /// unexplained "70 / 100". A KPI nobody can trace is a KPI nobody can act on.
    /// </summary>
    private static (int Score, IReadOnlyList<ComplianceScoreComponent> Breakdown) ComputeComplianceScore(
        QiwaDashboardSection qiwa, WpsDashboardSection wps, GosiDashboardSection gosi)
    {
        // ── QIWA (30%) — readiness percent when the module is on; full marks when it is off ──
        double qiwaScore;
        bool   qiwaMeasurable;
        string qiwaBasis;
        if (!qiwa.FeatureEnabled)
        {
            qiwaScore      = 100.0;
            qiwaMeasurable = true;
            qiwaBasis      = "QIWA is not switched on for this account, so it is not held against you and scores full marks.";
        }
        else if (qiwa.ReadinessPercent is null)
        {
            // NOTE: an unmeasurable QIWA component scores 0 while an unmeasurable GOSI component
            // scores 100 (below). That asymmetry is pre-existing and is exactly why a brand-new
            // tenant lands on 70. It is left untouched here on purpose — this change makes the
            // number explainable, it does not redefine it. Rebalancing the weights is a separate,
            // product-owned decision.
            qiwaScore      = 0.0;
            qiwaMeasurable = false;
            qiwaBasis      = "No active employees yet, so QIWA readiness cannot be measured. This section scores nothing until the first employee is added.";
        }
        else
        {
            qiwaScore      = qiwa.ReadinessPercent.Value;
            qiwaMeasurable = true;
            qiwaBasis      = $"{qiwa.ReadyForSync} of {Plural(qiwa.TotalEmployees, "active employee", "active employees")} have every detail QIWA asks for.";
        }

        // ── WPS (35%) — starts at 100; 25 off per blocking issue, 3 off per unusable IBAN (max 25) ──
        var issuePenalty = wps.BlockingIssues.Count * 25.0;
        var ibanPenalty  = wps.MissingIbanCount > 0 ? Math.Min(25.0, wps.MissingIbanCount * 3.0) : 0.0;
        var wpsScore     = Math.Max(0, 100.0 - issuePenalty - ibanPenalty);

        var wpsParts = new List<string>();
        if (issuePenalty > 0)
            wpsParts.Add($"{issuePenalty:0.#} off for {Plural(wps.BlockingIssues.Count, "payroll issue", "payroll issues")} blocking a salary file");
        if (ibanPenalty > 0)
            wpsParts.Add($"{ibanPenalty:0.#} off for {Plural(wps.MissingIbanCount, "employee", "employees")} without a usable bank IBAN");
        var wpsBasis = wpsParts.Count == 0
            ? "Starts at 100. Nothing is blocking a salary file and every employee has a usable bank IBAN."
            : $"Starts at 100. {string.Join("; ", wpsParts)}.";

        // ── GOSI (35%) — readiness percent; full marks while there is nobody to assess ──
        double gosiScore;
        bool   gosiMeasurable;
        string gosiBasis;
        var gosiPopulation = gosi.ReadyCount + gosi.BlockedCount;
        if (gosiPopulation == 0)
        {
            gosiScore      = 100.0;
            gosiMeasurable = false;
            gosiBasis      = "No active employees yet, so GOSI readiness cannot be measured. This section keeps full marks until the first employee is added.";
        }
        else
        {
            gosiScore      = gosi.ReadinessPercent ?? 100.0;
            gosiMeasurable = true;
            gosiBasis      = $"{gosi.ReadyCount} of {Plural(gosiPopulation, "active employee", "active employees")} are ready to be filed to GOSI.";
        }

        var breakdown = new List<ComplianceScoreComponent>
        {
            Component("QIWA", QiwaWeight, qiwaScore, qiwaMeasurable, qiwaBasis),
            Component("WPS",  WpsWeight,  wpsScore,  true,           wpsBasis),
            Component("GOSI", GosiWeight, gosiScore, gosiMeasurable, gosiBasis),
        };

        var score = (int)Math.Round((qiwaScore * QiwaWeight) + (wpsScore * WpsWeight) + (gosiScore * GosiWeight));
        return (score, breakdown);
    }

    private static ComplianceScoreComponent Component(
        string module, double weight, double score, bool measurable, string basis)
        => new(module,
               Math.Round(weight * 100.0, 0),
               Math.Round(score, 1),
               Math.Round(score * weight, 1),
               measurable,
               basis);

    /// <summary>"1 employee" / "2 employees" — user-facing copy never says "employee(s)".</summary>
    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

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
                $"{Plural(qiwa.BlockedFromSync, "employee is", "employees are")} blocked from QIWA sync",
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
                $"{Plural(qiwa.FailedSyncCount, "QIWA sync attempt", "QIWA sync attempts")} failed and need retrying",
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
                $"{Plural(wps.MissingIbanCount, "employee has", "employees have")} a missing or invalid IBAN",
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
                $"{Plural(wps.PendingApprovals, "payroll run is", "payroll runs are")} awaiting approval",
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
                $"{Plural(gosi.BlockedCount, "employee is", "employees are")} blocked from GOSI calculation",
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
                $"{Plural(gosi.GccEmployeeCount, "GCC employee", "GCC employees")} — contribution rates pending legal confirmation",
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
                $"{Plural(gosi.VarianceCount, "GOSI variance", "GOSI variances")} detected in the last payroll run",
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
    IReadOnlyList<string> EnabledModules,
    IReadOnlyList<ComplianceScoreComponent> ScoreBreakdown);

/// <summary>
/// One weighted input to the headline compliance score, with the evidence behind it.
/// <paramref name="Measurable"/> is false when the tenant has no records to assess yet, which is
/// what lets the UI say "nothing to measure" rather than present a confident-looking sub-score.
/// </summary>
public record ComplianceScoreComponent(
    string Module,
    double WeightPercent,
    double Score,
    double PointsContributed,
    bool Measurable,
    string Basis);

public record QiwaDashboardSection(
    bool FeatureEnabled,
    bool CredentialConfigured,
    string ConnectionStatus,
    DateTime? LastConnectedAt,
    int TotalEmployees,
    int ReadyForSync,
    int BlockedFromSync,
    /// <summary>null when there are no active employees — readiness is undefined, not 0% or 100%.</summary>
    double? ReadinessPercent,
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
    /// <summary>How many employees are affected — 0 when the tenant has no employees at all.</summary>
    int EmployeesMissingGosiEmployerId,
    /// <summary>Whether the company actually has a GOSI employer ID. Not derivable from the count above.</summary>
    bool GosiEmployerIdConfigured,
    int ReadyCount,
    int BlockedCount,
    int WarningCount,
    /// <summary>null when there are no active employees — readiness is undefined, not 0% or 100%.</summary>
    double? ReadinessPercent,
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
