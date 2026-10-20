using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MimeKit;

namespace Zayra.Api.Infrastructure.Recruitment;

public sealed class OfferDispatchService(ZayraDbContext db, OfferLifecycleService lifecycle, OfferArtifactService artifacts, IEmailService email)
{
    private IQueryable<OfferDispatch> Rows(OfferActor actor, Guid offerId) => db.OfferDispatches.Where(d =>
        d.TenantId == actor.TenantId && d.CompanyId == actor.CompanyId && d.OfferId == offerId);

    private async Task<OfferDispatch> LockedDispatch(OfferActor actor, OfferLetter offer, Guid id, CancellationToken ct)
    {
        // MutateAsync owns the application lock. Always reload; a previous transaction in this
        // same scoped service may have observed an older lease/version.
        var local = db.OfferDispatches.Local.FirstOrDefault(d => d.Id == id);
        if (local is not null) db.Entry(local).State = EntityState.Detached;
        return await Rows(actor, offer.Id).SingleOrDefaultAsync(d => d.Id == id && d.ApplicationId == offer.ApplicationId, ct)
            ?? throw new OfferLifecycleException("offer_dispatch_not_found", "Dispatch not found for this offer.", 404);
    }

    private async Task RequireActiveTenant(OfferActor actor, CancellationToken ct)
    {
        if (!await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == actor.TenantId && t.IsActive, ct))
            throw new OfferLifecycleException("offer_tenant_inactive", "This workspace is inactive; email cannot be submitted.");
    }

    private static string Fingerprint(OfferEmailRequest request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { request.Subject, request.Message, request.ReviewedSenderFingerprint, request.ResendReason, request.ConfirmResend }))));

    private static void Validate(OfferEmailRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160)
            throw new OfferLifecycleException("offer_idempotency_required", "Supply an idempotency key of at most 160 characters.", 422);
        if (string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length > 300 || request.Subject.Contains('\r') || request.Subject.Contains('\n')
            || string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 50000 || string.IsNullOrWhiteSpace(request.ReviewedSenderFingerprint))
            throw new OfferLifecycleException("offer_email_invalid", "Review the sender, subject and message before requesting email.", 422);
    }

    public Task<OfferDispatch> QueueAsync(OfferActor actor, Guid offerId, OfferEmailRequest request, CancellationToken ct = default) =>
        lifecycle.MutateAsync(actor, offerId, null, true, async (app, offer) =>
        {
            Validate(request);
            var existing = await Rows(actor, offerId).AsNoTracking().SingleOrDefaultAsync(d => d.IdempotencyKey == request.IdempotencyKey, ct);
            if (existing is not null)
            {
                if (existing.RequestFingerprint != Fingerprint(request)) throw new OfferLifecycleException("offer_idempotency_conflict", "This key already identifies a different email request.");
                return existing;
            }
            return await QueueLocked(actor, app, offer, request, ct);
        }, ct);

    private async Task<OfferDispatch> QueueLocked(OfferActor actor, JobApplication app, OfferLetter offer, OfferEmailRequest request, CancellationToken ct)
    {
        Validate(request); OfferApplicationBoundary.RequireVersion(offer, request.ExpectedVersion);
        await RequireActiveTenant(actor, ct);
        if (!MailboxAddress.TryParse(offer.RecipientEmail, out var address) || address.Address != offer.RecipientEmail)
            throw new OfferLifecycleException("offer_recipient_invalid", "Create a revision with a valid reviewed recipient email.", 422);
        if (offer.Status == "Sent" && (!request.ConfirmResend || string.IsNullOrWhiteSpace(request.ResendReason)))
            throw new OfferLifecycleException("offer_resend_confirmation_required", "Confirm resending and provide a reason.", 422);
        var sender = await email.GetEffectiveSenderAsync(actor.TenantId, ct);
        if (sender.ConfigurationFingerprint != request.ReviewedSenderFingerprint)
            throw new OfferLifecycleException("offer_sender_changed", "Email sender settings changed. Review the effective sender again.");
        var row = new OfferDispatch
        {
            TenantId = actor.TenantId, CompanyId = actor.CompanyId, ApplicationId = app.Id, OfferId = offer.Id,
            ArtifactId = offer.ArtifactId ?? Guid.Empty, IdempotencyKey = request.IdempotencyKey,
            RequestFingerprint = Fingerprint(request), Subject = request.Subject, Message = request.Message,
            RecipientEmail = offer.RecipientEmail!, RecipientName = offer.CandidateName,
            InitiatorUserId = actor.UserId, InitiatorName = actor.Name, ResendReason = request.ResendReason,
            FromAddress = sender.FromAddress, FromName = sender.FromName, SenderSource = sender.Source, SenderFingerprint = sender.ConfigurationFingerprint
        };
        var reservation = await lifecycle.ReserveIssuanceLockedAsync(actor, app, offer, row.Id, request.ResendReason, ct);
        row.ReservationVersion = reservation.ReservationVersion;
        db.OfferDispatches.Add(row); offer.CommunicationOutcome = "Queued";
        lifecycle.AddEvent(actor, offer, "EmailQueued", "Reviewed email queued for delivery.", new { dispatchId = row.Id });
        return row;
    }

    public Task<OfferDispatch?> ClaimAsync(OfferActor actor, Guid offerId, Guid dispatchId, CancellationToken ct = default) =>
        lifecycle.MutateAsync<OfferDispatch?>(actor, offerId, null, true, async (app, offer) =>
        {
            var row = await LockedDispatch(actor, offer, dispatchId, ct);
            if (row.Outcome == "Sending" && row.LeaseUntilUtc <= DateTime.UtcNow)
            {
                OfferLifecycleService.RequireReservation(app, offer.Id, row.Id, row.ReservationVersion);
                row.Outcome = "Unknown"; row.Version++; app.OfferReservationState = "Unknown"; app.Version++;
                offer.CommunicationOutcome = "Unknown"; offer.Version++;
                Evidence(row, "LeaseExpired", "Unknown", "transport_completion_unresolved");
                lifecycle.AddEvent(actor, offer, "EmailUnknown", "The sending lease expired; reconcile before another attempt.", new { dispatchId });
                return null;
            }
            if (row.Outcome != "Queued") return null;
            OfferLifecycleService.RequireReservation(app, offer.Id, row.Id, row.ReservationVersion);
            row.Outcome = "Sending"; row.LeaseToken = Guid.NewGuid(); row.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(5); row.Version++;
            app.OfferReservationState = "Sending"; app.Version++; offer.CommunicationOutcome = "Sending"; offer.Version++;
            Evidence(row, "Claimed", "Sending", "lease_claimed");
            return row;
        }, ct);

    public async Task ProcessAsync(OfferActor actor, Guid offerId, Guid dispatchId, CancellationToken ct = default)
    {
        var claimed = await ClaimAsync(actor, offerId, dispatchId, ct);
        if (claimed is null) return;
        // Worker acts with the retained human initiator for maker/checker eligibility.
        var initiator = actor with { UserId = claimed.InitiatorUserId, Name = claimed.InitiatorName };
        byte[] pdf;
        try
        {
            pdf = await lifecycle.MutateAsync(initiator, offerId, null, true, async (app, offer) =>
            {
                var row = await LockedDispatch(initiator, offer, dispatchId, ct); RequireLease(row, claimed);
                OfferLifecycleService.RequireReservation(app, offer.Id, row.Id, row.ReservationVersion);
                if (row.Outcome != "Sending" || row.LeaseUntilUtc <= DateTime.UtcNow)
                    throw new OfferLifecycleException("offer_lease_changed", "Sending lease changed.");
                await RequireActiveTenant(initiator, ct);
                await lifecycle.RequireIssuableAsync(initiator, app, offer, row.ResendReason, ct);
                if (offer.ArtifactId != row.ArtifactId) throw new OfferLifecycleException("offer_artifact_changed", "Reserved original changed.");
                var bytes = await artifacts.OriginalAsync(initiator, offerId, ct);
                row.TransportStartedAtUtc = DateTime.UtcNow; row.Version++;
                Evidence(row, "TransportStarted", "Sending", "submission_boundary_entered");
                return bytes;
            }, ct);
            // This check is still pre-transport even though the durable boundary marker exists.
            // An observed failure here proves this invocation never called the email service.
            await RequireActiveTenant(initiator, ct);
            ct.ThrowIfCancellationRequested();
        }
        catch (OfferLifecycleException e) when (e.Code is not ("offer_reservation_conflict" or "offer_dispatch_not_found" or "offer_not_found" or "offer_lease_changed"))
        { await StoreResult(initiator, claimed, "Failed", e.Code, CancellationToken.None); return; }
        catch (Exception e) when (e is not OfferLifecycleException)
        {
            await StoreResult(initiator, claimed, "Failed",
                e is OperationCanceledException ? "offer_pretransport_cancelled" : "offer_pretransport_failed", CancellationToken.None);
            return;
        }
        // No transaction/execution-strategy delegate encloses this call. A crash after this marker
        // and before durable result evidence is an unavoidable SMTP Unknown interval, never a retry.
        string outcome; string code;
        try
        {
            var result = await email.DeliverAsync(actor.TenantId, claimed.SenderFingerprint, claimed.RecipientEmail,
                claimed.RecipientName, claimed.Subject, claimed.Message,
                [new EmailAttachment($"offer-{offerId}.pdf", pdf, "application/pdf")], ct);
            outcome = result.Status.ToString(); code = "transport_" + outcome;
        }
        catch (OfferLifecycleException e) { outcome = "Failed"; code = e.Code; }
        catch (EmailTransportException e) { outcome = e.SubmissionMayHaveStarted ? "Unknown" : "Failed"; code = e.Code; }
        catch (Exception) { outcome = "Unknown"; code = "smtp_submission_unknown"; }
        // Persist even when request/worker cancellation fired after a known result.
        var retainsReservation = await StoreResult(initiator, claimed, outcome, code, CancellationToken.None);
        if (outcome == "AcceptedByRelay" && retainsReservation) await FinalizeRetained(initiator, offerId, dispatchId, CancellationToken.None);
    }

    private static void RequireLease(OfferDispatch row, OfferDispatch claimed)
    {
        if (row.LeaseToken is null || row.LeaseToken != claimed.LeaseToken)
            throw new OfferLifecycleException("offer_lease_changed", "Sending lease changed.");
    }

    private Task<bool> StoreResult(OfferActor actor, OfferDispatch claimed, string outcome, string code, CancellationToken ct) =>
        lifecycle.MutateAsync(actor, claimed.OfferId, null, true, async (app, offer) =>
        {
            var row = await LockedDispatch(actor, offer, claimed.Id, ct); RequireLease(row, claimed);
            var ownsReservation = app.OfferReservationId == row.Id && app.OfferReservationOfferId == offer.Id && app.OfferReservationVersion == row.ReservationVersion;
            if (await db.OfferDispatchAttempts.AnyAsync(a => a.TenantId == actor.TenantId && a.CompanyId == actor.CompanyId
                && a.DispatchId == row.Id && a.LeaseToken == row.LeaseToken && (a.Stage == "Result" || a.Stage == "LateResult"), ct)) return ownsReservation;
            if (!ownsReservation)
            {
                // Risk-authorized replacement or external reconciliation may win while the old
                // SMTP call is returning. Retain observed evidence only for its immutable parent
                // and original lease. Never touch the current offer/application/reservation.
                row.TransportCompletedAtUtc = DateTime.UtcNow; row.Version++;
                if (row.FinalizedAtUtc is null) row.Outcome = outcome;
                Evidence(row, "LateResult", outcome, code);
                return false;
            }
            OfferLifecycleService.RequireReservation(app, offer.Id, row.Id, row.ReservationVersion);
            row.Outcome = outcome; row.TransportCompletedAtUtc = DateTime.UtcNow; row.Version++;
            offer.CommunicationOutcome = outcome; offer.Version++;
            Evidence(row, "Result", outcome, code);
            if (outcome is "Captured" or "NotConfigured" or "Failed") lifecycle.ReleaseReservationLocked(app, offer.Id, row.Id, row.ReservationVersion);
            else { app.OfferReservationState = outcome == "Unknown" ? "Unknown" : "Sending"; app.Version++; }
            lifecycle.AddEvent(actor, offer, "EmailTransportResult", "Transport outcome recorded; relay acceptance is not inbox delivery.", new { dispatchId = row.Id, outcome, code });
            return true;
        }, ct);

    private Task<OfferDispatch> FinalizeRetained(OfferActor actor, Guid offerId, Guid dispatchId, CancellationToken ct) =>
        lifecycle.MutateAsync(actor, offerId, null, true, async (app, offer) =>
        {
            var row = await LockedDispatch(actor, offer, dispatchId, ct);
            if (row.FinalizedAtUtc is not null) return row;
            await FinalizeRetainedLocked(actor, app, offer, row, ct); return row;
        }, ct);

    private async Task FinalizeRetainedLocked(OfferActor actor, JobApplication app, OfferLetter offer, OfferDispatch row, CancellationToken ct)
    {
        var evidence = await db.OfferDispatchAttempts.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == actor.TenantId && a.CompanyId == actor.CompanyId
            && a.DispatchId == row.Id && a.OfferId == offer.Id && a.ApplicationId == app.Id && a.LeaseToken == row.LeaseToken
            && a.Stage == "Result" && a.Outcome == "AcceptedByRelay", ct)
            ?? throw new OfferLifecycleException("offer_relay_evidence_required", "No retained relay acceptance exists for this dispatch.");
        await lifecycle.FinalizeIssuanceLockedAsync(actor, app, offer, row.Id, row.ReservationVersion, "AcceptedByRelay", evidence.OccurredAtUtc, ct);
        row.Outcome = "AcceptedByRelay"; row.FinalizedAtUtc = DateTime.UtcNow; row.Version++;
        lifecycle.AddEvent(actor, offer, "EmailIssued", "Retained relay acceptance applied without repeating transport.", new { dispatchId = row.Id, evidenceId = evidence.Id });
    }

    private void Evidence(OfferDispatch row, string stage, string outcome, string code) => db.OfferDispatchAttempts.Add(new()
    { TenantId = row.TenantId, CompanyId = row.CompanyId, ApplicationId = row.ApplicationId, OfferId = row.OfferId,
        DispatchId = row.Id, LeaseToken = row.LeaseToken ?? Guid.Empty, Stage = stage, Outcome = outcome, Code = code });

    public Task<OfferDispatch> CancelAsync(OfferActor actor, Guid offerId, Guid dispatchId, OfferDispatchCancelRequest request, CancellationToken ct = default) =>
        lifecycle.MutateAsync(actor, offerId, null, true, async (app, offer) =>
        {
            var row = await LockedDispatch(actor, offer, dispatchId, ct); RequireVersion(row, request.ExpectedVersion); OfferLifecycleService.RequireReason(request.Reason);
            if (row.Outcome != "Queued") throw new OfferLifecycleException("offer_dispatch_not_queued", "Only queued email can be cancelled. Reconcile a claimed dispatch.");
            lifecycle.ReleaseReservationLocked(app, offer.Id, row.Id, row.ReservationVersion);
            row.Outcome = "Cancelled"; row.Version++; offer.CommunicationOutcome = "Cancelled"; offer.Version++;
            lifecycle.AddEvent(actor, offer, "EmailCancelled", request.Reason, new { dispatchId }); return row;
        }, ct);

    private static void RequireVersion(OfferDispatch row, long expected)
    { if (row.Version != expected) throw new OfferLifecycleException("offer_dispatch_version_conflict", "Dispatch changed; reload before continuing."); }

    public Task<OfferDispatch> ReconcileAsync(OfferActor actor, Guid offerId, Guid dispatchId, OfferDispatchReconcileRequest request, CancellationToken ct = default) =>
        lifecycle.MutateAsync(actor, offerId, null, true, async (app, offer) =>
        {
            var row = await LockedDispatch(actor, offer, dispatchId, ct);
            RequireVersion(row, request.ExpectedVersion); OfferLifecycleService.RequireReason(request.Reason);
            OfferLifecycleService.RequireReservation(app, offer.Id, row.Id, request.ExpectedReservationVersion);
            if (row.ReservationVersion != request.ExpectedReservationVersion)
                throw new OfferLifecycleException("offer_reservation_conflict", "Reserved dispatch version changed.");
            if (request.Action == OfferReconciliationAction.ApplyRetainedRelayEvidence)
            {
                await FinalizeRetainedLocked(actor, app, offer, row, ct);
                lifecycle.AddEvent(actor, offer, "RelayEvidenceReconciled", request.Reason, new { dispatchId }); return row;
            }
            if (row.Outcome != "Unknown") throw new OfferLifecycleException("offer_reconciliation_state", "Only an unknown submission requires external evidence or risk-acknowledged replacement.");
            await RequireActiveTenant(actor, ct);
            if (request.Action == OfferReconciliationAction.RecordConfirmedExternalSending)
            {
                if (string.IsNullOrWhiteSpace(request.EvidenceReference) || request.EvidenceReference.Length > 2000
                    || request.OccurredAtUtc is not { } occurred || occurred.Kind != DateTimeKind.Utc
                    || occurred < offer.FrozenAtUtc || occurred > DateTime.UtcNow)
                    throw new OfferLifecycleException("offer_external_evidence_invalid", "Provide an external sending reference and actual UTC sending time between freeze and now.", 422);
                await lifecycle.RequireIssuableAsync(actor, app, offer, request.Reason, ct);
                await lifecycle.FinalizeIssuanceLockedAsync(actor, app, offer, row.Id, row.ReservationVersion, "External", occurred, ct);
                row.Outcome = "External"; row.FinalizedAtUtc = DateTime.UtcNow; row.Version++;
                lifecycle.AddEvent(actor, offer, "ExternalSent", request.Reason, new { dispatchId, request.EvidenceReference, occurredAtUtc = occurred,
                    source = "StaffRecorded", candidateId = offer.CandidateId, recipientEmail = row.RecipientEmail }, occurredAt: occurred);
                return row;
            }
            if (request.Action != OfferReconciliationAction.AuthorizeRiskAcknowledgedResend || !request.AcknowledgeDuplicateRisk
                || request.Replacement is not { } replacement || !replacement.ConfirmResend || string.IsNullOrWhiteSpace(replacement.ResendReason))
                throw new OfferLifecycleException("offer_duplicate_risk_acknowledgement_required", "A new request key, resend confirmation, reason and explicit duplicate-message-risk acknowledgement are required.", 422);
            Validate(replacement);
            if (await Rows(actor, offerId).AnyAsync(d => d.IdempotencyKey == replacement.IdempotencyKey, ct))
                throw new OfferLifecycleException("offer_idempotency_conflict", "Risk-authorized resend requires a new request key.");
            // One application transaction: no observer can accept between release and reserve.
            lifecycle.ReleaseReservationLocked(app, offer.Id, row.Id, row.ReservationVersion);
            var next = await QueueLocked(actor, app, offer, replacement, ct);
            row.Version++;
            lifecycle.AddEvent(actor, offer, "DuplicateRiskResendAuthorized", request.Reason,
                new { previousDispatchId = row.Id, replacementDispatchId = next.Id, duplicateMessageRiskAcknowledged = true });
            return next;
        }, ct);

    public async Task<OfferDispatchDto> GetAsync(OfferActor actor, Guid offerId, Guid dispatchId, CancellationToken ct = default)
    {
        var offer = await lifecycle.ReadAsync(actor, offerId, ct);
        var row = await Rows(actor, offerId).AsNoTracking().SingleOrDefaultAsync(d => d.Id == dispatchId && d.ApplicationId == offer.ApplicationId, ct)
            ?? throw new OfferLifecycleException("offer_dispatch_not_found", "Dispatch not found for this offer.", 404);
        var app = await db.JobApplications.AsNoTracking().SingleAsync(a => a.TenantId == actor.TenantId && a.CompanyId == actor.CompanyId && a.Id == row.ApplicationId, ct);
        var attempts = await db.OfferDispatchAttempts.AsNoTracking().Where(a => a.TenantId == actor.TenantId && a.CompanyId == actor.CompanyId
            && a.DispatchId == row.Id && a.ApplicationId == app.Id && a.OfferId == offerId)
            .OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.Id).Take(100)
            .Select(a => new OfferAttemptDto(a.Id, a.Stage, a.Outcome, a.Code, a.OccurredAtUtc)).ToListAsync(ct);
        var owns = app.OfferReservationId == row.Id && app.OfferReservationOfferId == offerId && app.OfferReservationVersion == row.ReservationVersion;
        var actions = new List<string>();
        if (lifecycle.Enabled && owns)
        {
            if (row.Outcome == "Queued") actions.Add("Cancel");
            if (attempts.Any(a => a.Stage == "Result" && a.Outcome == "AcceptedByRelay") && row.FinalizedAtUtc is null) actions.Add("ApplyRetainedRelayEvidence");
            if (row.Outcome == "Unknown")
            {
                try
                {
                    await RequireActiveTenant(actor, ct);
                    await lifecycle.RequireIssuableAsync(actor, app, offer, "Reviewing reconciliation eligibility.", ct);
                    actions.Add("RecordConfirmedExternalSending"); actions.Add("AuthorizeRiskAcknowledgedResend");
                }
                catch (OfferLifecycleException) { /* Projection only; POST repeats the guard under the application lock. */ }
            }
        }
        return new(row.Id, row.OfferId, row.Version, row.Outcome, row.ReservationVersion, owns ? app.OfferReservationState : null,
            row.Subject, row.Message, row.RecipientEmail, row.RecipientName, row.ArtifactId, row.InitiatorUserId, row.InitiatorName,
            row.FromAddress, row.FromName, row.SenderSource, row.SenderFingerprint, row.CreatedAtUtc, row.FinalizedAtUtc, actions, attempts,
            attempts.FirstOrDefault(a => a.Stage is "Result" or "LateResult")?.Outcome);
    }
}
