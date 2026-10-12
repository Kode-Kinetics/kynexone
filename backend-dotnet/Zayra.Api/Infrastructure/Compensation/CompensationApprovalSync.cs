using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Compensation;

/// <summary>
/// Projects a shared-approval decision onto the merit worksheet it decided, inside
/// <c>ApprovalWorkflowService.DecideAsync</c>'s single SaveChanges (same shape as TimesheetApprovalSync): the decision
/// row and the worksheet state commit or roll back together, whether the decision is taken in the Approval Center or
/// anywhere else. Nothing is saved here.
///
/// <para><b>Separation of duties, on top of the engine's.</b> The engine already bars the requester, the subject (the
/// worksheet's manager, <c>RequestedForEmployeeId</c>) and anyone who decided an earlier step. A worksheet is about many
/// people, so <see cref="ValidateDecisionAsync"/> also bars every employee whose pay is on it, and requires the
/// compensation approver permission in addition to the step's role. A rejection needs a reason: it returns the
/// worksheet to the manager for revision.</para>
/// </summary>
public static class CompensationApprovalSync
{
    public const string EntityName = "CompensationWorksheet";

    public static bool IsWorksheet(ApprovalRequest approval) =>
        string.Equals(approval.EntityName, EntityName, StringComparison.OrdinalIgnoreCase);

    /// <param name="decision">The requested decision: "Approve" or "Reject".</param>
    public static async Task ValidateDecisionAsync(ZayraDbContext db, ApprovalRequest approval, string decision, RequestContext context, string? comments, CancellationToken ct)
    {
        if (!IsWorksheet(approval)) return;
        if (!Guid.TryParse(approval.EntityId, out var worksheetId)) throw new InvalidOperationException("The approval is not linked to a merit worksheet.");
        var sheet = await db.CompWorksheets.AsNoTracking().FirstOrDefaultAsync(w => w.TenantId == approval.TenantId && w.Id == worksheetId, ct)
            ?? throw new InvalidOperationException("The merit worksheet for this approval was not found.");
        if (sheet.Status != CompWorksheetStatuses.Submitted)
            throw new InvalidOperationException($"This merit worksheet is {sheet.Status.ToLowerInvariant()}, so it cannot be decided.");
        var cycleStatus = await db.CompCycles.AsNoTracking().Where(c => c.Id == sheet.CycleId).Select(c => c.Status).FirstOrDefaultAsync(ct);
        if (cycleStatus != CompCycleStatuses.Planning)
            throw new InvalidOperationException($"The merit cycle is {cycleStatus}, so its worksheets can no longer be decided.");
        if (!(context.Permissions ?? Array.Empty<string>()).Contains(CompPermissions.Approve, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Deciding a merit worksheet needs the compensation approver permission.");
        if (context.UserId is Guid userId)
        {
            var callerEmployees = await Zayra.Api.Infrastructure.Data.ScopedBypass.NullableTenantWide(db.Employees, approval.TenantId,
                    "Merit worksheet SoD: every employee row linked to the decider, whatever company they are employed by. Tenant re-applied.")
                .AsNoTracking().Where(e => e.UserAccountId == userId).Select(e => e.Id).ToListAsync(ct);
            if (callerEmployees.Count > 0 && await db.CompWorksheetLines.AnyAsync(l => l.WorksheetId == sheet.Id && callerEmployees.Contains(l.EmployeeId), ct))
                throw new InvalidOperationException("Segregation of duties: this worksheet includes your own pay, so you cannot approve or reject it.");
        }
        // Everyone who changed this revision (not only the last editor) is barred: HR who adjusted a line and then let the
        // manager save again would otherwise approve their own figures.
        if (context.UserId is Guid editor && (sheet.EditedBy(editor) || sheet.SubmittedByUserId == editor))
            throw new InvalidOperationException("Segregation of duties: you changed or submitted this revision of the worksheet, so a different approver must decide it.");
        if (string.Equals(decision, "Reject", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(comments))
            throw new InvalidOperationException("Say what the manager should change: returning a merit worksheet needs a comment.");
    }

    /// <summary>Rejected ⇒ Returned (revision +1, editable, with the approver's comments). Final approval ⇒ Approved
    /// (locked). Intermediate steps change nothing. Replays no-op: only a Submitted worksheet moves.</summary>
    public static async Task ApplyAsync(ZayraDbContext db, ApprovalRequest approval, string decision, RequestContext context, string? comments, CancellationToken ct)
    {
        if (!IsWorksheet(approval)) return;
        if (!Guid.TryParse(approval.EntityId, out var worksheetId)) return;
        var sheet = await db.CompWorksheets.FirstOrDefaultAsync(w => w.TenantId == approval.TenantId && w.Id == worksheetId, ct);
        if (sheet is null || sheet.Status != CompWorksheetStatuses.Submitted) return;
        var now = DateTime.UtcNow;
        var rejected = !string.Equals(decision, "Approved", StringComparison.OrdinalIgnoreCase);
        sheet.Status = rejected ? CompWorksheetStatuses.Returned : CompWorksheetStatuses.Approved;
        if (rejected) sheet.StartRevision();
        sheet.DecisionComments = string.IsNullOrWhiteSpace(comments) ? null : comments.Trim()[..Math.Min(comments.Trim().Length, 1000)];
        sheet.DecidedAtUtc = now;
        sheet.UpdatedAtUtc = now;
        sheet.Version++;
        // Staged into the decision's own SaveChanges, so the audit row exists exactly when the state change does.
        db.AuditLogs.Add(new Zayra.Api.Domain.Entities.AuditLog
        {
            TenantId = approval.TenantId,
            CompanyId = sheet.CompanyId,
            UserId = context.UserId,
            Action = rejected ? "compensation.worksheet.returned" : "compensation.worksheet.approved",
            EntityName = nameof(CompWorksheet),
            EntityId = sheet.Id.ToString(),
            Metadata = JsonSerializer.Serialize(new { approvalRequestId = approval.Id, revision = sheet.Revision, comments = sheet.DecisionComments }),
            CreatedAtUtc = now,
        });
    }

    /// <summary>
    /// The shared approval rows of some worksheets were closed WITHOUT a decision (their owner was removed or merged:
    /// EmployeesController's soft-remove cascade). A Submitted worksheet with no live approval would otherwise be stranded,
    /// so it returns to its owner's queue as Returned (revision +1) with the reason, and HR can reassign or close it.
    /// Stages only; the caller saves.
    /// </summary>
    public static async Task OnApprovalsClosedAsync(ZayraDbContext db, Guid tenantId, IEnumerable<ApprovalRequest> closed, string reason, Guid? actorUserId, CancellationToken ct)
    {
        var ids = closed.Where(IsWorksheet).Select(a => a.Id).ToList();
        if (ids.Count == 0) return;
        var sheets = await db.CompWorksheets.Where(w => w.TenantId == tenantId && w.ApprovalRequestId != null && ids.Contains(w.ApprovalRequestId.Value)
                                                     && w.Status == CompWorksheetStatuses.Submitted).ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var sheet in sheets)
        {
            sheet.Status = CompWorksheetStatuses.Returned;
            sheet.StartRevision();
            var note = $"The approval was closed without a decision: {reason} HR can assign this team to another worksheet owner or close the worksheet.";
            sheet.DecisionComments = note.Length > 1000 ? note[..1000] : note;
            sheet.DecidedAtUtc = now;
            sheet.UpdatedAtUtc = now;
            sheet.Version++;
            db.AuditLogs.Add(new Zayra.Api.Domain.Entities.AuditLog
            {
                TenantId = tenantId, CompanyId = sheet.CompanyId, UserId = actorUserId, Action = "compensation.worksheet.approval_closed",
                EntityName = nameof(CompWorksheet), EntityId = sheet.Id.ToString(),
                Metadata = JsonSerializer.Serialize(new { approvalRequestId = sheet.ApprovalRequestId, revision = sheet.Revision, reason }), CreatedAtUtc = now,
            });
        }
    }
}
