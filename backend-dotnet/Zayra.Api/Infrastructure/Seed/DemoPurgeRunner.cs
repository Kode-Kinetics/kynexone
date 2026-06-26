using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Seed;

/// <summary>
/// One-off cleanup: deactivates every known demo/sample tenant so a live
/// environment shows only real data. Reversible (soft deactivation — IsActive=false,
/// users deactivated, sessions revoked, subscription cancelled), never a hard delete.
///
/// Run as a Render one-off job:  dotnet Zayra.Api.dll --purge-demo
///
/// Protected slugs are NEVER touched, regardless of the demo list. The real
/// SeedAdmin tenant slug (from config) is always added to the protected set.
/// </summary>
public static class DemoPurgeRunner
{
    // Every tenant slug the demo seeders have ever created.
    private static readonly string[] DemoSlugs =
        ["intelliflow", "evostel", "alnakheel", "rasalmanar"];

    public static async Task RunAsync(
        ZayraDbContext db,
        string? protectedSlug,
        ILogger logger,
        CancellationToken ct = default)
    {
        // Hard guard: these are never deactivated even if they appear in DemoSlugs.
        var guarded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "zayra" };
        if (!string.IsNullOrWhiteSpace(protectedSlug)) guarded.Add(protectedSlug.Trim());

        var targets = DemoSlugs
            .Where(s => !guarded.Contains(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        logger.LogInformation(
            "DemoPurge: targeting {Targets}; protecting {Protected}.",
            string.Join(", ", targets), string.Join(", ", guarded));

        var tenants = await db.Tenants
            .Where(t => targets.Contains(t.Slug) && t.IsActive)
            .ToListAsync(ct);

        if (tenants.Count == 0)
        {
            logger.LogInformation("DemoPurge: no active demo tenants found — nothing to do.");
            return;
        }

        foreach (var tenant in tenants)
        {
            // Defensive double-check: never deactivate a guarded tenant.
            if (guarded.Contains(tenant.Slug))
            {
                logger.LogWarning("DemoPurge: refusing to touch protected tenant '{Slug}'.", tenant.Slug);
                continue;
            }

            logger.LogInformation("DemoPurge: deactivating '{Slug}' ({Id}).", tenant.Slug, tenant.Id);
            tenant.IsActive = false;

            await db.Users
                .Where(u => u.TenantId == tenant.Id && !u.IsDeleted)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.IsActive,     false)
                    .SetProperty(u => u.Status,       "Deactivated")
                    .SetProperty(u => u.UpdatedAtUtc, DateTime.UtcNow), ct);

            var userIds = await db.Users
                .Where(u => u.TenantId == tenant.Id)
                .Select(u => u.Id)
                .ToListAsync(ct);
            if (userIds.Count > 0)
                await db.RefreshTokens
                    .Where(r => r.RevokedAtUtc == null && userIds.Contains(r.UserId))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAtUtc, DateTime.UtcNow), ct);

            var sub = await db.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == tenant.Id, ct);
            if (sub is not null) sub.Status = "Cancelled";

            await db.SaveChangesAsync(ct);
            logger.LogInformation("DemoPurge: '{Slug}' deactivated.", tenant.Slug);
        }

        logger.LogInformation("DemoPurge: complete — {Count} tenant(s) deactivated.", tenants.Count);
    }
}
