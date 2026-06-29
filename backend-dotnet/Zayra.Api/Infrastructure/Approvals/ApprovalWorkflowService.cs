using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Approvals;

public class ApprovalWorkflowService : IApprovalWorkflowService
{
    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;

    public ApprovalWorkflowService(ZayraDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
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
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _db.ApprovalRequests.AsNoTracking().Include(x => x.Decisions).Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(entityName)) query = query.Where(x => x.EntityName == entityName);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).Select(x => x.ToDto()).ToListAsync(cancellationToken);
        return new PagedResult<ApprovalRequestDto>(items, total, page, pageSize);
    }

    public async Task<ApprovalRequestDto?> GetRequestAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
    {
        var request = await _db.ApprovalRequests.AsNoTracking().Include(x => x.Decisions).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, cancellationToken);
        return request?.ToDto();
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
            RequestedByUserId = context.UserId
        };
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

        // Segregation of duties: the requester can never approve their own request, and no single
        // user may satisfy more than one step of the same request (prevents one person pushing a
        // request through end-to-end).
        if (approval.RequestedByUserId is not null && approval.RequestedByUserId == context.UserId)
            throw new InvalidOperationException("You cannot approve your own request.");
        if (context.UserId is not null && approval.Decisions.Any(d => d.DecidedByUserId == context.UserId))
            throw new InvalidOperationException("You have already recorded a decision on this request.");

        var step = await _db.ApprovalWorkflowSteps.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.WorkflowId == approval.WorkflowId && x.StepOrder == approval.CurrentStepOrder, cancellationToken)
            ?? throw new InvalidOperationException("Current approval step was not found.");

        // Approver-binding: when a step targets a specific employee, only that employee may decide it
        // (an in-role colleague cannot stand in). Role-typed steps remain gated by the controller role.
        if (step.ApproverType == "SpecificEmployee" && step.SpecificEmployeeId is int reqEmpId)
        {
            var callerEmpId = await _db.Employees.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.UserAccountId == context.UserId)
                .Select(e => (int?)e.Id).FirstOrDefaultAsync(cancellationToken);
            if (callerEmpId != reqEmpId)
                throw new InvalidOperationException("This step must be approved by the assigned approver.");
        }

        var normalizedDecision = request.Decision.Equals("Reject", StringComparison.OrdinalIgnoreCase) ? "Rejected" : "Approved";
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
        }
        else if (step.IsFinalStep)
        {
            approval.Status = "Approved";
            approval.CompletedAtUtc = DateTime.UtcNow;
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
            });
        }
    }

    private async Task EnsureWorkflowCodeUnique(Guid tenantId, string code, Guid? excludedId, CancellationToken cancellationToken)
    {
        var clean = Clean(code).ToUpperInvariant();
        var exists = await _db.ApprovalWorkflows.AnyAsync(x => x.TenantId == tenantId && x.Code == clean && x.Id != excludedId, cancellationToken);
        if (exists) throw new InvalidOperationException("Approval workflow code already exists in this tenant.");
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
}
