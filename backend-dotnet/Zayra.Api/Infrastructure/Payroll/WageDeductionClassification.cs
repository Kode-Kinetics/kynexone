using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Which payslip deduction lines are DEBT-TYPE deductions for Saudi Labour Law Art. 92/93 — the
/// deductions the 50% cap applies to. Classified from the line's persisted <c>Source</c> (the same
/// classification that drives GL routing in <c>DeductionDriverKey</c>) and its pinned GL driver, never
/// from its display name.
///
/// <para><b>Counted (debt-type):</b></para>
/// <list type="bullet">
/// <item><c>Source = "Loan"</c> — loan instalments (LOAN_EMI) and salary-advance repayments
///   (ADVANCE_EMI); both route to <c>DED:LOAN</c>.</item>
/// <item><c>Source = "Adjustment"</c> — an approved NEGATIVE payroll adjustment whose TYPE is debt-like
///   (<see cref="DebtAdjustmentTypeCodes"/>: loan/advance recovery, penalty/fine, damages/compensation,
///   overpayment or debt recovery). The line carries the type as its code (<c>ADJ_{TYPE}</c>, the same
///   normalisation Process applies). A negative adjustment of any other type (a correction, an
///   allowance clawback) is not a debt and is not counted.</item>
/// <item>A tenant-configured component pinned to <c>DED:LOAN</c>.</item>
/// </list>
///
/// <para><b>Not counted:</b> statutory contributions (GOSI/SANED — Art. 92(3) deductions due by law),
/// absence/loss-of-pay and late deductions (<c>Attendance</c>), unpaid leave (<c>Leave</c>), tax, the
/// salary-structure fixed deduction, a prior void's receivable recovery (a non-duplication of payment,
/// not a deduction — see PayrollValidationEngine's ZERO_NET_FROM_RECEIVABLE_RECOVERY), and a final
/// settlement's own deductions (they reduce final dues). Counting those blocked lawful payslips: a
/// five-paid-day joiner whose GOSI is on the full-month base, or a long unpaid absence.</para>
/// </summary>
public static class WageDeductionClassification
{
    public const string LoanSource = "Loan";
    public const string AdjustmentSource = "Adjustment";
    public const string LoanGlDriver = "DED:LOAN";

    /// <summary>What an override of the cap must rest on. Pending counsel's review this names the kind of
    /// document only; it states no legal conclusion about when the cap may be exceeded.</summary>
    public const string CapOverrideGrounds = "a labour court / commission decision or other lawful written basis";

    /// <summary>Validation / wage-file code for a debt-type total above half the wage.</summary>
    public const string DeductionsExceedHalfWageCode = "DEDUCTIONS_EXCEED_HALF_WAGE";

    public static bool IsDebtType(PayrollDeduction line) =>
        !line.IsEmployerContribution && IsDebtType(line.Source, line.GlDriverKey, line.ComponentCode);

    /// <summary>Adjustment types (as normalised into the line code: upper-case, non-alphanumerics → '_')
    /// that represent a debt owed to the employer under Art. 92.</summary>
    public static readonly IReadOnlySet<string> DebtAdjustmentTypeCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "LOAN", "LOAN_RECOVERY", "LOAN_REPAYMENT", "ADVANCE", "ADVANCE_RECOVERY", "SALARY_ADVANCE",
        "PENALTY", "FINE", "DISCIPLINARY_PENALTY", "DISCIPLINARY_FINE",
        "DAMAGE", "DAMAGES", "COMPENSATION", "DAMAGE_COMPENSATION",
        "DEBT_RECOVERY", "OVERPAYMENT_RECOVERY",
    };

    public const string AdjustmentCodePrefix = "ADJ_";

    public static bool IsDebtType(string? source, string? glDriverKey, string? componentCode = null) =>
        string.Equals(source, LoanSource, StringComparison.Ordinal)
        || (string.Equals(source, AdjustmentSource, StringComparison.Ordinal)
            && componentCode is not null && componentCode.StartsWith(AdjustmentCodePrefix, StringComparison.Ordinal)
            && DebtAdjustmentTypeCodes.Contains(componentCode[AdjustmentCodePrefix.Length..]))
        || string.Equals(glDriverKey, LoanGlDriver, StringComparison.Ordinal);

    /// <summary>Σ debt-type deduction lines per employee.</summary>
    public static Dictionary<int, decimal> DebtTotalsByEmployee(IEnumerable<PayrollDeduction> lines) =>
        lines.Where(IsDebtType)
            .GroupBy(l => l.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));

    /// <summary>True when debt-type deductions exceed half of the wage due (Art. 93).</summary>
    public static bool ExceedsHalfWage(decimal debtDeductions, decimal grossWage) =>
        grossWage > 0m && debtDeductions > grossWage / 2m;
}
