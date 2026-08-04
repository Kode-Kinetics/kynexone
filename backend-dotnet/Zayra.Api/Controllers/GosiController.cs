using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/gosi")]
[Authorize(Roles = "Admin,HR Manager,Payroll Manager,Payroll Officer")]
public class GosiController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly GosiReconciliationService _reconciliation;

    public GosiController(ZayraDbContext db, GosiReconciliationService reconciliation)
    {
        _db = db;
        _reconciliation = reconciliation;
    }

    // ── Contribution Rules ────────────────────────────────────────────────────

    /// <summary>
    /// Lists all active GOSI contribution rules for the tenant (including system defaults).
    /// </summary>
    [HttpGet("contribution-rules")]
    public async Task<IActionResult> GetContributionRules(CancellationToken ct)
    {
        var tenantId = GetTenantId();
        // IgnoreQueryFilters is intentional: GosiContributionRule uses TenantId==Guid.Empty for
        // platform-wide defaults, which the global tenant filter would exclude. We re-apply
        // explicit tenant scope in the WHERE clause below (own tenant + Guid.Empty only).
        var rules = await _db.GosiContributionRules
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => (r.TenantId == Guid.Empty || r.TenantId == tenantId) && r.IsActive)
            .OrderBy(r => r.Classification)
            .ThenBy(r => r.Branch)
            .ThenBy(r => r.Payer)
            .ThenByDescending(r => r.EffectiveFrom)
            .ToListAsync(ct);

        return Ok(rules.Select(r => new
        {
            r.Id,
            r.TenantId,
            isDefault       = r.TenantId == Guid.Empty,
            r.CountryCode,
            r.Classification,
            r.Branch,
            r.Payer,
            r.Rate,
            r.MinContributoryWage,
            r.MaxContributoryWage,
            r.EffectiveFrom,
            r.EffectiveTo,
            r.IsActive,
            r.SourceReference,
            r.Notes,
        }));
    }

    /// <summary>
    /// Creates a tenant-specific GOSI contribution rule override.
    /// Requires payroll.manage permission.
    /// </summary>
    [HttpPost("contribution-rules")]
    public async Task<IActionResult> CreateContributionRule(
        [FromBody] CreateGosiRuleRequest req,
        CancellationToken ct)
    {
        // HARDENED (compliance boundary): GOSI contribution rates are the flagship statutory rate and
        // the actual computation source (GosiCalculationService), so overriding one is a bounded
        // statutory action — it requires the higher-trust payroll.rates.statutory_override permission
        // (not ordinary payroll.manage) and a non-empty reason. Every write is audited below.
        if (!HasPermission("payroll.rates.statutory_override")) return Forbid();
        if (string.IsNullOrWhiteSpace(req.SourceReference) && string.IsNullOrWhiteSpace(req.Notes))
            return BadRequest(new { error = "A reason (SourceReference or Notes) is required to override a GOSI contribution rate." });

        var tenantId = GetTenantId();
        var rule = new GosiContributionRule
        {
            TenantId           = tenantId,
            CountryCode        = req.CountryCode ?? "SA",
            Classification     = req.Classification,
            Branch             = req.Branch,
            Payer              = req.Payer,
            Rate               = req.Rate,
            MinContributoryWage = req.MinContributoryWage,
            MaxContributoryWage = req.MaxContributoryWage,
            EffectiveFrom      = req.EffectiveFrom,
            EffectiveTo        = req.EffectiveTo,
            SourceReference    = req.SourceReference,
            Notes              = req.Notes,
            CreatedBy          = GetUserId(),
        };

        _db.GosiContributionRules.Add(rule);
        await GosiAudit("gosi.rule.created", rule.Id.ToString(),
            new { rule.Classification, rule.Branch, rule.Payer, rule.Rate, rule.EffectiveFrom }, ct);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetContributionRules), new { }, rule);
    }

    /// <summary>
    /// Deactivates a tenant-specific GOSI contribution rule.
    /// Cannot deactivate system defaults (TenantId == Guid.Empty).
    /// </summary>
    [HttpDelete("contribution-rules/{id:guid}")]
    public async Task<IActionResult> DeactivateContributionRule(Guid id, CancellationToken ct)
    {
        if (!HasPermission("payroll.manage")) return Forbid();

        var tenantId = GetTenantId();
        // GosiContributionRule has no IsDeleted; the global filter is purely tenant-scoped
        // (TenantId == tenantId). IgnoreQueryFilters is not needed here — removed so the
        // global filter remains active as a second tenant-isolation guard.
        var rule = await _db.GosiContributionRules
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);

        if (rule is null) return NotFound(new { error = "Rule not found or belongs to a different tenant." });

        rule.IsActive = false;
        await GosiAudit("gosi.rule.deactivated", rule.Id.ToString(),
            new { rule.Classification, rule.Branch, rule.Payer }, ct);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    // ── Employee Readiness ────────────────────────────────────────────────────

    /// <summary>
    /// Returns a GOSI readiness summary for all active employees in the tenant.
    /// </summary>
    [HttpGet("readiness-summary")]
    public async Task<IActionResult> GetReadinessSummary(CancellationToken ct)
    {
        var tenantId  = GetTenantId();
        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active")
            .ToListAsync(ct);

        var salaries = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .ToListAsync(ct);

        var rules = await LoadRulesAsync(tenantId, ct);
        var periodDate = DateOnly.FromDateTime(DateTime.UtcNow);

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

        return Ok(new
        {
            totalEmployees  = reports.Count,
            readyCount      = reports.Count(r => r.IsReady),
            blockedCount    = reports.Count(r => !r.IsReady),
            warningCount    = reports.Count(r => r.WarningCount > 0),
            classificationBreakdown = reports
                .GroupBy(r => r.Classification)
                .Select(g => new { classification = g.Key, count = g.Count(), readyCount = g.Count(r => r.IsReady) }),
            blockedEmployees = reports
                .Where(r => !r.IsReady)
                .Select(r => new
                {
                    r.EmployeeId,
                    r.EmployeeCode,
                    r.Classification,
                    blockingIssues = r.BlockingIssues.Select(i => new { i.Code, i.Message }),
                }),
        });
    }

    /// <summary>
    /// Returns GOSI readiness detail for a single employee.
    /// </summary>
    [HttpGet("employees/{employeeId:int}/readiness")]
    public async Task<IActionResult> GetEmployeeReadiness(int employeeId, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var employee = await _db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == employeeId, ct);
        if (employee is null) return NotFound();

        var salary = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.EmployeeId == employeeId && s.IsActive && s.EffectiveDate <= DateOnly.FromDateTime(DateTime.UtcNow))
            .OrderByDescending(s => s.EffectiveDate)
            .FirstOrDefaultAsync(ct);

        var rules      = await LoadRulesAsync(tenantId, ct);
        var periodDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var applicable = GosiCalculationService.SelectActiveRules(
            GosiCalculationService.DeriveClassification(employee.Nationality),
            rules, periodDate, tenantId);

        var report = GosiReadinessValidator.Validate(employee, salary?.BasicSalary, applicable);

        GosiContributionResult? preview = null;
        if (report.IsReady && salary?.BasicSalary > 0)
            preview = GosiCalculationService.Calculate(
                employee.Nationality, salary.BasicSalary, rules, periodDate, tenantId);

        return Ok(new
        {
            report.EmployeeId,
            report.EmployeeCode,
            report.Classification,
            report.IsReady,
            blockingIssues = report.BlockingIssues.Select(i => new { i.Code, i.Message, i.IsBlocking }),
            warnings       = report.Warnings.Select(i => new { i.Code, i.Message }),
            contributionPreview = preview is null ? null : new
            {
                preview.EmployeeTotal,
                preview.EmployerTotal,
                lines = preview.Lines.Select(l => new
                {
                    l.Branch,
                    l.Payer,
                    l.Rate,
                    l.ContributoryWage,
                    l.Amount,
                }),
            },
        });
    }

    // ── Payroll-Run GOSI Summary ──────────────────────────────────────────────

    /// <summary>
    /// Returns a per-branch GOSI contribution summary for a completed payroll run, reconciled against
    /// what the run actually deducted, what the run's pack recomputes as expected, and what the GL
    /// actually posted to Social Insurance Payable (EE 2101 / ER 2106) for the run.
    /// </summary>
    [HttpGet("payroll-runs/{runId:guid}/contribution-summary")]
    public async Task<IActionResult> GetRunContributionSummary(Guid runId, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == runId, ct);
        if (run is null) return NotFound();

        // POD-A1: one GOSI truth. All figures come from GosiReconciliationService, which reads the run's
        // real persisted rows (Source == "Statutory", authoritative IsEmployerContribution flag) and
        // recomputes "expected" with the SAME country pack + covered-wage base the run used.
        var recon = await _reconciliation.ReconcileAsync(tenantId, run, ct);

        // Per-component-code breakdown, split by the authoritative employer flag (not by a code guess).
        var branchBreakdown = recon.Deductions
            .GroupBy(d => d.ComponentCode)
            .Select(g => new
            {
                componentCode          = g.Key,
                componentName          = g.First().ComponentName,
                isEmployerContribution = g.Any(d => d.IsEmployerContribution),
                totalAmount            = g.Sum(d => d.Amount),
                employeeCount          = g.Select(d => d.EmployeeId).Distinct().Count(),
            })
            .OrderBy(x => x.componentCode)
            .ToList();

        return Ok(new
        {
            runId,
            period               = recon.Period,
            hasStatutoryData     = recon.HasStatutoryData,
            packResolved         = recon.PackResolved,
            packStatusNote       = recon.PackStatusNote,
            // POD-B2 — this run is only PART of the period's filing once siblings exist, and its expected
            // figure is then a standalone recomputation against an actual that is a period delta. Both
            // fields are inert (0 / false / null) for a single-run period.
            siblingRunCount         = recon.SiblingRunCount,
            expectedIsPeriodPartial = recon.ExpectedIsPeriodPartial,
            periodScopeNote         = recon.PeriodScopeNote,

            // ── Actual (persisted deductions) ──
            totalEmployeeContrib = recon.ActualEmployeeTotal,
            totalEmployerContrib = recon.ActualEmployerTotal,
            totalGosi            = recon.ActualEmployeeTotal + recon.ActualEmployerTotal,

            // ── Expected (recomputed via the run's pack + base) ──
            expectedEmployeeContrib = recon.ExpectedEmployeeTotal,
            expectedEmployerContrib = recon.ExpectedEmployerTotal,
            expectedVsActualEmployeeDelta = recon.ExpectedVsActualEmployeeDelta,
            expectedVsActualEmployerDelta = recon.ExpectedVsActualEmployerDelta,

            // ── Slip witnesses (persisted at run time; 4-way tie-out with lines/GL/expected) ──
            slipEmployeeStatutoryTotal = recon.SlipEmployeeStatutoryTotal,
            slipEmployerStatutoryTotal = recon.SlipEmployerStatutoryTotal,
            runEmployerStatutoryCost   = recon.RunEmployerStatutoryCost,

            // ── GL liability tie-out (2101 EE / 2106 ER, honoring any per-company account override) ──
            glPosted             = recon.GlPosted,
            glEmployeeLiability  = recon.GlEmployeeLiability,
            glEmployerLiability  = recon.GlEmployerLiability,
            glEmployeeAccount    = recon.GlEmployeeAccount,
            glEmployerAccount    = recon.GlEmployerAccount,
            glEmployeeDelta      = recon.GlEmployeeDelta,
            glEmployerDelta      = recon.GlEmployerDelta,

            branchBreakdown,
        });
    }

    // ── POD-B2 (M8): PERIOD-level GOSI summary — the FILING view ──────────────────────────────
    //
    // The run-level endpoint above is keyed on a single runId. POD-A1's invariant (deducted == report
    // == GL) survives per run, but a GOSI submission is filed per PERIOD, and once POD-B2 allows an
    // off-cycle/supplementary run alongside the monthly one, a per-run report is only PART of the
    // filing. This unions every non-voided run for the (company, year, month) and recomputes
    // "expected" ONCE on the period-aggregated covered wage, so the statutory ceiling — a period
    // concept — is applied to the period total exactly once.
    /// <summary>
    /// Period-level GOSI contribution summary across ALL non-voided payroll runs for the month.
    /// Use this, not the per-run summary, as the source for a statutory filing.
    /// </summary>
    [HttpGet("periods/{year:int}/{month:int}/contribution-summary")]
    public async Task<IActionResult> GetPeriodContributionSummary(
        int year, int month, [FromQuery] Guid? companyId, CancellationToken ct)
    {
        if (month < 1 || month > 12) return BadRequest(new { error = "invalid_month", message = "month must be between 1 and 12." });
        var tenantId = GetTenantId();
        var recon = await _reconciliation.ReconcilePeriodAsync(tenantId, companyId, year, month, ct);

        var branchBreakdown = recon.Deductions
            .GroupBy(d => d.ComponentCode)
            .Select(g => new
            {
                componentCode          = g.Key,
                componentName          = g.First().ComponentName,
                isEmployerContribution = g.Any(d => d.IsEmployerContribution),
                totalAmount            = g.Sum(d => d.Amount),
                employeeCount          = g.Select(d => d.EmployeeId).Distinct().Count(),
            })
            .OrderBy(x => x.componentCode)
            .ToList();

        return Ok(new
        {
            period               = recon.Period,
            companyId            = recon.CompanyId,
            runCount             = recon.RunCount,
            runIds               = recon.RunIds,
            // Which run contributed what — a preparer must be able to see that the month's filing is the
            // sum of a Regular run plus, say, an OffCycle bonus run.
            runs                 = recon.RunSummaries,
            hasStatutoryData     = recon.HasStatutoryData,
            packResolved         = recon.PackResolved,
            packStatusNote       = recon.PackStatusNote,

            totalEmployeeContrib = recon.ActualEmployeeTotal,
            totalEmployerContrib = recon.ActualEmployerTotal,
            totalGosi            = recon.ActualEmployeeTotal + recon.ActualEmployerTotal,

            expectedEmployeeContrib = recon.ExpectedEmployeeTotal,
            expectedEmployerContrib = recon.ExpectedEmployerTotal,
            expectedVsActualEmployeeDelta = recon.ExpectedVsActualEmployeeDelta,
            expectedVsActualEmployerDelta = recon.ExpectedVsActualEmployerDelta,

            glPosted             = recon.GlPosted,
            glEmployeeLiability  = recon.GlEmployeeLiability,
            glEmployerLiability  = recon.GlEmployerLiability,
            glEmployeeDelta      = recon.GlEmployeeDelta,
            glEmployerDelta      = recon.GlEmployerDelta,

            varianceCount        = recon.VarianceCount,
            employees            = recon.Rows,
            branchBreakdown,
        });
    }

    /// <summary>
    /// Reconciliation/variance report: compares the GOSI deductions actually stored for each employee
    /// in the run against the amounts recomputed from the SAME country pack + covered-wage base the run
    /// used (basic + housing + eligible bonus, capped at the statutory ceiling), for BOTH the employee
    /// and employer sides. A non-zero variance is a true drift finding (salary/bonus/nationality/rate
    /// changed since the run), not a report bug. The run-vs-GL tie-out is surfaced at the run level.
    /// Variances above SAR 0.01 are flagged.
    /// </summary>
    [HttpGet("payroll-runs/{runId:guid}/variance-report")]
    public async Task<IActionResult> GetVarianceReport(Guid runId, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == runId, ct);
        if (run is null) return NotFound();

        // POD-A1: one GOSI truth — see GosiReconciliationService. "expected" is the run's own pack, not
        // the parallel GosiCalculationService/GosiContributionRule path (which used basic-only on a
        // different rate store and flagged 100% of Saudi employees).
        var recon = await _reconciliation.ReconcileAsync(tenantId, run, ct);

        var rows = recon.Rows
            .OrderBy(r => r.EmployeeCode, StringComparer.Ordinal)
            .Select(r => new GosiVarianceRow(
                EmployeeId:              r.EmployeeId,
                EmployeeCode:            r.EmployeeCode,
                EmployeeName:            r.EmployeeName,
                Classification:          r.Classification,
                CoveredWageBase:         r.CoveredWageBase,
                ExpectedEmployeeContrib: r.ExpectedEmployee,
                ActualEmployeeContrib:   r.ActualEmployee,
                EmployeeVariance:        r.EmployeeVariance,
                ExpectedEmployerContrib: r.ExpectedEmployer,
                ActualEmployerContrib:   r.ActualEmployer,
                EmployerVariance:        r.EmployerVariance,
                HasVariance:             r.HasVariance,
                ExpectedLines:           r.ExpectedLines
                    .Select(l => new GosiVarianceLine(l.Code, l.Label, l.EmployeeAmount, l.EmployerAmount))
                    .ToList()))
            .ToList();

        return Ok(new
        {
            runId,
            period            = recon.Period,
            packResolved      = recon.PackResolved,
            packStatusNote    = recon.PackStatusNote,
            hasStatutoryData  = recon.HasStatutoryData,
            totalEmployees    = rows.Count,
            withVariance      = rows.Count(r => r.HasVariance),
            // POD-B2 — when true, a variance below is expected arithmetic (period-delta actual vs
            // standalone expected), NOT a filing defect. Inert for a single-run period.
            siblingRunCount         = recon.SiblingRunCount,
            expectedIsPeriodPartial = recon.ExpectedIsPeriodPartial,
            periodScopeNote         = recon.PeriodScopeNote,
            // Run-level tie-out so a CFO sees the halala reconciliation alongside the per-employee drift.
            expectedEmployeeContrib = recon.ExpectedEmployeeTotal,
            actualEmployeeContrib   = recon.ActualEmployeeTotal,
            expectedEmployerContrib = recon.ExpectedEmployerTotal,
            actualEmployerContrib   = recon.ActualEmployerTotal,
            expectedVsActualEmployeeDelta = recon.ExpectedVsActualEmployeeDelta,
            expectedVsActualEmployerDelta = recon.ExpectedVsActualEmployerDelta,
            glPosted            = recon.GlPosted,
            glEmployeeLiability = recon.GlEmployeeLiability,
            glEmployerLiability = recon.GlEmployerLiability,
            glEmployeeDelta     = recon.GlEmployeeDelta,
            glEmployerDelta     = recon.GlEmployerDelta,
            rows,
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // IgnoreQueryFilters is intentional: same reasoning as GetContributionRules.
    // Platform defaults (TenantId==Guid.Empty) are excluded by the global filter, so we bypass
    // it and re-apply explicit scope: own tenant rows + Guid.Empty defaults only.
    private async Task<IReadOnlyList<GosiContributionRule>> LoadRulesAsync(Guid tenantId, CancellationToken ct) =>
        await _db.GosiContributionRules
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => (r.TenantId == Guid.Empty || r.TenantId == tenantId) && r.IsActive)
            .ToListAsync(ct);

    private Guid GetTenantId() => Guid.Parse(User.FindFirstValue("tenant_id")!);
    private Guid? GetUserId()  => Guid.TryParse(
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"),
        out var id) ? id : null;
    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission"
                          && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    private async Task GosiAudit(string action, string entityId, object? metadata, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        _db.AuditLogs.Add(new AuditLog
        {
            TenantId    = tenantId,
            Action      = action,
            EntityName  = "GosiContributionRule",
            EntityId    = entityId,
            UserId      = GetUserId(),
            Metadata    = System.Text.Json.JsonSerializer.Serialize(metadata),
            CreatedAtUtc = DateTime.UtcNow,
        });
    }
}

// ── Request records ────────────────────────────────────────────────────────────

public record CreateGosiRuleRequest(
    string   Classification,
    string   Branch,
    string   Payer,
    decimal  Rate,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo        = null,
    string?  CountryCode         = null,
    decimal? MinContributoryWage = null,
    decimal? MaxContributoryWage = null,
    string?  SourceReference     = null,
    string?  Notes               = null
);

// ── Variance-report response DTOs (concrete, not dynamic) ───────────────────────

public record GosiVarianceRow(
    int      EmployeeId,
    string   EmployeeCode,
    string   EmployeeName,
    string   Classification,
    decimal  CoveredWageBase,
    decimal  ExpectedEmployeeContrib,
    decimal  ActualEmployeeContrib,
    decimal  EmployeeVariance,
    decimal  ExpectedEmployerContrib,
    decimal  ActualEmployerContrib,
    decimal  EmployerVariance,
    bool     HasVariance,
    IReadOnlyList<GosiVarianceLine> ExpectedLines
);

public record GosiVarianceLine(string Code, string Label, decimal EmployeeAmount, decimal EmployerAmount);
