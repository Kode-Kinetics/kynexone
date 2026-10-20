using Zayra.Api.Application.Recruitment;
using Zayra.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Zayra.Api.Infrastructure.Recruitment;

public sealed partial class OfferLifecycleService
{
    public Task<OfferApproval> AddApprovalAsync(OfferActor actor, Guid offerId, long? expectedVersion,
        Guid? approverId, string name, string? role, CancellationToken ct = default) =>
        MutateAsync(actor, offerId, expectedVersion, false, async (app, offer) =>
        {
            RequireApprovalVersion(expectedVersion);
            OfferApplicationBoundary.RequireUnreserved(app); OfferApplicationBoundary.RequireActive(app);
            await RequireApprovalOriginalAsync(actor, offer, ct);
            if (offer.Status is not ("Draft" or "PendingApproval"))
                throw new OfferLifecycleException("invalid_offer_state", "Approval steps require Draft or PendingApproval status.");
            if (await OfferRules.HasRejectedApprovalAsync(db, actor.TenantId, offerId, ct))
                throw new OfferLifecycleException("offer_approval_rejected", OfferRules.ApprovalRejectedMessage);
            var verdict = await OfferRules.EvaluateApproverAsync(db, actor.TenantId, offerId, approverId, ct);
            if (verdict != OfferApproverVerdict.Eligible)
            {
                var refusal = OfferRules.ApproverRefusal(verdict);
                throw new OfferLifecycleException(refusal.Error, refusal.Message,
                    verdict == OfferApproverVerdict.ApproverIsAuthor ? 403 : verdict == OfferApproverVerdict.AuthorUnknown ? 409 : 422);
            }
            var steps = await db.OfferApprovals.Where(a => a.TenantId == actor.TenantId && a.OfferLetterId == offerId).ToListAsync(ct);
            if (steps.Any(a => a.ApproverUserId == approverId && a.Status is "Pending" or "Approved"))
                throw new OfferLifecycleException("offer_approver_already_named", "This person is already an approver on the offer.");
            var approval = new OfferApproval { TenantId = actor.TenantId, OfferLetterId = offerId, ApplicationId = app.Id,
                StepOrder = steps.Count == 0 ? 1 : steps.Max(a => a.StepOrder) + 1,
                ApproverUserId = approverId, ApproverName = string.IsNullOrWhiteSpace(name)
                    ? await db.Users.Where(u => u.TenantId == actor.TenantId && u.Id == approverId).Select(u => u.FullName).SingleAsync(ct) : name,
                ApproverRole = role ?? string.Empty };
            db.OfferApprovals.Add(approval); offer.Status = "PendingApproval"; offer.Version++; app.Version++;
            AddEvent(actor, offer, "ApprovalRequested", "Submitted the frozen original to a named checker.", new { approval.Id, approval.StepOrder, approval.ApproverUserId, offer.ArtifactId });
            db.RecruitmentAuditLogs.Add(OfferRules.AuditRow(actor.TenantId, offerId, "ApprovalRequested", actor.UserId, actor.Name,
                new { approval.StepOrder, approval.ApproverUserId, approval.ApproverName }));
            return approval;
        }, ct);

    public Task<OfferApproval> DecideApprovalAsync(OfferActor actor, Guid offerId, Guid approvalId, long? expectedVersion,
        string decision, string? comments, CancellationToken ct = default) =>
        MutateAsync(actor, offerId, expectedVersion, false, async (app, offer) =>
        {
            RequireApprovalVersion(expectedVersion);
            OfferApplicationBoundary.RequireUnreserved(app); OfferApplicationBoundary.RequireActive(app);
            await RequireApprovalOriginalAsync(actor, offer, ct);
            if (decision is not ("Approved" or "Rejected")) throw new OfferLifecycleException("invalid_decision", "Decision must be Approved or Rejected.", 422);
            var approval = await db.OfferApprovals.SingleOrDefaultAsync(a => a.TenantId == actor.TenantId && a.OfferLetterId == offerId && a.Id == approvalId, ct)
                ?? throw new OfferLifecycleException("offer_approval_not_found", "Approval step not found.", 404);
            if (approval.Status != "Pending") throw new OfferLifecycleException("approval_already_decided", "This step has already been decided.");
            if (offer.Status != "PendingApproval") throw new OfferLifecycleException("invalid_offer_state", "Approval decisions require PendingApproval status.");
            if (decision == "Approved" && (await OfferRules.TermsWritersAsync(db, actor.TenantId, offerId, ct)).Contains(actor.UserId))
                throw new OfferLifecycleException("offer_maker_checker", "The person who wrote the offer cannot approve it.", 403);
            if (approval.ApproverUserId != actor.UserId)
                throw new OfferLifecycleException("not_the_named_approver", "Only the named approver can decide this step.", 403);
            if (!await OfferRules.EligibleApprovers(db, actor.TenantId).AnyAsync(u => u.Id == actor.UserId, ct))
                throw new OfferLifecycleException("offer_approver_ineligible", "The named approver is no longer eligible.", 403);
            if (await db.OfferApprovals.AnyAsync(a => a.TenantId == actor.TenantId && a.OfferLetterId == offerId && a.Id != approvalId
                && a.Status == "Approved" && a.ApproverUserId == actor.UserId, ct))
                throw new OfferLifecycleException("offer_earlier_step_approver", "A different person must decide this step.", 403);
            approval.Status = decision; approval.Comments = comments ?? string.Empty; approval.DecidedAtUtc = DateTime.UtcNow;
            if (decision == "Rejected") offer.Status = "Draft";
            else if (!await db.OfferApprovals.AnyAsync(a => a.TenantId == actor.TenantId && a.OfferLetterId == offerId && a.Id != approvalId && a.Status != "Approved", ct))
                offer.Status = "Approved";
            offer.Version++; app.Version++;
            AddEvent(actor, offer, "ApprovalDecided", comments ?? decision, new { approvalId, decision, offer.ArtifactId });
            db.RecruitmentAuditLogs.Add(OfferRules.AuditRow(actor.TenantId, offerId, OfferRules.ApprovalDecidedAction, actor.UserId, actor.Name,
                new { approvalId, approval.StepOrder, decision, offerStatus = offer.Status }));
            return approval;
        }, ct);

    private static void RequireApprovalVersion(long? version)
    {
        if (version is null) throw new OfferLifecycleException("offer_version_required", "Reload the offer and submit its expectedVersion.");
    }

    private async Task RequireApprovalOriginalAsync(OfferActor actor, OfferLetter offer, CancellationToken ct)
    {
        if (offer.SeriesId is null) throw new OfferLifecycleException("offer_revision_required", "Legacy submitted terms cannot endorse a new original. Adopt an unsubmitted draft or create a managed revision.");
        if (offer.ArtifactId is null || offer.FrozenAtUtc is null
            || !await db.OfferArtifacts.AnyAsync(a => a.TenantId == actor.TenantId && a.CompanyId == actor.CompanyId && a.Id == offer.ArtifactId
                && a.OfferId == offer.Id && a.State == "Finalized", ct))
            throw new OfferLifecycleException("offer_artifact_pending", "Finalize the reviewed original before requesting or deciding approval.");
        if (artifacts is null) throw new OfferLifecycleException("offer_storage_unavailable", "Original document verification is unavailable.");
        await artifacts.OriginalAsync(actor, offer.Id, ct);
    }
}
