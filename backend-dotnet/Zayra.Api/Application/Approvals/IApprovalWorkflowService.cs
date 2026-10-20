using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Application.Approvals;

public interface IApprovalWorkflowService
{
    Task<PagedResult<ApprovalWorkflowDto>> GetWorkflowsAsync(Guid tenantId, string? entityName, int page, int pageSize, CancellationToken cancellationToken);
    Task<ApprovalWorkflowDto?> GetWorkflowAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ApprovalWorkflowDto> CreateWorkflowAsync(Guid tenantId, ApprovalWorkflowRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<ApprovalWorkflowDto?> UpdateWorkflowAsync(Guid tenantId, Guid id, ApprovalWorkflowRequest request, RequestContext context, CancellationToken cancellationToken);

    Task<PagedResult<ApprovalRequestDto>> GetRequestsAsync(Guid tenantId, string? status, string? entityName, int page, int pageSize, CancellationToken cancellationToken);
    Task<PagedResult<ApprovalRequestDto>> GetRequestsAsync(Guid tenantId, string? status, string? entityName, string? queue, int page, int pageSize, RequestContext context, CancellationToken cancellationToken);
    Task<ApprovalRequestDto?> GetRequestAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
    Task<ApprovalRequestDto?> GetRequestAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken);
    Task<ApprovalRequestDto> CreateRequestAsync(Guid tenantId, CreateApprovalRequest request, RequestContext context, CancellationToken cancellationToken);
    /// <summary>
    /// Runs every check <see cref="CreateRequestAsync"/> runs before writing — workflow resolution, first-step routing and
    /// "could anyone decide this" — and writes nothing. Producers that save their own record first call it before that
    /// write, so a refused request never leaves a half-saved record behind. Throws what CreateRequestAsync would.
    /// </summary>
    Task EnsureRoutableAsync(Guid tenantId, CreateApprovalRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<ApprovalRequestDto?> DecideAsync(Guid tenantId, Guid approvalRequestId, ApprovalDecisionRequest request, RequestContext context, CancellationToken cancellationToken);
    /// <summary>Before → after of a pending employee-change approval, for someone who may decide it now; null otherwise.</summary>
    Task<EmployeeChangeDetailDto?> GetEmployeeChangeDetailAsync(Guid tenantId, Guid approvalRequestId, RequestContext context, CancellationToken cancellationToken);
    Task<ApprovalRequestDto?> WithdrawAsync(Guid tenantId, Guid approvalRequestId, string? reason, RequestContext context, CancellationToken cancellationToken);
}
