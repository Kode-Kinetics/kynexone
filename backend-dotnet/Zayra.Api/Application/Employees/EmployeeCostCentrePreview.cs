namespace Zayra.Api.Application.Employees;

/// <summary>Where the person's cost would land. <see cref="Source"/> is Employee, Position, Department, ParentDepartment, CompanyDefault or None;
/// <see cref="InvalidReason"/> is the resolver's <c>&lt;level&gt;_cost_centre_invalid</c> (null when it resolves, or when nothing is set anywhere).</summary>
public sealed record EmployeeCostCentrePreviewDto(Guid? CostCentreId, string? Code, string? NameEn, string? NameAr, string Source,
    string? SourceName, string? SourceNameAr, string? Level, bool IsOverride, string? InvalidReason);

/// <summary>The employee's current placement, read untracked and tenant-pinned, for the controller's scope checks.</summary>
public sealed record CostCentrePreviewSubject(Guid? CompanyId, Guid? DepartmentId, Guid? PositionId);

public enum CostCentrePreviewRefusal { None, DepartmentNotFound, PositionNotFound }

/// <summary>
/// Cost centre on transfer, Wave A: the read-only "where would this person's cost go" behind
/// <c>GET /api/employees/{id}/cost-centre/preview</c>. Never writes: nothing is tracked, staged or saved.
/// </summary>
public interface IEmployeeCostCentrePreviewService
{
    /// <summary>Null when no live employee with this id exists in the tenant.</summary>
    Task<CostCentrePreviewSubject?> FindAsync(Guid tenantId, int employeeId, CancellationToken ct);

    /// <summary>A department or position outside the employee's company refuses exactly as a missing one does; a foreign cost centre is simply
    /// invalid, with no code or name.</summary>
    Task<(CostCentrePreviewRefusal Refusal, EmployeeCostCentrePreviewDto? Preview)> PreviewAsync(Guid tenantId, CostCentrePreviewSubject subject,
        Guid? departmentId, Guid? positionId, Guid? costCenterId, CancellationToken ct);
}
