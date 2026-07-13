using Zayra.Api.Application.Auth;

namespace Zayra.Api.Application.Organization;

// ── DTOs ─────────────────────────────────────────────────────────────────────

public record OrgChartNodeDto(
    int Id,
    string EmployeeCode,
    string FullName,
    string Designation,
    string Department,
    string? ProfilePhotoUrl,
    IReadOnlyList<OrgChartNodeDto> DirectReports);

public record ReportingLineDto(
    Guid Id,
    int EmployeeId,
    string EmployeeName,
    int ManagerEmployeeId,
    string ManagerName,
    string RelationshipType,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo,
    bool IsPrimary,
    bool IsActive);

public record AddReportingLineRequest(
    int ManagerEmployeeId,
    string RelationshipType,     // SolidLine | DottedLine | Temporary | Functional
    DateTime? EffectiveFrom,
    DateTime? EffectiveTo,
    bool IsPrimary = false);

public record HierarchyPersonDto(
    int EmployeeId,
    string EmployeeCode,
    string FullName,
    string Designation,
    string Department,
    Guid? CompanyId,
    int Level);

public record HierarchyResolverDto(
    int EmployeeId,
    IReadOnlyList<HierarchyPersonDto> ManagerChain,
    HierarchyPersonDto? DirectManager,
    HierarchyPersonDto? SecondLevelManager,
    HierarchyPersonDto? DepartmentHead,
    HierarchyPersonDto? CompanyHead);

public record WorkflowApproverResolutionDto(
    string WorkflowType,
    int EmployeeId,
    IReadOnlyList<HierarchyPersonDto> Approvers,
    IReadOnlyList<string> MissingRoles);

// ── Service interface ─────────────────────────────────────────────────────────

public interface IHrmHierarchyService
{
    /// <summary>Returns the full org chart tree, optionally rooted at a specific employee.</summary>
    Task<IReadOnlyList<OrgChartNodeDto>> GetOrgChartAsync(Guid tenantId, int? rootEmployeeId, int maxDepth, CancellationToken ct);

    /// <summary>All active reporting lines (any type) for an employee.</summary>
    Task<IReadOnlyList<ReportingLineDto>> GetReportingLinesAsync(Guid tenantId, int employeeId, CancellationToken ct);

    /// <summary>
    /// Sets Employee.ManagerEmployeeId and creates/updates the corresponding SolidLine ReportingLine.
    /// Throws InvalidOperationException on self-reference or circular chain.
    /// </summary>
    Task SetManagerAsync(Guid tenantId, int employeeId, int? managerEmployeeId, RequestContext context, CancellationToken ct);

    /// <summary>Adds a non-solid-line reporting relationship.</summary>
    Task<ReportingLineDto> AddReportingLineAsync(Guid tenantId, int employeeId, AddReportingLineRequest req, RequestContext context, CancellationToken ct);

    /// <summary>Deactivates a reporting line (soft end-date).</summary>
    Task<bool> RemoveReportingLineAsync(Guid tenantId, int employeeId, Guid reportingLineId, RequestContext context, CancellationToken ct);

    /// <summary>Resolves the manager chain and common hierarchy anchors for an employee.</summary>
    Task<HierarchyResolverDto> ResolveHierarchyAsync(Guid tenantId, int employeeId, int maxDepth, CancellationToken ct);

    /// <summary>Resolves workflow approvers from the hierarchy source of truth.</summary>
    Task<WorkflowApproverResolutionDto> ResolveWorkflowApproversAsync(Guid tenantId, int employeeId, string workflowType, CancellationToken ct);

    /// <summary>
    /// Validates that setting employee as manager of subject would not create a circular chain.
    /// Returns the chain length on success; throws InvalidOperationException if circular.
    /// </summary>
    Task<int> ValidateNoCircularManagerAsync(Guid tenantId, int employeeId, int newManagerId, CancellationToken ct);
}
