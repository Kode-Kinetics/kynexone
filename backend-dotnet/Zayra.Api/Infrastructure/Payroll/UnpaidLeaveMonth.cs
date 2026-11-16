using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// One month's unpaid-leave deduction against what that month actually earns (spec R4/R5, acceptance L6-L9).
///
/// <para><b>Full period (L6/L9).</b> A month slice of unpaid leave that covers every deductible day of the employee's
/// employment window in that month deducts the whole earned base (basic x proration, or the prorated package on the
/// full-package absence base). Pricing it as days x day rate instead would deduct 20/30 of a Sun-Thu month and still pay
/// 10/30 for a month nobody worked.</para>
///
/// <para>Only the unpaid-leave slice type is subject to the full-period rule; a sick-leave pay-scale row keeps its own
/// amount (it is already a share of the wage).</para>
/// </summary>
public static class UnpaidLeaveMonth
{
    public static decimal SliceDeduction(
        LeavePayrollImpact slice, decimal priced, decimal earnedBase, DateOnly paidFrom, DateOnly paidTo, Func<DateOnly, bool> isDeductibleDay)
    {
        if (slice.ImpactType == CompanyPayrollRules.UnpaidLeaveImpactType
            && slice.SliceFrom is DateOnly from && slice.SliceTo is DateOnly to
            && CoversEveryDeductibleDay(from, to, paidFrom, paidTo, isDeductibleDay))
            return Math.Max(0m, earnedBase);
        return priced;
    }

    /// <summary>True when [from, to] includes the first and the last deductible day of the window (and so every one between).
    /// A window with no deductible day at all is never "covered".</summary>
    public static bool CoversEveryDeductibleDay(DateOnly from, DateOnly to, DateOnly windowFrom, DateOnly windowTo, Func<DateOnly, bool> isDeductibleDay)
    {
        DateOnly? first = null, last = null;
        for (var d = windowFrom; d <= windowTo; d = d.AddDays(1))
        {
            if (!isDeductibleDay(d)) continue;
            first ??= d;
            last = d;
        }
        return first is DateOnly f && last is DateOnly l && from <= f && to >= l;
    }
}
