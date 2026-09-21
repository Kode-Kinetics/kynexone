using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Recruitment;

/// <summary>
/// Projects a shared-approval decision onto the <see cref="ManpowerRequisition"/> it decided.
///
/// <para><b>The hole this closes.</b> <c>RequisitionsController.Submit</c> created an
/// <see cref="ApprovalRequest"/> on the shared aggregate and then nothing ever completed it: the
/// module's own <c>Approve</c>/<c>Reject</c> stamped the requisition and left the shared row
/// <c>Pending</c> forever. So a requisition could be Approved on one screen and still sitting in the
/// Approval Center queue on another, with no decision ledger, no maker-checker and no step routing —
/// two records of the same fact, permanently disagreeing. The requisition was also the only entity
/// with a producer and no seeded workflow, so in a fresh tenant the router returned null, no shared
/// row was created at all, and the approval existed nowhere but a status string.</para>
///
/// <para><b>Why a hook inside the engine.</b> Same reasoning as
/// <c>TimesheetApprovalSync</c> and <c>SyncEmployeeChangeDecisionAsync</c>: a requisition can be
/// decided from the Approval Center, which knows nothing about requisitions. A projection that only
/// runs when somebody opens the recruitment screen is the read step that always gets skipped. This
/// runs inside <c>ApprovalWorkflowService.DecideAsync</c>'s single <c>SaveChangesAsync</c>, so the
/// decision and the projected status commit or roll back together.</para>
///
/// <para>Nothing is saved here. The caller owns the save.</para>
/// </summary>
public static class RequisitionApprovalSync
{
    /// <summary>The entity name the router and the shared aggregate know a requisition by.</summary>
    public const string ApprovalEntityName = nameof(ManpowerRequisition);

    /// <summary>
    /// No-ops unless <paramref name="approval"/> is a requisition approval on a requisition that is
    /// still awaiting one, so a replayed or duplicated decision cannot overwrite a settled outcome.
    /// </summary>
    public static async Task ApplyAsync(
        ZayraDbContext db, ApprovalRequest approval, string decision, string? comments, CancellationToken ct)
    {
        if (!string.Equals(approval.EntityName, ApprovalEntityName, StringComparison.OrdinalIgnoreCase)) return;
        if (!Guid.TryParse(approval.EntityId, out var requisitionId)) return;

        var requisition = await db.ManpowerRequisitions
            .FirstOrDefaultAsync(r => r.TenantId == approval.TenantId && r.Id == requisitionId, ct);
        if (requisition is null) return;
        if (requisition.Status is not ("Submitted" or "PendingApproval")) return;

        var now = DateTime.UtcNow;
        if (string.Equals(decision, "Approved", StringComparison.OrdinalIgnoreCase))
        {
            requisition.Status = "Approved";
            requisition.ApprovedAtUtc = now;
            requisition.RejectionReason = string.Empty;
            return;
        }

        requisition.Status = "Rejected";
        requisition.RejectedAtUtc = now;
        // The requester is handed the reason the approver gave, not a bare "Rejected". An empty
        // comment stays empty rather than inventing text the approver did not write.
        requisition.RejectionReason = comments?.Trim() ?? string.Empty;
    }
}
