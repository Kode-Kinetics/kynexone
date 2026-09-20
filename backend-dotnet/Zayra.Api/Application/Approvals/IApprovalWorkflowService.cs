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
    Task<ApprovalRequestDto?> DecideAsync(Guid tenantId, Guid approvalRequestId, ApprovalDecisionRequest request, RequestContext context, CancellationToken cancellationToken);

    // W2-E — send back to the requester, and the requester's resubmission (restarts at step 1).
    Task<ApprovalRequestDto?> SendBackAsync(Guid tenantId, Guid approvalRequestId, SendBackApprovalRequest request, RequestContext context, CancellationToken cancellationToken);
    Task<ApprovalRequestDto?> ResubmitAsync(Guid tenantId, Guid approvalRequestId, ResubmitApprovalRequest request, RequestContext context, CancellationToken cancellationToken);

    // W2-E — workflow configuration screen.
    Task<IReadOnlyList<ApprovalEntityDescriptor>> GetEntitiesAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ApprovalWorkflowDto?> SetWorkflowActiveAsync(Guid tenantId, Guid id, bool active, RequestContext context, CancellationToken cancellationToken);
    Task<ApprovalRoutePreviewDto> PreviewRouteAsync(Guid tenantId, string entityName, int? employeeId, CancellationToken cancellationToken);
    Task<ApprovalGovernanceSettingsDto> GetGovernanceSettingsAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ApprovalGovernanceSettingsDto> SaveGovernanceSettingsAsync(Guid tenantId, ApprovalGovernanceSettingsDto settings, RequestContext context, CancellationToken cancellationToken);
}
