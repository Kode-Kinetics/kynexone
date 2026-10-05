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
/// <item><c>Source = "Adjustment"</c> — approved NEGATIVE payroll adjustments. This is the product's only
///   route for an employer-imposed penalty, fine, or damages/compensation charge: statutory contributions,
///   absence, unpaid leave and tax all have their own sources and are never adjustments.</item>
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

    /// <summary>Validation / wage-file code for a debt-type total above half the wage.</summary>
    public const string DeductionsExceedHalfWageCode = "DEDUCTIONS_EXCEED_HALF_WAGE";

    public static bool IsDebtType(PayrollDeduction line) =>
        !line.IsEmployerContribution && IsDebtType(line.Source, line.GlDriverKey);

    public static bool IsDebtType(string? source, string? glDriverKey) =>
        string.Equals(source, LoanSource, StringComparison.Ordinal)
        || string.Equals(source, AdjustmentSource, StringComparison.Ordinal)
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
