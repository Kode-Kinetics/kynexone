using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Contracts;

// Release A slice R5 owns this file. R0 created it and wired the call inside ApprovalWorkflowService.DecideAsync
// (the shared hot spot), so R5 only fills ApplyAsync. Same pattern as RequisitionApprovalSync / TimesheetApprovalSync.

/// <summary>
/// Projects a shared-approval decision onto the renewal case it decided (T7 return, T8/T9 final approval), inside
/// <c>ApprovalWorkflowService.DecideAsync</c>'s single SaveChanges, so the decision and the case state commit or
/// roll back together. Nothing is saved here; the caller owns the save.
/// </summary>
public static class ContractRenewalApprovalSync
{
    /// <summary>One case's offer version.</summary>
    public const string ApprovalEntityName = "ContractRenewal";

    /// <summary>A fast-lane batch: approval enqueues per-case transitions and never applies inline.</summary>
    public const string BatchApprovalEntityName = "ContractRenewalBatch";

    public static readonly IReadOnlyList<string> EntityNames = [ApprovalEntityName, BatchApprovalEntityName];

    public static bool IsRenewal(ApprovalRequest approval) =>
        EntityNames.Contains(approval.EntityName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// No-op for every other approval. A renewal approval cannot exist until R5 registers its producer
    /// (ApprovalEntities), so reaching the renewal branch before then is a defect and fails loudly rather than
    /// recording a decision the case never sees.
    /// </summary>
    public static Task ApplyAsync(ZayraDbContext db, ApprovalRequest approval, string decision, string? comments, CancellationToken ct)
    {
        if (!IsRenewal(approval)) return Task.CompletedTask;
        throw new NotImplementedException("Contract renewal approvals arrive with Release A slice R5.");
    }
}
