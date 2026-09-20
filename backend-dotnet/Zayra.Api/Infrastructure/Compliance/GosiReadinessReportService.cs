using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Compliance;

/// <summary>
/// Builds a per-employee GOSI readiness report using the official contribution-rule
/// engine (GosiCalculationService + GosiReadinessValidator).
///
/// No sensitive identifiers (GosiReference, NationalId, Iqama, IBAN, or raw
/// contributory wage) appear in any output record.
///
/// <para><b>THE CONTRIBUTORY WAGE COMES FROM ONE PLACE.</b> This report used to build its own
/// contributory wage inline (<c>salary.BasicSalary + salary.HousingAllowance</c>) and then hand it
/// to <c>GosiCalculationService.Calculate</c>, which caps using
/// <c>GosiContributionRule.MaxContributoryWage</c>. The payslip caps using the effective-dated
/// statutory rule <c>gosi.covered_wage_ceiling_sar</c>. When the rule table's column is null — and
/// the platform seeder does not set it — this report computed UNCAPPED and disagreed with the
/// payslip by the whole excess: SAR 5,850 reported against SAR 4,387.50 deducted on a SAR 60,000
/// wage. It is the reported figure a finance team reconciles against the GOSI portal, so the wrong
/// one was the trusted one. Both the base and the ceiling now come from
/// <see cref="GosiContributoryWageBasis"/>, which reads exactly what the payslip reads.</para>
/// </summary>
public sealed class GosiReadinessReportService
{
    private readonly ZayraDbContext _db;
    private readonly IStatutoryRuleReader _rules;

    public GosiReadinessReportService(ZayraDbContext db, IStatutoryRuleReader rules)
    {
        _db = db;
        _rules = rules;
    }

    public async Task<GosiReadinessReport> BuildAsync(Guid tenantId, CancellationToken ct)
    {
        var periodDate = DateOnly.FromDateTime(DateTime.UtcNow);

        // Resolved once per report: the ceiling is statutory and period-scoped, not per employee.
        var ceiling = await GosiContributoryWageBasis.CeilingAsync(_rules, periodDate, ct);
        var ceilingBoundCount = 0;

        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active")
            .ToListAsync(ct);

        var salaries = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive)
            .ToListAsync(ct);

        // IgnoreQueryFilters is intentional: platform-wide default rules carry TenantId==Guid.Empty
        // and are excluded by the global tenant filter. We bypass the filter and re-apply explicit
        // scope: own-tenant overrides + Guid.Empty defaults only. No other tenant's rows are visible.
        var rules = await _db.GosiContributionRules
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => (r.TenantId == Guid.Empty || r.TenantId == tenantId) && r.IsActive)
            .ToListAsync(ct);

        var rows = new List<GosiEmployeeReadinessRow>(employees.Count);
        var readyCount = 0;

        foreach (var emp in employees)
        {
            var salary = salaries
                .Where(s => s.EmployeeId == emp.Id && s.EffectiveDate <= periodDate)
                .OrderByDescending(s => s.EffectiveDate)
                .FirstOrDefault();

            var applicable = GosiCalculationService.SelectActiveRules(
                GosiCalculationService.DeriveClassification(emp.Nationality),
                rules, periodDate, tenantId);

            var readiness = GosiReadinessValidator.Validate(emp, salary?.BasicSalary, applicable);

            decimal employeeTotal = 0m;
            decimal employerTotal = 0m;
            var lines = Array.Empty<GosiContributionLineDto>();

            if (readiness.IsReady)
            {
                // S1/A2(b) — basic + housing is the GOSI contributory wage for a Saudi national, and
                // is what the payroll run's country pack has always deducted on. Passing basic alone
                // here made this report under-state every contribution against the actual payslip.
                //
                // …and the other half of the same defect: the payslip also CAPS that wage at the
                // effective-dated statutory ceiling, which this report did not, because
                // GosiCalculationService caps off a different column that nothing populates. The
                // base and the ceiling now both come from the payslip's own source.
                var uncapped = GosiContributoryWageBasis.CoveredWage(
                    salary!.BasicSalary, salary.HousingAllowance);
                var contributoryWage = Math.Min(uncapped, ceiling);
                if (contributoryWage < uncapped) ceilingBoundCount++;

                var calc = GosiCalculationService.Calculate(
                    emp.Nationality, contributoryWage, rules, periodDate, tenantId);

                employeeTotal = calc.EmployeeTotal;
                employerTotal = calc.EmployerTotal;
                // ContributoryWage excluded — it reveals the employee's basic salary.
                lines = calc.Lines
                    .Select(l => new GosiContributionLineDto(l.Branch, l.Payer, l.Rate, l.Amount))
                    .ToArray();

                readyCount++;
            }

            rows.Add(new GosiEmployeeReadinessRow(
                EmployeeId:               emp.Id,
                EmployeeCode:             emp.EmployeeCode,
                FullName:                 emp.FullName,
                Classification:           readiness.Classification,
                IsReady:                  readiness.IsReady,
                BlockingIssues:           readiness.BlockingIssues.Select(i => new GosiIssueDto(i.Code, i.Message)).ToArray(),
                Warnings:                 readiness.Warnings.Select(i => new GosiIssueDto(i.Code, i.Message)).ToArray(),
                EmployeeContributionTotal: employeeTotal,
                EmployerContributionTotal: employerTotal,
                Lines:                    lines));
        }

        return new GosiReadinessReport(
            TotalEmployees: employees.Count,
            ReadyCount:     readyCount,
            BlockedCount:   employees.Count - readyCount,
            PeriodDate:     periodDate,
            CalculatedAt:   DateTime.UtcNow,
            Disclaimer:     "This report is illustrative readiness guidance based on configured rules; validate statutory filings with GOSI and qualified Saudi compliance advisers.",
            ContributoryWageCeiling: ceiling,
            ContributoryWageBasis:
                "Basic + housing, capped at the effective-dated statutory ceiling "
                + $"('{GosiContributoryWageBasis.CeilingRuleKey}' = {ceiling:N0} SAR on {periodDate:yyyy-MM-dd}) — "
                + "the same base and the same ceiling the payslip deducts on.",
            EmployeesAtWageCeiling: ceilingBoundCount,
            Employees:      rows);
    }
}

// ── Response types (no sensitive salary or identifier values) ─────────────────

public record GosiReadinessReport(
    int                                  TotalEmployees,
    int                                  ReadyCount,
    int                                  BlockedCount,
    DateOnly                             PeriodDate,
    DateTime                             CalculatedAt,
    string                               Disclaimer,
    // The statutory covered-wage ceiling actually applied, in SAR. Published so a finance team
    // reconciling against the GOSI portal can see WHY a high earner's contribution is smaller than
    // their salary implies, instead of concluding the figure is wrong.
    decimal                              ContributoryWageCeiling,
    // Plain-language statement of the base and ceiling, naming the rule key.
    string                               ContributoryWageBasis,
    // How many employees had the ceiling bind. Zero means it never applied.
    int                                  EmployeesAtWageCeiling,
    IReadOnlyList<GosiEmployeeReadinessRow> Employees);

public record GosiEmployeeReadinessRow(
    int                                  EmployeeId,
    string                               EmployeeCode,
    string                               FullName,
    string                               Classification,
    bool                                 IsReady,
    IReadOnlyList<GosiIssueDto>          BlockingIssues,
    IReadOnlyList<GosiIssueDto>          Warnings,
    decimal                              EmployeeContributionTotal,
    decimal                              EmployerContributionTotal,
    IReadOnlyList<GosiContributionLineDto> Lines);

/// <summary>Issue DTO — symbolic code + human-readable message.  Never contains raw identifiers.</summary>
public record GosiIssueDto(string Code, string Message);

/// <summary>Per-branch contribution line.  ContributoryWage omitted to avoid leaking basic salary.</summary>
public record GosiContributionLineDto(string Branch, string Payer, decimal Rate, decimal Amount);
