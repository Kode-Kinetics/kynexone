using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Models;
using Zayra.Api.Data;
using System.Text.Json;

namespace Zayra.Api.Infrastructure.Recruitment;

public sealed partial class OfferLifecycleService
{
    public async Task<OfferResponseDto> RespondAsync(OfferActor actor, Guid offerId, OfferResponseRequest request, CancellationToken ct = default)
    {
        var offer = await ReadAsync(actor, offerId, ct);
        if (offer.SeriesId is not null) RequireEnabled();
        ValidateResponse(request);
        if (request.Decision == "Accepted")
        {
            var outcome = await new RecruitmentService(db).AcceptOfferResponseAsync(actor, offerId, request, ct);
            if (!outcome.IsSuccess)
                throw new OfferLifecycleException(outcome.Outcome == OfferAcceptanceOutcome.NotFound ? "offer_not_found" : "offer_response_blocked",
                    outcome.Message, outcome.Outcome == OfferAcceptanceOutcome.NotFound ? 404 : 409);
            offer = await ReadAsync(actor, offerId, ct);
            return new(offer.Id, offer.Version, offer.Status, outcome.OnboardingDraftId, !outcome.WasAcceptedNow);
        }
        return await MutateAsync(actor, offerId, request.ExpectedVersion, false, async (app, current) =>
        {
            await RequireResponseEligibleAsync(db, current, app, request, ct);
            current.Status = "Declined"; current.DeclinedAtUtc = request.OccurredAtUtc;
            current.DeclineReason = request.Reason; current.Version++; app.Version++;
            AddResponseEvent(db, actor, current, request);
            db.RecruitmentAuditLogs.Add(OfferRules.AuditRow(actor.TenantId, current.Id, "Declined", actor.UserId, actor.Name, request));
            return new OfferResponseDto(current.Id, current.Version, current.Status, null, false);
        }, ct);
    }

    internal static void ValidateResponse(OfferResponseRequest request)
    {
        RequireReason(request.Reason);
        if (request.Decision is not ("Accepted" or "Declined") || request.Source != "StaffRecorded"
            || request.OccurredAtUtc.Kind != DateTimeKind.Utc || request.OccurredAtUtc > DateTime.UtcNow
            || request.OccurredAtUtc == DateTime.MinValue || request.EvidenceReference?.Length > 2000)
            throw new OfferLifecycleException("offer_response_evidence_invalid", "Record Accepted or Declined, source StaffRecorded, the actual UTC response time, and a reason.", 422);
    }

    internal static async Task RequireResponseEligibleAsync(ZayraDbContext context, OfferLetter offer, JobApplication app,
        OfferResponseRequest request, CancellationToken ct)
    {
        OfferApplicationBoundary.RequireUnreserved(app); OfferApplicationBoundary.RequireActive(app);
        if (offer.Status != "Sent") throw new OfferLifecycleException("invalid_offer_state", OfferRules.DeclineStateMessage);
        if (offer.SupersededAtUtc is not null || (app.CurrentIssuedOfferId is not null && app.CurrentIssuedOfferId != offer.Id))
            throw new OfferLifecycleException("offer_not_current", "This offer is no longer the current issued revision.");
        if (offer.SentAtUtc is { } sent && request.OccurredAtUtc < sent)
            throw new OfferLifecycleException("offer_response_evidence_invalid", "The response cannot precede issuance.", 422);
        if (offer.SeriesId is null) return; // Legacy sent rows are explicitly delivery-unverified, not fabricated originals.
        if (offer.ResponseDeadline is null || offer.ResponseDeadline <= DateTime.UtcNow)
            throw new OfferLifecycleException("offer_expired", "The frozen response deadline has passed. Create a revision to extend it.");
        if (app.CurrentIssuedOfferId != offer.Id) throw new OfferLifecycleException("offer_not_current", "This is not the current issued revision.");
        if (offer.FrozenAtUtc is null || offer.ArtifactId is null
            || !await context.OfferArtifacts.AnyAsync(a => a.TenantId == offer.TenantId && a.CompanyId == offer.CompanyId
                && a.Id == offer.ArtifactId && a.OfferId == offer.Id && a.ApplicationId == app.Id && a.State == "Finalized", ct))
            throw new OfferLifecycleException("offer_artifact_pending", "The original offer document has not been finalized.");
    }

    internal static void AddResponseEvent(ZayraDbContext context, OfferActor actor, OfferLetter offer, OfferResponseRequest request)
    {
        var data = JsonSerializer.Serialize(request, OfferCompensation.Json);
        if (offer.CompanyId is null)
        {
            context.ApplicationEvents.Add(new() { TenantId = actor.TenantId, ApplicationId = offer.ApplicationId,
                EventType = "Response" + request.Decision, Notes = data, PerformedByUserId = actor.UserId, PerformedByName = actor.Name });
            return;
        }
        context.OfferLifecycleEvents.Add(new() { TenantId = actor.TenantId, CompanyId = offer.CompanyId,
            OfferId = offer.Id, ApplicationId = offer.ApplicationId, EventType = "Response" + request.Decision,
            ActorUserId = actor.UserId, ActorName = actor.Name, Reason = request.Reason, DataJson = data, OccurredAtUtc = request.OccurredAtUtc });
    }

    public Task<JobApplication> ChangeApplicationAsync(OfferActor actor, Guid applicationId, long? expectedVersion,
        string action, string reason, CancellationToken ct = default) =>
        OfferApplicationBoundary.ExecuteAsync(db, actor, applicationId, app =>
        {
            if (expectedVersion is null) throw new OfferLifecycleException("application_version_required", "Reload the application and submit its expectedVersion.");
            if (app.Version != expectedVersion) throw new OfferLifecycleException("application_version_conflict", "The application changed. Reload before continuing.");
            OfferApplicationBoundary.RequireUnreserved(app); OfferApplicationBoundary.RequireActive(app);
            if (action == "Reject") { RequireReason(reason); app.Status = "Rejected"; app.RejectionReason = reason; }
            else if (action == "Advance")
            {
                var next = RecruitmentStages.Next(app.Stage);
                if (next is null) throw new OfferLifecycleException("application_final_stage", "The application is already at its final stage.");
                if (next == "Hired") throw new OfferLifecycleException("hire_requires_offer_acceptance", "Accept the offer to hire and create the employee draft.");
                app.Stage = next; app.StageOrder = RecruitmentStages.OrderOf(next); app.StageChangedAtUtc = DateTime.UtcNow;
            }
            else throw new OfferLifecycleException("application_action_invalid", "Choose Advance or Reject.", 422);
            var tracked = db.JobApplications.Local.FirstOrDefault(a => a.Id == app.Id);
            if (tracked is not null) db.Entry(tracked).State = EntityState.Detached;
            db.JobApplications.Attach(app); db.Entry(app).State = EntityState.Modified; app.Version++;
            db.ApplicationEvents.Add(new() { TenantId = actor.TenantId, ApplicationId = app.Id,
                EventType = action == "Reject" ? "Rejected" : "StageAdvanced", Stage = app.Stage, Notes = reason,
                PerformedByUserId = actor.UserId, PerformedByName = actor.Name });
            return Task.FromResult(app);
        }, ct);
}
