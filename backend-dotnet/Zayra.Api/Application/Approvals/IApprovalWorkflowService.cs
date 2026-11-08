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

    /// <summary>
    /// The pending approvals the caller can decide RIGHT NOW: the same check the decision endpoint runs (routed to them or their role, or an
    /// <c>approvals.override</c> holder; never their own request, one about them, or a step they already decided), and only for a caller who holds
    /// <c>approvals.decide</c>, which the decision endpoint requires. <paramref name="subjectPopulation"/> limits it to approvals about those employees
    /// (a data-scoped caller); null = no limit. <c>Count</c> is the total (at most <see cref="DecidableApprovals.ScanLimit"/> newest are examined);
    /// <c>NewestIds</c> are the <paramref name="take"/> newest, newest first.
    /// </summary>
    Task<DecidableApprovals> GetDecidablePendingAsync(Guid tenantId, RequestContext context, IReadOnlyCollection<int>? subjectPopulation, int take, CancellationToken cancellationToken);
}

/// <summary>What <see cref="IApprovalWorkflowService.GetDecidablePendingAsync"/> found: how many the caller can decide now, and the newest ones.</summary>
public sealed record DecidableApprovals(int Count, IReadOnlyList<Guid> NewestIds, bool Capped = false)
{
    /// <summary>The most pending requests examined per call (newest first); beyond it the count is a floor, never an overstatement, and <c>Capped</c> is true.</summary>
    public const int ScanLimit = 500;

    public static readonly DecidableApprovals None = new(0, Array.Empty<Guid>());
}
