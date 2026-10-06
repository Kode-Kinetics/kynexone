using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Finance;

/// <param name="MaxAvailableAmount">The money ceiling from the policy amount limits and, for a grade-limited
/// type, the grade — the figure this record has always carried.</param>
/// <param name="GradeLimit">The grade check; <c>Applies</c> false for a type that is not grade-limited.</param>
/// <param name="Available">The strictest of every evaluated limit in <paramref name="Limits"/> (including the
/// instalment-percentage rule, expressed as principal); 0 when the grade blocks the type outright.</param>
/// <param name="BindingLimit">The <see cref="LoanLimitKinds"/> code of the limit that sets <paramref name="Available"/>.</param>
public record LoanEligibilityAssessment(bool Eligible, string[] Reasons, string[] Codes, decimal? MaxAvailableAmount,
    Guid? PolicyId, int? PolicyVersion, string PolicySnapshotJson, string EmploymentSnapshotJson,
    decimal MonthlySalary, decimal CommittedAmount, GradeLoanLimitResult? GradeLimit = null,
    decimal? Available = null, string? BindingLimit = null, IReadOnlyList<LoanLimitBreakdown>? Limits = null,
    LoanArt92Check? Art92 = null);

/// <summary>
/// Saudi Labour Law Art. 92: an employer-loan instalment deducted from the wage may be at most 10% of the wage unless the
/// employee consents in writing. Evaluated for every non-preview assessment; ENFORCED (a <c>LoanDeductionConsent</c>
/// document required) only for tenants with Release A on — see <c>LoansController</c>.
/// </summary>
/// <param name="Instalment">The monthly instalment this request implies (amount ÷ instalments, monthly equivalent).</param>
/// <param name="WageDue">The monthly wage the test used (the active salary structure); null when it is not known.</param>
/// <param name="Pct">Instalment ÷ wage, 0–100, two decimals; null when the wage is not known.</param>
/// <param name="RequiresConsent">True when the instalment is deducted from pay and is above 10% of the wage — or the wage is
/// unknown, so the 10% cannot be shown to hold (fail-closed).</param>
/// <param name="DeductedFromPay">False for a loan repaid outside payroll: Art. 92 governs deductions from the wage.</param>
public sealed record LoanArt92Check(decimal Instalment, decimal? WageDue, decimal? Pct, bool RequiresConsent, bool DeductedFromPay)
{
    public const decimal ThresholdPercent = 10m;

    public static LoanArt92Check Evaluate(decimal monthlyInstalment, decimal? wage, bool deductedFromPay)
    {
        var knownWage = wage is > 0m ? wage : null;
        decimal? pct = knownWage is decimal w ? Math.Round(monthlyInstalment / w * 100m, 2) : null;
        var above = knownWage is not decimal ww || monthlyInstalment > ww * ThresholdPercent / 100m;
        return new LoanArt92Check(Math.Round(monthlyInstalment, 2), knownWage, pct, deductedFromPay && above, deductedFromPay);
    }
}

/// <summary>Stable codes this service adds beyond the policy rules (the UI maps codes to Arabic).</summary>
public static class LoanEligibilityCodes
{
    public const string InterestNotPermitted = "InterestNotPermitted";
    public const string InterestNotPermittedText = "Employee loans must be interest-free under Saudi law.";
    public const string TypeNotOffered = "LoanTypeNotOffered";
    public const string TypeNotOfferedText = "Loans of this type aren't offered by your company.";
}

/// <summary>One assessment is used for preview, submission, HR approval and release of funds.
/// Policy terms are frozen; employee facts and existing commitments are always read afresh.</summary>
public sealed class LoanEligibilityService(ZayraDbContext db)
{
    public async Task<LoanEligibilityAssessment> EvaluateAsync(Guid tid, Employee employee, LoanType loanType,
        decimal amount, int installments, string repaymentMethod, Guid? excludeLoanId = null,
        string? policySnapshotJson = null, CancellationToken ct = default, bool preview = false)
    {
        // Preview: no amount chosen yet. Report the limits for this type without the rules that judge an
        // amount (or an instalment count the employee has not picked). Never used to create or approve.
        var reasons = new List<string>();
        var codes = new List<string>();
        void Refuse(string code, string reason) { codes.Add(code); reasons.Add(reason); }
        // Qard: an employer loan is principal only. Civil Transactions Law Art. 385 voids any increase over
        // the principal, and charging profit risks unlicensed finance. Hard, never exceptionable, and checked
        // at every assessment so a type that somehow carries interest can neither be applied for nor approved.
        if (!loanType.IsInterestFree || loanType.InterestRate != 0)
            Refuse(LoanEligibilityCodes.InterestNotPermitted, LoanEligibilityCodes.InterestNotPermittedText);
        // New applications use the current type; subsequent decisions must retain the frequency
        // saved on that employee's loan, just as its repayment schedule does.
        var repaymentFrequency = excludeLoanId.HasValue
            ? await db.EmployeeLoans.AsNoTracking()
                .Where(x => x.TenantId == tid && x.Id == excludeLoanId.Value && x.LoanTypeId == loanType.Id
                    && (x.EmployeeIntId == employee.Id || x.EmployeeId == employee.PublicId))
                .Select(x => x.RepaymentFrequency).FirstOrDefaultAsync(ct) ?? loanType.RepaymentFrequency
            : loanType.RepaymentFrequency;
        LoanPolicy? policy = null;
        var hasFrozenPolicy = !string.IsNullOrWhiteSpace(policySnapshotJson) && policySnapshotJson != "{}";
        if (hasFrozenPolicy)
        {
            try { policy = JsonSerializer.Deserialize<LoanPolicy>(policySnapshotJson!); }
            catch (JsonException) { Refuse("PolicyInvalid", "The saved policy assessment is invalid; HR review is required."); }
            if (policy == null || policy.TenantId != tid || policy.LoanTypeId != loanType.Id
                || (policy.CompanyId.HasValue && policy.CompanyId != employee.CompanyId))
                Refuse("PolicyInvalid", "The saved policy does not match the employee's company or loan type.");
        }
        else
        {
            policy = await db.Set<LoanPolicy>().AsNoTracking()
                .Where(x => x.TenantId == tid && x.LoanTypeId == loanType.Id && x.IsActive
                    && (x.CompanyId == employee.CompanyId || x.CompanyId == null))
                .OrderByDescending(x => x.CompanyId.HasValue).ThenByDescending(x => x.Version)
                .ThenByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
            // Not offered when (a) the company's own active policy says so explicitly — for any type, since it is
            // an explicit HR decision — or (b) a grade-limited type has no policy at all. Types that are not
            // grade-limited and have no policy keep the loan-type baseline below, exactly as before (live tenants).
            if ((policy is { CompanyId: not null, IsOffered: false }) || (policy == null && loanType.GradeLimited))
                Refuse(LoanEligibilityCodes.TypeNotOffered, LoanEligibilityCodes.TypeNotOfferedText);
        }
        // Freeze the legacy type limits as a baseline snapshot too. A missing policy never disables HR approval.
        policy ??= new LoanPolicy { Id = Guid.Empty, TenantId = tid, CompanyId = employee.CompanyId,
            LoanTypeId = loanType.Id, Version = 0, PolicyName = "Loan type baseline",
            MaxAmount = loanType.MaxAmount, MaxInstallments = loanType.MaxInstallments,
            MinServiceMonths = loanType.MinServiceMonths, MaxConcurrentLoans = int.MaxValue };
        if (!hasFrozenPolicy)
        {
            // Company policy supplies additional limits, not permission to exceed its loan type.
            // Freeze the effective limits only at initial assessment; future type edits cannot rewrite approved terms.
            if (loanType.MaxAmount > 0)
                policy.MaxAmount = policy.MaxAmount > 0 ? Math.Min(policy.MaxAmount, loanType.MaxAmount) : loanType.MaxAmount;
            if (loanType.MaxInstallments is >= 1 and <= 600)
                policy.MaxInstallments = Math.Min(policy.MaxInstallments, loanType.MaxInstallments);
            else Refuse("PolicyInvalid", "The loan type must have a valid installment limit.");
            policy.MinServiceMonths = Math.Max(policy.MinServiceMonths, loanType.MinServiceMonths);
        }
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var currency = await GlAccountResolver.ResolveCurrencyAsync(db, tid, employee.CompanyId, ct);
        var salary = await SalaryAsOfAsync(tid, employee.Id, today, ct);
        var monthlySalary = MonthlyWage(salary);
        var notice = await db.Set<EmployeeOffboarding>().AnyAsync(x => x.TenantId == tid && x.EmployeeId == employee.Id && x.Status == "InProgress", ct);
        var statuses = ReadList(policy.AllowedEmploymentStatusesJson, Refuse);
        var contracts = ReadList(policy.AllowedContractTypesJson, Refuse);
        var methods = ReadList(policy.AllowedRepaymentMethodsJson, Refuse);
        var frequencies = ReadList(policy.AllowedRepaymentFrequenciesJson, Refuse);
        if (employee.IsDeleted || !statuses.Contains(employee.Status, StringComparer.OrdinalIgnoreCase))
            Refuse("EmploymentStatus", $"Employment status '{employee.Status}' is not eligible under this policy.");
        if (policy.BlockDuringNotice && (notice || employee.Status == EmployeeStatuses.Offboarded)) Refuse("Notice", "Employees serving notice cannot receive a new loan.");
        if (employee.JoiningDate == default || employee.JoiningDate.Date > DateTime.UtcNow.Date)
            Refuse("EmploymentDate", "A valid joining date is required before a loan can be assessed.");
        else if (policy.MinServiceMonths is < 0 or > 600)
            Refuse("PolicyInvalid", "The policy service requirement is invalid.");
        else if (employee.JoiningDate.Date > DateTime.UtcNow.Date.AddMonths(-policy.MinServiceMonths))
            Refuse("MinService", $"At least {policy.MinServiceMonths} completed service month(s) are required.");
        if (policy.RequireProbationCompleted && !(employee.ConfirmationDate <= today
            || (employee.ProbationEndDate.HasValue && employee.ProbationEndDate.Value < today)))
            Refuse("Probation", "Probation must be completed before applying.");
        if (contracts.Length > 0 && !contracts.Contains(employee.ContractType, StringComparer.OrdinalIgnoreCase)) Refuse("ContractType", "The employee's contract type is not eligible.");
        if (!methods.Contains(repaymentMethod, StringComparer.Ordinal)) Refuse("RepaymentMethod", "The repayment method is not permitted by company policy.");
        if (!frequencies.Contains(repaymentFrequency, StringComparer.Ordinal)) Refuse("RepaymentFrequency", "The repayment frequency is not permitted by company policy.");
        if (preview)
        {
            amount = 0;
            // The instalment-percentage limit is shown for the longest schedule the policy allows.
            if (installments < 1 || installments > policy.MaxInstallments) installments = Math.Clamp(policy.MaxInstallments, 1, 600);
        }
        if (!preview && (installments < 1 || installments > policy.MaxInstallments || installments > 600)) Refuse("Installments", $"Installments must be between 1 and {policy.MaxInstallments}.");
        if (!preview && (amount <= 0 || decimal.Round(amount, 2) != amount || amount > 999999999999.99m || amount < installments * .01m)) Refuse("InvalidAmount", "Enter a positive two-decimal amount covering every installment.");
        if (repaymentMethod == "PayrollDeduction" && repaymentFrequency != "Monthly") Refuse("RepaymentMethod", "Payroll deduction requires monthly repayments.");
        // IgnoreQueryFilters: eligibility must retain this employee's prior legal-entity debts after a transfer.
        // Caller already authorizes the employee; explicit tenant, employee and soft-delete predicates never expose another person's loans.
        var loans = await ScopedBypass.TenantWide(db.EmployeeLoans, tid, "Loan eligibility retains only the authorized employee's historical legal-entity debts after transfer.").AsNoTracking().Where(x => x.TenantId == tid && x.EmployeeIntId == employee.Id
            && !x.IsDeleted && (!excludeLoanId.HasValue || x.Id != excludeLoanId.Value)).ToListAsync(ct);
        var commitments = loans.Where(x => x.Status is "Pending" or "Approved" || x.OutstandingBalance > 0).ToList();
        var sameCurrencyCommitments = new List<EmployeeLoan>();
        foreach (var commitment in commitments)
        {
            var debtCurrency = string.IsNullOrWhiteSpace(commitment.Currency)
                ? await db.Companies.AsNoTracking().Where(x => x.TenantId == tid && x.Id == commitment.CompanyId)
                    .Select(x => x.DefaultCurrency).FirstOrDefaultAsync(ct) : commitment.Currency;
            if (!string.Equals(debtCurrency, currency, StringComparison.OrdinalIgnoreCase))
            { if (!codes.Contains("CommitmentCurrency")) Refuse("CommitmentCurrency", "Existing loan exposure uses another currency. Finance must reconcile it before a new loan is granted."); }
            else sameCurrencyCommitments.Add(commitment);
        }
        var committedAmount = sameCurrencyCommitments.Sum(x => x.DisbursementDate.HasValue || x.OutstandingBalance > 0
            ? x.OutstandingBalance : x.Status == "Approved" ? x.ApprovedAmount : x.RequestedAmount);
        if (commitments.Count >= policy.MaxConcurrentLoans) Refuse("ConcurrentLoans", $"Maximum concurrent loans ({policy.MaxConcurrentLoans}) would be exceeded, including pending requests.");
        var liveIds = commitments.Select(x => x.Id).ToArray();
        if (policy.BlockOnOverdue && (commitments.Any(x => x.Status == "Overdue")
            || await db.LoanInstallments.AnyAsync(x => x.TenantId == tid && liveIds.Contains(x.LoanId) && x.DueDate < today && x.AmountPaid < x.AmountDue && x.Status != "Waived", ct)))
            Refuse("Overdue", "An overdue loan must be resolved before another loan is granted.");
        if (policy.CooldownMonthsAfterRepayment > 0 && loans.Any(x => x.Status is "Settled" or "Closed"
            && x.UpdatedAtUtc > DateTime.UtcNow.AddMonths(-policy.CooldownMonthsAfterRepayment)))
            Refuse("Cooldown", $"Wait {policy.CooldownMonthsAfterRepayment} month(s) after the previous loan was settled.");
        decimal? maximum = policy.MaxAmount > 0 ? policy.MaxAmount : null;
        if (policy.MaxTotalOutstanding > 0) maximum = Min(maximum, Math.Max(0, policy.MaxTotalOutstanding - committedAmount));
        if (policy.MaxMultiplierOfSalary > 0 || policy.MaxInstallmentPercentOfSalary > 0)
        {
            if (salary == null || monthlySalary <= 0 || !string.Equals(salary.Currency, currency, StringComparison.OrdinalIgnoreCase))
                Refuse("SalaryCurrency", "An active salary structure in the company's loan currency is required for salary-based eligibility.");
            else
            {
                if (policy.MaxMultiplierOfSalary > 0) maximum = Min(maximum, Math.Max(0, monthlySalary * policy.MaxMultiplierOfSalary - committedAmount));
                var monthlyCommitments = sameCurrencyCommitments.Sum(x => MonthlyEquivalent(x.InstallmentAmount > 0 ? x.InstallmentAmount
                    : x.RequestedAmount / Math.Max(1, x.RequestedInstallments), x.RepaymentFrequency));
                if (!preview && policy.MaxInstallmentPercentOfSalary > 0 && MonthlyEquivalent(amount / Math.Max(1, installments), repaymentFrequency)
                    + monthlyCommitments > monthlySalary * policy.MaxInstallmentPercentOfSalary / 100m)
                    Refuse("SalaryAffordability", "Total scheduled loan installments exceed the permitted percentage of monthly salary.");
            }
        }
        if (!preview && maximum.HasValue && amount > maximum.Value) Refuse("AmountLimit", $"Available loan limit is {maximum.Value:0.00} {currency}, including existing commitments.");

        // ── Explainable limits: every money rule that was evaluated, as a breakdown the UI can render ──
        var limits = new List<LoanLimitBreakdown>();
        if (policy.MaxAmount > 0)
            limits.Add(new(LoanLimitKinds.PolicyMaxAmount, GradeEntitlementValueTypes.Amount, null, null, policy.MaxAmount, null, policy.MaxAmount));
        if (policy.MaxTotalOutstanding > 0)
            limits.Add(new(LoanLimitKinds.PolicyTotalOutstanding, GradeEntitlementValueTypes.Amount, null, null, policy.MaxTotalOutstanding,
                committedAmount, Math.Max(0, policy.MaxTotalOutstanding - committedAmount)));
        var salaryUsable = salary != null && monthlySalary > 0 && string.Equals(salary.Currency, currency, StringComparison.OrdinalIgnoreCase);
        if (salaryUsable && policy.MaxMultiplierOfSalary > 0)
        {
            var cap = monthlySalary * policy.MaxMultiplierOfSalary;
            limits.Add(new(LoanLimitKinds.PolicySalaryMultiple, GradeEntitlementValueTypes.MultipleOfGross, policy.MaxMultiplierOfSalary,
                monthlySalary, cap, committedAmount, Math.Max(0, cap - committedAmount)));
        }
        if (salaryUsable && policy.MaxInstallmentPercentOfSalary > 0 && installments >= 1)
        {
            var monthlyCap = monthlySalary * policy.MaxInstallmentPercentOfSalary / 100m;
            var monthlyNow = sameCurrencyCommitments.Sum(x => MonthlyEquivalent(x.InstallmentAmount > 0 ? x.InstallmentAmount
                : x.RequestedAmount / Math.Max(1, x.RequestedInstallments), x.RepaymentFrequency));
            var perMonthOfOneUnit = MonthlyEquivalent(1m, repaymentFrequency);
            var principal = decimal.Floor(Math.Max(0, monthlyCap - monthlyNow) / perMonthOfOneUnit * installments * 100m) / 100m;
            limits.Add(new(LoanLimitKinds.PolicyInstallmentPercent, GradeEntitlementValueTypes.MultipleOfGross,
                policy.MaxInstallmentPercentOfSalary / 100m, monthlySalary, monthlyCap, monthlyNow, principal,
                LoanLimitUnits.MonthlyInstalment, installments));
        }
        if (policy.MaxConcurrentLoans is > 0 and < int.MaxValue)
            limits.Add(new(LoanLimitKinds.PolicyConcurrentLoans, "Count", null, null, policy.MaxConcurrentLoans, commitments.Count,
                commitments.Count >= policy.MaxConcurrentLoans ? 0 : null, LoanLimitUnits.Loans));

        // ── Grade limit (opt-in per loan type). Outstanding = same loan type, same currency, including pending
        // principal, read above inside the caller's creation lock. Strictest of grade and policy wins. ──
        var sameTypeOutstanding = sameCurrencyCommitments.Where(x => x.LoanTypeId == loanType.Id)
            .Sum(x => x.DisbursementDate.HasValue || x.OutstandingBalance > 0
                ? x.OutstandingBalance : x.Status == "Approved" ? x.ApprovedAmount : x.RequestedAmount);
        var grade = await new GradeLoanLimitResolver(db).ResolveAsync(tid, employee, loanType, today, sameTypeOutstanding, preview ? null : amount, currency, ct);
        if (grade.Applies)
        {
            for (var i = 0; i < grade.Codes.Length; i++) Refuse(grade.Codes[i], grade.Reasons[i]);
            limits.InsertRange(0, grade.Limits);
            if (grade.Available is decimal gradeAvailable) maximum = Min(maximum, gradeAvailable);
            else if (grade.Codes.Length > 0 && grade.Limits.Count == 0) maximum = 0;
        }

        if (excludeLoanId.HasValue && policy.AllowExceptions && codes.Count > 0)
        {
            var loan = await db.EmployeeLoans.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == excludeLoanId.Value, ct);
            if (loan != null)
                for (var i = codes.Count - 1; i >= 0; i--)
                    // Only a code that is exceptionable at all can be waived; grade and legal codes never are.
                    if (LoanLifecycleService.ExceptionCodes.Contains(codes[i])
                        && await new LoanLifecycleService(db).IsExceptionApprovedAsync(tid, loan, codes[i], ct))
                    { codes.RemoveAt(i); reasons.RemoveAt(i); }
        }
        var gradeBlocksOutright = grade.Applies && grade.Limits.Count == 0 && grade.Codes.Length > 0;
        var binding = limits.Where(x => x.Available.HasValue)
            .OrderBy(x => x.Available).ThenBy(x => Array.IndexOf(LoanLimitKinds.Order, x.Limit)).FirstOrDefault();
        // A preview skips the amount rules, so "nothing left to borrow" must be said explicitly: never report
        // eligible when the strictest limit leaves 0.
        if (preview && binding is { Available: 0m } && PreviewExhaustedCode(binding.Limit) is { } exhaustedCode && !codes.Contains(exhaustedCode))
            Refuse(exhaustedCode, $"Nothing is available to borrow right now: {LimitPhrase(binding.Limit)} is fully used.");
        decimal? available = gradeBlocksOutright ? 0m : binding?.Available;
        // Art. 92 (Release A): the instalment as a share of the wage. A salary in another currency is not a usable wage.
        var art92 = preview ? null : LoanArt92Check.Evaluate(
            MonthlyEquivalent(amount / Math.Max(1, installments), repaymentFrequency),
            salaryUsable ? monthlySalary : null, repaymentMethod == "PayrollDeduction");
        return new(reasons.Count == 0, reasons.ToArray(), codes.ToArray(), maximum,
            policy.Id == Guid.Empty ? null : policy.Id, policy.Version == 0 ? null : policy.Version,
            JsonSerializer.Serialize(policy), "{}", monthlySalary, committedAmount,
            grade, available, gradeBlocksOutright ? null : binding?.Limit, limits, art92);
    }

    /// <summary>The employee's salary structure in force on <paramref name="asOf"/> (latest active row), or null.</summary>
    private Task<EmployeeSalaryStructure?> SalaryAsOfAsync(Guid tid, int employeeId, DateOnly asOf, CancellationToken ct) =>
        db.Set<EmployeeSalaryStructure>().AsNoTracking()
            .Where(x => x.TenantId == tid && x.EmployeeId == employeeId && x.IsActive && x.EffectiveDate <= asOf)
            .OrderByDescending(x => x.EffectiveDate).ThenByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);

    /// <summary>The monthly wage of a salary structure (basic + every fixed allowance); 0 for none.</summary>
    public static decimal MonthlyWage(EmployeeSalaryStructure? s) => s == null ? 0 : s.BasicSalary + s.HousingAllowance
        + s.TransportAllowance + s.FoodAllowance + s.MobileAllowance + s.OtherAllowance;

    /// <summary>
    /// Art. 92 for an instalment that is not a new request — a reschedule, a switch to payroll collection, a consent
    /// attached to a pending loan. Same rule and the same wage as <see cref="EvaluateAsync"/>: the salary in force today,
    /// usable only in the company's loan currency; an unknown wage requires consent.
    /// </summary>
    public async Task<LoanArt92Check> Art92ForInstalmentAsync(Guid tid, Employee employee, decimal monthlyInstalment,
        string repaymentMethod, CancellationToken ct)
    {
        var currency = await GlAccountResolver.ResolveCurrencyAsync(db, tid, employee.CompanyId, ct);
        var salary = await SalaryAsOfAsync(tid, employee.Id, DateOnly.FromDateTime(DateTime.UtcNow), ct);
        var wage = MonthlyWage(salary);
        var usable = salary != null && wage > 0 && string.Equals(salary.Currency, currency, StringComparison.OrdinalIgnoreCase);
        return LoanArt92Check.Evaluate(monthlyInstalment, usable ? wage : null, repaymentMethod == "PayrollDeduction");
    }

    /// <summary>Stamps the grade witnesses onto a loan from an assessment (at request, and again at the
    /// approval re-check). A type that is not grade-limited clears nothing and writes nothing.</summary>
    public static void StampGradeWitness(EmployeeLoan loan, LoanEligibilityAssessment assessment)
    {
        if (assessment.GradeLimit is not { Applies: true } grade) return;
        loan.GradeIdAtRequest = grade.GradeId;
        loan.GradeEntitlementId = grade.CellId;
        loan.GradePerLoanCap = grade.PerLoanCap;
        loan.GradeOutstandingCap = grade.OutstandingCap;
    }

    private static string? PreviewExhaustedCode(string limit) => limit switch
    {
        LoanLimitKinds.GradePerLoan => GradeLimitCodes.PerLoan,
        LoanLimitKinds.GradeOutstanding => GradeLimitCodes.Outstanding,
        LoanLimitKinds.PolicyInstallmentPercent => "SalaryAffordability",
        LoanLimitKinds.PolicyConcurrentLoans => "ConcurrentLoans",
        _ => "AmountLimit",
    };

    private static string LimitPhrase(string limit) => limit switch
    {
        LoanLimitKinds.GradePerLoan => "the grade's per-loan maximum",
        LoanLimitKinds.GradeOutstanding => "the grade's total outstanding maximum",
        LoanLimitKinds.PolicyMaxAmount => "the policy's maximum loan amount",
        LoanLimitKinds.PolicyTotalOutstanding => "the policy's total outstanding maximum",
        LoanLimitKinds.PolicySalaryMultiple => "the policy's salary multiple",
        LoanLimitKinds.PolicyInstallmentPercent => "the policy's instalment share of salary",
        _ => "the policy's maximum number of open loans",
    };

    private static decimal Min(decimal? current, decimal next) => current.HasValue ? Math.Min(current.Value, next) : next;
    private static decimal MonthlyEquivalent(decimal amount, string frequency) => frequency switch
    { "Weekly" => amount * 52m / 12m, "BiWeekly" => amount * 26m / 12m, "Quarterly" => amount / 3m, _ => amount };
    private static string[] ReadList(string json, Action<string,string> refuse)
    {
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { refuse("PolicyInvalid", "A policy eligibility list is invalid; HR must correct the policy."); return []; }
    }
}
