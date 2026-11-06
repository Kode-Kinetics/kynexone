using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// The single rule for "which active overtime policy applies when none is named". Several policies
/// can be active at once, and picking "the first" without an order let the database choose. Every
/// resolution site (manual request, attendance detection, calculation, payroll divisor) now orders
/// through here, and the order is exactly the one payroll always used for its hour divisor and rate
/// basis (oldest first), so no existing tenant's pay moves; code and id are added only to settle exact ties
/// that used to be left to the database. <see cref="Order"/> does not consult the scope columns
/// (branch/department/grade); <see cref="ForEmployee"/> does, and is what a request raised for an employee uses.
/// </summary>
public static class OvertimePolicyDefault
{
    public static IOrderedQueryable<OvertimePolicy> Order(IQueryable<OvertimePolicy> policies) =>
        policies
            .OrderBy(p => p.CreatedAtUtc)
            .ThenBy(p => p.Code)
            .ThenBy(p => p.Id);

    /// <summary>
    /// The active policies that APPLY to an employee: each of BranchId / DepartmentId / GradeId is either unset (the policy
    /// is not limited on that axis) or equal to the employee's. A policy limited on an axis the employee has no value for
    /// does not apply. Callers pass only active, non-deleted, same-tenant policies.
    /// </summary>
    public static IQueryable<OvertimePolicy> ApplicableTo(IQueryable<OvertimePolicy> policies, Guid? branchId, Guid? departmentId, Guid? gradeId) =>
        policies.Where(p => (p.BranchId == null || p.BranchId == branchId)
                         && (p.DepartmentId == null || p.DepartmentId == departmentId)
                         && (p.GradeId == null || p.GradeId == gradeId));

    /// <summary>
    /// The policy a request for this employee is stamped with when none is chosen: among the applicable ones, the most
    /// specific (most scope columns set) first, then the tenant default order. Without the specificity step a tenant-wide
    /// policy would always outrank a policy written for the employee's own grade or branch.
    /// </summary>
    public static IOrderedQueryable<OvertimePolicy> ForEmployee(IQueryable<OvertimePolicy> policies, Guid? branchId, Guid? departmentId, Guid? gradeId) =>
        ApplicableTo(policies, branchId, departmentId, gradeId)
            .OrderByDescending(p => (p.BranchId != null ? 1 : 0) + (p.DepartmentId != null ? 1 : 0) + (p.GradeId != null ? 1 : 0))
            .ThenBy(p => p.CreatedAtUtc)
            .ThenBy(p => p.Code)
            .ThenBy(p => p.Id);

    /// <summary>True when the policy applies to an employee with these scope values (the in-memory twin of <see cref="ApplicableTo"/>).</summary>
    public static bool Applies(OvertimePolicy policy, Guid? branchId, Guid? departmentId, Guid? gradeId) =>
        (policy.BranchId is null || policy.BranchId == branchId)
        && (policy.DepartmentId is null || policy.DepartmentId == departmentId)
        && (policy.GradeId is null || policy.GradeId == gradeId);
}
