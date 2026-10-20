using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Recruitment;

public sealed partial class OfferLifecycleService
{
    public Task<OfferLetter> RecordExternalSendAsync(OfferActor actor, Guid offerId, OfferExternalSendRequest request, CancellationToken ct = default) =>
        MutateAsync(actor, offerId, null, true, async (app, offer) =>
        {
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160)
                throw new OfferLifecycleException("offer_idempotency_required", "Supply an idempotency key of at most 160 characters.", 422);
            var fingerprint = ExternalFingerprint(request);
            var existing = await db.OfferLifecycleEvents.AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == actor.TenantId
                && e.CompanyId == actor.CompanyId && e.OfferId == offer.Id && e.IdempotencyKey == request.IdempotencyKey, ct);
            if (existing is not null)
            {
                using var payload = JsonDocument.Parse(existing.DataJson);
                if (existing.EventType != "ExternalSent" || payload.RootElement.GetProperty("fingerprint").GetString() != fingerprint)
                    throw new OfferLifecycleException("offer_idempotency_conflict", "This key already records different sending evidence.");
                return offer;
            }
            OfferApplicationBoundary.RequireVersion(offer, request.ExpectedVersion);
            OfferApplicationBoundary.RequireUnreserved(app);
            if (request.Channel is not ("Email" or "InPerson" or "Other")
                || request.OccurredAtUtc.Kind != DateTimeKind.Utc || offer.FrozenAtUtc is null
                || request.OccurredAtUtc < offer.FrozenAtUtc || request.OccurredAtUtc > DateTime.UtcNow
                || (request.Channel != "Email" && string.IsNullOrWhiteSpace(request.Reference)))
                throw new OfferLifecycleException("offer_external_evidence_invalid",
                    "Record a supported channel and an actual UTC sending time between freeze and now. In-person and other channels require a reference.", 422);
            await RequireIssuableAsync(actor, app, offer, offer.Status == "Sent" ? request.Reason : null, ct);
            await FinalizeIssuanceLockedAsync(actor, app, offer, null, null, "External", request.OccurredAtUtc, ct);
            AddEvent(actor, offer, "ExternalSent", request.Reason ?? "Staff confirmed external sending.", new
            {
                fingerprint, request.Channel, request.Reference, request.OccurredAtUtc,
                candidateId = offer.CandidateId, candidateName = offer.CandidateName, recipientEmail = offer.RecipientEmail,
                artifactId = offer.ArtifactId
            }, request.IdempotencyKey, request.OccurredAtUtc);
            return offer;
        }, ct);

    public Task<OfferReservationDto> ReserveIssuanceAsync(OfferActor actor, Guid offerId, long expectedVersion, Guid operationId, CancellationToken ct = default) =>
        MutateAsync(actor, offerId, expectedVersion, true,
            (app, offer) => ReserveIssuanceLockedAsync(actor, app, offer, operationId, null, ct), ct);

    /// <summary>Task 3 calls this inside MutateAsync while adding its dispatch row, making the
    /// reservation and dispatch request one atomic write. The transport never acquires a separate lock.</summary>
    internal async Task<OfferReservationDto> ReserveIssuanceLockedAsync(OfferActor actor, JobApplication app,
        OfferLetter offer, Guid operationId, string? resendReason, CancellationToken ct)
    {
        RequireTransaction(); RequireEnabled();
        OfferApplicationBoundary.RequireUnreserved(app);
        if (operationId == Guid.Empty) throw new ArgumentException("A reservation operation ID is required.", nameof(operationId));
        await RequireIssuableAsync(actor, app, offer, resendReason, ct);
        app.Version++; offer.Version++;
        app.OfferReservationId = operationId; app.OfferReservationOfferId = offer.Id;
        app.OfferReservationState = "Queued"; app.OfferReservationVersion = app.Version;
        AddEvent(actor, offer, "IssuanceReserved", resendReason ?? "Reserved reviewed offer issuance.", new { operationId, reservationVersion = app.OfferReservationVersion });
        return new(app.Id, offer.Id, operationId, "Queued", app.Version, offer.Version);
    }

    internal async Task RequireIssuableAsync(OfferActor actor, JobApplication app, OfferLetter offer,
        string? resendReason, CancellationToken ct)
    {
        OfferApplicationBoundary.RequireActive(app);
        if (offer.SeriesId is null || offer.ArtifactId is null || offer.FrozenAtUtc is null
            || !await db.OfferArtifacts.AnyAsync(a => a.TenantId == actor.TenantId && a.CompanyId == actor.CompanyId
                && a.Id == offer.ArtifactId && a.OfferId == offer.Id && a.ApplicationId == app.Id && a.State == "Finalized", ct))
            throw new OfferLifecycleException("offer_artifact_pending", "Finalize the reviewed original before approval or issuance.");
        if (offer.SupersededAtUtc is not null || (offer.Status == "Sent" && app.CurrentIssuedOfferId != offer.Id))
            throw new OfferLifecycleException("offer_not_current", "This offer is no longer the current issued revision.");
        if (offer.ResponseDeadline is null || offer.ResponseDeadline <= DateTime.UtcNow)
            throw new OfferLifecycleException("offer_expired", "The frozen response deadline has passed. Create a revision to extend it.");
        if (offer.Status == "Sent") RequireReason(resendReason ?? string.Empty);
        var verdict = await OfferRules.EvaluateSendAsync(db, offer, actor.UserId, ct, allowIssued: offer.Status == "Sent");
        if (verdict == OfferSendVerdict.InvalidState)
            throw new OfferLifecycleException("invalid_offer_state", "The offer is not eligible for issuance.");
        if (verdict != OfferSendVerdict.Sendable)
        {
            var refusal = OfferRules.SendRefusal(verdict);
            throw new OfferLifecycleException(refusal.Error, refusal.Message, verdict == OfferSendVerdict.SenderIsApprover ? 403 : 409);
        }
        if (artifacts is null) throw new OfferLifecycleException("offer_storage_unavailable", "Original document verification is unavailable.");
        await artifacts.OriginalAsync(actor, offer.Id, ct);
    }

    /// <summary>Caller retains the application lock and persists its evidence/dispatch outcome in
    /// the same transaction. A retained AcceptedByRelay outcome is never converted into a new send.</summary>
    internal async Task FinalizeIssuanceLockedAsync(OfferActor actor, JobApplication app, OfferLetter offer,
        Guid? operationId, long? expectedReservationVersion, string evidence, DateTime occurredAtUtc, CancellationToken ct)
    {
        RequireTransaction(); OfferApplicationBoundary.RequireActive(app);
        if (evidence is not ("External" or "AcceptedByRelay")) throw new ArgumentException("Issuance requires explicit external or accepted-relay evidence.", nameof(evidence));
        if (operationId is { } operation)
            RequireReservation(app, offer.Id, operation, expectedReservationVersion ?? -1);
        else OfferApplicationBoundary.RequireUnreserved(app);
        if (offer.ArtifactId is null || offer.FrozenAtUtc is null || offer.SupersededAtUtc is not null
            || !await db.OfferArtifacts.AnyAsync(a => a.TenantId == actor.TenantId && a.CompanyId == actor.CompanyId
                && a.Id == offer.ArtifactId && a.OfferId == offer.Id && a.ApplicationId == app.Id && a.State == "Finalized", ct))
            throw new OfferLifecycleException("offer_artifact_pending", "Only the reserved frozen revision can be finalized.");
        var prior = await db.OfferLetters.Where(o => o.TenantId == actor.TenantId && o.CompanyId == actor.CompanyId
            && o.ApplicationId == app.Id && o.Id != offer.Id && o.Status == "Sent" && o.SupersededAtUtc == null).ToListAsync(ct);
        foreach (var old in prior)
        {
            old.SupersededAtUtc = DateTime.UtcNow; old.Version++;
            AddEvent(actor, old, "Superseded", "A replacement was actually issued.", new { replacementOfferId = offer.Id });
        }
        offer.Status = "Sent"; offer.SentAtUtc ??= occurredAtUtc; offer.CommunicationOutcome = evidence; offer.Version++;
        app.CurrentIssuedOfferId = offer.Id; app.Version++;
        app.OfferReservationId = null; app.OfferReservationOfferId = null; app.OfferReservationState = null; app.OfferReservationVersion = null;
        db.RecruitmentAuditLogs.Add(OfferRules.AuditRow(actor.TenantId, offer.Id, OfferRules.SentAction, actor.UserId, actor.Name, new { evidence, occurredAtUtc }));
    }

    internal static void RequireReservation(JobApplication app, Guid offerId, Guid operationId, long expectedVersion)
    {
        if (app.OfferReservationId != operationId || app.OfferReservationOfferId != offerId
            || app.OfferReservationVersion != expectedVersion)
            throw new OfferLifecycleException("offer_reservation_conflict", "The offer reservation changed. Reload its dispatch before continuing.");
    }

    internal void ReleaseReservationLocked(JobApplication app, Guid offerId, Guid operationId, long expectedVersion)
    {
        RequireTransaction(); RequireReservation(app, offerId, operationId, expectedVersion);
        app.OfferReservationId = null; app.OfferReservationOfferId = null; app.OfferReservationState = null; app.OfferReservationVersion = null; app.Version++;
    }

    private void RequireTransaction()
    {
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Issuance mutation requires the application transaction.");
    }

    private static string ExternalFingerprint(OfferExternalSendRequest request) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { request.Channel, request.OccurredAtUtc, request.Reference, request.Reason }, OfferCompensation.Json))));
}
