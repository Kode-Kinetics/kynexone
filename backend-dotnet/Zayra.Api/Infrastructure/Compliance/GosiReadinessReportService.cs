using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
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
///
/// <para><b>INTEGRATION NOTE (wave6 + feat/ksa-compliance-truth).</b> Both streams fixed this, and
/// the merged shape keeps one mechanism, not two. <c>fix/money-figures</c> removed the per-rule
/// <c>Min/MaxContributoryWage</c> columns from every calculation and made
/// <c>GosiCalculationService.Calculate</c> take <c>GosiWageBounds</c> resolved by
/// <c>KsaGosiWageBounds</c>; that is the clamp, and it is applied in one place.
/// <c>feat/ksa-compliance-truth</c> added the base delegation to
/// <c>SalaryBreakdown.GosiCoveredWage</c> and the three published fields below
/// (<c>ContributoryWageCeiling</c>, <c>ContributoryWageBasis</c>, <c>EmployeesAtWageCeiling</c>)
/// so the surface SAYS why a high earner's contribution is smaller than their salary implies.
/// <see cref="GosiContributoryWageBasis"/> now delegates to <c>KsaGosiWageBounds</c> rather than
/// reading the rule a second time.</para>
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

        // ── ONE CEILING, ONE SOURCE ───────────────────────────────────────────────────────────
        // Resolved once per report: the bounds are statutory and period-scoped, not per employee.
        // GosiContributoryWageBasis now DELEGATES to KsaGosiWageBounds.ResolveAsync — the very
        // call KsaDeductionCalculator makes to cap the payslip — so this report and the payslip
        // do not merely agree on the number, they execute the same resolver against the same rule
        // key and the same effective date. Without it this report computed uncapped while the
        // payslip capped: SAR 5,850 shown against SAR 4,387.50 deducted on a SAR 60,000 covered
        // wage, in the figure a finance team reconciles against the GOSI portal.
        //
        // Two names, one resolver and one memoized read per report: BoundsAsync is what the
        // calculator clamps with (it carries the floor too, so if a floor is ever configured this
        // report moves with the payslip instead of against it), CeilingAsync is the single figure
        // the report PUBLISHES so a finance team can see which ceiling bound.
        var bounds  = await GosiContributoryWageBasis.BoundsAsync(_rules, periodDate, ct);
        var ceiling = await GosiContributoryWageBasis.CeilingAsync(_rules, periodDate, ct);
        var ceilingBoundCount = 0;

        var employees = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active")
            .ToListAsync(ct);

        var salaries = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.IsActive)
            .ToListAsync(ct);

        var rows = new List<GosiEmployeeReadinessRow>(employees.Count);
        var readyCount = 0;

        foreach (var emp in employees)
        {
            // The salary the RUN would use for this month (effective by the period END), and the
            // readiness verdict taken from the payslip engine's own result with the run's own codes —
            // a new entrant or an unconfigured GCC national is Not ready here exactly as the run blocks
            // them. The retired gosi_contribution_rules table plays no part.
            var salary = GosiReadinessValidator.SalaryForPeriod(salaries, emp.Id, periodDate);
            var (readiness, calc) = await GosiReadinessValidator.AssessAsync(_rules, emp, salary, periodDate, ct);

            decimal employeeTotal = 0m;
            decimal employerTotal = 0m;
            var lines = Array.Empty<GosiContributionLineDto>();

            if (readiness.IsReady && calc is not null)
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
                var contributoryWage = bounds.Clamp(uncapped);
                if (contributoryWage < uncapped) ceilingBoundCount++;

                // ONE ENGINE, ONE STORE. The amounts come from the payslip's own calculator reading the
                // payslip's own effective-dated statutory rules — not from gosi_contribution_rules,
                // which a tenant override could move without moving the payslip. The engine applies
                // the same ceiling itself; `contributoryWage` above only lets the report say WHICH
                // employees the ceiling bound.
                // calc above is the payslip engine's result for this employee — ONE ENGINE, ONE STORE.
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
                Lines:                    lines)
            {
                Cohort = readiness.Cohort,
                Basis  = readiness.Basis,
            });
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
            Employees:      rows)
        {
            TenantWarnings = await IgnoredGosiOverridesAsync(tenantId, ct),
        };
    }

    /// <summary>
    /// GOSI rate/ceiling values this tenant saved before such writes were refused. Payroll reads the
    /// platform row only, so they were saved and never applied; surfaced so nobody believes they are in force.
    /// </summary>
    private async Task<IReadOnlyList<GosiIssueDto>> IgnoredGosiOverridesAsync(Guid tenantId, CancellationToken ct)
    {
        var ignored = await GosiStatutoryValues.FindIgnoredTenantOverridesAsync(_db, tenantId, ct);
        return ignored.Count == 0
            ? Array.Empty<GosiIssueDto>()
            : new[] { new GosiIssueDto(GosiStatutoryValues.IgnoredOverrideWarningCode, GosiStatutoryValues.IgnoredOverrideWarning(ignored)) };
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
    IReadOnlyList<GosiEmployeeReadinessRow> Employees)
{
    /// <summary>
    /// Tenant-level findings that are not about one employee — today, GOSI rate/ceiling values this
    /// tenant saved before such writes were refused, which payroll has never applied.
    /// </summary>
    public IReadOnlyList<GosiIssueDto> TenantWarnings { get; init; } = Array.Empty<GosiIssueDto>();
}

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
    IReadOnlyList<GosiContributionLineDto> Lines)
{
    /// <summary>The GOSI cohort the payslip engine computed on — the same one the run would use.</summary>
    public string? Cohort { get; init; }

    /// <summary>The payslip engine's plain-language basis (cohort, rates, period) for the figure.</summary>
    public string? Basis { get; init; }
}

/// <summary>Issue DTO — symbolic code + human-readable message.  Never contains raw identifiers.</summary>
public record GosiIssueDto(string Code, string Message);

/// <summary>Per-branch contribution line.  ContributoryWage omitted to avoid leaking basic salary.</summary>
public record GosiContributionLineDto(string Branch, string Payer, decimal Rate, decimal Amount);
