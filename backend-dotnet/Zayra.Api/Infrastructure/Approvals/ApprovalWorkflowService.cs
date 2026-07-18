using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Approvals;

public class ApprovalWorkflowService : IApprovalWorkflowService
{
    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;
    private readonly IHrmHierarchyService _hierarchy;

    public ApprovalWorkflowService(ZayraDbContext db, IAuditService audit)
        : this(db, audit, new HrmHierarchyService(db, audit))
    {
    }

    public ApprovalWorkflowService(ZayraDbContext db, IAuditService audit, IHrmHierarchyService hierarchy)
    {
        _db = db;
        _audit = audit;
        _hierarchy = hierarchy;
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
        await EnsureWorkflowCodeUnique(tenantId, request.Code, null, cancellationToken);
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
                query = query.Where(x =>
                    x.Status == "Pending" &&
                    ((context.UserId != null && x.CurrentApproverUserId == context.UserId) ||
                     (callerEmployeeId != null && x.CurrentApproverEmployeeId == callerEmployeeId) ||
                     ((x.CurrentApproverType ?? string.Empty).ToLower() == "role" &&
                      roles.Contains((x.CurrentApproverRole ?? string.Empty).ToLower()))));
            }
            else if (q is "team")
            {
                var callerEmployeeId = await ResolveCallerEmployeeIdAsync(tenantId, context.UserId, cancellationToken);
                if (callerEmployeeId is null) query = query.Where(x => false);
                else
                {
                    var teamIds = await ResolveTeamEmployeeIdsAsync(tenantId, callerEmployeeId.Value, cancellationToken);
                    query = query.Where(x => x.Status == "Pending" && x.RequestedForEmployeeId != null && teamIds.Contains(x.RequestedForEmployeeId.Value));
                }
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
            items.Add(approval.ToDto(await CanDecideRequestAsync(approval, context, cancellationToken)));
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
        return request.ToDto(await CanDecideRequestAsync(request, context, cancellationToken));
    }

    public async Task<ApprovalRequestDto> CreateRequestAsync(Guid tenantId, CreateApprovalRequest request, RequestContext context, CancellationToken cancellationToken)
    {
        var workflow = await _db.ApprovalWorkflows.Include(x => x.Steps).FirstOrDefaultAsync(x => x.Id == request.WorkflowId && x.TenantId == tenantId && x.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("Approval workflow was not found or is inactive.");
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
        var step = await _db.ApprovalWorkflowSteps.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.WorkflowId == approval.WorkflowId && x.StepOrder == approval.CurrentStepOrder, cancellationToken)
            ?? throw new InvalidOperationException("Current approval step was not found.");

        var requestedDecision = Clean(request.Decision);
        if (!requestedDecision.Equals("Approve", StringComparison.OrdinalIgnoreCase) &&
            !requestedDecision.Equals("Reject", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Decision must be Approve or Reject.");
        if (context.UserId is not null && approval.RequestedByUserId == context.UserId)
            throw new InvalidOperationException("Maker-checker violation: requester cannot approve or reject their own approval request.");
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
            if (nextStep is null)
            {
                approval.Status = "Approved";
                approval.CompletedAtUtc = DateTime.UtcNow;
            }
            else
            {
                approval.CurrentStepOrder = nextStep.StepOrder;
                await RouteCurrentStepAsync(approval, nextStep, cancellationToken);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync("approval.request_decided", nameof(ApprovalRequest), approval.Id.ToString(), context, normalizedDecision, cancellationToken);
        return (await GetRequestAsync(tenantId, approval.Id, cancellationToken))!;
    }

    private static void Apply(ApprovalWorkflow workflow, ApprovalWorkflowRequest request, Guid tenantId)
    {
        workflow.Code = Clean(request.Code).ToUpperInvariant();
        workflow.Name = Clean(request.Name);
        workflow.EntityName = Clean(request.EntityName);
        workflow.IsActive = request.IsActive;
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

        var approverEmployeeId = await ResolveApproverEmployeeIdAsync(approval.TenantId, subjectEmployeeId, step, cancellationToken);
        if (approverEmployeeId is null)
        {
            if (!string.Equals(approval.CurrentApproverType, "Role", StringComparison.OrdinalIgnoreCase))
            {
                approval.CurrentApproverType = "Role";
                approval.CurrentApproverRole = "HR Manager";
                approval.CurrentQueue = "Role:HR Manager";
                approval.EscalatedToRole = "HR Manager";
            }
            return;
        }

        var approver = await _db.Employees.AsNoTracking()
            .Where(x => x.TenantId == approval.TenantId && x.Id == approverEmployeeId.Value && !x.IsDeleted)
            .Select(x => new { x.Id, x.FullName, x.Designation, x.UserAccountId })
            .FirstOrDefaultAsync(cancellationToken);
        if (approver is null) return;

        approval.CurrentApproverEmployeeId = approver.Id;
        approval.CurrentApproverUserId = approver.UserAccountId;
        approval.CurrentApproverName = approver.FullName;
        if (string.IsNullOrWhiteSpace(approval.CurrentApproverRole)) approval.CurrentApproverRole = approver.Designation ?? approval.CurrentApproverType;
        approval.CurrentQueue = $"{approval.CurrentApproverType}:{approver.FullName}";
    }

    private async Task<int?> ResolveApproverEmployeeIdAsync(Guid tenantId, int? subjectEmployeeId, ApprovalWorkflowStep step, CancellationToken cancellationToken)
    {
        var type = Clean(step.ApproverType).ToUpperInvariant();
        if (type == "SPECIFICEMPLOYEE") return step.SpecificEmployeeId;
        if (subjectEmployeeId is null) return null;

        var hierarchy = await _hierarchy.ResolveHierarchyAsync(tenantId, subjectEmployeeId.Value, 20, cancellationToken);
        return type switch
        {
            "MANAGER" or "DIRECTMANAGER" => hierarchy.DirectManager?.EmployeeId,
            "SENIORMANAGER" or "SECONDLEVELMANAGER" => hierarchy.SecondLevelManager?.EmployeeId,
            "SUPERVISOR" => await _db.Employees.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == subjectEmployeeId.Value).Select(x => x.SupervisorEmployeeId).FirstOrDefaultAsync(cancellationToken),
            "DEPARTMENTHEAD" => hierarchy.DepartmentHead?.EmployeeId,
            "COMPANYHEAD" => hierarchy.CompanyHead?.EmployeeId,
            "HRBUSINESSPARTNER" => await _db.Employees.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == subjectEmployeeId.Value).Select(x => x.HRBusinessPartnerEmployeeId).FirstOrDefaultAsync(cancellationToken),
            _ => null
        };
    }

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
        if (exists) throw new InvalidOperationException("Approval workflow code already exists in this tenant.");
    }

    private async Task<bool> CanDecideStepAsync(ApprovalRequest approval, ApprovalWorkflowStep step, RequestContext context, CancellationToken cancellationToken)
    {
        return await CanDecideRequestAsync(approval, context, cancellationToken);
    }

    private async Task<bool> CanDecideRequestAsync(ApprovalRequest approval, RequestContext? context, CancellationToken cancellationToken)
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
        if (await CanDecideRequestAsync(approval, context, cancellationToken)) return true;
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
        ApplyEmployeeChange(employee, changes);
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
}
