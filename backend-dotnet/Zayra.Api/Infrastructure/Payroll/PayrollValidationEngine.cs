using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Centralised payroll validation engine. Runs all 9 compliance rules against a
/// processed payroll run and returns <see cref="PayrollValidationResult"/> rows.
///
/// Severity = "Error"   → blocks Approve and Lock (enforced server-side).
/// Severity = "Warning" → informational; workflow may proceed with acknowledgment.
///
/// Rules:
///   1  Missing salary structure OR missing payroll profile for any active employee
///   2  GOSI: Saudi/GCC employee must have non-zero GOSI employee deductions;
///            expat must have zero; 45,000 SAR covered-wage ceiling flagged
///   3  Net salary not negative; not zero when gross > 0
///   4  Duplicate employee entry in run
///   5  WPS readiness: IBAN present + valid Saudi format (SA + 22 alphanumeric);
///            MOL ID present on payroll profile for KSA runs
///   6  Nationality present on employee record (drives GOSI branch)
///   7  Run-level totals reconcile: Σ(gross), Σ(deductions), Σ(net) match header
///   8  GL pre-check: TotalGross = TotalDeductions + TotalNet (journal will balance)
///   9  Salary currency matches company default currency
/// </summary>
public static class PayrollValidationEngine
{
    private const decimal GosiCoveredWageCeiling = 45_000m;
    private const int GosiRateStalenessThresholdMonths = 18;

    public static List<PayrollValidationResult> Run(PayrollValidationContext ctx)
    {
        var results = new List<PayrollValidationResult>();
        var tid = ctx.Run.TenantId;
        var rid = ctx.Run.Id;
        var now = DateTime.UtcNow;

        void Err(string code, string message, int? empId = null) =>
            results.Add(new PayrollValidationResult
            {
                TenantId = tid, PayrollRunId = rid, EmployeeId = empId,
                Severity = "Error", Code = code, Message = message, CreatedAtUtc = now,
            });

        void Warn(string code, string message, int? empId = null) =>
            results.Add(new PayrollValidationResult
            {
                TenantId = tid, PayrollRunId = rid, EmployeeId = empId,
                Severity = "Warning", Code = code, Message = message, CreatedAtUtc = now,
            });

        // ── Run-level company/pack guards (must precede per-slip rules) ───────────
        // These mirror the fail-loud abort in Process(); a second pass here catches
        // edge cases where the run was created before the guard was added.
        if (ctx.Company is null)
            Err("COMPANY_NOT_RESOLVED",
                "No active company is linked to this payroll run. " +
                "The statutory deduction pack cannot be resolved without a company. " +
                "Reprocess the run after linking an active company with a CountryCode.");
        else if (string.IsNullOrWhiteSpace(ctx.Company.CountryCode))
            Err("COUNTRY_CODE_MISSING",
                $"Company '{ctx.Company.LegalNameEn}' (id: {ctx.Company.Id}) has no CountryCode. " +
                "Set the company country in Setup → Companies then reprocess.");

        // Accept both ISO 3166-1 alpha-2 ("SA") and alpha-3 ("SAU") for KSA — data may use either.
        var isKsa = string.Equals(ctx.Company?.CountryCode, "SAU", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(ctx.Company?.CountryCode, "SA",  StringComparison.OrdinalIgnoreCase);
        var companyCurrency = ctx.Company?.DefaultCurrency ?? "SAR";

        // ── WARN_GOSI_RATES_REQUIRE_SIGNOFF ─────────────────────────────────
        // Fire at the start of every validation for KSA runs when the GOSI rate
        // effective date is older than the staleness threshold.
        if (isKsa && ctx.GosiRatesEffectiveFrom.HasValue)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var ageMonths = (today.Year - ctx.GosiRatesEffectiveFrom.Value.Year) * 12
                          + (today.Month - ctx.GosiRatesEffectiveFrom.Value.Month);
            if (ageMonths >= GosiRateStalenessThresholdMonths)
                Warn("WARN_GOSI_RATES_REQUIRE_SIGNOFF",
                    $"GOSI contribution rates in use (effective from {ctx.GosiRatesEffectiveFrom.Value:yyyy-MM-dd}) have not been confirmed against " +
                    $"current GOSI circulars. Ensure rates are reviewed by a Saudi compliance officer before " +
                    $"locking this payroll run.");
        }

        // ── Indexes ───────────────────────────────────────────────────────────
        var salaryByEmp  = ctx.SalaryAssignments.GroupBy(x => x.EmployeeId).ToDictionary(g => g.Key);
        var profileByEmp = ctx.Profiles.ToDictionary(p => p.EmployeeId);
        var empById      = ctx.ActiveEmployees.ToDictionary(e => e.Id);

        // Fill in employees that appear in slips but may be inactive/deleted (edge-case guard).
        foreach (var s in ctx.Slips)
        {
            empById.TryAdd(s.EmployeeId,
                new Employee { Id = s.EmployeeId, EmployeeCode = s.EmployeeCode, FullName = s.EmployeeName ?? string.Empty });
        }

        // ── Rule 1: Missing salary structure / payroll profile ────────────────
        foreach (var emp in ctx.ActiveEmployees)
        {
            if (!salaryByEmp.ContainsKey(emp.Id))
                Err("MISSING_SALARY_STRUCTURE",
                    $"Employee {emp.EmployeeCode} ({emp.FullName}) has no active salary structure. " +
                    "All active employees in the run must have a salary assignment before payroll is processed.",
                    emp.Id);

            if (!profileByEmp.ContainsKey(emp.Id))
                Warn("MISSING_PAYROLL_PROFILE",
                    $"Employee {emp.EmployeeCode} ({emp.FullName}) has no payroll profile. " +
                    "Bank details and payment settings are required for disbursement.",
                    emp.Id);
        }

        // ── Rule 4: Duplicate employee in run ────────────────────────────────
        var seen = new HashSet<int>();
        foreach (var slip in ctx.Slips)
        {
            if (!seen.Add(slip.EmployeeId))
                Err("DUPLICATE_EMPLOYEE",
                    $"Employee {slip.EmployeeCode} appears more than once in this payroll run. " +
                    "Each employee must have exactly one payslip per run.",
                    slip.EmployeeId);
        }

        // ── GOSI EE deduction indexes (for Rule 2) ───────────────────────────
        // Employee-side GOSI codes end with "-EE" (e.g. GOSI-ANN-EE, GOSI-SANED-EE).
        var gosiEeByEmp = ctx.Deductions
            .Where(d => d.Source == "Statutory" && IsGosiEeCode(d.ComponentCode))
            .GroupBy(d => d.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.Amount));

        // ── Per-slip rules ────────────────────────────────────────────────────
        foreach (var slip in ctx.Slips.GroupBy(s => s.EmployeeId).Select(g => g.First()))
        {
            empById.TryGetValue(slip.EmployeeId, out var emp);
            profileByEmp.TryGetValue(slip.EmployeeId, out var profile);

            // Rule 3: net not negative; not zero when gross > 0
            if (slip.NetSalary < 0m)
                Err("NEGATIVE_NET",
                    $"Employee {slip.EmployeeCode} net salary is negative ({slip.NetSalary:N2}). " +
                    "Deductions exceed gross pay; deduction amounts must be reviewed.",
                    slip.EmployeeId);

            if (slip.NetSalary == 0m && slip.GrossSalary > 0m)
                Err("ZERO_NET_WITH_GROSS",
                    $"Employee {slip.EmployeeCode} net salary is zero but gross is {slip.GrossSalary:N2}. " +
                    "This usually indicates an over-deduction; verify deduction amounts.",
                    slip.EmployeeId);

            // Rule 6: nationality present
            if (emp is not null && string.IsNullOrWhiteSpace(emp.Nationality))
                Warn("MISSING_NATIONALITY",
                    $"Employee {slip.EmployeeCode} has no nationality recorded. " +
                    "Nationality drives GOSI classification (Saudi vs GCC vs expat) and must be set before processing.",
                    slip.EmployeeId);

            // Rule 2: GOSI rate check — KSA runs only
            if (isKsa && emp is not null)
            {
                var classification = GosiCalculationService.DeriveClassification(emp.Nationality);
                var gosiEeAmount   = gosiEeByEmp.TryGetValue(slip.EmployeeId, out var g) ? g : 0m;
                var hasGosiEe      = gosiEeAmount > 0m;

                if (classification is "Saudi" or "GCC")
                {
                    if (!hasGosiEe)
                        Err("GOSI_MISSING_FOR_SAUDI",
                            $"Employee {slip.EmployeeCode} is classified as {classification} but has zero GOSI employee deductions. " +
                            "Saudi and GCC nationals must contribute to GOSI Annuities (GOSI-ANN-EE) and SANED (GOSI-SANED-EE).",
                            slip.EmployeeId);

                    // 45 k ceiling warning
                    var coveredWage = slip.BasicSalary + slip.HousingAllowance;
                    if (coveredWage > GosiCoveredWageCeiling)
                        Warn("GOSI_CEILING_EXCEEDED",
                            $"Employee {slip.EmployeeCode} covered wage (Basic + Housing = {coveredWage:N2} SAR) exceeds the GOSI 45,000 SAR ceiling. " +
                            "Verify that contributions were calculated on 45,000 SAR, not {coveredWage:N2} SAR.",
                            slip.EmployeeId);
                }
                else  // NonSaudi / expat
                {
                    if (hasGosiEe)
                        Err("GOSI_APPLIED_TO_EXPAT",
                            $"Employee {slip.EmployeeCode} is classified as {classification} (expat) but has GOSI employee deductions of {gosiEeAmount:N2} SAR. " +
                            "Expatriate employees must not contribute to GOSI Annuities or SANED.",
                            slip.EmployeeId);
                }
            }

            // Rule 5a: IBAN present + valid Saudi format
            var iban = profile?.Iban ?? string.Empty;
            if (string.IsNullOrWhiteSpace(iban))
                Err("MISSING_IBAN",
                    $"Employee {slip.EmployeeCode} has no IBAN on their payroll profile. " +
                    "Bank details are required for WPS payment disbursement.",
                    slip.EmployeeId);
            else if (!IbanValidator.IsValid(iban))
                Err("INVALID_IBAN",
                    $"Employee {slip.EmployeeCode} IBAN '{iban}' fails ISO 13616 mod-97 validation. " +
                    "Correct the IBAN before approving this run.",
                    slip.EmployeeId);
            else if (isKsa && !IbanValidator.IsSaudiIban(iban))
                Warn("NON_SAUDI_IBAN",
                    $"Employee {slip.EmployeeCode} IBAN does not start with 'SA'. " +
                    "For a Saudi payroll run, confirm the bank account is held in Saudi Arabia.",
                    slip.EmployeeId);

            // Rule 5b: MOL ID required for KSA regulatory reporting
            if (isKsa)
            {
                var molId = profile?.MolId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(molId))
                    Warn("MISSING_MOL_ID",
                        $"Employee {slip.EmployeeCode} has no MOL ID on their payroll profile. " +
                        "MOL ID is required for WPS (Mudad) regulatory reporting in Saudi Arabia.",
                        slip.EmployeeId);
            }

            // Rule 9: salary currency must match company default
            var empCurrency = profile?.SalaryCurrency ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(empCurrency) &&
                !string.Equals(empCurrency, companyCurrency, StringComparison.OrdinalIgnoreCase))
                Warn("CURRENCY_MISMATCH",
                    $"Employee {slip.EmployeeCode} salary currency '{empCurrency}' differs from company default '{companyCurrency}'. " +
                    "All payslips in a run should use the company's reporting currency.",
                    slip.EmployeeId);
        }

        // ── Rule 7: Run-level totals reconcile ───────────────────────────────
        if (ctx.Slips.Count > 0)
        {
            var sumGross = ctx.Slips.Sum(s => s.GrossSalary);
            var sumDed   = ctx.Slips.Sum(s => s.Deductions);
            var sumNet   = ctx.Slips.Sum(s => s.NetSalary);

            if (Math.Abs(ctx.Run.TotalGrossSalary - sumGross) > 0.01m)
                Err("TOTALS_GROSS_MISMATCH",
                    $"Run header gross ({ctx.Run.TotalGrossSalary:N2}) does not match sum of payslip gross ({sumGross:N2}). " +
                    "Re-process the run to recalculate totals.");

            if (Math.Abs(ctx.Run.TotalDeductions - sumDed) > 0.01m)
                Err("TOTALS_DEDUCTIONS_MISMATCH",
                    $"Run header deductions ({ctx.Run.TotalDeductions:N2}) does not match sum of payslip deductions ({sumDed:N2}). " +
                    "Re-process the run to recalculate totals.");

            if (Math.Abs(ctx.Run.TotalNetSalary - sumNet) > 0.01m)
                Err("TOTALS_NET_MISMATCH",
                    $"Run header net ({ctx.Run.TotalNetSalary:N2}) does not match sum of payslip net ({sumNet:N2}). " +
                    "Re-process the run to recalculate totals.");
        }

        // ── Rule 8: GL pre-check ─────────────────────────────────────────────
        // Accounting equation: Σ(earnings DR) = Σ(employee deductions CR) + net salary payable CR.
        // Employer statutory contributions cancel (DR expense = CR liability).
        // This reduces to: TotalGross = TotalDeductions + TotalNet at the run level.
        if (ctx.Slips.Count > 0)
        {
            var glImbalance = ctx.Run.TotalGrossSalary - (ctx.Run.TotalDeductions + ctx.Run.TotalNetSalary);
            if (Math.Abs(glImbalance) > 0.01m)
                Err("GL_WILL_NOT_BALANCE",
                    $"GL pre-check failed: gross ({ctx.Run.TotalGrossSalary:N2}) ≠ deductions ({ctx.Run.TotalDeductions:N2}) + net ({ctx.Run.TotalNetSalary:N2}). " +
                    $"Difference: {glImbalance:N2}. The journal will not balance on lock; re-process the run.");
        }

        // ── Rule 10: No attendance or OT data processed for active employee ──────
        // WARN only — full salary is still paid; payroll can proceed.
        // Trigger: employee has no AttendanceDailyRecord AND no OT impact in period.
        // Absence of data may mean attendance was never processed (not "perfect attendance").
        foreach (var emp in ctx.ActiveEmployees)
        {
            var hasAttendance = ctx.AttendanceProcessedEmployeeIds.Contains(emp.Id);
            var hasOt         = ctx.OvertimeHoursByEmployee.ContainsKey(emp.Id);
            if (!hasAttendance && !hasOt)
                Warn("WARN_NO_ATTENDANCE",
                    $"No attendance or overtime data was processed for employee {emp.EmployeeCode} ({emp.FullName}) " +
                    $"in {ctx.Run.Year}-{ctx.Run.Month:D2}. Full salary assumed. " +
                    "Verify attendance processing ran before approving this run.",
                    emp.Id);
        }

        // ── Rule 11: OT hours exist but hourly rate cannot be resolved ────────
        // ERROR — blocks approve/lock; prevents silent zero-pay for approved overtime.
        // Trigger: employee has approved OT hours but basic salary is zero (rate = 0).
        foreach (var kvp in ctx.OvertimeHoursByEmployee)
        {
            if (kvp.Value <= 0m) continue;
            var sal   = ctx.SalaryAssignments
                .Where(x => x.EmployeeId == kvp.Key)
                .OrderByDescending(x => x.EffectiveDate)
                .FirstOrDefault();
            var basic = sal?.BasicSalary ?? 0m;
            if (basic <= 0m)
            {
                empById.TryGetValue(kvp.Key, out var emp2);
                Err("OT_RATE_UNRESOLVED",
                    $"Employee {emp2?.EmployeeCode ?? kvp.Key.ToString()} has {kvp.Value:N2} approved " +
                    "overtime hours but basic salary is zero — hourly rate cannot be computed. " +
                    "Set basic salary before approving this run.",
                    kvp.Key);
            }
        }

        return results;
    }

    private static bool IsGosiEeCode(string code) =>
        code.StartsWith("GOSI-", StringComparison.OrdinalIgnoreCase) &&
        code.EndsWith("-EE", StringComparison.OrdinalIgnoreCase);
}

/// <summary>All data required by <see cref="PayrollValidationEngine.Run"/>.</summary>
public sealed record PayrollValidationContext(
    PayrollRun                             Run,
    IReadOnlyList<PayrollSlip>             Slips,
    IReadOnlyList<Employee>                ActiveEmployees,
    IReadOnlyList<EmployeeSalaryStructure> SalaryAssignments,
    IReadOnlyList<EmployeePayrollProfile>  Profiles,
    IReadOnlyList<PayrollDeduction>        Deductions,
    IReadOnlyList<PayrollEarning>          Earnings,
    Company?                               Company)
{
    // Set from Process/Validate to enable Rules 10+11.
    // Default to empty so existing callers that don't supply these are safe.

    /// <summary>Total approved OT hours per employee in this pay period.</summary>
    public IReadOnlyDictionary<int, decimal> OvertimeHoursByEmployee { get; init; } =
        new Dictionary<int, decimal>();

    /// <summary>
    /// Employee IDs that have at least one AttendanceDailyRecord in the pay period.
    /// An employee absent from this set has never had attendance processed.
    /// </summary>
    public IReadOnlySet<int> AttendanceProcessedEmployeeIds { get; init; } =
        new HashSet<int>();

    /// <summary>
    /// The EffectiveFrom date of the most recent system-default GOSI rate rules.
    /// If set and older than 18 months, the engine emits WARN_GOSI_RATES_REQUIRE_SIGNOFF.
    /// Populated by the Process/Validate endpoints from GosiContributionRule data.
    /// </summary>
    public DateOnly? GosiRatesEffectiveFrom { get; init; }
}
