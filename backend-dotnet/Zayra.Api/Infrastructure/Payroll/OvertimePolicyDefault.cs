using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// The single rule for "which active overtime policy applies when none is named". Several policies
/// can be active at once, and picking "the first" without an order let the database choose. Every
/// resolution site (manual request, attendance detection, calculation, payroll divisor) now orders
/// through here, and the order is exactly the one payroll always used for its hour divisor and rate
/// basis (oldest first), so no existing tenant's pay moves; code and id are added only to settle exact ties
/// that used to be left to the database. Scope columns (branch/department/grade) are not consulted.
/// </summary>
public static class OvertimePolicyDefault
{
    public static IOrderedQueryable<OvertimePolicy> Order(IQueryable<OvertimePolicy> policies) =>
        policies
            .OrderBy(p => p.CreatedAtUtc)
            .ThenBy(p => p.Code)
            .ThenBy(p => p.Id);
}
