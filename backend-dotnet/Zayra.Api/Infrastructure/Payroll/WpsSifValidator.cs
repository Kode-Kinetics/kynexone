using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Validates a payroll run for WPS/SIF export eligibility.
///
/// Returns <see cref="WpsValidationResult"/> with two categories:
///   <c>BlockingErrors</c> — must all be resolved before export is allowed.
///   <c>Warnings</c>       — informational; do not block export.
///
/// The same rules are enforced server-side inside the export endpoint so
/// the validator is never the sole enforcement point.
/// </summary>
public static class WpsSifValidator
{
    private static readonly IReadOnlySet<string> ExportableRunStatuses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Approved", "Locked", "Paid",
        };

    // A wage file must never carry a non-employed person. Draft/Suspended are included: a Draft was
    // never activated (never a payable relationship) and a Suspended employee's pay is legally on hold —
    // both previously slipped through entirely. Being in this set is now a BLOCKING error, not a warning.
    private static readonly IReadOnlySet<string> InactiveEmployeeStatuses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Archived", "Offboarded", "Terminated", "Draft", "Suspended", "Invited", "Inactive", "Exited",
        };

    /// <summary>
    /// Validates the payroll run + its slips + employee profiles + employee master data
    /// and returns a full validation result.
    ///
    /// <paramref name="payBlockedEmployeeIds"/> is the readiness pay-block set — computed by the CALLER
    /// via the ONE readiness evaluator (missing GOSI/social-insurance ref, expired Iqama/visa/EID/QID/CivilId
    /// at pay date, missing MolId/routing where required, State==Blocked). The validator stays PURE: it
    /// simply turns membership into a blocking error so activation and pay read the same evaluator.
    /// </summary>
    public static WpsValidationResult Validate(
        PayrollRun                       run,
        IReadOnlyList<PayrollSlip>       slips,
        IReadOnlyList<EmployeePayrollProfile> profiles,
        IReadOnlyList<Employee>          employees,
        IReadOnlySet<int>?               payBlockedEmployeeIds = null)
    {
        var errors   = new List<WpsValidationIssue>();
        var warnings = new List<WpsValidationIssue>();

        // ── Run-level checks ─────────────────────────────────────────────────

        if (!ExportableRunStatuses.Contains(run.Status))
            errors.Add(RunError("RUN_NOT_APPROVED",
                $"Payroll run status is '{run.Status}'. Run must be Approved (or Locked/Paid) before WPS export."));

        if (slips.Count == 0)
            errors.Add(RunError("NO_PAYROLL_ROWS",
                "No payroll slips found for this run. Process the run before exporting."));

        // Total mismatch: run header vs sum of slips
        if (slips.Count > 0)
        {
            var slipTotal = slips.Sum(s => s.NetSalary);
            if (run.TotalNetSalary != 0 && Math.Abs(run.TotalNetSalary - slipTotal) > 0.01m)
                errors.Add(RunError("TOTAL_MISMATCH",
                    $"Run total net salary ({run.TotalNetSalary:F2}) does not match the sum of payslips ({slipTotal:F2}). Re-process the run to recalculate totals."));
        }

        // ── Employee-level checks ────────────────────────────────────────────

        var seenEmployees = new HashSet<int>();

        foreach (var slip in slips)
        {
            var empId   = slip.EmployeeId;
            var empCode = slip.EmployeeCode;

            // Duplicate employee in the same run
            if (!seenEmployees.Add(empId))
            {
                errors.Add(EmpError("DUPLICATE_EMPLOYEE", empId, empCode,
                    $"Employee {empCode} appears more than once in this payroll run."));
                continue; // skip further checks for the duplicate row
            }

            // Salary amount checks
            if (slip.NetSalary <= 0)
                errors.Add(EmpError("INVALID_NET_SALARY", empId, empCode,
                    $"Employee {empCode} has a non-positive net salary ({slip.NetSalary:F2}). Correct the payroll data before exporting."));

            if (slip.GrossSalary < 0)
                errors.Add(EmpError("NEGATIVE_GROSS_SALARY", empId, empCode,
                    $"Employee {empCode} has a negative gross salary ({slip.GrossSalary:F2})."));

            // IBAN checks
            var profile = profiles.FirstOrDefault(p => p.EmployeeId == empId);
            var iban    = profile?.Iban;

            if (string.IsNullOrWhiteSpace(iban))
                errors.Add(EmpError("MISSING_IBAN", empId, empCode,
                    $"Employee {empCode} has no IBAN on their payroll profile. Add bank details before exporting."));
            else if (!IbanValidator.IsValid(iban))
                errors.Add(EmpError("INVALID_IBAN", empId, empCode,
                    $"Employee {empCode} has an invalid IBAN (fails ISO 13616 mod-97 check)."));
            else if (!IbanValidator.IsSaudiIban(iban))
                warnings.Add(EmpWarning("NON_SAUDI_IBAN", empId, empCode,
                    $"Employee {empCode} IBAN does not start with 'SA'. Confirm the bank account is in Saudi Arabia."));

            // WpsEligible is now LOAD-BEARING (honours the exit cascade's WpsEligible=false): an
            // ineligible profile can never reach a wage file, independent of any other check.
            if (profile is not null && !profile.WpsEligible)
                errors.Add(EmpError("WPS_INELIGIBLE", empId, empCode,
                    $"Employee {empCode} is marked WPS-ineligible (e.g. separated / exit cascade). They cannot be included in a WPS export."));

            // Readiness pay-block: the ONE evaluator's verdict (missing/expired statutory details, State==Blocked).
            if (payBlockedEmployeeIds is not null && payBlockedEmployeeIds.Contains(empId))
                errors.Add(EmpError("READINESS_PAY_BLOCKED", empId, empCode,
                    $"Employee {empCode} is not payroll-ready — required statutory details are missing or expired. Complete their readiness checklist before exporting."));

            // Identity/master-data checks
            var emp = employees.FirstOrDefault(e => e.Id == empId);
            if (emp is null)
            {
                warnings.Add(EmpWarning("EMPLOYEE_NOT_IN_MASTER", empId, empCode,
                    $"Employee {empCode} payslip exists but no master employee record was found."));
            }
            else
            {
                if (string.IsNullOrWhiteSpace(emp.IdNumber))
                    errors.Add(EmpError("MISSING_ID_NUMBER", empId, empCode,
                        $"Employee {empCode} is missing a government ID number required for WPS regulatory reporting."));

                // Non-employed status is now a BLOCKING error (was a warning): a Draft/Suspended/terminal
                // employee must never be swept into a wage file.
                if (InactiveEmployeeStatuses.Contains(emp.Status))
                    errors.Add(EmpError("INACTIVE_EMPLOYEE", empId, empCode,
                        $"Employee {empCode} has status '{emp.Status}' and cannot be included in a WPS export."));
            }
        }

        return new WpsValidationResult(errors, warnings);
    }

    // ── Issue factory helpers ─────────────────────────────────────────────────

    private static WpsValidationIssue RunError(string code, string message) =>
        new(code, message, null, null, WpsValidationScope.Run, true);

    private static WpsValidationIssue EmpError(string code, int empId, string empCode, string message) =>
        new(code, message, empId, empCode, WpsValidationScope.Employee, true);

    private static WpsValidationIssue EmpWarning(string code, int empId, string empCode, string message) =>
        new(code, message, empId, empCode, WpsValidationScope.Employee, false);
}

// ── Result types ─────────────────────────────────────────────────────────────

/// <summary>Scope of a validation issue.</summary>
public enum WpsValidationScope { Run, Employee, Bank }

/// <summary>A single validation issue from <see cref="WpsSifValidator"/>.</summary>
public record WpsValidationIssue(
    string             Code,
    string             Message,
    int?               EmployeeId,
    string?            EmployeeCode,
    WpsValidationScope Scope,
    bool               IsBlocking
);

/// <summary>
/// Full result from <see cref="WpsSifValidator.Validate"/>.
/// <c>CanExport</c> is <c>true</c> only when there are no blocking errors.
/// </summary>
public record WpsValidationResult(
    IReadOnlyList<WpsValidationIssue> BlockingErrors,
    IReadOnlyList<WpsValidationIssue> Warnings
)
{
    public bool CanExport     => BlockingErrors.Count == 0;
    public int  IssueCount    => BlockingErrors.Count + Warnings.Count;
    public int  WarningCount  => Warnings.Count;
    public int  ErrorCount    => BlockingErrors.Count;
}
