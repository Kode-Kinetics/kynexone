using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Leave;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Timesheets;
using Zayra.Api.Models;
using Zayra.Api.Application.Jawazat;
using Zayra.Api.Infrastructure.Jawazat;

using Zayra.Api.Infrastructure.Common;

namespace Zayra.Api.Infrastructure.Approvals;

public class ApprovalWorkflowService : IApprovalWorkflowService
{
    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;
    private readonly IEstablishmentGuard _establishmentGuard;
    private readonly ILeaveService _leaveService;
    private readonly IApprovalRouter _router;
    // Seals the "what the approver saw" baseline of a future-dated employee change (EmployeeChangeBaseline).
    // Optional so direct constructions keep compiling; without it a future-dated change is still scheduled,
    // but with no baseline, so on its effective date it goes back for review instead of being applied.
    private readonly IDataProtector? _changeBaselineProtector;
    private readonly IDataScopeService _dataScopes;
    private readonly IHttpContextAccessor? _http;
    private readonly Dictionary<RequestContext, DataScope> _jawazatScopes = new();
    private readonly Dictionary<(Guid TenantId, int EmployeeId), IReadOnlyCollection<Guid>> _subjectUserIds = new();

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
        IDataProtectionProvider? dataProtection = null,
        IDataScopeService? dataScopes = null,
        IHttpContextAccessor? http = null)
    {
        _db = db;
        _audit = audit;
        _changeBaselineProtector = dataProtection is null ? null : EmployeeChangeBaseline.CreateProtector(dataProtection);
        // Optional with concrete fallback so direct constructions keep compiling AND enforcing.
        _establishmentGuard = establishmentGuard ?? new EstablishmentGuardService(db);
        // F1 — ONE router shared by this service and the leave aggregate it delegates to.
        _router = router ?? new ApprovalRouter(db, hierarchy);
        _leaveService = leaveService ?? new LeaveService(db, _router);
        _http = http;
        _dataScopes = dataScopes ?? new Zayra.Api.Infrastructure.Common.DataScopeService(db, http: http);
    }

    public async Task<PagedResult<ApprovalWorkflowDto>> GetWorkflowsAsync(Guid tenantId, string? entityName, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.ApprovalWorkflows.AsNoTracking().Include(x => x.Steps).Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(entityName)) query = query.Where(x => x.EntityName == entityName);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.Code).Skip((page - 1) * pageSize).Take(pageSize).Select(x => x.ToDto()).ToListAsync(cancellationToken);
        return new PagedResult<ApprovalWorkflowDto>(items, total, page, pageSize);
    }

    public async Task<ApprovalWorkflowDto?> GetWorkflowAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var workflow = await _db.ApprovalWorkflows.AsNoTracking().Include(x => x.Steps).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        return workflow?.ToDto();
    }

    public async Task<ApprovalWorkflowDto> CreateWorkflowAsync(Guid tenantId, ApprovalWorkflowRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        EnsureRoleStepsNameARole(request);
        await EnsureWorkflowCodeUnique(tenantId, request.Code, null, cancellationToken);
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
        EnsureRoleStepsNameARole(request);
        await EnsureWorkflowCodeUnique(tenantId, request.Code, id, cancellationToken);
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
        // Company query filters do not enforce employee/department visibility. Apply Jawazat's
        // employee boundary before Count/Skip/Take so excluded titles and totals never leak.
        var jawazatScope = await ResolveJawazatScopeAsync(tenantId, context, cancellationToken);
        var jawazatIds = jawazatScope.AllowedEmployeeIds ?? Array.Empty<int>();
        var jawazatEntity = JawazatConstants.ApprovalEntityName.ToLowerInvariant();
        query = query.Where(x => x.EntityName.ToLower() != jawazatEntity || (x.RequestedForEmployeeId != null
            && (jawazatScope.IsUnrestricted || jawazatIds.Contains(x.RequestedForEmployeeId.Value))));
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
            else if (q is "overdue")
            {
                var now = DateTime.UtcNow;
                query = query.Where(x => x.Status == "Pending" && x.DueAtUtc != null && x.DueAtUtc < now);
                if (!CanViewAllApprovalRequests(context))
                {
                    var callerEmployeeId = await ResolveCallerEmployeeIdAsync(tenantId, context.UserId, cancellationToken);
                    var roles = (context.Roles ?? Array.Empty<string>())
                        .Select(Clean)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.ToLower())
                        .ToArray();
                    query = query.Where(x =>
                        (context.UserId != null && x.CurrentApproverUserId == context.UserId) ||
                        (callerEmployeeId != null && x.CurrentApproverEmployeeId == callerEmployeeId) ||
                        ((x.CurrentApproverType ?? string.Empty).ToLower() == "role" &&
                         roles.Contains((x.CurrentApproverRole ?? string.Empty).ToLower())));
                }
            }
            else
            {
                // `all` and unknown queue names used to fall through with no predicate, allowing a
                // scoped caller to enumerate the tenant queue. Only tenant-wide viewers may request it.
                if (!CanViewAllApprovalRequests(context))
                    query = query.Where(x => false);
            }
        }
        var total = await query.CountAsync(cancellationToken);
        var approvals = await query
            .OrderBy(x => x.DueAtUtc ?? DateTime.MaxValue)
            .ThenByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var summaries = await LoadChangeSummariesAsync(tenantId, approvals, cancellationToken);
        var otherDeciders = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var items = new List<ApprovalRequestDto>(approvals.Count);
        foreach (var approval in approvals)
        {
            items.Add(await ProjectAsync(approval, context, summaries, otherDeciders, cancellationToken));
        }
        return new PagedResult<ApprovalRequestDto>(items, total, page, pageSize);
    }

    public async Task<ApprovalRequestDto?> GetRequestAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var request = await _db.ApprovalRequests.AsNoTracking().Include(x => x.Decisions).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (request is not null && JawazatApprovalSync.IsJawazat(request)) return null;
        return request?.ToDto();
    }

    public async Task<ApprovalRequestDto?> GetRequestAsync(Guid tenantId, Guid id, RequestContext context, CancellationToken cancellationToken)
    {
        var request = await _db.ApprovalRequests.AsNoTracking().Include(x => x.Decisions).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        if (request is null) return null;
        if (!await CanViewRequestAsync(request, context, cancellationToken)) return null;
        var summaries = await LoadChangeSummariesAsync(tenantId, new[] { request }, cancellationToken);
        return await ProjectAsync(request, context, summaries, new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase), cancellationToken);
    }

    /// <summary>
    /// The requester takes back their own pending employee change. Only the requester may, only while it
    /// is pending, and only for <see cref="EmployeeChangeRequest"/>: other entities (leave, timesheets,
    /// offers…) own a state machine of their own and are cancelled from their own screen, so closing the
    /// approval alone here would strand them. Without this a mistaken or duplicate submission could only
    /// sit in the queue until someone else rejected it.
    /// </summary>
    public async Task<ApprovalRequestDto?> WithdrawAsync(Guid tenantId, Guid approvalRequestId, string? reason, RequestContext context, CancellationToken cancellationToken)
    {
        var approval = await _db.ApprovalRequests.Include(x => x.Decisions).FirstOrDefaultAsync(x => x.Id == approvalRequestId && x.TenantId == tenantId, cancellationToken);
        if (approval is null || !await CanViewRequestAsync(approval, context, cancellationToken)) return null;
        if (approval.Status != "Pending") throw new InvalidOperationException("Only a pending request can be withdrawn.");
        if (context.UserId is null || approval.RequestedByUserId != context.UserId)
            throw new InvalidOperationException("Only the person who requested this can withdraw it.");
        if (!string.Equals(approval.EntityName, nameof(EmployeeChangeRequest), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This kind of request is withdrawn from its own screen, not from the Approval Center.");

        var note = string.IsNullOrWhiteSpace(reason) ? "Withdrawn by the requester." : Clean(reason);
        if (Guid.TryParse(approval.EntityId, out var changeId)
            && await _db.EmployeeChangeRequests.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == changeId, cancellationToken) is { } change)
        {
            if (!string.Equals(change.Status, EmployeeChangeStatuses.PendingApproval, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"This change is already '{change.Status}' and can no longer be withdrawn.");
            change.Status = EmployeeChangeStatuses.Cancelled;
            change.RejectionReason = note;
        }
        approval.Status = "Cancelled";
        approval.CompletedAtUtc = DateTime.UtcNow;
        // Same compare-and-swap as DecideAsync: a withdrawal racing an approval cannot both commit.
        approval.DecisionVersion++;
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new InvalidOperationException("This request was decided while you were withdrawing it. Refresh to see the outcome.", ex);
        }
        await _audit.WriteAsync("approval.request_withdrawn", nameof(ApprovalRequest), approval.Id.ToString(), context,
            JsonSerializer.Serialize(new { reason = note, stepOrder = approval.CurrentStepOrder }), cancellationToken);
        return await GetRequestAsync(tenantId, approval.Id, context, cancellationToken);
    }

    // context is null on the legacy context-less listing: nobody can decide there, and no reason is owed.
    private async Task<ApprovalRequestDto> ProjectAsync(ApprovalRequest approval, RequestContext? context,
        IReadOnlyDictionary<string, string?> summaries, Dictionary<string, bool> otherDeciders, CancellationToken cancellationToken)
    {
        // The bar is resolved once per row and shared by canDecide and the reason below.
        var bar = context is null ? DecisionBar.None : await ResolveDecisionBarAsync(approval, context, cancellationToken);
        var canDecide = bar == DecisionBar.None
            && await CanDecideRequestAsync(approval, context, cancellationToken, separationOfDuties: false);
        var requestedByCaller = context?.UserId is not null && approval.RequestedByUserId == context.UserId;
        var canWithdraw = approval.Status == "Pending" && requestedByCaller
            && string.Equals(approval.EntityName, nameof(EmployeeChangeRequest), StringComparison.OrdinalIgnoreCase);
        string? blockedReason = null;
        if (context is not null && !canDecide && approval.Status == "Pending")
        {
            if (bar is DecisionBar.Subject or DecisionBar.DecidedEarlierStep)
            {
                // Same shape as maker-checker below: say why, then who can unblock it. A sole Admin who is
                // the subject, or who decided step 1, must be told nobody else can ever decide it.
                blockedReason = bar == DecisionBar.Subject
                    ? "This request is about you, so someone else must decide it."
                    : "You already decided an earlier step of this request, so a different person must decide this one.";
                blockedReason += await AnyoneElseCanDecideAsync(approval, otherDeciders, cancellationToken)
                    ? $" It is waiting for {OwnerLabel(approval)}."
                    : NobodyElseSentence(approval, "decide", canWithdraw: false);
            }
            else if (requestedByCaller)
            {
                // Maker-checker is deliberate and stays. What was missing is saying so: the screen showed
                // "Watching", and a sole administrator had no way to learn that nobody could ever decide.
                blockedReason = "You requested this, so someone else must approve it (maker-checker).";
                blockedReason += await AnyoneElseCanDecideAsync(approval, otherDeciders, cancellationToken)
                    ? $" It is waiting for {OwnerLabel(approval)}."
                    : NobodyElseSentence(approval, "approve", canWithdraw);
            }
            else
            {
                blockedReason = $"This step is assigned to {OwnerLabel(approval)}, which your access does not cover.";
            }
        }
        return approval.ToDto(canDecide, blockedReason, canWithdraw, summaries.GetValueOrDefault(approval.EntityId));
    }

    // A step routed to a named person is unblocked by reassigning it, not by granting "their" role.
    private static string NobodyElseSentence(ApprovalRequest approval, string verb, bool canWithdraw) =>
        ApprovalUnblock.NobodyElseSentence(verb,
            approval.CurrentApproverUserId is not null || approval.CurrentApproverEmployeeId is not null
                ? Clean(approval.CurrentApproverName) is { Length: > 0 } name ? name : "the named approver"
                : null,
            Clean(approval.CurrentApproverRole) is "" ? null : OwnerLabel(approval), canWithdraw);

    private static string OwnerLabel(ApprovalRequest approval) =>
        new[] { approval.CurrentApproverName, approval.CurrentApproverRole, approval.CurrentQueue }
            .Select(Clean).FirstOrDefault(x => x.Length > 0) ?? "another approver";

    /// <summary>
    /// Whether any active user other than the requester holds the routed role or a role carrying
    /// approvals.override. Role grants only: a hint for the "who can unblock this" sentence, never an
    /// authorisation decision (that is <see cref="CanDecideRequestAsync"/> alone).
    /// </summary>
    private async Task<bool> AnyoneElseCanDecideAsync(ApprovalRequest approval, Dictionary<string, bool> cache, CancellationToken cancellationToken)
    {
        // Everyone the separation-of-duties bars exclude: the requester, every earlier-step decider and every
        // login linked to the subject employee. None of them can unblock the request, so none of them count.
        var excluded = new HashSet<Guid>();
        if (approval.RequestedByUserId is Guid requester) excluded.Add(requester);
        foreach (var decision in approval.Decisions)
            if (decision.DecidedByUserId is Guid decider) excluded.Add(decider);
        var subject = approval.RequestedForEmployeeId ?? await ResolveSubjectEmployeeIdAsync(approval, cancellationToken);
        if (subject is int subjectId)
            foreach (var linked in await SubjectUserIdsAsync(approval.TenantId, subjectId, cancellationToken)) excluded.Add(linked);

        if (approval.CurrentApproverUserId is Guid named)
            return !excluded.Contains(named);
        var role = Clean(approval.CurrentApproverRole);
        var excludedIds = excluded.OrderBy(x => x).ToArray();
        var key = $"{role}|{string.Join(",", excludedIds)}";
        if (cache.TryGetValue(key, out var known)) return known;

        // An empty or "Any" role is open to approvals.decide holders who also hold manager.approve or
        // approvals.override (CanDecideRequestAsync), so only they count as someone who could unblock it.
        var anyRole = role.Length == 0 || role.Equals("Any", StringComparison.OrdinalIgnoreCase);
        var exists = anyRole
            ? await ApprovalUnblock.AnyOtherUserWithPermissionAndAnyOfAsync(_db, approval.TenantId, "approvals.decide",
                new[] { AnyStepApproverPermission, "approvals.override" }, excludedIds, cancellationToken)
            : await ApprovalUnblock.AnyOtherUserInRolesAsync(_db, approval.TenantId, new[] { role }, orOverride: true, excludedIds, cancellationToken);
        cache[key] = exists;
        return exists;
    }

    /// <summary>"IBAN, passport" for each employee-change approval, keyed by EntityId, in one query.</summary>
    private async Task<IReadOnlyDictionary<string, string?>> LoadChangeSummariesAsync(Guid tenantId, IReadOnlyCollection<ApprovalRequest> approvals, CancellationToken cancellationToken)
    {
        var changeIds = approvals
            .Where(x => string.Equals(x.EntityName, nameof(EmployeeChangeRequest), StringComparison.OrdinalIgnoreCase))
            .Select(x => Guid.TryParse(x.EntityId, out var id) ? id : (Guid?)null)
            .Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        if (changeIds.Count == 0) return new Dictionary<string, string?>();
        var rows = await _db.EmployeeChangeRequests.AsNoTracking()
            .Where(x => x.TenantId == tenantId && changeIds.Contains(x.Id))
            .Select(x => new { x.Id, x.SensitiveFields })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(x => x.Id.ToString(), x => Zayra.Api.Controllers.DashboardController.FormatChangedFields(x.SensitiveFields), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ApprovalRequestDto> CreateRequestAsync(Guid tenantId, CreateApprovalRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var entityName = Clean(request.EntityName);
        if (string.Equals(entityName, JawazatConstants.ApprovalEntityName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Jawazat approvals are created by the governed Jawazat request workflow, not directly.");
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
        // Segregation of duties, ahead of every role and override check: neither the employee the
        // request is about nor someone who already decided one of its steps may decide it.
        switch (await ResolveDecisionBarAsync(approval, context, cancellationToken))
        {
            case DecisionBar.Subject:
                throw new InvalidOperationException(SubjectBarMessage);
            case DecisionBar.DecidedEarlierStep:
                throw new InvalidOperationException(EarlierStepBarMessage);
        }
        await JawazatApprovalSync.ValidateDecisionAsync(_db, approval, context, cancellationToken,
            JawazatApprovalSync.IsJawazat(approval) ? await ResolveJawazatScopeAsync(tenantId, context, cancellationToken) : null);
        if (string.Equals(approval.EntityName, nameof(LeaveRequest), StringComparison.OrdinalIgnoreCase))
        {
            // LeaveRequest/LeaveApproval/balance is the aggregate of record. ApprovalRequest is
            // only its indexed routing projection, so dispatch before generic workflow lookup and
            // let LeaveService own the single relational transaction for decisions from either UI.
            if (!await CanDecideRequestAsync(approval, context, cancellationToken))
                throw new InvalidOperationException($"Current step requires approver role '{approval.CurrentApproverRole}'.");
            if (context.UserId is null)
                throw new InvalidOperationException("An authenticated approver is required.");
            if (!Guid.TryParse(approval.EntityId, out var leaveRequestId))
                throw new InvalidOperationException("The approval request is not linked to a valid leave request.");

            var approverName = await _db.Employees.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.UserAccountId == context.UserId && !x.IsDeleted)
                .Select(x => x.FullName)
                .FirstOrDefaultAsync(cancellationToken)
                ?? await _db.Users.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.Id == context.UserId && !x.IsDeleted)
                    .Select(x => x.FullName)
                    .FirstOrDefaultAsync(cancellationToken)
                ?? context.UserId.Value.ToString();

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
        if (approval.Decisions.Any(x => x.StepOrder == step.StepOrder))
            throw new InvalidOperationException("Current approval step has already been decided.");
        if (!await CanDecideStepAsync(approval, step, context, cancellationToken))
            throw new InvalidOperationException($"Current step requires approver role '{step.ApproverRole}'.");

        var normalizedDecision = requestedDecision.Equals("Reject", StringComparison.OrdinalIgnoreCase) ? "Rejected" : "Approved";
        var approvalDecision = new ApprovalDecision
        {
            TenantId = tenantId,
            ApprovalRequestId = approval.Id,
            StepOrder = step.StepOrder,
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
            await JawazatApprovalSync.ApplyAsync(_db, approval, normalizedDecision, context, Clean(request.Comments), cancellationToken,
                JawazatApprovalSync.IsJawazat(approval) ? await ResolveJawazatScopeAsync(tenantId, context, cancellationToken) : null);
            await TimesheetApprovalSync.ApplyAsync(_db, approval, normalizedDecision, Clean(request.Comments), cancellationToken);
            await Zayra.Api.Infrastructure.Recruitment.RequisitionApprovalSync.ApplyAsync(_db, approval, normalizedDecision, Clean(request.Comments), cancellationToken);
        }
        else if (step.IsFinalStep)
        {
            approval.Status = "Approved";
            approval.CompletedAtUtc = DateTime.UtcNow;
            await SyncEmployeeChangeDecisionAsync(approval, normalizedDecision, context, Clean(request.Comments), cancellationToken);
            await JawazatApprovalSync.ApplyAsync(_db, approval, normalizedDecision, context, Clean(request.Comments), cancellationToken,
                JawazatApprovalSync.IsJawazat(approval) ? await ResolveJawazatScopeAsync(tenantId, context, cancellationToken) : null);
            // Timesheets: project the decision onto the timesheet and, on approval, write the
            // attendance reconciliation its hours feed — in THIS SaveChanges, so a decision taken
            // in the Approval Center and one taken on the timesheet screen are the same write.
            await TimesheetApprovalSync.ApplyAsync(_db, approval, normalizedDecision, Clean(request.Comments), cancellationToken);
            // Requisitions: the shared row and the requisition's own status are now one write. Before
            // this the module stamped its status and left this row Pending for ever.
            await Zayra.Api.Infrastructure.Recruitment.RequisitionApprovalSync.ApplyAsync(_db, approval, normalizedDecision, Clean(request.Comments), cancellationToken);
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
        return JawazatApprovalSync.IsJawazat(approval)
            ? await GetRequestAsync(tenantId, approval.Id, context, cancellationToken)
            : (await GetRequestAsync(tenantId, approval.Id, cancellationToken))!;
    }

    /// <summary>
    /// A Role step must name a role. A blank or "Any" role made the step decidable by every approvals.decide
    /// holder (now: every manager.approve holder) in the tenant. New saves are refused; workflows already saved
    /// that way still load and route, so live requests are not stranded.
    /// </summary>
    internal static void EnsureRoleStepsNameARole(ApprovalWorkflowRequest request)
    {
        foreach (var step in request.Steps ?? Array.Empty<ApprovalWorkflowStepRequest>())
        {
            var type = string.IsNullOrWhiteSpace(step.ApproverType) ? "Role" : step.ApproverType.Trim();
            if (!type.Equals("Role", StringComparison.OrdinalIgnoreCase)) continue;
            var role = (step.ApproverRole ?? string.Empty).Trim();
            if (role.Length == 0 || role.Equals("Any", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Step {step.StepOrder} ('{Clean(step.StepName)}') is a Role step and must name the role that decides it, " +
                    "for example HR Manager. A blank or \"Any\" role would let any approver in the company decide it.");
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
            // Tenant-wide: the subject must resolve whichever legal entity the caller is switched to, or the
            // separation-of-duties bar fails open for a change about an employee in another company.
            return await Zayra.Api.Infrastructure.Data.ScopedBypass.TenantWide(_db.EmployeeChangeRequests, approval.TenantId,
                    "Resolve an approval's subject employee for routing and the separation-of-duties bar, whatever company the caller has selected.")
                .AsNoTracking()
                .Where(x => x.Id == changeId)
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
        if (exists) throw new InvalidOperationException("Approval workflow code already exists in this tenant.");
    }

    /// <summary>
    /// F1 — the router needs an unambiguous answer. Two ACTIVE workflows for the same entity and the
    /// same org scope (department, grade) would leave the choice to a tie-break, so a new one is
    /// refused. A scoped workflow cannot be the tenant default.
    /// </summary>
    private async Task EnsureScopeUnambiguousAsync(Guid tenantId, ApprovalWorkflowRequest request, Guid? excludedId, CancellationToken cancellationToken)
    {
        if (request.IsDefault && (request.DepartmentId.HasValue || request.GradeId.HasValue))
            throw new InvalidOperationException("A workflow scoped to a department or grade cannot be the tenant default.");
        if (request.DepartmentId.HasValue && !await _db.Departments.AnyAsync(d => d.TenantId == tenantId && d.Id == request.DepartmentId.Value, cancellationToken))
            throw new InvalidOperationException("The workflow's department was not found in this tenant.");
        if (!request.IsActive) return;

        var entity = Clean(request.EntityName);
        var clash = await _db.ApprovalWorkflows.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsActive && x.EntityName == entity && x.Id != excludedId
                && x.DepartmentId == request.DepartmentId && x.GradeId == request.GradeId)
            .OrderBy(x => x.Code)
            .Select(x => x.Code)
            .FirstOrDefaultAsync(cancellationToken);
        if (clash is not null)
            throw new InvalidOperationException(
                $"Active approval workflow '{clash}' already covers '{entity}' for this department/grade scope. Deactivate it or change the scope.");
    }

    private async Task<bool> CanDecideStepAsync(ApprovalRequest approval, ApprovalWorkflowStep step, RequestContext context, CancellationToken cancellationToken)
    {
        return await CanDecideRequestAsync(approval, context, cancellationToken);
    }

    internal const string SubjectBarMessage =
        "Segregation of duties: this request is about you, so you cannot approve or reject it.";
    internal const string EarlierStepBarMessage =
        "Segregation of duties: you already decided an earlier step of this request, so a different approver must decide this one.";

    /// <summary>Why the caller may never decide this request, whatever their role or permissions.</summary>
    private enum DecisionBar { None, Requester, Subject, DecidedEarlierStep }

    /// <summary>
    /// The segregation-of-duties bars, in one place for the decision itself, canDecide and the
    /// "why can't I decide" sentence. They outrank approvals.override and an empty or "Any" approver
    /// role: those widen WHO may decide a step, never to the person the request is about or to
    /// someone who already decided another step of it.
    /// </summary>
    private async Task<DecisionBar> ResolveDecisionBarAsync(ApprovalRequest approval, RequestContext context, CancellationToken cancellationToken)
    {
        if (context.UserId is not Guid userId) return DecisionBar.None;
        if (approval.RequestedByUserId == userId) return DecisionBar.Requester;
        var subject = approval.RequestedForEmployeeId ?? await ResolveSubjectEmployeeIdAsync(approval, cancellationToken);
        if (subject is int subjectId)
        {
            if ((await SubjectUserIdsAsync(approval.TenantId, subjectId, cancellationToken)).Contains(userId))
                return DecisionBar.Subject;
            // EITHER link marks the subject: the login rows above (Employee.UserAccountId and the account
            // links), OR the employee the caller's own token is linked to (CallerEmployeeResolver, the lookup
            // every other surface uses). This bar only ever gets stricter; neither lookup can clear it.
            if (_http?.HttpContext?.User is { Identity.IsAuthenticated: true } principal
                && await CallerEmployeeResolver.ResolveAsync(_db, principal, approval.TenantId, cancellationToken) == subjectId)
                return DecisionBar.Subject;
        }
        // Every load of a request for a decision or a listing includes its decision ledger.
        if (approval.Decisions.Any(x => x.DecidedByUserId == userId)) return DecisionBar.DecidedEarlierStep;
        return DecisionBar.None;
    }

    /// <summary>
    /// Every login linked to the subject employee, read TENANT-WIDE. The company-filtered caller lookup
    /// (<see cref="ResolveCallerEmployeeIdAsync"/>) resolves to null when the caller's own employee row is
    /// in another legal entity, or the company switcher is on one, and the bar then failed open. Asking
    /// "which users is this employee?" instead of "which employee is this user?" also covers a login
    /// linked to more than one employee row. Cached per subject: a listing asks once per row.
    /// </summary>
    private async Task<IReadOnlyCollection<Guid>> SubjectUserIdsAsync(Guid tenantId, int subjectEmployeeId, CancellationToken cancellationToken)
    {
        if (_subjectUserIds.TryGetValue((tenantId, subjectEmployeeId), out var known)) return known;
        var linked = await ApprovalUnblock.SubjectUserIdsAsync(_db, tenantId, subjectEmployeeId, cancellationToken);
        _subjectUserIds[(tenantId, subjectEmployeeId)] = linked;
        return linked;
    }

    /// <param name="separationOfDuties">False only for visibility: a subject or earlier-step decider
    /// who is routed this step may still SEE it (and be told why they cannot decide it).</param>
    /// <summary>The key an "Any" (unassigned) approval step requires besides approvals.decide.</summary>
    internal const string AnyStepApproverPermission = "manager.approve";

    private async Task<bool> CanDecideRequestAsync(ApprovalRequest approval, RequestContext? context, CancellationToken cancellationToken,
        bool separationOfDuties = true)
    {
        if (context is null || approval.Status != "Pending") return false;
        if (context.UserId is not null && approval.RequestedByUserId == context.UserId) return false;
        if (separationOfDuties && await ResolveDecisionBarAsync(approval, context, cancellationToken) != DecisionBar.None) return false;
        if (JawazatApprovalSync.IsJawazat(approval))
        {
            try { await JawazatApprovalSync.ValidateDecisionAsync(_db, approval, context, cancellationToken,
                await ResolveJawazatScopeAsync(approval.TenantId, context, cancellationToken)); }
            catch (JawazatException) { return false; }
        }

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
            // An unassigned ("Any") step was open to every approvals.decide holder in the tenant: Payroll Manager,
            // Finance, Finance Approver and ManagerPortal employees could approve an employee's IBAN or salary
            // change. It now needs an approver's key on top of approvals.decide and the bars above.
            // (approvals.override already returned true above.)
            return permissions.Any(x => x.Equals(AnyStepApproverPermission, StringComparison.OrdinalIgnoreCase));
        var roles = context.Roles ?? Array.Empty<string>();
        return roles.Any(x => x.Equals(requiredRole, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> CanViewRequestAsync(ApprovalRequest approval, RequestContext context, CancellationToken cancellationToken)
    {
        if (JawazatApprovalSync.IsJawazat(approval))
        {
            var scope = await ResolveJawazatScopeAsync(approval.TenantId, context, cancellationToken);
            if (approval.RequestedForEmployeeId is not int subject || !scope.CanAccessEmployee(subject)) return false;
            if (scope.CallerEmployeeId == subject) return true;
        }
        if (CanViewAllApprovalRequests(context)) return true;
        if (await CanDecideRequestAsync(approval, context, cancellationToken, separationOfDuties: false)) return true;
        if (context.UserId is not null && approval.RequestedByUserId == context.UserId) return true;

        var callerEmployeeId = await ResolveCallerEmployeeIdAsync(approval.TenantId, context.UserId, cancellationToken);
        if (callerEmployeeId is null || approval.RequestedForEmployeeId is null) return false;
        var teamIds = await ResolveTeamEmployeeIdsAsync(approval.TenantId, callerEmployeeId.Value, cancellationToken);
        return teamIds.Contains(approval.RequestedForEmployeeId.Value);
    }

    private async Task<DataScope> ResolveJawazatScopeAsync(Guid tenantId, RequestContext? context, CancellationToken ct)
    {
        if (context is not null && context.TenantId == tenantId && _jawazatScopes.TryGetValue(context, out var cached)) return cached;
        var scope = await JawazatApprovalAccess.ResolveAsync(_db, tenantId, context, ct, _dataScopes, _http);
        if (context is not null && context.TenantId == tenantId) _jawazatScopes[context] = scope;
        return scope;
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
            // Nothing is written to the employee now. EffectiveChangeJobHandler applies it — through the
            // same ApplyApprovedChangeAsync as below — once the date arrives in the tenant's timezone. Until
            // this, the status was set and nothing ever read it again: an approved IBAN never reached payroll.
            change.Status = EmployeeChangeStatuses.ApprovedPendingEffectiveDate;
            await RecordScheduledChangeAsync(approval.TenantId, change, approverId, comments, cancellationToken);
            return;
        }

        var employee = await _db.Employees.FirstOrDefaultAsync(x => x.TenantId == approval.TenantId && x.Id == change.EmployeeId && !x.IsDeleted, cancellationToken);
        if (employee is null) throw new InvalidOperationException("Employee for this change request was not found.");
        var changes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(change.ProposedChangesJson) ?? new();
        // A change the effective-date job sent back for review: the reviewer approved what the re-review showed,
        // so a value that moved since is refused, not overwritten (throws => nothing saved, request stays Pending;
        // the remedy is to reject it). A change never returned for review passes straight through.
        var review = await EmployeeChangeBaseline.CheckUnchangedSinceReviewAsync(
            _db, _changeBaselineProtector, approval.TenantId, change.Id, employee, changes.Keys, cancellationToken);
        if (!review.Unchanged)
            throw new InvalidOperationException(review.Refusal(Zayra.Api.Controllers.DashboardController.FormatChangedFields));
        // Tenant + reporting-cycle check on a manager id, BEFORE anything is applied (throws ⇒ nothing saved,
        // the request stays Pending). Same rule set as every other apply path.
        if (await Zayra.Api.Application.Employees.EmployeeChangeApplier
                .ValidateManagerChangeAsync(_db, employee, changes, null, cancellationToken) is { } managerRejection)
            throw new InvalidOperationException(managerRejection.Message);
        var priorDeptId = employee.DepartmentId;
        var priorDesigId = employee.DesignationId;
        // ONE shared applier with EmployeesController (Application/Employees/EmployeeChangeApplier).
        // This used to be a hand-copied duplicate of the controller's switch that had drifted: six keys
        // missing (iqamaExpiryDate, emiratesIdExpiryDate, qidExpiryDate, civilIdExpiryDate, idNumber,
        // sponsorName — four of them fail-closed PAY gates) and NO default arm, so approving a change
        // to any of them returned success and wrote nothing, leaving the employee blocked on the very
        // value the approver had just accepted.
        // Unrecognised keys are REPORTED, never dropped: they are recorded on the EmployeeHistory row
        // written below (same unit of work — an audit write here would SaveChanges and commit a
        // half-applied change before the establishment guard can block it). The payload was validated
        // against EditableEmployeeFields when the change was REQUESTED, so an unknown key here is a
        // stored patch from an older build; refusing would strand an in-flight approval with no remedy.
        //
        // ApplyApprovedChangeAsync is every apply step in one call: the employee columns, the keys stored on
        // the payroll profile (socialInsuranceReference — a fail-closed pay gate in five GCC branches), the
        // free-text department/designation/branch resolved to ids (unresolvable ⇒ throws, surfaced to the
        // decider), and — P0 (money) — the approved bank field(s) mirrored onto EmployeePayrollProfile, which
        // the WPS/SIF export pays from. This path used to skip that last step, so an IBAN approved from the
        // approval queue left payroll on the OLD account. It only stages tracked rows; DecideAsync's single
        // SaveChanges commits them in one relational transaction with the change request, so there is no
        // window in which Employee.BankIban is new while EmployeePayrollProfile.Iban is stale.
        var unknownApproved = await EmployeeChangeApplier.ApplyApprovedChangeAsync(
            _db, approval.TenantId, employee, changes, approverId, cancellationToken);
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
        change.Status = EmployeeChangeStatuses.ApprovedApplied;
        change.AppliedAtUtc = DateTime.UtcNow;
        _db.EmployeeHistories.Add(new EmployeeHistory
        {
            TenantId = approval.TenantId,
            EmployeeId = employee.Id,
            EventType = "SensitiveChangeApproved",
            FieldName = change.SensitiveFields,
            EffectiveDate = change.EffectiveDate,
            Reason = comments,
            // Any key the shared applier did not recognise is named here rather than discarded in
            // silence, so an operator can see exactly which approved value did NOT reach a column.
            SnapshotJson = unknownApproved.Count == 0
                ? "{}"
                : JsonSerializer.Serialize(new { unappliedFields = unknownApproved }),
            CreatedByUserId = approverId
        });
    }

    /// <summary>
    /// Records a future-dated approval on the employee's history — who approved it, for which date — with
    /// the sealed baseline of the columns it will write (<see cref="EmployeeChangeBaseline"/>). On the
    /// effective date the job compares the record with that baseline: a column that moved in between sends
    /// the change back for review rather than overwriting it. Stages the row only; DecideAsync's single
    /// SaveChanges commits it with the decision. Never throws: a change it cannot baseline (employee gone,
    /// no key ring, unreadable field) is still scheduled, and is reviewed rather than applied when due.
    /// </summary>
    private async Task RecordScheduledChangeAsync(Guid tenantId, EmployeeChangeRequest change, Guid? approverId,
        string comments, CancellationToken cancellationToken)
    {
        var employee = await _db.Employees
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == change.EmployeeId && !x.IsDeleted, cancellationToken);
        if (employee is null) return;

        string? sealedBaseline = null;
        if (_changeBaselineProtector is not null)
        {
            Dictionary<string, JsonElement>? changes = null;
            try { changes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(change.ProposedChangesJson); }
            catch (JsonException) { /* unreadable patch: no baseline, so it is reviewed when due */ }
            if (changes is not null)
            {
                var profile = await _db.EmployeePayrollProfiles
                    .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && !x.IsDeleted, cancellationToken);
                if (EmployeeChangeBaseline.Capture(employee, profile, changes.Keys) is { } baseline)
                    sealedBaseline = EmployeeChangeBaseline.Protect(_changeBaselineProtector, change.Id, baseline);
            }
        }

        _db.EmployeeHistories.Add(new EmployeeHistory
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            EventType = EmployeeChangeBaseline.ScheduledEventType,
            FieldName = change.SensitiveFields,
            EffectiveDate = change.EffectiveDate,
            Reason = comments,
            ApprovedByUserId = approverId,
            SnapshotJson = EmployeeChangeBaseline.SnapshotJson(change.Id, sealedBaseline),
            CreatedByUserId = approverId,
        });
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };
}
