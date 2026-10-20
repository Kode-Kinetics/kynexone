using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Recruitment;

/// <summary>Whole-tenant erasure only. Durable keys survive every unconfirmed storage deletion.
/// Normal orphan cleanup must never use this path to delete a referenced original.</summary>
public static class OfferArtifactErasure
{
    private const string Scope = "Owner-confirmed or retention-authorized erasure of one soft-deleted tenant; all company rows remain pinned to that tenant.";

    public sealed record Result(int FilesDeleted, Dictionary<string, int> RowsDeleted);

    private static async Task<int> DeleteAllFilesLockedAsync(ZayraDbContext db, IDocumentStorage? storage, Guid tenantId, CancellationToken ct)
    {
        if (await Rows<OfferArtifact>(db, tenantId).AnyAsync(a => a.State == "Pending", ct))
            throw new IOException("Offer upload completion is unresolved. Retain its durable obligation; an operator must establish that every remote write has finished before tenant erasure.");
        var keys = await Rows<OfferArtifact>(db, tenantId).AsNoTracking().Where(a => a.State != "Deleted")
            .Select(a => a.StorageKey).Distinct().ToListAsync(ct);
        if (keys.Count == 0) return 0;
        if (storage is null) throw new IOException("Offer artifact storage is unavailable; durable deletion obligations are retained.");
        foreach (var key in keys) await storage.DeleteStrictAsync(tenantId, key, ct);
        return keys.Count;
    }

    // Files and metadata share the upload fence. Never expose separate "delete then erase" calls:
    // a live upload could otherwise complete between them. Selfie cleanup remains the caller's prerequisite.
    public static async Task<Result> EraseAsync(ZayraDbContext db, IDocumentStorage? storage, Guid tenantId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) return await EraseLockedAsync(db, storage, tenantId, ct);
        Result? erased = null;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            erased = await EraseLockedAsync(db, storage, tenantId, ct);
            await tx.CommitAsync(ct);
        });
        return erased!;
    }

    private static async Task<Result> EraseLockedAsync(ZayraDbContext db, IDocumentStorage? storage, Guid tenantId, CancellationToken ct)
    {
        if (!await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId && !t.IsActive && t.Slug.Contains("__deleted_"), ct))
            throw new InvalidOperationException("Offer erasure requires a soft-deleted tenant.");
        // Application-first, deterministic lock order also holds for this terminal mutation.
        await Rows<JobApplication>(db, tenantId).AsNoTracking().OrderBy(a => a.Id)
            .TagWith(RowLockingInterceptor.ForUpdateTag).ToListAsync(ct);
        if (await Rows<OfferDispatch>(db, tenantId).AnyAsync(d => d.Outcome == "Sending" || d.Outcome == "Unknown"
            || (d.TransportStartedAtUtc != null && d.TransportCompletedAtUtc == null), ct))
            throw new IOException("Offer dispatch completion is unresolved. Retain its transport evidence before tenant erasure.");
        var files = await DeleteAllFilesLockedAsync(db, storage, tenantId, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.offer_lifecycle_erasure', {tenantId.ToString()}, true)", ct);
        // The outer whole-tenant purge removes drafts later. Release restrict source references
        // only after every upload/dispatch obligation is resolved, in this same rollback boundary.
        await Rows<EmployeeDraft>(db, tenantId).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.AcceptedOfferId, (Guid?)null).SetProperty(d => d.AcceptedOfferCompanyId, (Guid?)null)
            .SetProperty(d => d.OfferCompensationPlacementCompanyId, (Guid?)null)
            .SetProperty(d => d.AcceptedOfferCompensationJson, (string?)null).SetProperty(d => d.OfferCompensationDispositionsJson, (string?)null)
            .SetProperty(d => d.OfferCompensationConfirmedFingerprint, (string?)null).SetProperty(d => d.OfferCompensationConfirmedAtUtc, (DateTime?)null)
            .SetProperty(d => d.OfferCompensationConfirmedByUserId, (Guid?)null).SetProperty(d => d.OfferCompensationVersion, 0L), ct);
        await Rows<JobApplication>(db, tenantId).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.CurrentIssuedOfferId, (Guid?)null).SetProperty(a => a.OfferReservationId, (Guid?)null)
            .SetProperty(a => a.OfferReservationOfferId, (Guid?)null).SetProperty(a => a.OfferReservationState, (string?)null)
            .SetProperty(a => a.OfferReservationVersion, (long?)null), ct);
        await Rows<OfferLetter>(db, tenantId).ExecuteUpdateAsync(s => s.SetProperty(o => o.ArtifactId, (Guid?)null)
            .SetProperty(o => o.FrozenAtUtc, (DateTime?)null).SetProperty(o => o.PreviousRevisionId, (Guid?)null), ct);
        await Rows<OfferSeries>(db, tenantId).ExecuteUpdateAsync(s => s.SetProperty(o => o.LegacyRootOfferId, (Guid?)null), ct);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [nameof(OfferDispatchAttempt)] = await Rows<OfferDispatchAttempt>(db, tenantId).ExecuteDeleteAsync(ct),
            [nameof(OfferDispatch)] = await Rows<OfferDispatch>(db, tenantId).ExecuteDeleteAsync(ct),
            [nameof(OfferApproval)] = await Rows<OfferApproval>(db, tenantId).ExecuteDeleteAsync(ct),
            [nameof(OfferLifecycleEvent)] = await Rows<OfferLifecycleEvent>(db, tenantId).ExecuteDeleteAsync(ct),
            [nameof(OfferArtifact)] = await Rows<OfferArtifact>(db, tenantId).ExecuteDeleteAsync(ct),
            [nameof(OfferLetter)] = await Rows<OfferLetter>(db, tenantId).ExecuteDeleteAsync(ct),
            [nameof(OfferSeries)] = await Rows<OfferSeries>(db, tenantId).ExecuteDeleteAsync(ct)
        };
        // Raw graph erasure must not leave tracked originals that a later SaveChanges can resurrect.
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is EmployeeDraft or OfferLetter or OfferSeries or OfferArtifact or OfferLifecycleEvent or OfferApproval or JobApplication or OfferDispatch or OfferDispatchAttempt).ToArray())
            if (entry.Entity is Zayra.Api.Domain.Entities.ITenantOwned owned && owned.TenantId == tenantId) entry.State = EntityState.Detached;
        return new(files, counts);
    }

    private static IQueryable<T> Rows<T>(ZayraDbContext db, Guid tenantId) where T : class =>
        ScopedBypass.TenantWideByConvention(db.Set<T>(), tenantId, Scope);
}
