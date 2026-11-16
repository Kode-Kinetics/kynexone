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
/// <para><b>Cap (L7).</b> No slice, and no month's total, deducts more than the earned base. What a slice priced above it
/// is simply not wage: nothing is carried forward from the cap.</para>
///
/// <para>Only the unpaid-leave slice type is subject to the full-period rule; a sick-leave pay-scale row keeps its own
/// amount (it is already a share of the wage), but is still capped.</para>
/// </summary>
public static class UnpaidLeaveMonth
{
    public static decimal SliceDeduction(
        LeavePayrollImpact slice, decimal priced, decimal earnedBase, DateOnly paidFrom, DateOnly paidTo, Func<DateOnly, bool> isDeductibleDay)
    {
        var cap = Math.Max(0m, earnedBase);
        if (slice.ImpactType == CompanyPayrollRules.UnpaidLeaveImpactType
            && slice.SliceFrom is DateOnly from && slice.SliceTo is DateOnly to
            && CoversEveryDeductibleDay(from, to, paidFrom, paidTo, isDeductibleDay))
            return cap;
        return Math.Min(priced, cap);
    }

    public static decimal CapTotal(decimal total, decimal earnedBase) => Math.Min(total, Math.Max(0m, earnedBase));

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
