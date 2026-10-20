using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Zayra.Api.Infrastructure.Recruitment;

public sealed class OfferHistoryQueryService(ZayraDbContext db, OfferLifecycleService lifecycle)
{
    public async Task<OfferPage<OfferHistoryItem>> ListAsync(OfferActor actor, OfferHistoryQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page); var size = Math.Clamp(query.PageSize, 1, 100);
        // A null company on register queries permits the request's existing authorized group scope;
        // the DbContext company filter remains in force. Child reads always pin the exact parent company.
        var rows = db.OfferLetters.AsNoTracking().Where(o => o.TenantId == actor.TenantId);
        if (actor.CompanyId is { } company) rows = rows.Where(o => o.CompanyId == company);
        if (query.CompanyId is { } requested) rows = rows.Where(o => o.CompanyId == requested);
        if (query.ApplicationId is { } app) rows = rows.Where(o => o.ApplicationId == app);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim().ToLower();
            rows = rows.Where(o => o.CandidateName.ToLower().Contains(search) || o.OfferedJobTitle.ToLower().Contains(search));
        }
        if (!string.IsNullOrWhiteSpace(query.Position)) rows = rows.Where(o => o.OfferedJobTitle == query.Position);
        if (!string.IsNullOrWhiteSpace(query.Status)) rows = rows.Where(o => o.Status == query.Status);
        if (!string.IsNullOrWhiteSpace(query.CommunicationOutcome))
        {
            if (query.CommunicationOutcome == "LegacyUnverified") rows = rows.Where(o => o.CommunicationOutcome == null && o.SeriesId == null && (o.Status == "Sent" || o.Status == "Accepted" || o.Status == "Declined"));
            else if (query.CommunicationOutcome == "NotIssued") rows = rows.Where(o => o.CommunicationOutcome == null && !(o.SeriesId == null && (o.Status == "Sent" || o.Status == "Accepted" || o.Status == "Declined")));
            else rows = rows.Where(o => o.CommunicationOutcome == query.CommunicationOutcome);
        }
        if (query.IssuedFrom is { } from) { RequireUtc(from); rows = rows.Where(o => o.SentAtUtc >= from); }
        if (query.IssuedTo is { } to) { RequireUtc(to); rows = rows.Where(o => o.SentAtUtc <= to); }
        var total = await rows.CountAsync(ct);
        var items = await rows.OrderByDescending(o => o.GeneratedAtUtc).ThenByDescending(o => o.Id)
            .Skip((int)Math.Min((long)(page - 1) * size, int.MaxValue)).Take(size).ToListAsync(ct);
        return new(items.Select(Item).ToArray(), total, page, size);
    }

    public async Task<OfferDetailDto?> WorkspaceAsync(OfferActor actor, Guid offerId, CancellationToken ct = default)
    {
        var offer = await lifecycle.ReadAsync(actor, offerId, ct);
        var app = await db.JobApplications.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == actor.TenantId && a.CompanyId == actor.CompanyId && a.Id == offer.ApplicationId, ct)
            ?? throw new OfferLifecycleException("offer_not_found", "Application not found.", 404);
        var approvals = await db.OfferApprovals.AsNoTracking().Where(a => a.TenantId == actor.TenantId && a.OfferLetterId == offerId).OrderBy(a => a.StepOrder).ToListAsync(ct);
        var artifact = offer.ArtifactId is null ? null : await db.OfferArtifacts.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == actor.TenantId
            && a.CompanyId == actor.CompanyId && a.Id == offer.ArtifactId && a.OfferId == offerId && a.State == "Finalized", ct);
        var series = await db.OfferSeries.AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == actor.TenantId && s.CompanyId == actor.CompanyId
            && (s.Id == offer.SeriesId || s.LegacyRootOfferId == offer.Id), ct);
        var revisionRows = lifecycle.ScopedOffers(actor).AsNoTracking();
        revisionRows = series is null ? revisionRows.Where(o => o.Id == offer.Id)
            : revisionRows.Where(o => o.Id == offer.Id || o.SeriesId == series.Id || o.Id == series.LegacyRootOfferId);
        var revisions = await revisionRows
            .OrderByDescending(o => o.GeneratedAtUtc).ThenByDescending(o => o.Id).ToListAsync(ct);
        var previous = offer.PreviousRevisionId is null ? null : await lifecycle.ScopedOffers(actor).AsNoTracking().SingleOrDefaultAsync(o => o.Id == offer.PreviousRevisionId, ct);
        var blocked = !lifecycle.Enabled ? "Managed offer lifecycle is disabled." : app.OfferReservationId is not null
            ? "Cancel or reconcile the unresolved delivery first." : app.Status != "Active" || app.OnboardingDraftId is not null
            ? "The application is no longer active." : null;
        var editable = offer.Status == "Draft" && offer.FrozenAtUtc is null && offer.ArtifactId is null && approvals.Count == 0;
        var responseBlocked = app.OfferReservationId is not null ? "Cancel or reconcile the unresolved delivery first."
            : offer.Status != "Sent" || app.Status != "Active" ? "Only a sent offer on an active application can receive a new response."
            : offer.SupersededAtUtc is not null || (app.CurrentIssuedOfferId is not null && app.CurrentIssuedOfferId != offer.Id) ? "This offer has been superseded."
            : offer.SeriesId is not null && (offer.ResponseDeadline is null || offer.ResponseDeadline <= DateTime.UtcNow) ? "The frozen response deadline has passed."
            : offer.SeriesId is not null && (!lifecycle.Enabled || artifact is null) ? "The managed original is unavailable or lifecycle is disabled." : null;
        var send = await OfferRules.EvaluateSendAsync(db, offer, actor.UserId, ct, allowIssued: offer.Status == "Sent");
        var issueBlocked = blocked ?? (artifact is null ? "Freeze the original first." : offer.ResponseDeadline is null || offer.ResponseDeadline <= DateTime.UtcNow
            ? "The frozen response deadline has passed." : offer.SupersededAtUtc is not null ? "This revision has been superseded."
            : send == OfferSendVerdict.Sendable ? null : send == OfferSendVerdict.InvalidState ? "This offer is not issuable." : OfferRules.SendRefusal(send).Message);
        var capabilities = new Dictionary<string, OfferActionCapability>
        {
            ["editDraft"] = Capability(blocked ?? (editable ? null : "Create a new revision to change submitted terms.")),
            ["revise"] = Capability(blocked),
            ["freeze"] = Capability(blocked ?? (editable && offer.SeriesId is not null ? null : "Save an editable managed draft first.")),
            ["withdraw"] = Capability(blocked ?? (offer.Status is "Accepted" or "Withdrawn" or "Declined" || offer.SupersededAtUtc is not null ? "This revision cannot be withdrawn." : null)),
            ["recordResponse"] = Capability(responseBlocked), ["issue"] = Capability(issueBlocked),
            ["downloadOriginal"] = Capability(artifact is null ? "No verified original is available." : null)
        };
        var changed = previous is null ? [] : new[] { nameof(offer.StartDate), nameof(offer.ResponseDeadline), nameof(offer.ProbationMonths),
            nameof(offer.RecipientEmail), nameof(offer.CandidateName), nameof(offer.OfferedJobTitle), nameof(offer.OfferedDepartment),
            nameof(offer.CompensationSnapshotJson), nameof(offer.DesignSnapshotJson) }.Where(name =>
                !Equals(typeof(OfferLetter).GetProperty(name)!.GetValue(previous), typeof(OfferLetter).GetProperty(name)!.GetValue(offer))).ToArray();
        return new(offer, artifact is null ? null : new(artifact.Id, artifact.Sha256, artifact.SizeBytes, artifact.Provenance, offer.FrozenAtUtc, offer.Version),
            Provenance(offer), approvals, capabilities, revisions.Select(Item).ToArray(), changed, Communication(offer),
            SavedSnapshot(offer.CompensationSnapshotJson), SavedSnapshot(offer.DesignSnapshotJson));
    }

    public async Task<OfferPage<OfferLifecycleEvent>> EventsAsync(OfferActor actor, Guid offerId, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        await lifecycle.ReadAsync(actor, offerId, ct);
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var rows = db.OfferLifecycleEvents.AsNoTracking().Where(e => e.TenantId == actor.TenantId && e.CompanyId == actor.CompanyId && e.OfferId == offerId);
        return new(await rows.OrderByDescending(e => e.RecordedAtUtc).ThenByDescending(e => e.Id)
            .Skip((int)Math.Min((long)(page - 1) * pageSize, int.MaxValue)).Take(pageSize).ToListAsync(ct), await rows.CountAsync(ct), page, pageSize);
    }

    private static OfferActionCapability Capability(string? reason) => new(reason is null, reason);
    private static System.Text.Json.JsonElement? SavedSnapshot(string? value)
    {
        if (value is null) return null;
        try { using var document = System.Text.Json.JsonDocument.Parse(value); return document.RootElement.Clone(); }
        catch (System.Text.Json.JsonException) { return null; } // Corrupt legacy JSON is not reconstructed from live settings.
    }
    private static void RequireUtc(DateTime value) { if (value.Kind != DateTimeKind.Utc) throw new OfferLifecycleException("offer_history_date_invalid", "History dates must be UTC.", 422); }
    private static string Communication(OfferLetter offer) => offer.CommunicationOutcome ?? (offer.SeriesId is null && offer.Status is "Sent" or "Accepted" or "Declined" ? "LegacyUnverified" : "NotIssued");
    private static string Provenance(OfferLetter offer) => offer.FrozenAtUtc is not null && offer.ArtifactId is not null ? "FrozenOriginal" : offer.SeriesId is null ? "LegacyReconstructed" : "NotFrozen";
    private static OfferHistoryItem Item(OfferLetter offer) => new(offer.Id, offer.ApplicationId, offer.CompanyId, offer.SeriesId, offer.RevisionNumber,
        offer.Version, offer.CandidateName, offer.OfferedJobTitle, offer.Status, Communication(offer), Provenance(offer),
        offer.GeneratedAtUtc, offer.SentAtUtc, offer.ResponseDeadline, offer.SupersededAtUtc);
}
