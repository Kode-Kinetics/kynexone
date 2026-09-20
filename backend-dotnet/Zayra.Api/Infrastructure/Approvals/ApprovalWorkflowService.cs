using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Leave;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Approvals;

public class ApprovalWorkflowService : IApprovalWorkflowService
{
    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;
    private readonly IEstablishmentGuard _establishmentGuard;
    private readonly ILeaveService _leaveService;
    private readonly IApprovalRouter _router;
    private readonly INotificationService? _notifications;
    // W2-E — the distinct-approver setting, read once per tenant per request scope (list views ask per row).
    private readonly Dictionary<Guid, bool> _distinctRuleCache = new();

    public ApprovalWorkflowService(ZayraDbContext db, IAuditService audit)
        : this(db, audit, new HrmHierarchyService(db, audit))
    {
    }

    public ApprovalWorkflowService(
        ZayraDbContext db,
        IAuditService audit,
        IHrmHierarchyService hierarchy,
        IEstablishmentGuard? establishmentGuard = null,
        ILeaveService? leaveService = null,
        IApprovalRouter? router = null,
        INotificationService? notifications = null)
    {
        _db = db;
        _notifications = notifications;
        _audit = audit;
        // Optional with concrete fallback so direct constructions keep compiling AND enforcing.
        _establishmentGuard = establishmentGuard ?? new EstablishmentGuardService(db);
        // F1 — ONE router shared by this service and the leave aggregate it delegates to.
        _router = router ?? new ApprovalRouter(db, hierarchy);
        _leaveService = leaveService ?? new LeaveService(db, _router);
    }

    public async Task<PagedResult<ApprovalWorkflowDto>> GetWorkflowsAsync(Guid tenantId, string? entityName, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.ApprovalWorkflows.AsNoTracking().Include(x => x.Steps).Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(entityName)) query = query.Where(x => x.EntityName == entityName);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.Code).Skip((page - 1) * pageSize).Take(pageSize).Select(x => x.ToDto()).ToListAsync(cancellationToken);
        var inFlight = await InFlightCountsAsync(tenantId, items.Select(x => x.Id).ToList(), cancellationToken);
        items = items.Select(x => x with { InFlightRequests = inFlight.GetValueOrDefault(x.Id) }).ToList();
        return new PagedResult<ApprovalWorkflowDto>(items, total, page, pageSize);
    }

    public async Task<ApprovalWorkflowDto?> GetWorkflowAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var workflow = await _db.ApprovalWorkflows.AsNoTracking().Include(x => x.Steps).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (workflow is null) return null;
        var inFlight = await InFlightCountsAsync(tenantId, [workflow.Id], cancellationToken);
        return workflow.ToDto() with { InFlightRequests = inFlight.GetValueOrDefault(workflow.Id) };
    }

    /// <summary>W2-E — requests pinned to each workflow that are still open (Pending or sent back to the requester).</summary>
    private async Task<Dictionary<Guid, int>> InFlightCountsAsync(Guid tenantId, List<Guid> workflowIds, CancellationToken cancellationToken)
    {
        if (workflowIds.Count == 0) return new();
        return await _db.ApprovalRequests.AsNoTracking()
            .Where(x => x.TenantId == tenantId && workflowIds.Contains(x.WorkflowId)
                && (x.Status == ApprovalStatuses.Pending || x.Status == ApprovalStatuses.ReturnedToRequester))
            .GroupBy(x => x.WorkflowId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
    }

    public async Task<ApprovalWorkflowDto> CreateWorkflowAsync(Guid tenantId, ApprovalWorkflowRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        await EnsureWorkflowCodeUnique(tenantId, request.Code, null, cancellationToken);
        await ValidateStepsAsync(tenantId, request, cancellationToken);
        await EnsureScopeUnambiguousAsync(tenantId, request, null, cancellationToken);
        var workflow = new ApprovalWorkflow { TenantId = tenantId };
        Apply(workflow, request, tenantId);
        _db.ApprovalWorkflows.Add(workflow);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("approval.workflow_created", nameof(ApprovalWorkflow), workflow.Id.ToString(), context, null, cancellationToken);
        return workflow.ToDto();
    }

    public async Task<ApprovalWorkflowDto?> UpdateWorkflowAsync(Guid tenantId, Guid id, ApprovalWorkflowRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var workflow = await _db.ApprovalWorkflows.Include(x => x.Steps).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (workflow is null) return null;
        await EnsureWorkflowCodeUnique(tenantId, request.Code, id, cancellationToken);
        await ValidateStepsAsync(tenantId, request, cancellationToken);
        await EnsureScopeUnambiguousAsync(tenantId, request, id, cancellationToken);
        _db.ApprovalWorkflowSteps.RemoveRange(workflow.Steps);
        Apply(workflow, request, tenantId);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("approval.workflow_updated", nameof(ApprovalWorkflow), workflow.Id.ToString(), context, null, cancellationToken);
        return workflow.ToDto();
    }

    public async Task<PagedResult<ApprovalRequestDto>> GetRequestsAsync(Guid tenantId, string? status, string? entityName, int page, int pageSize, CancellationToken cancellationToken)
        => await GetRequestsCoreAsync(tenantId, status, entityName, null, page, pageSize, null, cancellationToken);

    public async Task<PagedResult<ApprovalRequestDto>> GetRequestsAsync(Guid tenantId, string? status, string? entityName, string? queue, int page, int pageSize, RequestContext context, CancellationToken cancellationToken)
        => await GetRequestsCoreAsync(tenantId, status, entityName, queue, page, pageSize, context, cancellationToken);

    private async Task<PagedResult<ApprovalRequestDto>> GetRequestsCoreAsync(Guid tenantId, string? status, string? entityName, string? queue, int page, int pageSize, RequestContext? context, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.ApprovalRequests.AsNoTracking().Include(x => x.Decisions).Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(entityName)) query = query.Where(x => x.EntityName == entityName);
        if (string.IsNullOrWhiteSpace(queue) && context is not null && !CanViewAllApprovalRequests(context))
        {
            queue = "mine";
        }
        if (!string.IsNullOrWhiteSpace(queue) && context is not null)
        {
            var q = Clean(queue).ToLowerInvariant();
            if (q is "mine" or "my")
            {
                var callerEmployeeId = await ResolveCallerEmployeeIdAsync(tenantId, context.UserId, cancellationToken);
                var roles = (context.Roles ?? Array.Empty<string>())
                    .Select(Clean)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.ToLower())
                    .ToArray();
                // Ownership predicate only. Do NOT fold Status=="Pending" in here: combining this queue
                // with an explicit status filter (e.g. status=Approved) would produce an impossible
                // "Pending AND Approved" query, so a just-approved item vanishes from the view even though
                // it shows in the unscoped "All" queue. When no explicit status is asked for, we still
                // default to the actionable Pending set below.
                query = query.Where(x =>
                    (context.UserId != null && x.CurrentApproverUserId == context.UserId) ||
                    (callerEmployeeId != null && x.CurrentApproverEmployeeId == callerEmployeeId) ||
                    ((x.CurrentApproverType ?? string.Empty).ToLower() == "role" &&
                     roles.Contains((x.CurrentApproverRole ?? string.Empty).ToLower())));
                if (string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == "Pending");
            }
            else if (q is "team")
            {
                var callerEmployeeId = await ResolveCallerEmployeeIdAsync(tenantId, context.UserId, cancellationToken);
                if (callerEmployeeId is null) query = query.Where(x => false);
                else
                {
                    var teamIds = await ResolveTeamEmployeeIdsAsync(tenantId, callerEmployeeId.Value, cancellationToken);
                    query = query.Where(x => x.RequestedForEmployeeId != null && teamIds.Contains(x.RequestedForEmployeeId.Value));
                    if (string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == "Pending");
                }
            }
            else if (q is "returned")
            {
                // W2-E — requests sent back to the caller (as requester or as the subject employee).
                var callerEmployeeId = await ResolveCallerEmployeeIdAsync(tenantId, context.UserId, cancellationToken);
                // Same rule as IsRequesterAsync: the requester, or the employee a leave request was taken for.
                query = query.Where(x =>
                    (context.UserId != null && x.RequestedByUserId == context.UserId) ||
                    (callerEmployeeId != null && x.EntityName == nameof(LeaveRequest) && x.RequestedForEmployeeId == callerEmployeeId));
                if (string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == ApprovalStatuses.ReturnedToRequester);
            }
            else if (q is "overdue")
            {
                var now = DateTime.UtcNow;
                query = query.Where(x => x.Status == "Pending" && x.DueAtUtc != null && x.DueAtUtc < now);
            }
        }
        var total = await query.CountAsync(cancellationToken);
        var approvals = await query
            .OrderBy(x => x.DueAtUtc ?? DateTime.MaxValue)
            .ThenByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var items = new List<ApprovalRequestDto>(approvals.Count);
        foreach (var approval in approvals)
        {
            var (canDecide, blocked) = await EvaluateDecisionAsync(approval, context, cancellationToken);
            items.Add(approval.ToDto(canDecide, blocked));
        }
        return new PagedResult<ApprovalRequestDto>(items, total, page, pageSize);
    }

    public async Task<ApprovalRequestDto?> GetRequestAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var request = await _db.ApprovalRequests.AsNoTracking().Include(x => x.Decisions).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        return request?.ToDto();
    }

    public async Task<ApprovalRequestDto?> GetRequestAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken)
    {
        var request = await _db.ApprovalRequests.AsNoTracking().Include(x => x.Decisions).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (request is null) return null;
        if (!await CanViewRequestAsync(request, context, cancellationToken)) return null;
        var (canDecide, blocked) = await EvaluateDecisionAsync(request, context, cancellationToken);
        return request.ToDto(canDecide, blocked);
    }

    public async Task<ApprovalRequestDto> CreateRequestAsync(Guid tenantId, CreateApprovalRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var entityName = Clean(request.EntityName);
        // A leave approval is the leave aggregate's routing projection (same id, balance reserved in
        // the same transaction). Starting one here would create a second, orphaned projection.
        if (string.Equals(entityName, nameof(LeaveRequest), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Leave approvals are started by submitting the leave request, not directly.");

        ApprovalWorkflow workflow;
        if (request.WorkflowId is { } explicitId && explicitId != Guid.Empty)
        {
            workflow = await _db.ApprovalWorkflows.Include(x => x.Steps).FirstOrDefaultAsync(x => x.Id == explicitId && x.TenantId == tenantId && x.IsActive, cancellationToken)
                ?? throw new InvalidOperationException("Approval workflow was not found or is inactive.");
            if (!string.Equals(workflow.EntityName, entityName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Approval workflow '{workflow.Code}' is for '{workflow.EntityName}', not '{entityName}'.");
        }
        else
        {
            // F1 — no explicit workflow: the ONE router picks it (department+grade → department →
            // grade → default). Nothing configured is a typed configuration error, never a guess.
            var route = await _router.ResolveAsync(tenantId, request.RequestedForEmployeeId, entityName, cancellationToken);
            workflow = await _db.ApprovalWorkflows.Include(x => x.Steps).FirstAsync(x => x.Id == route.WorkflowId && x.TenantId == tenantId, cancellationToken);
        }
        if (!workflow.Steps.Any()) throw new InvalidOperationException("Approval workflow has no steps.");
        var approval = new ApprovalRequest
        {
            TenantId = tenantId,
            WorkflowId = workflow.Id,
            EntityName = Clean(request.EntityName),
            EntityId = Clean(request.EntityId),
            Title = Clean(request.Title),
            CurrentStepOrder = workflow.Steps.Min(x => x.StepOrder),
            RequestedByUserId = context.UserId,
            RequestedForEmployeeId = request.RequestedForEmployeeId,
            CompanyId = request.CompanyId,
            Priority = string.IsNullOrWhiteSpace(request.Priority) ? "Normal" : Clean(request.Priority)
        };
        await RouteFirstStepAsync(approval, workflow, cancellationToken);
        _db.ApprovalRequests.Add(approval);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("approval.request_started", nameof(ApprovalRequest), approval.Id.ToString(), context, null, cancellationToken);
        return (await GetRequestAsync(tenantId, approval.Id, cancellationToken))!;
    }

    public async Task<ApprovalRequestDto?> DecideAsync(Guid tenantId, Guid approvalRequestId, ApprovalDecisionRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var approval = await _db.ApprovalRequests.Include(x => x.Decisions).FirstOrDefaultAsync(x => x.Id == approvalRequestId && x.TenantId == tenantId, cancellationToken);
        if (approval is null) return null;
        if (approval.Status != "Pending") throw new InvalidOperationException("Approval request is already completed.");

        var requestedDecision = Clean(request.Decision);
        if (!requestedDecision.Equals("Approve", StringComparison.OrdinalIgnoreCase) &&
            !requestedDecision.Equals("Reject", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Decision must be Approve or Reject.");
        if (context.UserId is not null && approval.RequestedByUserId == context.UserId)
            throw new InvalidOperationException("Maker-checker violation: requester cannot approve or reject their own approval request.");
        if (string.Equals(approval.EntityName, nameof(LeaveRequest), StringComparison.OrdinalIgnoreCase))
        {
            // LeaveRequest/LeaveApproval/balance is the aggregate of record. ApprovalRequest is
            // only its indexed routing projection, so dispatch before generic workflow lookup and
            // let LeaveService own the single relational transaction for decisions from either UI.
            if (!await IsRoutedDeciderAsync(approval, context, cancellationToken))
                throw new InvalidOperationException($"Current step requires approver role '{approval.CurrentApproverRole}'.");
            if (context.UserId is null)
                throw new InvalidOperationException("An authenticated approver is required.");
            if (!Guid.TryParse(approval.EntityId, out var leaveRequestId))
                throw new InvalidOperationException("The approval request is not linked to a valid leave request.");

            var approverName = await ResolveActorNameAsync(tenantId, context.UserId.Value, cancellationToken);

            if (requestedDecision.Equals("Reject", StringComparison.OrdinalIgnoreCase))
            {
                var reason = Clean(request.Comments);
                if (string.IsNullOrWhiteSpace(reason))
                    throw new InvalidOperationException("A rejection reason is required.");
                await _leaveService.RejectRequestAsync(tenantId, leaveRequestId, context.UserId.Value, approverName, reason, cancellationToken);
            }
            else
            {
                await _leaveService.ApproveRequestAsync(tenantId, leaveRequestId, context.UserId.Value, approverName, Clean(request.Comments), cancellationToken);
            }

            return await GetRequestAsync(tenantId, approvalRequestId, cancellationToken);
        }

        var step = await _db.ApprovalWorkflowSteps.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.WorkflowId == approval.WorkflowId && x.StepOrder == approval.CurrentStepOrder, cancellationToken)
            ?? throw new InvalidOperationException("Current approval step was not found.");
        if (approval.Decisions.Any(x => x.StepOrder == step.StepOrder && x.SubmissionRound == approval.SubmissionRound))
            throw new InvalidOperationException("Current approval step has already been decided.");
        if (!await CanDecideStepAsync(approval, step, context, cancellationToken))
            throw new InvalidOperationException($"Current step requires approver role '{step.ApproverRole}'.");
        // W2-E — tenant rule: a different person at each step. Applies to override holders too.
        await ApprovalGovernance.EnsureDistinctApproverAsync(_db, tenantId, approval.Id, approval.SubmissionRound, step.StepOrder, context.UserId, cancellationToken);

        var normalizedDecision = requestedDecision.Equals("Reject", StringComparison.OrdinalIgnoreCase) ? "Rejected" : "Approved";
        var approvalDecision = new ApprovalDecision
        {
            TenantId = tenantId,
            ApprovalRequestId = approval.Id,
            StepOrder = step.StepOrder,
            SubmissionRound = approval.SubmissionRound,
            Decision = normalizedDecision,
            Comments = Clean(request.Comments),
            DecidedByUserId = context.UserId
        };
        _db.Entry(approvalDecision).State = EntityState.Added;
        // Relational compare-and-swap. DecisionVersion is an EF concurrency token, so the UPDATE
        // carries `WHERE DecisionVersion = observedVersion`; every decision advances it. The
        // ApprovalDecision unique key is the immutable second line of defence.
        approval.DecisionVersion++;

        if (normalizedDecision == "Rejected")
        {
            approval.Status = "Rejected";
            approval.CompletedAtUtc = DateTime.UtcNow;
            await SyncEmployeeChangeDecisionAsync(approval, normalizedDecision, context, Clean(request.Comments), cancellationToken);
        }
        else if (step.IsFinalStep)
        {
            approval.Status = "Approved";
            approval.CompletedAtUtc = DateTime.UtcNow;
            await SyncEmployeeChangeDecisionAsync(approval, normalizedDecision, context, Clean(request.Comments), cancellationToken);
        }
        else
        {
            var nextStep = await _db.ApprovalWorkflowSteps.Where(x => x.TenantId == tenantId && x.WorkflowId == approval.WorkflowId && x.StepOrder > step.StepOrder).OrderBy(x => x.StepOrder).FirstOrDefaultAsync(cancellationToken);
            // F1 — only a step marked IsFinalStep completes a request. A non-final step with nothing
            // after it is a broken workflow; refuse the decision instead of approving by default.
            if (nextStep is null)
                throw new ApprovalRouteInvalidException(tenantId, approval.EntityName, approval.WorkflowId,
                    await _db.ApprovalWorkflows.Where(x => x.TenantId == tenantId && x.Id == approval.WorkflowId).Select(x => x.Code).FirstOrDefaultAsync(cancellationToken) ?? approval.WorkflowId.ToString(),
                    $"step {step.StepOrder} is not final and no step follows it.");
            approval.CurrentStepOrder = nextStep.StepOrder;
            await RouteCurrentStepAsync(approval, nextStep, cancellationToken);
        }

        try
        {
            // EF wraps this multi-entity batch in one relational transaction. A lost CAS or unique
            // decision conflict rolls back the decision row and every aggregate side effect together.
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new InvalidOperationException("Current approval step has already been decided.", ex);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new InvalidOperationException("Current approval step has already been decided.", ex);
        }
        // audit_logs.metadata is a `json` column — pass a JSON object, never a bare string (a bare
        // "Approved" is invalid JSON and would 500 AFTER the decision above already committed).
        await _audit.WriteAsync("approval.request_decided", nameof(ApprovalRequest), approval.Id.ToString(), context,
            JsonSerializer.Serialize(new { decision = normalizedDecision, stepOrder = step.StepOrder, comments = Clean(request.Comments) }), cancellationToken);
        return (await GetRequestAsync(tenantId, approval.Id, cancellationToken))!;
    }

    // ── W2-E: send back and resubmission ──────────────────────────────────────────────────────────

    public async Task<ApprovalRequestDto?> SendBackAsync(Guid tenantId, Guid approvalRequestId, SendBackApprovalRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var comments = Clean(request.Comments);
        if (comments.Length == 0)
            throw new InvalidOperationException("A comment explaining what to change is required.");
        if (comments.Length > 1000)
            throw new InvalidOperationException("Comments must be 1000 characters or fewer.");

        var approval = await _db.ApprovalRequests.Include(x => x.Decisions)
            .FirstOrDefaultAsync(x => x.Id == approvalRequestId && x.TenantId == tenantId, cancellationToken);
        if (approval is null) return null;
        if (approval.Status != ApprovalStatuses.Pending)
            throw new ApprovalStateConflictException($"Only a pending request can be sent back. This request is '{approval.Status}'.");
        if (context.UserId is null)
            throw new ApprovalNotPermittedException("An authenticated approver is required.");
        if (approval.RequestedByUserId == context.UserId)
            throw new ApprovalNotPermittedException("Maker-checker violation: the requester cannot send back their own request.");
        // Identical to /decisions: only the routed approver (or an override holder) may act on this step.
        if (!await IsRoutedDeciderAsync(approval, context, cancellationToken))
            throw new ApprovalNotPermittedException(
                $"Only the current approver can send this request back. Step {approval.CurrentStepOrder} is assigned to {OwnerLabel(approval)}.");
        await ApprovalGovernance.EnsureDistinctApproverAsync(_db, tenantId, approval.Id, approval.SubmissionRound,
            approval.CurrentStepOrder, context.UserId, cancellationToken);

        var stepOrder = approval.CurrentStepOrder;
        var (title, entityName, entityId, requestedBy, requestedFor) =
            (approval.Title, approval.EntityName, approval.EntityId, approval.RequestedByUserId, approval.RequestedForEmployeeId);

        if (IsLeave(approval))
        {
            // The leave aggregate owns the step, the decision row, the projection and the balance in ONE transaction.
            if (!Guid.TryParse(approval.EntityId, out var leaveRequestId))
                throw new InvalidOperationException("The approval request is not linked to a valid leave request.");
            var approverName = await ResolveActorNameAsync(tenantId, context.UserId.Value, cancellationToken);
            await _leaveService.SendBackRequestAsync(tenantId, leaveRequestId, context.UserId.Value, approverName, comments, cancellationToken);
        }
        else
        {
            if (approval.Decisions.Any(x => x.StepOrder == stepOrder && x.SubmissionRound == approval.SubmissionRound))
                throw new ApprovalStateConflictException("Current approval step has already been decided.");
            _db.Entry(new ApprovalDecision
            {
                TenantId = tenantId,
                ApprovalRequestId = approval.Id,
                StepOrder = stepOrder,
                SubmissionRound = approval.SubmissionRound,
                Decision = ApprovalStatuses.SentBackDecision,
                Comments = comments,
                DecidedByUserId = context.UserId
            }).State = EntityState.Added;
            approval.DecisionVersion++;
            approval.Status = ApprovalStatuses.ReturnedToRequester;
            approval.CompletedAtUtc = null;
            ClearCurrentApprover(approval);
            await SyncSourceStatusAsync(approval, "PendingApproval", ApprovalStatuses.ReturnedToRequester, cancellationToken);
            await SaveDecisionAsync(cancellationToken);
        }

        await _audit.WriteAsync("approval.request_sent_back", nameof(ApprovalRequest), approvalRequestId.ToString(), context,
            JsonSerializer.Serialize(new { decision = ApprovalStatuses.SentBackDecision, stepOrder, comments }), cancellationToken);
        await NotifyRequesterSentBackAsync(tenantId, approvalRequestId, title, entityName, entityId, requestedBy, requestedFor, stepOrder, comments, cancellationToken);
        return await GetRequestAsync(tenantId, approvalRequestId, cancellationToken);
    }

    public async Task<ApprovalRequestDto?> ResubmitAsync(Guid tenantId, Guid approvalRequestId, ResubmitApprovalRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var approval = await _db.ApprovalRequests.Include(x => x.Decisions)
            .FirstOrDefaultAsync(x => x.Id == approvalRequestId && x.TenantId == tenantId, cancellationToken);
        if (approval is null) return null;
        if (!await IsRequesterAsync(approval, context, cancellationToken))
            throw new ApprovalNotPermittedException("Only the person who submitted this request can resubmit it.");
        if (approval.Status != ApprovalStatuses.ReturnedToRequester)
            throw new ApprovalStateConflictException($"Only a request that was sent back can be resubmitted. This request is '{approval.Status}'.");

        var comments = Clean(request.Comments);
        if (IsLeave(approval))
        {
            if (!Guid.TryParse(approval.EntityId, out var leaveRequestId))
                throw new InvalidOperationException("The approval request is not linked to a valid leave request.");
            var actor = await ResolveActorNameAsync(tenantId, context.UserId!.Value, cancellationToken);
            await _leaveService.ResubmitRequestAsync(tenantId, leaveRequestId, actor, request.Leave, comments, cancellationToken);
        }
        else
        {
            if (request.Leave is not null)
                throw new InvalidOperationException("Leave changes can only be sent with a leave request.");
            // In-flight requests keep the workflow they were routed by; the chain restarts at its first step.
            var workflow = await _db.ApprovalWorkflows.Include(x => x.Steps)
                .FirstOrDefaultAsync(x => x.Id == approval.WorkflowId && x.TenantId == tenantId, cancellationToken)
                ?? throw new InvalidOperationException("The workflow this request was routed by no longer exists.");
            if (!workflow.Steps.Any()) throw new InvalidOperationException("Approval workflow has no steps.");
            approval.SubmissionRound++;
            approval.Status = ApprovalStatuses.Pending;
            approval.CompletedAtUtc = null;
            approval.DecisionVersion++;
            await RouteFirstStepAsync(approval, workflow, cancellationToken);
            await SyncSourceStatusAsync(approval, ApprovalStatuses.ReturnedToRequester, "PendingApproval", cancellationToken);
            await SaveDecisionAsync(cancellationToken);
        }

        await _audit.WriteAsync("approval.request_resubmitted", nameof(ApprovalRequest), approvalRequestId.ToString(), context,
            JsonSerializer.Serialize(new { comments }), cancellationToken);
        return await GetRequestAsync(tenantId, approvalRequestId, cancellationToken);
    }

    private static bool IsLeave(ApprovalRequest approval)
        => string.Equals(approval.EntityName, nameof(LeaveRequest), StringComparison.OrdinalIgnoreCase);

    private static string OwnerLabel(ApprovalRequest approval)
        => !string.IsNullOrWhiteSpace(approval.CurrentApproverName) ? approval.CurrentApproverName
            : !string.IsNullOrWhiteSpace(approval.CurrentApproverRole) ? $"the {approval.CurrentApproverRole} queue"
            : "another approver";

    private static void ClearCurrentApprover(ApprovalRequest approval)
    {
        approval.CurrentApproverEmployeeId = null;
        approval.CurrentApproverUserId = null;
        approval.CurrentApproverName = string.Empty;
        approval.CurrentQueue = string.Empty;
        approval.DueAtUtc = null;
    }

    private async Task SaveDecisionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // DecisionVersion compare-and-swap lost: someone else decided this step first.
            throw new ApprovalStateConflictException("This request was changed by someone else at the same time. Reload it and try again.");
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new ApprovalStateConflictException("Current approval step has already been decided.");
        }
    }

    /// <summary>Moves the source record between its pending and returned states, for the sources the engine owns.</summary>
    private async Task SyncSourceStatusAsync(ApprovalRequest approval, string fromStatus, string toStatus, CancellationToken cancellationToken)
    {
        if (!string.Equals(approval.EntityName, nameof(EmployeeChangeRequest), StringComparison.OrdinalIgnoreCase)) return;
        if (!Guid.TryParse(approval.EntityId, out var changeId)) return;
        var change = await _db.EmployeeChangeRequests.FirstOrDefaultAsync(x => x.TenantId == approval.TenantId && x.Id == changeId, cancellationToken);
        if (change is not null && string.Equals(change.Status, fromStatus, StringComparison.OrdinalIgnoreCase))
            change.Status = toStatus;
    }

    private async Task<bool> IsRequesterAsync(ApprovalRequest approval, RequestContext context, CancellationToken cancellationToken)
    {
        if (context.UserId is null) return false;
        if (approval.RequestedByUserId == context.UserId) return true;
        // A leave request belongs to its employee even when HR keyed it in for them.
        if (!IsLeave(approval) && approval.RequestedByUserId is not null) return false;
        var callerEmployeeId = await ResolveCallerEmployeeIdAsync(approval.TenantId, context.UserId, cancellationToken);
        return callerEmployeeId is not null && callerEmployeeId == approval.RequestedForEmployeeId;
    }

    private async Task<string> ResolveActorNameAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
        => await _db.Employees.AsNoTracking()
               .Where(x => x.TenantId == tenantId && x.UserAccountId == userId && !x.IsDeleted)
               .Select(x => x.FullName)
               .FirstOrDefaultAsync(cancellationToken)
           ?? await _db.Users.AsNoTracking()
               .Where(x => x.TenantId == tenantId && x.Id == userId && !x.IsDeleted)
               .Select(x => x.FullName)
               .FirstOrDefaultAsync(cancellationToken)
           ?? userId.ToString();

    private async Task NotifyRequesterSentBackAsync(Guid tenantId, Guid approvalRequestId, string title, string entityName, string entityId,
        Guid? requestedByUserId, int? requestedForEmployeeId, int stepOrder, string comments, CancellationToken cancellationToken)
    {
        if (_notifications is null) return;
        try
        {
            await _notifications.EnqueueAsync(new NotificationRequest
            {
                TenantId = tenantId,
                UserId = requestedByUserId,
                EmployeeId = requestedByUserId is null ? requestedForEmployeeId : null,
                EventCode = ApprovalStatuses.SentBackEventCode,
                EntityName = entityName,
                EntityId = entityId,
                Title = "Request sent back for changes",
                Message = $"\"{title}\" was sent back at step {stepOrder}: {comments}",
                Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["title"] = title,
                    ["comments"] = comments,
                    ["stepOrder"] = stepOrder.ToString(),
                    ["approvalRequestId"] = approvalRequestId.ToString(),
                },
            }, cancellationToken);
        }
        catch (Exception)
        {
            // Enqueue is contractually non-throwing; if an implementation does throw, the send back is
            // already committed and must not be reported as failed.
        }
    }

    // ── W2-E: workflow configuration ─────────────────────────────────────────────────────────────

    /// <summary>Entity types the approval router can route, with who applies each today.</summary>
    public static readonly IReadOnlyList<ApprovalEntityDescriptor> KnownEntities =
    [
        new(nameof(LeaveRequest), "Leave requests", true,
            "Every leave submission is routed by the workflow that applies to the employee."),
        new(nameof(EmployeeChangeRequest), "Sensitive employee changes", true,
            "Uses the workflow with code EMPLOYEE-CHANGE. The People module resets it to Manager then HR Manager if it is not a two-step Manager + Role chain."),
        new("ManpowerRequisition", "Manpower requisitions", true,
            "Submitting a requisition starts the tenant-wide workflow when one exists. Requisitions have no employee, so department and grade scopes never apply."),
        new("OvertimeRequest", "Overtime requests", false,
            "Stored but not yet applied: the Overtime module still runs its own manager-then-HR approval."),
        new("PayrollRun", "Payroll runs", false,
            "Stored but not yet applied: the Payroll module still runs its own approval stages."),
    ];

    public async Task<IReadOnlyList<ApprovalEntityDescriptor>> GetEntitiesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var configured = await _db.ApprovalWorkflows.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => x.EntityName)
            .Distinct()
            .ToListAsync(cancellationToken);
        var result = KnownEntities.ToList();
        foreach (var name in configured.Where(n => !string.IsNullOrWhiteSpace(n)).OrderBy(n => n))
            if (result.All(e => !string.Equals(e.EntityName, name, StringComparison.OrdinalIgnoreCase)))
                result.Add(new ApprovalEntityDescriptor(name, name, false,
                    "Routed only when a module or integration starts an approval for this entity through the approvals API."));
        return result;
    }

    public async Task<ApprovalWorkflowDto?> SetWorkflowActiveAsync(Guid tenantId, Guid id, bool active, RequestContext context, CancellationToken cancellationToken)
    {
        var workflow = await _db.ApprovalWorkflows.Include(x => x.Steps).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (workflow is null) return null;
        if (workflow.IsActive != active)
        {
            if (active)
            {
                // Re-activating must not create the overlap the create/update rules refuse.
                await EnsureScopeUnambiguousAsync(tenantId, new ApprovalWorkflowRequest(workflow.Code, workflow.Name, workflow.EntityName, true,
                    workflow.Steps.Select(x => new ApprovalWorkflowStepRequest(x.StepOrder, x.StepName, x.ApproverRole, x.ApproverType,
                        x.SpecificEmployeeId, x.EscalationAfterHours, x.IsFinalStep)).ToList(),
                    workflow.DepartmentId, workflow.GradeId, workflow.IsDefault), id, cancellationToken);
            }
            workflow.IsActive = active;
            await _db.SaveChangesAsync(cancellationToken);
            await _audit.WriteAsync(active ? "approval.workflow_activated" : "approval.workflow_deactivated",
                nameof(ApprovalWorkflow), workflow.Id.ToString(), context, null, cancellationToken);
        }
        return (await GetWorkflowAsync(tenantId, id, cancellationToken))!;
    }

    /// <summary>
    /// "For this employee, which workflow applies and who approves each step" — answered by the SAME
    /// router calls a submission makes (<see cref="IApprovalRouter.TryResolveAsync"/> then
    /// <see cref="IApprovalRouter.ResolveApproverAsync"/> per step), never a second implementation.
    /// </summary>
    public async Task<ApprovalRoutePreviewDto> PreviewRouteAsync(Guid tenantId, string entityName, int? employeeId, CancellationToken cancellationToken)
    {
        var entity = Clean(entityName);
        if (entity.Length == 0) throw new InvalidOperationException("Choose what kind of request to preview.");
        var employeeName = string.Empty;
        if (employeeId.HasValue)
        {
            employeeName = await _db.Employees.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.Id == employeeId.Value && !e.IsDeleted)
                .Select(e => e.FullName)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException($"Employee {employeeId.Value} was not found.");
        }
        var distinct = await DistinctRuleOnAsync(tenantId, cancellationToken);

        ApprovalRoute? route;
        try
        {
            route = await _router.TryResolveAsync(tenantId, employeeId, entity, cancellationToken);
        }
        catch (ApprovalRouteInvalidException ex)
        {
            return new ApprovalRoutePreviewDto(entity, employeeId, employeeName, "Invalid", ex.Code, ex.Message,
                ex.WorkflowId, null, null, null, distinct, [], []);
        }
        if (route is null)
        {
            var notConfigured = new ApprovalRouteNotConfiguredException(tenantId, entity, employeeId);
            return new ApprovalRoutePreviewDto(entity, employeeId, employeeName, "NotConfigured", notConfigured.Code, notConfigured.Message,
                null, null, null, null, distinct, [], []);
        }

        var steps = new List<ApprovalRoutePreviewStepDto>();
        var warnings = new List<string>();
        var finalReached = false;
        foreach (var step in route.Steps)
        {
            var approver = await _router.ResolveApproverAsync(tenantId, employeeId, step, cancellationToken);
            steps.Add(new ApprovalRoutePreviewStepDto(step.StepOrder, step.StepName, step.ApproverType, step.ApproverRole, step.IsFinalStep,
                step.EscalationAfterHours, approver.QueueRole, approver.EmployeeId, approver.UserId, approver.Name, approver.Escalated));
            if (finalReached)
                warnings.Add($"Step {step.StepOrder} comes after the final step and will never be reached.");
            if (approver.Escalated)
                warnings.Add(employeeId.HasValue
                    ? $"Step {step.StepOrder} ({step.ApproverType}) has nobody to resolve to for {employeeName}; it will go to the {approver.QueueRole} queue."
                    : $"Step {step.StepOrder} ({step.ApproverType}) depends on the employee. Pick an employee to see who it resolves to.");
            finalReached |= step.IsFinalStep;
        }
        if (distinct)
        {
            foreach (var group in steps.Where(x => x.ApproverEmployeeId is not null).GroupBy(x => x.ApproverEmployeeId).Where(g => g.Count() > 1))
            {
                var orders = group.Select(x => x.StepOrder).OrderBy(x => x).ToList();
                warnings.Add($"Steps {string.Join(" and ", orders)} both resolve to {group.First().ApproverName}. With the different-person rule on, " +
                             $"they can decide step {orders[0]} only; later steps will wait for another eligible approver.");
            }
        }
        return new ApprovalRoutePreviewDto(entity, employeeId, employeeName, "Routed", null, null,
            route.WorkflowId, route.Code, route.Name, route.MatchedOn, distinct, steps, warnings);
    }

    public Task<ApprovalGovernanceSettingsDto> GetGovernanceSettingsAsync(Guid tenantId, CancellationToken cancellationToken)
        => ApprovalGovernance.GetSettingsAsync(_db, tenantId, cancellationToken);

    public async Task<ApprovalGovernanceSettingsDto> SaveGovernanceSettingsAsync(Guid tenantId, ApprovalGovernanceSettingsDto settings, RequestContext context, CancellationToken cancellationToken)
    {
        var saved = await ApprovalGovernance.SaveSettingsAsync(_db, tenantId, settings, context.UserId, cancellationToken);
        _distinctRuleCache.Remove(tenantId);
        await _audit.WriteAsync("approval.settings_updated", "ApprovalSettings", tenantId.ToString(), context,
            JsonSerializer.Serialize(new { requireDistinctApproverPerStep = settings.RequireDistinctApproverPerStep }), cancellationToken);
        return saved;
    }

    private static readonly HashSet<string> SupportedApproverTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // The six the configuration screen offers …
        "Manager", "Supervisor", "DepartmentHead", "SpecificEmployee", "HR", "Role",
        // … and the other types the router already resolves, so existing API-created workflows stay valid.
        "DirectManager", "HRBusinessPartner", "SeniorManager", "SecondLevelManager", "CompanyHead",
    };

    /// <summary>
    /// W2-E — the chain rules the configuration screen enforces: unique step orders, exactly one final
    /// step and it is the last, a supported approver type, a role for Role steps and a real employee for
    /// SpecificEmployee steps. Errors carry a stable code the screen shows next to the step.
    /// </summary>
    private async Task ValidateStepsAsync(Guid tenantId, ApprovalWorkflowRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.EntityName))
            throw new ApprovalWorkflowValidationException(ApprovalWorkflowValidationException.EntityRequired, "Choose which kind of request this workflow approves.");
        var steps = (request.Steps ?? Array.Empty<ApprovalWorkflowStepRequest>()).OrderBy(x => x.StepOrder).ToList();
        if (steps.Count == 0)
            throw new ApprovalWorkflowValidationException(ApprovalWorkflowValidationException.NoFinalStep, "A workflow needs at least one step.");
        var duplicate = steps.GroupBy(x => x.StepOrder).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ApprovalWorkflowValidationException(ApprovalWorkflowValidationException.DuplicateStepOrder, $"Two steps share position {duplicate.Key}. Each step needs its own position.");
        var finals = steps.Where(x => x.IsFinalStep).ToList();
        if (finals.Count == 0)
            throw new ApprovalWorkflowValidationException(ApprovalWorkflowValidationException.NoFinalStep,
                "Mark the last step as the final step. Without a final step no request could ever be approved.");
        if (finals.Count > 1)
            throw new ApprovalWorkflowValidationException(ApprovalWorkflowValidationException.MultipleFinalSteps,
                $"Only one step can be final, but steps {string.Join(", ", finals.Select(x => x.StepOrder))} are marked final.");
        if (finals[0].StepOrder != steps[^1].StepOrder)
            throw new ApprovalWorkflowValidationException(ApprovalWorkflowValidationException.FinalStepNotLast,
                $"The final step must be the last one. Step {finals[0].StepOrder} is marked final but step {steps[^1].StepOrder} comes after it.");
        foreach (var step in steps)
        {
            var type = string.IsNullOrWhiteSpace(step.ApproverType) ? "Role" : step.ApproverType.Trim();
            if (!SupportedApproverTypes.Contains(type))
                throw new ApprovalWorkflowValidationException(ApprovalWorkflowValidationException.InvalidApproverType,
                    $"Step {step.StepOrder} has an approver type '{type}' that cannot be routed.");
            if (type.Equals("Role", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(step.ApproverRole))
                throw new ApprovalWorkflowValidationException(ApprovalWorkflowValidationException.RoleRequired, $"Step {step.StepOrder} needs a role.");
            if (type.Equals("SpecificEmployee", StringComparison.OrdinalIgnoreCase))
            {
                if (step.SpecificEmployeeId is null)
                    throw new ApprovalWorkflowValidationException(ApprovalWorkflowValidationException.SpecificEmployeeRequired, $"Step {step.StepOrder} needs an employee.");
                if (!await _db.Employees.AnyAsync(e => e.TenantId == tenantId && e.Id == step.SpecificEmployeeId.Value && !e.IsDeleted, cancellationToken))
                    throw new ApprovalWorkflowValidationException(ApprovalWorkflowValidationException.SpecificEmployeeRequired,
                        $"Step {step.StepOrder}'s employee was not found in this tenant.");
            }
        }
    }

    private static void Apply(ApprovalWorkflow workflow, ApprovalWorkflowRequest request, Guid tenantId)
    {
        workflow.Code = Clean(request.Code).ToUpperInvariant();
        workflow.Name = Clean(request.Name);
        workflow.EntityName = Clean(request.EntityName);
        workflow.IsActive = request.IsActive;
        workflow.DepartmentId = request.DepartmentId;
        workflow.GradeId = request.GradeId;
        workflow.IsDefault = request.IsDefault;
        workflow.Steps.Clear();
        var steps = request.Steps.OrderBy(x => x.StepOrder).ToList();
        for (var i = 0; i < steps.Count; i++)
        {
            workflow.Steps.Add(new ApprovalWorkflowStep
            {
                TenantId = tenantId,
                WorkflowId = workflow.Id,
                StepOrder = steps[i].StepOrder,
                StepName = Clean(steps[i].StepName),
                ApproverRole = Clean(steps[i].ApproverRole),
                IsFinalStep = steps[i].IsFinalStep || i == steps.Count - 1
                ,
                ApproverType = string.IsNullOrWhiteSpace(steps[i].ApproverType) ? "Role" : Clean(steps[i].ApproverType!),
                SpecificEmployeeId = steps[i].SpecificEmployeeId,
                EscalationAfterHours = steps[i].EscalationAfterHours
            });
        }
    }

    /// <summary>
    /// Routes a request to the first step of its workflow. A person-type first step that escalated to the
    /// HR Manager queue jumps to the chain's HR Manager step (pre-existing behaviour of the generic start,
    /// shared with W2-E resubmission so a restarted chain starts exactly like a new one).
    /// </summary>
    private async Task RouteFirstStepAsync(ApprovalRequest approval, ApprovalWorkflow workflow, CancellationToken cancellationToken)
    {
        approval.CurrentStepOrder = workflow.Steps.Min(x => x.StepOrder);
        var firstStep = workflow.Steps.First(x => x.StepOrder == approval.CurrentStepOrder);
        await RouteCurrentStepAsync(approval, firstStep, cancellationToken);
        if (!string.Equals(firstStep.ApproverType, "Role", StringComparison.OrdinalIgnoreCase)
            && approval.CurrentApproverEmployeeId is null
            && string.Equals(approval.CurrentApproverRole, "HR Manager", StringComparison.OrdinalIgnoreCase))
        {
            var hrStep = workflow.Steps
                .Where(x => x.StepOrder > firstStep.StepOrder && string.Equals(x.ApproverRole, "HR Manager", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.StepOrder)
                .FirstOrDefault();
            if (hrStep is not null)
            {
                approval.CurrentStepOrder = hrStep.StepOrder;
                await RouteCurrentStepAsync(approval, hrStep, cancellationToken);
            }
        }
    }

    private async Task RouteCurrentStepAsync(ApprovalRequest approval, ApprovalWorkflowStep step, CancellationToken cancellationToken)
    {
        approval.CurrentApproverEmployeeId = null;
        approval.CurrentApproverUserId = null;
        approval.CurrentApproverName = string.Empty;
        approval.CurrentApproverRole = Clean(step.ApproverRole);
        approval.CurrentApproverType = string.IsNullOrWhiteSpace(step.ApproverType) ? "Role" : Clean(step.ApproverType);
        approval.CurrentQueue = BuildQueueName(approval.CurrentApproverType, approval.CurrentApproverRole);
        approval.SlaHours = Math.Clamp(step.EscalationAfterHours ?? DefaultSlaHours(approval.EntityName, approval.CurrentApproverType), 1, 720);
        approval.DueAtUtc = DateTime.UtcNow.AddHours(approval.SlaHours);
        approval.LastRoutedAtUtc = DateTime.UtcNow;
        approval.EscalatedAtUtc = null;
        approval.EscalatedToRole = string.Empty;

        var subjectEmployeeId = approval.RequestedForEmployeeId ?? await ResolveSubjectEmployeeIdAsync(approval, cancellationToken);
        approval.RequestedForEmployeeId ??= subjectEmployeeId;
        if (approval.CompanyId is null && subjectEmployeeId is not null)
        {
            approval.CompanyId = await _db.Employees.AsNoTracking()
                .Where(x => x.TenantId == approval.TenantId && x.Id == subjectEmployeeId.Value)
                .Select(x => x.CompanyId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        // F1 — approver resolution is the router's, shared with the leave aggregate.
        var approver = await _router.ResolveApproverAsync(approval.TenantId, subjectEmployeeId, ToRouteStep(step), cancellationToken);
        if (approver.EmployeeId is null)
        {
            if (approver.Escalated || !string.Equals(approval.CurrentApproverType, "Role", StringComparison.OrdinalIgnoreCase))
            {
                approval.CurrentApproverType = "Role";
                approval.CurrentApproverRole = approver.QueueRole;
                approval.CurrentQueue = $"Role:{approver.QueueRole}";
                if (approver.Escalated) approval.EscalatedToRole = approver.QueueRole;
            }
            return;
        }

        approval.CurrentApproverEmployeeId = approver.EmployeeId;
        approval.CurrentApproverUserId = approver.UserId;
        approval.CurrentApproverName = approver.Name;
        if (string.IsNullOrWhiteSpace(approval.CurrentApproverRole)) approval.CurrentApproverRole = approver.QueueRole;
        approval.CurrentQueue = $"{approval.CurrentApproverType}:{approver.Name}";
    }

    private static ApprovalRouteStep ToRouteStep(ApprovalWorkflowStep step)
        => new(step.StepOrder, step.StepName,
            string.IsNullOrWhiteSpace(step.ApproverType) ? "Role" : step.ApproverType.Trim(),
            step.ApproverRole ?? string.Empty, step.SpecificEmployeeId, step.EscalationAfterHours, step.IsFinalStep);

    private async Task<int?> ResolveSubjectEmployeeIdAsync(ApprovalRequest approval, CancellationToken cancellationToken)
    {
        if (string.Equals(approval.EntityName, nameof(EmployeeChangeRequest), StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(approval.EntityId, out var changeId))
        {
            return await _db.EmployeeChangeRequests.AsNoTracking()
                .Where(x => x.TenantId == approval.TenantId && x.Id == changeId)
                .Select(x => (int?)x.EmployeeId)
                .FirstOrDefaultAsync(cancellationToken);
        }
        return null;
    }

    private async Task<int?> ResolveCallerEmployeeIdAsync(Guid tenantId, Guid? userId, CancellationToken cancellationToken)
    {
        if (userId is null) return null;
        return await _db.Employees.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.UserAccountId == userId && !x.IsDeleted)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<IReadOnlyCollection<int>> ResolveTeamEmployeeIdsAsync(Guid tenantId, int managerEmployeeId, CancellationToken cancellationToken)
    {
        var all = await _db.Employees.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => new { x.Id, x.ManagerEmployeeId })
            .ToListAsync(cancellationToken);
        var result = new HashSet<int>();
        var frontier = new Queue<int>();
        frontier.Enqueue(managerEmployeeId);
        var depth = 0;
        while (frontier.Count > 0 && depth++ < 20)
        {
            var current = frontier.Dequeue();
            foreach (var child in all.Where(x => x.ManagerEmployeeId == current && result.Add(x.Id)))
                frontier.Enqueue(child.Id);
        }
        return result;
    }

    private static string BuildQueueName(string approverType, string approverRole)
        => string.Equals(approverType, "Role", StringComparison.OrdinalIgnoreCase)
            ? $"Role:{(string.IsNullOrWhiteSpace(approverRole) ? "Any" : approverRole)}"
            : approverType;

    private static int DefaultSlaHours(string entityName, string approverType)
    {
        if (entityName.Contains("Payroll", StringComparison.OrdinalIgnoreCase)) return 12;
        if (approverType.Contains("HR", StringComparison.OrdinalIgnoreCase)) return 48;
        return 24;
    }

    private async Task EnsureWorkflowCodeUnique(Guid tenantId, string code, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = Clean(code).ToUpperInvariant();
        var exists = await _db.ApprovalWorkflows.AnyAsync(x => x.TenantId == tenantId && x.Code == clean && x.Id != excludedId, cancellationToken);
        if (exists) throw new ApprovalWorkflowValidationException(ApprovalWorkflowValidationException.CodeTaken, "Approval workflow code already exists in this tenant.");
    }

    /// <summary>
    /// F1 — the router needs an unambiguous answer. Two ACTIVE workflows for the same entity and the
    /// same org scope (department, grade) would leave the choice to a tie-break, so a new one is
    /// refused. A scoped workflow cannot be the tenant default.
    /// </summary>
    private async Task EnsureScopeUnambiguousAsync(Guid tenantId, ApprovalWorkflowRequest request, Guid? excludedId, CancellationToken cancellationToken)
    {
        if (request.IsDefault && (request.DepartmentId.HasValue || request.GradeId.HasValue))
            throw new ApprovalWorkflowValidationException(ApprovalWorkflowValidationException.ScopeInvalid, "A workflow scoped to a department or grade cannot be the tenant default.");
        if (request.DepartmentId.HasValue && !await _db.Departments.AnyAsync(d => d.TenantId == tenantId && d.Id == request.DepartmentId.Value, cancellationToken))
            throw new ApprovalWorkflowValidationException(ApprovalWorkflowValidationException.ScopeInvalid, "The workflow's department was not found in this tenant.");
        // W2-E — the grade had no existence check, so a mistyped id created a workflow no employee could ever match.
        if (request.GradeId.HasValue && !await _db.Grades.AnyAsync(g => g.TenantId == tenantId && g.Id == request.GradeId.Value, cancellationToken))
            throw new ApprovalWorkflowValidationException(ApprovalWorkflowValidationException.ScopeInvalid, "The workflow's grade was not found in this tenant.");
        if (!request.IsActive) return;

        var entity = Clean(request.EntityName);
        var clash = await _db.ApprovalWorkflows.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsActive && x.EntityName == entity && x.Id != excludedId
                && x.DepartmentId == request.DepartmentId && x.GradeId == request.GradeId)
            .OrderBy(x => x.Code)
            .Select(x => x.Code)
            .FirstOrDefaultAsync(cancellationToken);
        if (clash is not null)
            throw new ApprovalWorkflowValidationException(ApprovalWorkflowValidationException.ScopeOverlap,
                $"Active approval workflow '{clash}' already covers '{entity}' for this department/grade scope. Deactivate it or change the scope.");
    }

    private async Task<bool> CanDecideStepAsync(ApprovalRequest approval, ApprovalWorkflowStep step, RequestContext context, CancellationToken cancellationToken)
    {
        // Routing eligibility only; the distinct-approver rule is raised separately as its own error.
        return await IsRoutedDeciderAsync(approval, context, cancellationToken);
    }

    /// <summary>
    /// Whether the caller may decide the current step: routed eligibility AND (W2-E) the tenant's
    /// distinct-approver rule. With the rule off this is exactly the pre-W2-E check.
    /// </summary>
    private async Task<bool> CanDecideRequestAsync(ApprovalRequest approval, RequestContext? context, CancellationToken cancellationToken)
        => (await EvaluateDecisionAsync(approval, context, cancellationToken)).CanDecide;

    private async Task<(bool CanDecide, string? BlockedReason)> EvaluateDecisionAsync(ApprovalRequest approval, RequestContext? context, CancellationToken cancellationToken)
    {
        if (!await IsRoutedDeciderAsync(approval, context, cancellationToken)) return (false, null);
        if (!await DistinctRuleOnAsync(approval.TenantId, cancellationToken)) return (true, null);
        var earlier = approval.Decisions
            .Where(d => d.SubmissionRound == approval.SubmissionRound && d.StepOrder < approval.CurrentStepOrder && d.DecidedByUserId == context!.UserId && context.UserId != null)
            .OrderBy(d => d.StepOrder)
            .Select(d => (int?)d.StepOrder)
            .FirstOrDefault();
        return earlier is null
            ? (true, null)
            : (false, new ApprovalDistinctApproverException(earlier.Value, approval.CurrentStepOrder).Message);
    }

    private async Task<bool> DistinctRuleOnAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_distinctRuleCache.TryGetValue(tenantId, out var on)) return on;
        on = await ApprovalGovernance.RequiresDistinctApproverAsync(_db, tenantId, cancellationToken);
        _distinctRuleCache[tenantId] = on;
        return on;
    }

    /// <summary>The routing check: maker-checker, then override, then the routed person / role. Unchanged from develop.</summary>
    private async Task<bool> IsRoutedDeciderAsync(ApprovalRequest approval, RequestContext? context, CancellationToken cancellationToken)
    {
        if (context is null || approval.Status != "Pending") return false;
        if (context.UserId is not null && approval.RequestedByUserId == context.UserId) return false;

        var permissions = context.Permissions ?? Array.Empty<string>();
        if (permissions.Any(x => x.Equals("approvals.override", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (approval.CurrentApproverUserId is not null)
            return context.UserId == approval.CurrentApproverUserId;

        if (approval.CurrentApproverEmployeeId is not null)
        {
            var callerEmployeeId = await ResolveCallerEmployeeIdAsync(approval.TenantId, context.UserId, cancellationToken);
            return callerEmployeeId == approval.CurrentApproverEmployeeId;
        }

        var requiredRole = Clean(approval.CurrentApproverRole);
        if (string.IsNullOrWhiteSpace(requiredRole) || requiredRole.Equals("Any", StringComparison.OrdinalIgnoreCase))
            return true;
        var roles = context.Roles ?? Array.Empty<string>();
        return roles.Any(x => x.Equals(requiredRole, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> CanViewRequestAsync(ApprovalRequest approval, RequestContext context, CancellationToken cancellationToken)
    {
        if (CanViewAllApprovalRequests(context)) return true;
        if (await IsRoutedDeciderAsync(approval, context, cancellationToken)) return true;
        if (context.UserId is not null && approval.RequestedByUserId == context.UserId) return true;

        var callerEmployeeId = await ResolveCallerEmployeeIdAsync(approval.TenantId, context.UserId, cancellationToken);
        if (callerEmployeeId is null || approval.RequestedForEmployeeId is null) return false;
        var teamIds = await ResolveTeamEmployeeIdsAsync(approval.TenantId, callerEmployeeId.Value, cancellationToken);
        return teamIds.Contains(approval.RequestedForEmployeeId.Value);
    }

    private static bool CanViewAllApprovalRequests(RequestContext context)
    {
        var roles = context.Roles ?? Array.Empty<string>();
        var permissions = context.Permissions ?? Array.Empty<string>();
        return roles.Any(x => x.Equals("Admin", StringComparison.OrdinalIgnoreCase)
                              || x.Equals("HR Manager", StringComparison.OrdinalIgnoreCase)
                              || x.Equals("Auditor", StringComparison.OrdinalIgnoreCase))
               || permissions.Any(x => x.Equals("approvals.override", StringComparison.OrdinalIgnoreCase));
    }

    private async Task SyncEmployeeChangeDecisionAsync(ApprovalRequest approval, string decision, RequestContext context, string comments, CancellationToken cancellationToken)
    {
        if (!string.Equals(approval.EntityName, nameof(EmployeeChangeRequest), StringComparison.OrdinalIgnoreCase)) return;
        if (!Guid.TryParse(approval.EntityId, out var changeId)) return;

        var change = await _db.EmployeeChangeRequests.FirstOrDefaultAsync(x => x.TenantId == approval.TenantId && x.Id == changeId, cancellationToken);
        if (change is null || !string.Equals(change.Status, "PendingApproval", StringComparison.OrdinalIgnoreCase)) return;

        if (decision == "Rejected")
        {
            change.Status = "Rejected";
            change.RejectionReason = comments;
            change.ApprovedByUserId = context.UserId;
            change.ApprovedAtUtc = DateTime.UtcNow;
            return;
        }

        var approverId = context.UserId;
        if (approverId is not null && change.RequestedByUserId == approverId)
            throw new InvalidOperationException("Maker-checker violation: requester cannot approve their own sensitive change.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        change.ApprovedByUserId = approverId;
        change.ApprovedAtUtc = DateTime.UtcNow;
        if (change.EffectiveDate > today)
        {
            change.Status = "ApprovedPendingEffectiveDate";
            return;
        }

        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == approval.TenantId && x.Id == change.EmployeeId && !x.IsDeleted, cancellationToken);
        if (employee is null) throw new InvalidOperationException("Employee for this change request was not found.");
        var changes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(change.ProposedChangesJson) ?? new();
        var priorDeptId = employee.DepartmentId;
        var priorDesigId = employee.DesignationId;
        ApplyEmployeeChange(employee, changes);
        // Same shared resolver as EmployeesController.ApplyChanges (consultant R-B): free-text
        // department/designation/branch changes resolve to IDs (unresolvable ⇒ throws, surfaced
        // to the decider) — this duplicate apply path can no longer manufacture string-only rows.
        await Zayra.Api.Application.Employees.EmployeeOrgFieldResolver
            .ResolveAppliedChangesAsync(_db, approval.TenantId, employee, changes.Keys, cancellationToken);
        // ESTABLISHMENT GUARD (path "approval" via generic decide): authoritative re-check at
        // apply. A block throws BEFORE DecideAsync saves anything, so the approval request stays
        // Pending and the change stays PendingApproval — re-approve after a budget raise. The 409
        // contract is rendered by ApprovalRequestsController's catch site.
        if (employee.DepartmentId != priorDeptId || employee.DesignationId != priorDesigId)
        {
            await _establishmentGuard.EnforceAsync(approval.TenantId, employee.DepartmentId, employee.DesignationId,
                excludeEmployeeId: employee.Id, path: "approval", context, cancellationToken);
        }
        employee.UpdatedAtUtc = DateTime.UtcNow;
        employee.UpdatedBy = approverId;
        change.Status = "ApprovedApplied";
        change.AppliedAtUtc = DateTime.UtcNow;
        _db.EmployeeHistories.Add(new EmployeeHistory
        {
            TenantId = approval.TenantId,
            EmployeeId = employee.Id,
            EventType = "SensitiveChangeApproved",
            FieldName = change.SensitiveFields,
            EffectiveDate = change.EffectiveDate,
            Reason = comments,
            SnapshotJson = "{}",
            CreatedByUserId = approverId
        });
    }

    private static void ApplyEmployeeChange(Employee employee, Dictionary<string, JsonElement> changes)
    {
        foreach (var (field, value) in changes)
        {
            switch (field)
            {
                case "englishName": employee.EnglishName = value.GetString() ?? employee.EnglishName; employee.FullName = employee.EnglishName; break;
                case "arabicName": employee.ArabicName = value.GetString() ?? employee.ArabicName; break;
                case "preferredName": employee.PreferredName = value.GetString() ?? employee.PreferredName; break;
                case "gender": employee.Gender = value.GetString() ?? employee.Gender; break;
                case "nationality": employee.Nationality = value.GetString() ?? employee.Nationality; break;
                case "personalEmail": employee.PersonalEmail = value.GetString() ?? employee.PersonalEmail; break;
                case "workEmail": employee.WorkEmail = value.GetString() ?? employee.WorkEmail; break;
                case "phone": employee.Phone = value.GetString() ?? employee.Phone; break;
                case "jobTitle": employee.JobTitle = value.GetString() ?? employee.JobTitle; break;
                case "employmentType": employee.EmploymentType = value.GetString() ?? employee.EmploymentType; break;
                case "joiningDate": if (value.ValueKind == JsonValueKind.String && DateTime.TryParse(value.GetString(), out var joining)) employee.JoiningDate = DateTime.SpecifyKind(joining, DateTimeKind.Utc); break;
                case "department": employee.Department = value.GetString() ?? employee.Department; break;
                case "designation": employee.Designation = value.GetString() ?? employee.Designation; break;
                case "branch": employee.Branch = value.GetString() ?? employee.Branch; break;
                case "workLocation": employee.WorkLocation = value.GetString() ?? employee.WorkLocation; break;
                case "managerEmployeeId": employee.ManagerEmployeeId = value.ValueKind == JsonValueKind.Null ? null : value.GetInt32(); break;
                case "dateOfBirth": employee.DateOfBirth = ReadDateOnly(value); break;
                case "maritalStatus": employee.MaritalStatus = value.GetString() ?? employee.MaritalStatus; break;
                case "emergencyContactName": employee.EmergencyContactName = value.GetString() ?? employee.EmergencyContactName; break;
                case "emergencyContactPhone": employee.EmergencyContactPhone = value.GetString() ?? employee.EmergencyContactPhone; break;
                case "contractType": employee.ContractType = value.GetString() ?? employee.ContractType; break;
                case "grade": employee.Grade = value.GetString() ?? employee.Grade; break;
                case "costCenter": employee.CostCenter = value.GetString() ?? employee.CostCenter; break;
                case "salary": employee.Salary = value.GetDecimal(); break;
                case "bankName": employee.BankName = value.GetString() ?? employee.BankName; break;
                case "bankIban": employee.BankIban = value.GetString() ?? employee.BankIban; break;
                case "wpsBankDetails": employee.WpsBankDetails = value.GetString() ?? employee.WpsBankDetails; break;
                case "passportNumber": employee.PassportNumber = value.GetString() ?? employee.PassportNumber; break;
                case "passportIssueDate": employee.PassportIssueDate = ReadDateOnly(value); break;
                case "passportExpiryDate": employee.PassportExpiryDate = ReadDateOnly(value); break;
                case "visaNumber": employee.VisaNumber = value.GetString() ?? employee.VisaNumber; break;
                case "visaIssueDate": employee.VisaIssueDate = ReadDateOnly(value); break;
                case "visaExpiryDate": employee.VisaExpiryDate = ReadDateOnly(value); break;
                case "iqamaNumber": employee.IqamaNumber = value.GetString() ?? employee.IqamaNumber; break;
                case "muqeemNumber": employee.MuqeemNumber = value.GetString() ?? employee.MuqeemNumber; break;
                case "gosiReference": employee.GosiReference = value.GetString() ?? employee.GosiReference; break;
                case "qiwaContractNumber": employee.QiwaContractNumber = value.GetString() ?? employee.QiwaContractNumber; break;
                case "emiratesId": employee.EmiratesId = value.GetString() ?? employee.EmiratesId; break;
                case "laborCardNumber": employee.LaborCardNumber = value.GetString() ?? employee.LaborCardNumber; break;
                case "visaFileNumber": employee.VisaFileNumber = value.GetString() ?? employee.VisaFileNumber; break;
                case "qid": employee.Qid = value.GetString() ?? employee.Qid; break;
                case "workPermitNumber": employee.WorkPermitNumber = value.GetString() ?? employee.WorkPermitNumber; break;
                case "workPermitIssueDate": employee.WorkPermitIssueDate = ReadDateOnly(value); break;
                case "civilId": employee.CivilId = value.GetString() ?? employee.CivilId; break;
                case "residencyNumber": employee.ResidencyNumber = value.GetString() ?? employee.ResidencyNumber; break;
                case "residencyIssueDate": employee.ResidencyIssueDate = ReadDateOnly(value); break;
                case "terminationReason": employee.TerminationReason = value.GetString() ?? employee.TerminationReason; break;
            }
        }
    }

    private static DateOnly? ReadDateOnly(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return DateOnly.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };
}
