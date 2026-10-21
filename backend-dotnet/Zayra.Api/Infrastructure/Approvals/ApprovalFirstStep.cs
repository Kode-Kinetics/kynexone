using System.Text.Json;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Approvals;

/// <summary>
/// First-step routing for the producers that write their <see cref="ApprovalRequest"/> themselves instead of through
/// <c>IApprovalWorkflowService.CreateRequestAsync</c>: benefit claims, additional benefit grants and Jawazat. Same rule
/// as everywhere else (<see cref="IApprovalRouter.CheckDeciderAsync"/>): the requester and every login of the subject
/// never count; nobody at all ⇒ <see cref="ApprovalNoEligibleDeciderException"/> before anything is written; the step's
/// own approver cannot act but Admin / approvals.override can ⇒ the Admin queue, with the same
/// <c>approval.routed_to_override</c> audit row staged in the caller's unit of work.
/// </summary>
public static class ApprovalFirstStep
{
    public const string OverrideRoutedAction = "approval.routed_to_override";

    /// <param name="Approver">Who the first step goes to (the override queue when <paramref name="OverrideAudit"/> is set).</param>
    /// <param name="OverrideAudit">Audit metadata when the step was moved to the override queue; null otherwise.</param>
    public sealed record Routed(ResolvedApprover Approver, string? OverrideAudit);

    public static async Task<Routed> RouteAsync(ZayraDbContext db, IApprovalRouter router, Guid tenantId, string entityName,
        int subjectEmployeeId, Guid? requesterUserId, ApprovalRouteStep step, CancellationToken ct)
    {
        var approver = await router.ResolveApproverAsync(tenantId, subjectEmployeeId, step, ct);
        var excluded = new HashSet<Guid>(await ApprovalUnblock.SubjectUserIdsAsync(db, tenantId, subjectEmployeeId, ct));
        if (requesterUserId is Guid requester) excluded.Add(requester);
        var check = await router.CheckDeciderAsync(tenantId, approver, excluded, null, ct);
        if (check.Eligible) return new Routed(approver, null);
        if (!check.OverrideAvailable)
            throw new ApprovalNoEligibleDeciderException(tenantId, entityName, step.StepOrder, step.StepName, check.Owner, check.NamedPerson);
        return new Routed(ApprovalRouter.OverrideQueue(step.ApproverType), JsonSerializer.Serialize(new
        {
            stepOrder = step.StepOrder,
            stepName = step.StepName,
            routedTo = check.Owner,
            namedPerson = check.NamedPerson,
            reason = check.NamedPerson ? "named_approver_has_no_active_login" : "role_has_no_eligible_holder",
            movedTo = IApprovalRouter.OverrideQueueRole,
            atSubmission = true,
            overrideQueueEmpty = false,
        }));
    }

    /// <summary>Marks a just-built request as escalated to the override queue and stages its audit row.</summary>
    public static void StageOverride(ZayraDbContext db, ApprovalRequest approval, Guid? actorUserId, Routed routed)
    {
        if (routed.OverrideAudit is null) return;
        approval.EscalatedToRole = IApprovalRouter.OverrideQueueRole;
        approval.EscalatedAtUtc = DateTime.UtcNow;
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = approval.TenantId, CompanyId = approval.CompanyId, UserId = actorUserId,
            EntityName = nameof(ApprovalRequest), EntityId = approval.Id.ToString(), Action = OverrideRoutedAction,
            Metadata = routed.OverrideAudit, CreatedAtUtc = DateTime.UtcNow,
        });
    }
}
