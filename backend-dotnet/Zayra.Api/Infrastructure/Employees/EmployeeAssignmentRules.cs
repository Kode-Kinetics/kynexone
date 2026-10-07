using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>Why a position cannot receive an employee. <see cref="None"/> means it can.</summary>
public enum PositionRefusal
{
    None,
    FrozenOrClosed,
    Occupied,
    JoiningDateUnknown,
    NotYetEffective,
    Expired,
    CompanyMismatch,
    BranchMismatch,
    DepartmentMismatch,
    CostCenterMismatch,
    DesignationMismatch,
    GradeMismatch,
}

/// <summary>
/// The assignment rules the Add Employee FORM enforces, in one place, so the CSV import applies the same
/// ones. The import used to be more permissive than the form: it linked a position that was not yet
/// effective (or had expired) on the joining date, or whose cost centre differed; it linked an INACTIVE
/// grade or designation; and with several companies it filed a row that named no company into the
/// OLDEST one. The form refuses all of those. The two doors still answer differently on purpose (the form
/// refuses the save; the import keeps the person and leaves the link empty with a review gap, because the
/// import never guesses and never drops a person), but the DECISION is now this one function on both.
/// </summary>
public static class EmployeeAssignmentRules
{
    /// <summary>
    /// The employing company to use when none was named: the tenant's only active company, or none at all.
    /// With two or more there is nothing to choose by, so the answer is "unassigned", never the oldest.
    /// </summary>
    public static T? DefaultCompany<T>(IReadOnlyCollection<T> activeCompanies) =>
        activeCompanies.Count == 1 ? activeCompanies.First() : default;

    /// <summary>
    /// Whether <paramref name="position"/> can receive the employee described by the other arguments.
    /// <paramref name="employeeId"/> is the employee being saved (null for a new hire), so re-saving the
    /// current incumbent is not "occupied". A null <paramref name="joiningDate"/> means the date is not
    /// known, and a position's effective window cannot be checked against an unknown date.
    /// </summary>
    public static PositionRefusal CheckPosition(
        Position position, int? employeeId, DateOnly? joiningDate,
        Guid? companyId, Guid? branchId, Guid? departmentId, Guid? costCenterId, Guid? designationId, Guid? gradeId)
    {
        if (position.Status is PositionStatuses.Frozen or PositionStatuses.Closed) return PositionRefusal.FrozenOrClosed;
        if (joiningDate is null) return PositionRefusal.JoiningDateUnknown;
        if (position.EffectiveFrom > joiningDate.Value) return PositionRefusal.NotYetEffective;
        if (position.EffectiveTo is not null && position.EffectiveTo < joiningDate.Value) return PositionRefusal.Expired;
        if (position.IncumbentEmployeeId is not null && position.IncumbentEmployeeId != employeeId) return PositionRefusal.Occupied;
        if (position.CompanyId is not null && position.CompanyId != companyId) return PositionRefusal.CompanyMismatch;
        if (position.BranchId is not null && position.BranchId != branchId) return PositionRefusal.BranchMismatch;
        if (position.DepartmentId is not null && position.DepartmentId != departmentId) return PositionRefusal.DepartmentMismatch;
        if (position.CostCenterId is not null && position.CostCenterId != costCenterId) return PositionRefusal.CostCenterMismatch;
        if (position.DesignationId is not null && position.DesignationId != designationId) return PositionRefusal.DesignationMismatch;
        if (position.GradeId is not null && position.GradeId != gradeId) return PositionRefusal.GradeMismatch;
        return PositionRefusal.None;
    }
}
