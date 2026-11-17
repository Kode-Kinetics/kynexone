using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Recruitment;

public enum HireReleaseOutcome { Released, AlreadyReleased, NotAnOfferHire, EmployeeAlreadyCreated }

public sealed record HireReleaseResult(HireReleaseOutcome Outcome, string Message);

/// <summary>
/// The way back when an offer's hire draft is rejected or withdrawn before it became an employee: the candidate returns to the Offer stage
/// (the stage before Hired) as an active application, the offer is acceptable again, and the opening's seat is released. Compare-and-swap on
/// the application's link to this draft, so it happens exactly once however often it is called, and the acceptance stays on the
/// timeline with a "HireReleased" event beside it. It stages tracked audit rows and set-based updates and does NOT save: the caller runs it
/// inside the same unit of work that closes the draft. A hire that already produced an employee is never reversed here.
/// </summary>
public static class OfferHireReversal
{
    public static async Task<HireReleaseResult> ReleaseAsync(ZayraDbContext db, Guid tenantId, Guid draftId,
        Guid? actorUserId, string actorName, string reason, CancellationToken ct)
    {
        var draft = await db.EmployeeDrafts.AsNoTracking()
            .Where(d => d.Id == draftId && d.TenantId == tenantId)
            .Select(d => new { d.Status, d.AcceptedOfferId }).FirstOrDefaultAsync(ct);
        if (draft?.AcceptedOfferId is not Guid offerId)
            return new(HireReleaseOutcome.NotAnOfferHire, "This draft did not come from an accepted offer.");
        if (draft.Status == EmployeeDraftStatuses.Activated)
            return new(HireReleaseOutcome.EmployeeAlreadyCreated,
                "An employee has already been created from this hire, so the accepted offer cannot be reversed here.");

        var app = await db.JobApplications.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.OnboardingDraftId == draftId && a.Status == "Hired")
            .Select(a => new { a.Id, a.JobOpeningId }).FirstOrDefaultAsync(ct);
        if (app is null) return new(HireReleaseOutcome.AlreadyReleased, "This hire was already released.");

        var now = DateTime.UtcNow;
        var offerStage = "Offer";
        if (db.Database.IsRelational())
        {
            var released = await db.JobApplications
                .Where(a => a.Id == app.Id && a.TenantId == tenantId && a.OnboardingDraftId == draftId && a.Status == "Hired")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.OnboardingDraftId, (Guid?)null)
                    .SetProperty(a => a.Version, a => a.Version + 1)
                    .SetProperty(a => a.Stage, offerStage)
                    .SetProperty(a => a.StageOrder, RecruitmentStages.OrderOf(offerStage))
                    .SetProperty(a => a.Status, "Active")
                    .SetProperty(a => a.HiredAtUtc, (DateTime?)null)
                    .SetProperty(a => a.StageChangedAtUtc, (DateTime?)now), ct);
            if (released != 1) return new(HireReleaseOutcome.AlreadyReleased, "This hire was already released.");
            await db.OfferLetters
                .Where(o => o.Id == offerId && o.TenantId == tenantId && o.Status == "Accepted")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status, "Sent")
                    .SetProperty(o => o.AcceptedAtUtc, (DateTime?)null)
                    .SetProperty(o => o.Version, o => o.Version + 1), ct);
            await db.JobOpenings
                .Where(j => j.Id == app.JobOpeningId && j.TenantId == tenantId && j.FilledCount > 0)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, j => j.Status == "Closed" && j.FilledCount >= j.HeadCount
                        ? (j.FilledCount - 1 > 0 ? "InProgress" : "Open") : j.Status)
                    .SetProperty(j => j.FilledCount, j => j.FilledCount - 1), ct);
        }
        else
        {
            // EF's in-memory provider cannot run set-based updates; the same state machine on the tracked rows.
            var tracked = await db.JobApplications.FirstAsync(a => a.Id == app.Id && a.TenantId == tenantId, ct);
            tracked.OnboardingDraftId = null; tracked.Version++; tracked.Stage = offerStage;
            tracked.StageOrder = RecruitmentStages.OrderOf(offerStage); tracked.Status = "Active";
            tracked.HiredAtUtc = null; tracked.StageChangedAtUtc = now;
            var offer = await db.OfferLetters.FirstOrDefaultAsync(o => o.Id == offerId && o.TenantId == tenantId && o.Status == "Accepted", ct);
            if (offer is not null) { offer.Status = "Sent"; offer.AcceptedAtUtc = null; offer.Version++; }
            var opening = await db.JobOpenings.FirstOrDefaultAsync(j => j.Id == app.JobOpeningId && j.TenantId == tenantId && j.FilledCount > 0, ct);
            if (opening is not null)
            {
                if (opening.Status == "Closed" && opening.FilledCount >= opening.HeadCount) opening.Status = opening.FilledCount - 1 > 0 ? "InProgress" : "Open";
                opening.FilledCount--;
            }
        }

        var why = string.IsNullOrWhiteSpace(reason) ? "no reason given" : reason.Trim();
        db.ApplicationEvents.Add(new ApplicationEvent
        {
            TenantId = tenantId, ApplicationId = app.Id, EventType = "HireReleased", Stage = offerStage, FromStage = "Hired", ToStage = offerStage,
            Notes = $"The hire draft was closed ({draft.Status}): {why}. The seat was released and the candidate is back at the Offer stage.",
            PerformedByUserId = actorUserId, PerformedByName = actorName,
        });
        db.RecruitmentAuditLogs.Add(new RecruitmentAuditLog
        {
            TenantId = tenantId, EntityType = "Offer", EntityId = offerId.ToString(), Action = "HireReleased",
            PerformedByUserId = actorUserId, PerformedByName = actorName,
            OldValuesJson = JsonSerializer.Serialize(new { status = "Accepted", onboardingDraftId = draftId }),
            NewValuesJson = JsonSerializer.Serialize(new { status = "Sent", draftStatus = draft.Status, reason = why }),
        });
        return new(HireReleaseOutcome.Released, "The seat was released and the candidate returned to the Offer stage.");
    }
}
