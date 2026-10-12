using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Succession;

/// <summary>
/// Projects a shared-approval decision onto the succession slate version it decided, inside
/// <c>ApprovalWorkflowService.DecideAsync</c>'s single SaveChanges (the RequisitionApprovalSync pattern): the decision
/// row and the slate's new state commit or roll back together. Also the slate's separation of duties, consulted by the
/// engine's <c>CanDecideRequestAsync</c> and by the decision itself, so "can decide" and "may decide" never disagree.
///
/// <para><b>Leak-proof by construction.</b> A slate approval has <c>RequestedForEmployeeId = null</c> and a title and
/// payload of ids and a hash only. The team queue lists requests about the caller's reports — a slate is about nobody,
/// so it never appears there; the all-requests view (HR Manager, Admin, Auditor) shows "Succession slate vN · hash".
/// Candidates are visible only through the succession API, to the routed approver and to HR.</para>
/// </summary>
public static class SuccessionSlateApprovalSync
{
    /// <summary>The entity name the router and the shared aggregate know a slate version by.</summary>
    public const string EntityName = "SuccessionSlate";

    public const string DefaultWorkflowCode = "SUCCESSION-SLATE-DEFAULT";

    public static bool IsSlate(ApprovalRequest approval) => string.Equals(approval.EntityName, EntityName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// SoD: nobody who is a candidate in the snapshot, or reports (up the chain) to a candidate, may decide it. Throws
    /// <see cref="InvalidOperationException"/> (the engine's refusal type) with the reason; no-op for other entities.
    /// </summary>
    public static async Task ValidateDecisionAsync(ZayraDbContext db, ApprovalRequest approval, RequestContext? context, CancellationToken ct)
    {
        if (!IsSlate(approval)) return;
        var slate = await LoadAsync(db, approval, ct)
            ?? throw new InvalidOperationException("The succession slate this approval decides was not found.");
        var snapshot = SlateSnapshot.Parse(slate.SnapshotJson);
        var candidates = snapshot?.Candidates.Select(c => c.EmployeeId).ToList() ?? [];
        var access = new SuccessionAccess(db);
        // N2: every employee record the login is linked to; a login linked to none cannot be checked, so it cannot decide.
        var deciders = context?.UserId is Guid uid ? await access.EmployeesOfUserAsync(approval.TenantId, uid, ct) : [];
        if (deciders.Count == 0)
            throw new InvalidOperationException("Link your login to your employee record to decide succession slates: conflicts of interest are checked against it.");
        var decider = deciders[0];
        foreach (var id in deciders)
            if (await access.SlateDecisionBarAsync(approval.TenantId, id, candidates, ct) is { } bar)
                throw new InvalidOperationException(bar);
        // Also anyone who (or whose manager) is a live nominee for the position, even if this version leaves them off.
        if ((await access.BarAsync(approval.TenantId, deciders, ct)).Positions.Contains(slate.CriticalPositionId))
            throw new InvalidOperationException("Segregation of duties: you or someone you report to is a candidate for this position, so you cannot decide its slate.");
        // approvals.override alone does not reach a slate: its candidates are HR-confidential (Admin is withheld
        // succession.slates.read). The decider is the person or role the step is routed to, or HR who may read slates.
        var permissions = context?.Permissions ?? [];
        var roles = context?.Roles ?? [];
        var holdsSlates = permissions.Any(p => string.Equals(p, SuccessionPermissions.SlatesRead, StringComparison.OrdinalIgnoreCase));
        var routed = (approval.CurrentApproverUserId is Guid named && named == context?.UserId)
                     || (approval.CurrentApproverEmployeeId is int routedEmployee && routedEmployee == decider)
                     || (approval.CurrentApproverUserId is null && approval.CurrentApproverEmployeeId is null
                         && roles.Any(r => string.Equals(r, approval.CurrentApproverRole?.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (!holdsSlates && !routed)
            throw new InvalidOperationException(
                "A succession slate is decided by the approver it is routed to, or by HR with access to succession slates.");
    }

    /// <summary>
    /// Approved: the version becomes Approved and effective today, the previous Approved version of the position becomes
    /// Superseded (same transaction; the one-approved constraint is deferred to commit), its candidates go OnSlate, and
    /// candidates the new version dropped go back to Calibrated. Rejected: the version is ReturnedForRevision with the
    /// approver's reason (required). No-op for other entities, and for a slate that is not waiting on THIS request.
    /// </summary>
    public static async Task ApplyAsync(ZayraDbContext db, ApprovalRequest approval, string decision, string? comments, RequestContext? context,
        CancellationToken ct)
    {
        if (!IsSlate(approval)) return;
        var slate = await LoadAsync(db, approval, ct, tracked: true);
        if (slate is null || slate.Status != SlateStatuses.Submitted || slate.ApprovalRequestId != approval.Id) return;
        var now = DateTime.UtcNow;
        if (!string.Equals(decision, "Approved", StringComparison.OrdinalIgnoreCase))
        {
            var reason = comments?.Trim();
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidOperationException("Say why the slate is being returned: the reason goes back to HR with it.");
            slate.Status = SlateStatuses.ReturnedForRevision;
            slate.ReturnedAtUtc = now;
            slate.ReturnReason = reason.Length <= 1000 ? reason : reason[..1000];
            slate.DecidedByUserId = context?.UserId;
            slate.UpdatedAtUtc = now;
            slate.Version++;
            return;
        }

        var previous = await SuccessionDb.TenantRows(db.SuccessionSlateVersions, approval.TenantId)
            .Where(s => s.CriticalPositionId == slate.CriticalPositionId && s.Status == SlateStatuses.Approved && s.Id != slate.Id).ToListAsync(ct);
        foreach (var p in previous)
        {
            p.Status = SlateStatuses.Superseded;
            p.SupersededAtUtc = now;
            p.UpdatedAtUtc = now;
            p.Version++;
        }
        slate.Status = SlateStatuses.Approved;
        slate.ApprovedAtUtc = now;
        slate.EffectiveFrom = DateOnly.FromDateTime(now);
        slate.DecidedByUserId = context?.UserId;
        slate.UpdatedAtUtc = now;
        slate.Version++;

        var onSlate = (SlateSnapshot.Parse(slate.SnapshotJson)?.Candidates ?? []).Select(c => c.NominationId).ToHashSet();
        var nominations = await SuccessionDb.TenantRows(db.SuccessionNominations, approval.TenantId)
            .Where(n => n.CriticalPositionId == slate.CriticalPositionId
                        && (n.Status == NominationStatuses.Calibrated || n.Status == NominationStatuses.OnSlate)).ToListAsync(ct);
        foreach (var n in nominations)
        {
            var target = onSlate.Contains(n.Id) ? NominationStatuses.OnSlate : NominationStatuses.Calibrated;
            if (n.Status == target) continue;
            n.Status = target;
            n.UpdatedAtUtc = now;
            n.Version++;
        }
    }

    /// <summary>After the decision committed: the slate's own audit row, and the decision notice to whoever submitted it.</summary>
    public static async Task AfterCommitAsync(ZayraDbContext db, IAuditService audit, IServiceProvider? services, ApprovalRequest approval, string decision,
        RequestContext context, CancellationToken ct)
    {
        if (!IsSlate(approval) || !Guid.TryParse(approval.EntityId, out var slateId)) return;
        var slate = await SuccessionDb.TenantRows(db.SuccessionSlateVersions, approval.TenantId).AsNoTracking().FirstOrDefaultAsync(s => s.Id == slateId, ct);
        if (slate is null || slate.ApprovalRequestId != approval.Id) return;
        var approved = slate.Status == SlateStatuses.Approved;
        if (!approved && slate.Status != SlateStatuses.ReturnedForRevision) return;
        await audit.WriteAsync(approved ? "succession.slate.approved" : "succession.slate.returned", nameof(SuccessionSlateVersion), slate.Id.ToString(),
            context, JsonSerializer.Serialize(new { slateId = slate.Id, approvalRequestId = approval.Id, slate.VersionNo, slate.SnapshotSha256 }), ct);
        if (slate.SubmittedByUserId is not Guid submitter || services?.GetService(typeof(INotificationService)) is not INotificationService notifications) return;
        try
        {
            await notifications.EnqueueAsync(new NotificationRequest
            {
                TenantId = approval.TenantId, UserId = submitter, EventCode = SuccessionEventCodes.SlateDecided, EntityName = nameof(SuccessionSlateVersion),
                EntityId = slate.Id.ToString(),
                Title = approved ? $"Succession slate v{slate.VersionNo} approved" : $"Succession slate v{slate.VersionNo} returned for revision",
                Message = approved ? "The slate is now the approved succession plan for its position."
                    : "The approver returned the slate. Open it to read the reason and revise it.",
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A notice never undoes a committed decision.
            _ = ex;
        }
    }

    /// <summary>
    /// The retire path: closes a pending slate approval exactly as a requester's withdrawal does (Cancelled, completed now,
    /// decision version advanced so a decision racing it cannot also commit). Staged on the caller's context; the caller saves.
    /// </summary>
    public static async Task CancelPendingAsync(ZayraDbContext db, Guid tenantId, Guid approvalRequestId, DateTime now, CancellationToken ct)
    {
        var approval = await SuccessionDb.TenantRows(db.ApprovalRequests, tenantId).FirstOrDefaultAsync(a => a.Id == approvalRequestId, ct);
        if (approval is null || approval.Status != "Pending" || !IsSlate(approval)) return;
        approval.Status = "Cancelled";
        approval.CompletedAtUtc = now;
        approval.DecisionVersion++;
    }

    /// <summary>
    /// The default chain, installed once per tenant the first time a slate is submitted (insert-if-absent, never touching
    /// a chain the tenant configured): one HR Director step. HR Manager prepares and submits; HR Director decides, so the
    /// maker never checks their own slate. Tenants can edit it like any other workflow.
    /// </summary>
    public static async Task EnsureDefaultWorkflowAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        var exists = await SuccessionDb.TenantRows(db.ApprovalWorkflows, tenantId).AsNoTracking()
            .AnyAsync(w => w.EntityName == EntityName && w.IsActive || w.Code == DefaultWorkflowCode, ct);
        if (exists) return;
        var workflow = new ApprovalWorkflow
        {
            TenantId = tenantId, Code = DefaultWorkflowCode, Name = "Default Succession Slate Approval", EntityName = EntityName, IsDefault = true, IsActive = true,
        };
        workflow.Steps.Add(new ApprovalWorkflowStep
        {
            TenantId = tenantId, WorkflowId = workflow.Id, StepOrder = 1, StepName = "HR Director approval", ApproverType = "Role",
            ApproverRole = "HR Director", IsFinalStep = true,
        });
        db.ApprovalWorkflows.Add(workflow);
        await db.SaveChangesAsync(ct);
    }

    private static async Task<SuccessionSlateVersion?> LoadAsync(ZayraDbContext db, ApprovalRequest approval, CancellationToken ct, bool tracked = false)
    {
        if (!Guid.TryParse(approval.EntityId, out var slateId)) return null;
        var query = SuccessionDb.TenantRows(db.SuccessionSlateVersions, approval.TenantId).Where(s => s.Id == slateId);
        return await (tracked ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct);
    }
}
