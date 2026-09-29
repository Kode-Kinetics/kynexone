using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// What still stands between the period's active employees and being paid, BEFORE a run exists.
///
/// <para><b>Why this exists.</b> Payroll readiness used to measure salary coverage only, so a tenant
/// whose 250 active employees all had a salary read as ready — while the first validation of the run
/// then raised 250 missing-IBAN errors that block approval, plus missing payroll-profile, MOL-ID and
/// attendance warnings. Those were knowable up front and per employee; this evaluator names them,
/// with the next action for each, in the same terms the run validation will later use.</para>
///
/// <para><b>Blocking vs recommended.</b> "Blocking" mirrors what the run validation raises as an
/// Error for a regular run (it stops approval) or what the bank file refuses. "Recommended" mirrors
/// its Warnings: payroll can proceed, but the output is weaker or needs a human check. The two are
/// never merged, so a warning cannot make a payroll look blocked and an error cannot hide among
/// suggestions.</para>
///
/// <para><b>One root cause, one item.</b> An employee with no payroll profile at all is reported once
/// (<see cref="MissingPayrollProfile"/>). When the run is validated that same gap surfaces as
/// MISSING_IBAN (Error), MISSING_PAYROLL_PROFILE (Warning) and, for KSA, MISSING_MOL_ID (Warning);
/// listing all three here would triple-count one missing record.</para>
/// </summary>
public static class PayrollPaymentPrerequisites
{
    // ── Blocking (mirror validation Errors / bank-file refusals) ─────────────────────────────────
    public const string MissingSalary = "MISSING_SALARY_STRUCTURE";
    public const string MissingPayrollProfile = "MISSING_PAYROLL_PROFILE";
    public const string MissingIban = "MISSING_IBAN";
    public const string InvalidIban = "INVALID_IBAN";
    public const string ReadinessPayBlocked = "READINESS_PAY_BLOCKED";
    public const string CompanyCountryMissing = "COUNTRY_CODE_MISSING";
    public const string CompanyCurrencyMissing = "COMPANY_CURRENCY_MISSING";
    public const string CompanyNotResolved = "COMPANY_NOT_RESOLVED";

    // ── Recommended (mirror validation Warnings) ─────────────────────────────────────────────────
    public const string MissingMolId = "MISSING_MOL_ID";
    public const string NonSaudiIban = "NON_SAUDI_IBAN";
    public const string MissingNationality = "MISSING_NATIONALITY";
    public const string NoAttendance = "WARN_NO_ATTENDANCE";
    public const string WpsEmployerIdMissing = "WPS_EMPLOYER_ID_MISSING";

    public const int DefaultEmployeeListLimit = 500;

    private static readonly IReadOnlyDictionary<string, (string Label, string NextAction)> Copy =
        new Dictionary<string, (string, string)>
        {
            [MissingSalary] = ("No salary assigned for this period",
                "Assign a salary in Payroll → Employee Salary"),
            [MissingPayrollProfile] = ("No payroll profile — no bank IBAN or MOL ID on file",
                "Open the employee and add their payroll and bank details"),
            [MissingIban] = ("No bank IBAN on the payroll profile",
                "Open the employee and add their IBAN"),
            [InvalidIban] = ("IBAN fails the bank format check",
                "Open the employee and correct their IBAN"),
            [ReadinessPayBlocked] = ("Required statutory details are missing or expired",
                "Open the employee and complete their readiness checklist"),
            [MissingMolId] = ("No MOL ID for WPS (Mudad) reporting",
                "Open the employee and add their MOL ID"),
            [NonSaudiIban] = ("IBAN is not a Saudi account",
                "Confirm the employee's bank account is held in Saudi Arabia"),
            [MissingNationality] = ("Nationality not recorded (it decides GOSI treatment)",
                "Open the employee and record their nationality"),
            [NoAttendance] = ("No attendance recorded in this period (full salary would be assumed)",
                "Process or import attendance for this period"),
            [CompanyCountryMissing] = ("The company has no country, so statutory deductions cannot be worked out",
                "Set the country in Setup → Companies"),
            [CompanyCurrencyMissing] = ("The company has no currency, so the run cannot be posted or locked",
                "Set the default currency in Setup → Companies"),
            [CompanyNotResolved] = ("Employees are not assigned to an active legal entity",
                "Assign each employee to an active company"),
            [WpsEmployerIdMissing] = ("No WPS employer (MOL establishment) ID on the company",
                "Add the WPS employer ID in Setup → Companies"),
        };

    public static (string Label, string NextAction) Describe(string code) =>
        Copy.TryGetValue(code, out var copy) ? copy : (code, "Review in payroll validation");

    public sealed record Input(
        IReadOnlyList<Employee> ActiveEmployees,
        IReadOnlyList<Company> Companies,
        IReadOnlySet<int> EmployeesWithSalary,
        IReadOnlyList<EmployeePayrollProfile> Profiles,
        IReadOnlySet<int> ReadinessPayBlocked,
        // Null when the period has not started yet — absence of attendance is then meaningless.
        IReadOnlySet<int>? EmployeesWithAttendance,
        int EmployeeListLimit = DefaultEmployeeListLimit);

    public static PayrollPaymentPrerequisitesDto Evaluate(Input input)
    {
        var companiesById = input.Companies.ToDictionary(c => c.Id);
        // A legacy employee with no company belongs to the tenant's only company, as Process treats it.
        var soleCompany = input.Companies.Count == 1 ? input.Companies[0] : null;
        var profileByEmployee = input.Profiles
            .GroupBy(p => p.EmployeeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.UpdatedAtUtc ?? p.CreatedAtUtc).First());

        var employees = new List<PayrollPrerequisiteEmployee>();
        var usedCompanyIds = new HashSet<Guid>();
        var unassignedEmployees = 0;

        foreach (var emp in input.ActiveEmployees)
        {
            var blocking = new List<string>();
            var recommended = new List<string>();

            Company? company = null;
            if (emp.CompanyId is Guid cid) companiesById.TryGetValue(cid, out company);
            company ??= emp.CompanyId is null ? soleCompany : null;
            if (company is not null) usedCompanyIds.Add(company.Id);
            else unassignedEmployees++;
            var isKsa = IsSaudi(company?.CountryCode);

            if (!input.EmployeesWithSalary.Contains(emp.Id)) blocking.Add(MissingSalary);

            if (!profileByEmployee.TryGetValue(emp.Id, out var profile))
            {
                blocking.Add(MissingPayrollProfile);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(profile.Iban)) blocking.Add(MissingIban);
                else if (!IbanValidator.IsValid(profile.Iban)) blocking.Add(InvalidIban);
                else if (isKsa && !IbanValidator.IsSaudiIban(profile.Iban)) recommended.Add(NonSaudiIban);

                if (isKsa && string.IsNullOrWhiteSpace(profile.MolId)) recommended.Add(MissingMolId);
            }

            if (input.ReadinessPayBlocked.Contains(emp.Id)) blocking.Add(ReadinessPayBlocked);
            if (string.IsNullOrWhiteSpace(emp.Nationality)) recommended.Add(MissingNationality);
            if (input.EmployeesWithAttendance is { } attended && !attended.Contains(emp.Id)) recommended.Add(NoAttendance);

            if (blocking.Count > 0 || recommended.Count > 0)
                employees.Add(new PayrollPrerequisiteEmployee(
                    emp.Id, emp.EmployeeCode, string.IsNullOrWhiteSpace(emp.FullName) ? emp.EnglishName : emp.FullName,
                    company?.Id, blocking, recommended));
        }

        var companyBlocking = new List<PayrollCompanyPrerequisite>();
        var companyRecommended = new List<PayrollCompanyPrerequisite>();
        foreach (var company in input.Companies.Where(c => usedCompanyIds.Contains(c.Id)).OrderBy(c => c.LegalNameEn))
        {
            var name = string.IsNullOrWhiteSpace(company.TradeName) ? company.LegalNameEn : company.TradeName;
            if (string.IsNullOrWhiteSpace(company.CountryCode))
                companyBlocking.Add(CompanyItem(company.Id, name, CompanyCountryMissing));
            if (string.IsNullOrWhiteSpace(company.DefaultCurrency))
                companyBlocking.Add(CompanyItem(company.Id, name, CompanyCurrencyMissing));
            if (IsSaudi(company.CountryCode) && string.IsNullOrWhiteSpace(company.WpsEmployerId))
                companyRecommended.Add(CompanyItem(company.Id, name, WpsEmployerIdMissing));
        }
        if (unassignedEmployees > 0)
            companyBlocking.Add(new PayrollCompanyPrerequisite(null, null, CompanyNotResolved,
                $"{unassignedEmployees} active employee(s) are not assigned to an active legal entity, so no run will pay them",
                Describe(CompanyNotResolved).NextAction));

        var ordered = employees
            .OrderByDescending(e => e.Blocking.Count > 0)
            .ThenBy(e => e.EmployeeCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var limit = Math.Max(0, input.EmployeeListLimit);

        return new PayrollPaymentPrerequisitesDto(
            EvaluatedEmployees: input.ActiveEmployees.Count,
            BlockedEmployees: employees.Count(e => e.Blocking.Count > 0),
            EmployeesWithRecommendations: employees.Count(e => e.Recommended.Count > 0),
            AttendanceChecked: input.EmployeesWithAttendance is not null,
            CompanyBlocking: companyBlocking,
            CompanyRecommended: companyRecommended,
            Blocking: Tally(employees.SelectMany(e => e.Blocking)),
            Recommended: Tally(employees.SelectMany(e => e.Recommended)),
            Employees: ordered.Take(limit).ToList(),
            EmployeesTruncated: ordered.Count > limit);
    }

    private static PayrollCompanyPrerequisite CompanyItem(Guid companyId, string companyName, string code)
    {
        var (label, next) = Describe(code);
        return new PayrollCompanyPrerequisite(companyId, companyName, code, label, next);
    }

    private static IReadOnlyList<PayrollPrerequisiteCount> Tally(IEnumerable<string> codes) =>
        codes.GroupBy(c => c)
            .Select(g => { var (label, next) = Describe(g.Key); return new PayrollPrerequisiteCount(g.Key, label, next, g.Count()); })
            .OrderByDescending(x => x.Count).ThenBy(x => x.Code, StringComparer.Ordinal)
            .ToList();

    // Accept ISO-2 and ISO-3, as the validation engine does.
    private static bool IsSaudi(string? countryCode) =>
        string.Equals(countryCode, "SA", StringComparison.OrdinalIgnoreCase)
        || string.Equals(countryCode, "SAU", StringComparison.OrdinalIgnoreCase);
}

public sealed record PayrollPrerequisiteEmployee(
    int EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    Guid? CompanyId,
    IReadOnlyList<string> Blocking,
    IReadOnlyList<string> Recommended);

public sealed record PayrollPrerequisiteCount(string Code, string Label, string NextAction, int Count);

public sealed record PayrollCompanyPrerequisite(Guid? CompanyId, string? CompanyName, string Code, string Label, string NextAction);

public sealed record PayrollPaymentPrerequisitesDto(
    int EvaluatedEmployees,
    int BlockedEmployees,
    int EmployeesWithRecommendations,
    bool AttendanceChecked,
    IReadOnlyList<PayrollCompanyPrerequisite> CompanyBlocking,
    IReadOnlyList<PayrollCompanyPrerequisite> CompanyRecommended,
    IReadOnlyList<PayrollPrerequisiteCount> Blocking,
    IReadOnlyList<PayrollPrerequisiteCount> Recommended,
    IReadOnlyList<PayrollPrerequisiteEmployee> Employees,
    bool EmployeesTruncated);
