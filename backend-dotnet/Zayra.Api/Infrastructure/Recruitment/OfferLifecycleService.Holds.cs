using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Recruitment;

public sealed partial class OfferLifecycleService
{
    /// <summary>
    /// When a candidate is blacklisted, every offer of theirs still waiting in the dispatch queue is held: the queued email is cancelled (never sent), the
    /// application's reservation is released so the offer can be reviewed again, and the offer's timeline and the recruitment audit log say why and by whom.
    /// Returns how many were held. Managed offers only exist while the lifecycle is on, so with it off there is nothing to hold.
    /// </summary>
    public async Task<int> HoldQueuedDispatchesForBlacklistedCandidateAsync(Guid tenantId, Guid candidateId, Guid userId, string userName, CancellationToken ct = default)
    {
        if (!Enabled) return 0;
        var queued = await db.OfferDispatches.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.Outcome == "Queued"
                && db.JobApplications.Any(a => a.TenantId == tenantId && a.Id == d.ApplicationId && a.CandidateId == candidateId))
            .Select(d => new { d.Id, d.OfferId, d.CompanyId }).ToListAsync(ct);
        var held = 0;
        foreach (var row in queued)
        {
            var actor = new OfferActor(tenantId, row.CompanyId, userId, userName);
            var done = await MutateAsync(actor, row.OfferId, null, true, async (app, offer) =>
            {
                var local = db.OfferDispatches.Local.FirstOrDefault(d => d.Id == row.Id);
                if (local is not null) db.Entry(local).State = EntityState.Detached;
                var dispatch = await db.OfferDispatches.SingleOrDefaultAsync(d => d.TenantId == tenantId && d.Id == row.Id && d.OfferId == offer.Id, ct);
                if (dispatch is null || dispatch.Outcome != "Queued") return false;
                ReleaseReservationLocked(app, offer.Id, dispatch.Id, dispatch.ReservationVersion);
                dispatch.Outcome = "Cancelled"; dispatch.Version++; offer.CommunicationOutcome = "Cancelled"; offer.Version++;
                AddEvent(actor, offer, "EmailHeld", "Held, not sent: the candidate was blacklisted.", new { dispatchId = dispatch.Id });
                db.RecruitmentAuditLogs.Add(new RecruitmentAuditLog
                {
                    TenantId = tenantId, EntityType = "Offer", EntityId = offer.Id.ToString(), Action = "OfferDispatchHeld",
                    PerformedByUserId = userId, PerformedByName = userName,
                    NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { dispatchId = dispatch.Id, candidateId, because = "candidate_blacklisted" }),
                });
                return true;
            }, ct);
            if (done) held++;
        }
        return held;
    }
}
