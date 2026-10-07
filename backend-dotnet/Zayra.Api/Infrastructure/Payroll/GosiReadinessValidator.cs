using Zayra.Api.Application.CountryPack;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Validates whether an employee is ready for GOSI, using the SAME findings the payroll run raises.
///
/// <para><b>One verdict.</b> "Ready" used to mean "has a GOSI reference and a basic salary, and some row
/// exists in gosi_contribution_rules". The run, meanwhile, BLOCKED a new entrant (the post-3-July-2024
/// schedule is not modelled) and a GCC national whose home-state scheme is not configured. So the
/// readiness screen said Ready for exactly the people payroll would refuse. The verdict is now taken from
/// the payslip engine's own result (<see cref="GosiCalculationService.CalculateAsync"/>) with the run's
/// own codes and severities:</para>
/// <list type="bullet">
/// <item><see cref="PayrollValidationEngine.GosiNewEntrantScheduleNotModelled"/> — blocking, as in the run.</item>
/// <item><see cref="PayrollValidationEngine.GosiGccSchemeNotConfigured"/> — blocking, as in the run.</item>
/// <item><see cref="PayrollValidationEngine.GosiCohortNotRecorded"/> — a warning, as in the run.</item>
/// </list>
/// <para>The retired gosi_contribution_rules table plays no part.</para>
/// </summary>
public static class GosiReadinessValidator
{
    /// <summary>
    /// The salary row the run would use for <paramref name="periodDate"/>'s month: active, effective on
    /// or before the period END, latest first — the run's own selection (PayrollController.Process).
    /// </summary>
    public static EmployeeSalaryStructure? SalaryForPeriod(
        IEnumerable<EmployeeSalaryStructure> salaries, int employeeId, DateOnly periodDate)
    {
        var periodEnd = PeriodEnd(periodDate);
        return salaries
            .Where(s => s.EmployeeId == employeeId && s.IsActive && s.EffectiveDate <= periodEnd)
            .OrderByDescending(s => s.EffectiveDate)
            .FirstOrDefault();
    }

    /// <summary>The last day of <paramref name="d"/>'s month.</summary>
    public static DateOnly PeriodEnd(DateOnly d) => new DateOnly(d.Year, d.Month, 1).AddMonths(1).AddDays(-1);

    /// <summary>
    /// Computes the employee's GOSI with the payslip engine (when there is a wage to compute on) and
    /// judges readiness from that result. Every GOSI readiness surface calls this.
    /// </summary>
    public static async Task<(GosiReadinessReport Readiness, GosiContributionResult? Calculation)> AssessAsync(
        IStatutoryRuleReader rules, Employee employee, EmployeeSalaryStructure? salary, DateOnly periodDate, CancellationToken ct)
    {
        GosiContributionResult? calc = null;
        if (salary is { BasicSalary: > 0m })
            calc = await GosiCalculationService.CalculateAsync(
                rules, employee.Nationality, salary.BasicSalary, salary.HousingAllowance,
                periodDate, employee.GosiFirstRegisteredOn, ct);
        return (Validate(employee, salary?.BasicSalary, calc), calc);
    }

    /// <summary>
    /// Judges readiness from the payslip engine's result for this employee (null when there is no wage
    /// to compute on).
    /// </summary>
    public static GosiReadinessReport Validate(
        Employee                employee,
        decimal?                basicSalary,
        GosiContributionResult? computed)
    {
        var blocking = new List<GosiReadinessIssue>();
        var warnings = new List<GosiReadinessIssue>();

        if (string.IsNullOrWhiteSpace(employee.GosiReference))
            blocking.Add(new GosiReadinessIssue(
                Code:        "MISSING_GOSI_REFERENCE",
                Message:     "Employee does not have a GOSI reference number. Register the employee with GOSI before processing deductions.",
                IsBlocking:  true));

        if (basicSalary is null || basicSalary <= 0)
            blocking.Add(new GosiReadinessIssue(
                Code:        "MISSING_BASIC_SALARY",
                Message:     "Employee has no basic salary. GOSI contribution cannot be calculated without a contributory wage.",
                IsBlocking:  true));

        var classification = GosiCalculationService.DeriveClassification(employee.Nationality);

        if (string.IsNullOrWhiteSpace(employee.Nationality))
            warnings.Add(new GosiReadinessIssue(
                Code:        "MISSING_NATIONALITY",
                Message:     "Employee nationality is not set. Defaulting to NonSaudi classification — no Annuities or SANED deductions will be applied.",
                IsBlocking:  false));

        if (computed is not null && classification == GosiClassifications.Saudi)
        {
            if (computed.Cohort == GosiCohorts.NewEntrant)
                blocking.Add(new GosiReadinessIssue(
                    Code:       PayrollValidationEngine.GosiNewEntrantScheduleNotModelled,
                    Message:    "First registered with GOSI on or after 3 July 2024, so this employee is a NEW ENTRANT on the " +
                                "separate new-entrant schedule, which this product does not model yet. Payroll blocks this " +
                                "employee; exclude them from the run and calculate their GOSI outside the system, or correct " +
                                "the first-registration date if it is wrong.",
                    IsBlocking: true));
            else if (computed.Cohort != GosiCohorts.PreJuly2024)
                warnings.Add(new GosiReadinessIssue(
                    Code:       PayrollValidationEngine.GosiCohortNotRecorded,
                    Message:    "GOSI cohort not recorded: there is no GOSI first-registration date, so the pre-3-July-2024 " +
                                "schedule is assumed. Record the date from the employee's GOSI record.",
                    IsBlocking: false));
        }

        if (classification == GosiClassifications.GCC)
        {
            var home = GosiCalculationService.DeriveGccHomeState(employee.Nationality) ?? "GCC";
            if (computed is not null && computed.EmployeeTotal <= 0m)
                blocking.Add(new GosiReadinessIssue(
                    Code:       PayrollValidationEngine.GosiGccSchemeNotConfigured,
                    Message:    $"{home} national: insured under {home}'s own scheme, collected by GOSI. {home} rates are not " +
                                $"configured ('gosi.gcc.{home}.employee_rate' / 'gosi.gcc.{home}.employer_rate'), so payroll " +
                                "computes no contribution and blocks this employee.",
                    IsBlocking: true));
            else
                warnings.Add(new GosiReadinessIssue(
                    Code:       "GCC_RULES_PENDING_CONFIRMATION",
                    Message:    $"{home} national: contributions follow {home}'s home-state scheme under the GCC extension. " +
                                "Confirm the configured rates against the current circular before filing.",
                    IsBlocking: false));
        }

        return new GosiReadinessReport(
            EmployeeId:     employee.Id,
            EmployeeCode:   employee.EmployeeCode,
            Classification: classification,
            IsReady:        blocking.Count == 0,
            BlockingIssues: blocking,
            Warnings:       warnings)
        {
            Cohort = computed?.Cohort,
            Basis  = computed?.Basis,
        };
    }
}

// ── Result types ─────────────────────────────────────────────────────────────

public record GosiReadinessReport(
    int                          EmployeeId,
    string                       EmployeeCode,
    string                       Classification,
    bool                         IsReady,
    IReadOnlyList<GosiReadinessIssue> BlockingIssues,
    IReadOnlyList<GosiReadinessIssue> Warnings
)
{
    public int BlockingCount => BlockingIssues.Count;
    public int WarningCount  => Warnings.Count;

    /// <summary>The GOSI cohort the payslip engine computed on (null when nothing was computed).</summary>
    public string? Cohort { get; init; }

    /// <summary>The payslip engine's plain-language basis for the figure (cohort, rates, period).</summary>
    public string? Basis { get; init; }
}

public record GosiReadinessIssue(string Code, string Message, bool IsBlocking);
