using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Recruitment;

public sealed class OfferLifecycleException(string code, string message, int status = 409) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

/// <summary>Every competing offer/application mutation locks the application before any offer write.
/// Locks are transaction-owned; an unresolved dispatch reservation never expires implicitly.</summary>
public static class OfferApplicationBoundary
{
    public static Task<JobApplication?> LockAsync(ZayraDbContext db, Guid tenantId, Guid? companyId,
        Guid applicationId, CancellationToken ct)
    {
        var query = db.JobApplications.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.CompanyId == companyId && a.Id == applicationId);
        if (db.Database.IsRelational())
        {
            if (db.Database.CurrentTransaction is null)
                throw new InvalidOperationException("The application lock requires a transaction.");
            query = query.TagWith(RowLockingInterceptor.ForUpdateTag);
        }
        return query.SingleOrDefaultAsync(ct);
    }

    public static void RequireUnreserved(JobApplication app)
    {
        if (app.OfferReservationId is not null)
            throw new OfferLifecycleException("offer_issuance_in_flight",
                "This application has an unresolved offer delivery. Cancel or reconcile it before continuing.");
    }

    public static void RequireActive(JobApplication app)
    {
        if (app.Status != "Active" || app.OnboardingDraftId is not null)
            throw new OfferLifecycleException("application_not_active", "The application is no longer eligible for an offer mutation.");
    }

    public static void RequireVersion(OfferLetter offer, long expectedVersion)
    {
        if (offer.Version != expectedVersion)
            throw new OfferLifecycleException("offer_version_conflict", "The offer changed. Reload it before continuing.");
    }

    public static async Task<T> ExecuteAsync<T>(ZayraDbContext db, OfferActor actor, Guid applicationId,
        Func<JobApplication, Task<T>> mutation, CancellationToken ct)
    {
        T? result = default;
        var attempt = 0;
        try
        {
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                if (attempt++ > 0) db.ChangeTracker.Clear();
                await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
                var app = await LockAsync(db, actor.TenantId, actor.CompanyId, applicationId, ct)
                    ?? throw new OfferLifecycleException("offer_not_found", "Application not found.", 404);
                result = await mutation(app);
                await db.SaveChangesAsync(ct);
                if (tx is not null) await tx.CommitAsync(ct);
            });
        }
        catch
        {
            // A caller may recover within this request scope. Never carry uncommitted events or
            // entities from a rolled-back attempt into that recovery's next SaveChanges.
            db.ChangeTracker.Clear();
            throw;
        }
        return result!;
    }
}
