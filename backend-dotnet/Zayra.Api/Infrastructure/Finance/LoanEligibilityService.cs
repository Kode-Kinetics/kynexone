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
    decimal? Available = null, string? BindingLimit = null, IReadOnlyList<LoanLimitBreakdown>? Limits = null);

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
            // A grade-limited type is offered only where the company has published a policy for it. Types that
            // are not grade-limited keep the loan-type baseline below, exactly as before (live tenants).
            if (policy == null && loanType.GradeLimited)
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
        var salary = await db.Set<EmployeeSalaryStructure>().AsNoTracking()
            .Where(x => x.TenantId == tid && x.EmployeeId == employee.Id && x.IsActive && x.EffectiveDate <= today)
            .OrderByDescending(x => x.EffectiveDate).ThenByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        var monthlySalary = salary == null ? 0 : salary.BasicSalary + salary.HousingAllowance + salary.TransportAllowance
            + salary.FoodAllowance + salary.MobileAllowance + salary.OtherAllowance;
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
        decimal? available = gradeBlocksOutright ? 0m : binding?.Available;
        return new(reasons.Count == 0, reasons.ToArray(), codes.ToArray(), maximum,
            policy.Id == Guid.Empty ? null : policy.Id, policy.Version == 0 ? null : policy.Version,
            JsonSerializer.Serialize(policy), "{}", monthlySalary, committedAmount,
            grade, available, gradeBlocksOutright ? null : binding?.Limit, limits);
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

    private static decimal Min(decimal? current, decimal next) => current.HasValue ? Math.Min(current.Value, next) : next;
    private static decimal MonthlyEquivalent(decimal amount, string frequency) => frequency switch
    { "Weekly" => amount * 52m / 12m, "BiWeekly" => amount * 26m / 12m, "Quarterly" => amount / 3m, _ => amount };
    private static string[] ReadList(string json, Action<string,string> refuse)
    {
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { refuse("PolicyInvalid", "A policy eligibility list is invalid; HR must correct the policy."); return []; }
    }
}
