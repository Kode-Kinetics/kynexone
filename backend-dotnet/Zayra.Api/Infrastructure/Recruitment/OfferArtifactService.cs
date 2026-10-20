using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Recruitment;

public sealed class OfferArtifactService(ZayraDbContext db, IDocumentStorage storage, IConfiguration configuration)
{
    private OfferLifecycleService Lifecycle => new(db, configuration);
    private IQueryable<OfferArtifact> Artifacts(OfferActor actor) => db.OfferArtifacts
        .Where(a => a.TenantId == actor.TenantId && a.CompanyId == actor.CompanyId);

    public async Task<OfferArtifactDto> FreezeAsync(OfferActor actor, Guid offerId, long expectedVersion, CancellationToken ct = default)
    {
        var initial = await Lifecycle.ReadAsync(actor, offerId, ct);
        Lifecycle.RequireEnabled();
        if (initial.SeriesId is null)
            throw new OfferLifecycleException("offer_adoption_required", "Review a managed revision before freezing its original.");
        OfferArtifact artifact;
        if (initial.ArtifactId is { } existingId)
        {
            artifact = await Artifacts(actor).AsNoTracking().SingleOrDefaultAsync(a => a.Id == existingId && a.OfferId == offerId, ct)
                ?? throw new OfferLifecycleException("offer_artifact_missing", "The original document metadata is missing.");
            if (expectedVersion != initial.Version && expectedVersion != artifact.SourceOfferVersion)
                throw new OfferLifecycleException("offer_version_conflict", "The offer changed. Reload before freezing.");
            if (artifact.State == "Finalized")
            {
                await VerifiedBytesAsync(actor, artifact, ct);
                return Dto(artifact, initial);
            }
            if (artifact.State == "Pending")
                throw new OfferLifecycleException("offer_upload_unresolved", "Upload completion is unresolved. No automatic upload retry or tenant erasure is safe; an operator must establish that the original write has finished.");
            if (artifact.State != "Uploaded" || initial.Version != artifact.PendingOfferVersion)
                throw new OfferLifecycleException("offer_artifact_pending", "The pending document requires review before continuing.");
            // A retry never uploads again. It can only finalize the exact previously uploaded bytes.
            try { await VerifiedBytesAsync(actor, artifact, ct); }
            catch (OfferLifecycleException e) when (e.Code == "offer_artifact_missing")
            { throw new OfferLifecycleException("offer_artifact_pending", "The original upload is unfinished. No approval or issuance is available."); }
        }
        else
        {
            var pending = await Lifecycle.MutateAsync(actor, offerId, expectedVersion, true, async (app, offer) =>
            {
                OfferApplicationBoundary.RequireUnreserved(app); OfferApplicationBoundary.RequireActive(app);
                if (offer.Status != "Draft" || offer.FrozenAtUtc is not null || offer.ArtifactId is not null
                    || await db.OfferApprovals.AnyAsync(a => a.TenantId == actor.TenantId && a.OfferLetterId == offerId, ct))
                    throw new OfferLifecycleException("offer_terms_immutable", "Only an unsubmitted managed draft can be frozen.");
                await RequireReviewedPolicyAsync(actor, offer, ct);
                if (offer.CandidateId is null || string.IsNullOrWhiteSpace(offer.RecipientEmail)
                    || offer.ResponseDeadline is null || offer.ResponseDeadline <= DateTime.UtcNow
                    || offer.CompensationSnapshotJson is null || offer.DesignSnapshotJson is null)
                    throw new OfferLifecycleException("offer_review_required", "Save reviewed terms, recipient, and a future response deadline before freezing.");
                var snapshot = JsonSerializer.Deserialize<OfferDesignSnapshot>(offer.DesignSnapshotJson, OfferLetterDesign.Json)
                    ?? throw new OfferLifecycleException("offer_design_invalid", "The saved offer design is invalid.", 422);
                // Task 5 extends this renderer's saved compensation snapshot; no live catalogue/template lookup is permitted here.
                var bytes = OfferLetterDesign.Render(snapshot);
                if (bytes.Length == 0) throw new OfferLifecycleException("offer_artifact_empty", "The renderer produced an empty document.");
                var row = new OfferArtifact
                {
                    TenantId = actor.TenantId, CompanyId = actor.CompanyId, OfferId = offer.Id, ApplicationId = app.Id,
                    Sha256 = Hash(bytes), SizeBytes = bytes.LongLength, SourceOfferVersion = offer.Version,
                    PendingOfferVersion = offer.Version + 1
                };
                row.StorageKey = storage.TenantKey(actor.TenantId, $"offers/{offer.Id:N}/{row.Id:N}.pdf");
                db.OfferArtifacts.Add(row); offer.ArtifactId = row.Id; offer.Version++; app.Version++;
                return (Artifact: row, Bytes: bytes);
            }, ct);
            artifact = pending.Artifact;
            // Pending is durable intent, not proof that the remote writer has stopped. An exception
            // or process death leaves it unresolved even when the key is currently absent/present.
            await UploadOnceAsync(actor, artifact, pending.Bytes, ct);
            await VerifiedBytesAsync(actor, artifact, ct);
        }

        try
        {
            return await Lifecycle.MutateAsync(actor, offerId, artifact.PendingOfferVersion, true, async (app, offer) =>
            {
                OfferApplicationBoundary.RequireUnreserved(app); OfferApplicationBoundary.RequireActive(app);
                await RequireReviewedPolicyAsync(actor, offer, ct);
                var row = await ReloadAsync(actor, artifact.Id, ct);
                if (offer.ArtifactId != row.Id || row.State != "Uploaded" || offer.Version != row.PendingOfferVersion)
                    throw new OfferLifecycleException("offer_version_conflict", "The pending original no longer matches this offer version.");
                row.State = "Finalized"; row.FinalizedAtUtc = DateTime.UtcNow; row.Version++;
                offer.FrozenAtUtc = row.FinalizedAtUtc; offer.Version++; app.Version++;
                Lifecycle.AddEvent(actor, offer, "Frozen", "Finalized the original reviewed PDF.",
                    new { artifactId = row.Id, row.Sha256, row.SizeBytes, row.RendererVersion });
                return Dto(row, offer);
            }, ct);
        }
        catch (OfferLifecycleException e) when (e.Code == "offer_policy_changed")
        {
            // This is a known rejection, not an unknown commit result. Release the editable draft
            // while retaining the unique blob key until strict deletion is confirmed.
            await AbandonUploadAsync(actor, artifact, ct);
            throw;
        }
    }

    private async Task UploadOnceAsync(OfferActor actor, OfferArtifact artifact, byte[] bytes, CancellationToken ct)
    {
        try
        {
            // This strategy NEVER replays its delegate, even when Npgsql normally retries DB work.
            // The application lock fences live uploads against both tenant erasure paths.
            await new UploadSingleAttemptStrategy(db).ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                _ = await OfferApplicationBoundary.LockAsync(db, actor.TenantId, actor.CompanyId, artifact.ApplicationId, ct)
                    ?? throw new OfferLifecycleException("offer_not_found", "The application was removed before upload.", 404);
                var row = await ReloadAsync(actor, artifact.Id, ct);
                if (row.State != "Pending" || !await db.OfferLetters.AsNoTracking().AnyAsync(o => o.TenantId == actor.TenantId
                    && o.CompanyId == actor.CompanyId && o.Id == artifact.OfferId && o.ArtifactId == artifact.Id, ct))
                    throw new OfferLifecycleException("offer_upload_unresolved", "The upload intent no longer matches its parent.");
                await storage.PutAtAsync(actor.TenantId, artifact.StorageKey, bytes, "application/pdf", ct);
                row.State = "Uploaded"; row.Version++;
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            });
        }
        catch
        {
            // Do not infer remote completion from an exception or retry the same key. If commit
            // was ambiguous, a later read sees either durable Uploaded or unresolved Pending.
            db.ChangeTracker.Clear(); throw;
        }
    }

    private sealed class UploadSingleAttemptStrategy(ZayraDbContext context) : ExecutionStrategy(context, 0, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => false;
    }

    private async Task RequireReviewedPolicyAsync(OfferActor actor, OfferLetter offer, CancellationToken ct)
    {
        if (actor.CompanyId is not { } company)
            throw new OfferLifecycleException("offer_company_required", "Managed offers require a legal company.");
        var policy = await OfferComponentOptions.PolicyAsync(db, actor.TenantId, company, ct);
        if (offer.OfferPolicyVersion != policy.ExpectedVersion || offer.CompensationCurrency != policy.Currency
            || offer.CompensationPrecision != policy.Precision)
            throw new OfferLifecycleException("offer_policy_changed", "The company offer policy changed. Review and explicitly save the draft before freezing.");
    }

    private async Task<OfferArtifact> ReloadAsync(OfferActor actor, Guid id, CancellationToken ct)
    {
        var stale = db.OfferArtifacts.Local.FirstOrDefault(a => a.Id == id);
        if (stale is not null) db.Entry(stale).State = EntityState.Detached;
        return await Artifacts(actor).SingleAsync(a => a.Id == id, ct);
    }

    private Task<bool> AbandonUploadAsync(OfferActor actor, OfferArtifact artifact, CancellationToken ct) =>
        OfferApplicationBoundary.ExecuteAsync(db, actor, artifact.ApplicationId, async app =>
        {
            var row = await ReloadAsync(actor, artifact.Id, ct);
            if (row.State != "Uploaded") return false;
            var stale = db.OfferLetters.Local.FirstOrDefault(o => o.Id == artifact.OfferId);
            if (stale is not null) db.Entry(stale).State = EntityState.Detached;
            var offer = await db.OfferLetters.SingleAsync(o => o.Id == artifact.OfferId && o.TenantId == actor.TenantId && o.CompanyId == actor.CompanyId, ct);
            if (offer.ArtifactId == row.Id && offer.FrozenAtUtc is null)
            { offer.ArtifactId = null; offer.Version++; }
            row.State = "DeletionPending"; row.Version++;
            return true;
        }, ct);

    public async Task<byte[]> OriginalAsync(OfferActor actor, Guid offerId, CancellationToken ct = default)
    {
        var offer = await Lifecycle.ReadAsync(actor, offerId, ct);
        if (offer.ArtifactId is not { } id || offer.FrozenAtUtc is null)
            throw new OfferLifecycleException("offer_original_unavailable", "This offer has no finalized original. Legacy downloads are reconstructed.", 404);
        var artifact = await Artifacts(actor).AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.OfferId == offer.Id && a.State == "Finalized", ct)
            ?? throw new OfferLifecycleException("offer_artifact_pending", "The original document has not been finalized.");
        return await VerifiedBytesAsync(actor, artifact, ct);
    }

    private async Task<byte[]> VerifiedBytesAsync(OfferActor actor, OfferArtifact artifact, CancellationToken ct)
    {
        byte[] bytes;
        try { bytes = await storage.GetBytesAsync(actor.TenantId, artifact.StorageKey, ct); }
        catch (FileNotFoundException) { throw new OfferLifecycleException("offer_artifact_missing", "The stored original is missing. Issuance is blocked."); }
        if (bytes.LongLength != artifact.SizeBytes || Hash(bytes) != artifact.Sha256)
            throw new OfferLifecycleException("offer_artifact_integrity", "The stored original failed its integrity check. Issuance is blocked.");
        return bytes;
    }

    public async Task<int> CleanupOrphansAsync(OfferActor actor, int limit, CancellationToken ct = default)
    {
        var candidates = await Artifacts(actor).AsNoTracking().Where(a => a.State == "DeletionPending")
            .OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Id).Take(Math.Clamp(limit, 1, 100)).ToListAsync(ct);
        var deleted = 0;
        foreach (var candidate in candidates)
        {
            var removed = await OfferApplicationBoundary.ExecuteAsync(db, actor, candidate.ApplicationId, async app =>
            {
                var row = await ReloadAsync(actor, candidate.Id, ct);
                if (row.State != "DeletionPending" || await db.OfferLetters.AnyAsync(o => o.TenantId == actor.TenantId && o.ArtifactId == row.Id, ct)) return false;
                await storage.DeleteStrictAsync(actor.TenantId, row.StorageKey, ct);
                row.State = "Deleted"; row.DeletedAtUtc = DateTime.UtcNow; row.Version++;
                return true;
            }, ct);
            if (removed) deleted++;
        }
        return deleted;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static OfferArtifactDto Dto(OfferArtifact row, OfferLetter offer) =>
        new(row.Id, row.Sha256, row.SizeBytes, row.Provenance, offer.FrozenAtUtc, offer.Version);
}
