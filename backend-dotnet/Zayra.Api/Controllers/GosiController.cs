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

    public GosiController(ZayraDbContext db) => _db = db;

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
    /// Returns a per-branch GOSI contribution summary for a completed payroll run.
    /// </summary>
    [HttpGet("payroll-runs/{runId:guid}/contribution-summary")]
    public async Task<IActionResult> GetRunContributionSummary(Guid runId, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == runId, ct);
        if (run is null) return NotFound();

        var deductions = await _db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.PayrollRunId == runId
                     && d.Source == "GOSI")
            .ToListAsync(ct);

        var employeeCodes = new HashSet<string>
        {
            "GOSI_ANNUITIES_EMP", "GOSI_SANED_EMP",
            "GOSI_EMPLOYEE",  // backward-compat for runs processed before PR-3
        };
        var employerCodes = new HashSet<string>
        {
            "GOSI_ANNUITIES_ER", "GOSI_SANED_ER", "GOSI_OCHAZARDS_ER",
        };

        var branchSummary = deductions
            .GroupBy(d => d.ComponentCode)
            .Select(g => new
            {
                componentCode = g.Key,
                componentName = g.First().ComponentName,
                totalAmount   = g.Sum(d => d.Amount),
                employeeCount = g.Select(d => d.EmployeeId).Distinct().Count(),
            })
            .OrderBy(x => x.componentCode)
            .ToList();

        return Ok(new
        {
            runId,
            period               = $"{run.Year}-{run.Month:D2}",
            totalEmployeeContrib = deductions.Where(d => employeeCodes.Contains(d.ComponentCode)).Sum(d => d.Amount),
            totalEmployerContrib = deductions.Where(d => employerCodes.Contains(d.ComponentCode)).Sum(d => d.Amount),
            totalGosi            = deductions.Sum(d => d.Amount),
            branchBreakdown      = branchSummary,
        });
    }

    /// <summary>
    /// Reconciliation/variance report: compares GOSI deductions actually stored for
    /// each employee in the run against the amounts that would be computed from
    /// the current rules.  Variances above SAR 0.01 are flagged.
    /// </summary>
    [HttpGet("payroll-runs/{runId:guid}/variance-report")]
    public async Task<IActionResult> GetVarianceReport(Guid runId, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var run = await _db.PayrollRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == runId, ct);
        if (run is null) return NotFound();

        var periodEnd  = new DateTime(run.Year, run.Month,
            DateTime.DaysInMonth(run.Year, run.Month));
        var periodDate = DateOnly.FromDateTime(periodEnd);

        // Load employees, salaries, and actual deductions for the run
        var runEmployeeIds = await _db.PayrollRunEmployees.AsNoTracking()
            .Where(re => re.TenantId == tenantId && re.PayrollRunId == runId)
            .Select(re => re.EmployeeId)
            .ToListAsync(ct);

        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && runEmployeeIds.Contains(e.Id))
            .ToListAsync(ct);

        var salaries = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(s => s.TenantId == tenantId && runEmployeeIds.Contains(s.EmployeeId)
                     && s.EffectiveDate <= periodDate)
            .ToListAsync(ct);

        var actualDeductions = await _db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.PayrollRunId == runId
                     && d.Source == "GOSI")
            .ToListAsync(ct);

        var rules = await LoadRulesAsync(tenantId, ct);

        var rows = new List<object>();
        foreach (var emp in employees)
        {
            var salary = salaries
                .Where(s => s.EmployeeId == emp.Id)
                .OrderByDescending(s => s.EffectiveDate)
                .FirstOrDefault();
            var basic  = salary?.BasicSalary ?? 0m;

            var expected = GosiCalculationService.Calculate(
                emp.Nationality, basic, rules, periodDate, tenantId);

            var actualEmployeeTotal = actualDeductions
                .Where(d => d.EmployeeId == emp.Id
                         && (d.ComponentCode.EndsWith("_EMP") || d.ComponentCode == "GOSI_EMPLOYEE"))
                .Sum(d => d.Amount);

            var variance = Math.Round(actualEmployeeTotal - expected.EmployeeTotal, 2);

            rows.Add(new
            {
                employeeId         = emp.Id,
                employeeCode       = emp.EmployeeCode,
                employeeName       = emp.FullName,
                classification     = expected.Classification,
                basicSalary        = basic,
                expectedEmployeeContrib = expected.EmployeeTotal,
                actualEmployeeContrib   = actualEmployeeTotal,
                variance,
                hasVariance        = Math.Abs(variance) > 0.01m,
                expectedLines      = expected.Lines
                    .Where(l => l.Payer == GosiPayers.Employee)
                    .Select(l => new { l.Branch, l.Rate, l.Amount }),
            });
        }

        var variantRows = rows.Cast<dynamic>().Where(r => r.hasVariance).ToList();

        return Ok(new
        {
            runId,
            period          = $"{run.Year}-{run.Month:D2}",
            totalEmployees  = rows.Count,
            withVariance    = variantRows.Count,
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
