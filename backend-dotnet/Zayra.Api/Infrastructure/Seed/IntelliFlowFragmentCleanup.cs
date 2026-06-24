using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Seed;

/// <summary>
/// One-time idempotent cleanup of the 5 split-tenant IntelliFlow fragments produced by
/// SeedDemoData corruption. Soft-deletes each fragment (marks inactive, deactivates users,
/// revokes tokens, cancels subscription, frees slug for reuse).
///
/// CRITICAL GUARD: rasalmanar (15b0c4c2-bc3f-4428-a448-bc92e937038f) is EXPLICITLY excluded.
///
/// Fragments targeted:
///   4afd127d-*  slug=intelliflow-system   (15 employees, 0 companies)
///   807f277f-*  slug=intelliflow           (9 users, 1 company, 0 employees)
///   67802831-*  slug=intelliflow__deleted_67802831
///   dd7c2ff9-*  slug=intelliflow__deleted_dd7c2ff9
///   004e58eb-*  slug=intelliflow__deleted_004e58eb
/// </summary>
public static class IntelliFlowFragmentCleanup
{
    // The 5 fragment slugs — used for lookup (avoids partial-UUID matching).
    private static readonly string[] FragmentSlugs =
    [
        "intelliflow-system",
        "intelliflow",
        "intelliflow__deleted_67802831",
        "intelliflow__deleted_dd7c2ff9",
        "intelliflow__deleted_004e58eb",
    ];

    // Rasalmanar: MUST NOT be touched under any circumstances.
    private static readonly Guid RasalmanarId = new("15b0c4c2-bc3f-4428-a448-bc92e937038f");

    public static async Task RunAsync(ZayraDbContext db, ILogger logger, CancellationToken ct = default)
    {
        var slugSet = new HashSet<string>(FragmentSlugs, StringComparer.OrdinalIgnoreCase);

        var fragments = await db.Tenants
            .Where(t => slugSet.Contains(t.Slug))
            .ToListAsync(ct);

        if (fragments.Count == 0)
        {
            logger.LogInformation("IntelliFlowFragmentCleanup: no fragments found — already clean.");
            return;
        }

        // ── CRITICAL GUARD ────────────────────────────────────────────────────────
        if (fragments.Any(t => t.Id == RasalmanarId))
        {
            logger.LogCritical(
                "IntelliFlowFragmentCleanup: ABORTED — rasalmanar (15b0c4c2) was found in the " +
                "fragment set. This should never happen. No changes made.");
            throw new InvalidOperationException(
                "IntelliFlowFragmentCleanup: rasalmanar tenant matched fragment slug list — abort.");
        }
        // ─────────────────────────────────────────────────────────────────────────

        logger.LogInformation(
            "IntelliFlowFragmentCleanup: found {Count} fragment(s): {Slugs}",
            fragments.Count, string.Join(", ", fragments.Select(t => $"'{t.Slug}' ({t.Id.ToString()[..8]})")));

        // Pre-flight: report FK child-row counts per tenant so the log is auditable.
        await ReportFkCountsAsync(db, fragments.Select(t => t.Id).ToList(), logger, ct);

        // Soft-delete each fragment.
        foreach (var tenant in fragments)
        {
            if (!tenant.IsActive && tenant.Slug.Contains("__deleted_"))
            {
                logger.LogInformation(
                    "IntelliFlowFragmentCleanup: '{Slug}' ({Id}) already soft-deleted — skipping.",
                    tenant.Slug, tenant.Id.ToString()[..8]);
                continue;
            }

            logger.LogInformation(
                "IntelliFlowFragmentCleanup: soft-deleting '{Slug}' ({Id})...",
                tenant.Slug, tenant.Id.ToString()[..8]);

            var originalSlug = tenant.Slug;
            tenant.IsActive = false;
            // Free the slug so the new clean IntelliFlow tenant can claim "intelliflow".
            // Match the pattern used by PlatformController.DeleteTenant for consistency.
            tenant.Slug = $"{originalSlug}__deleted_{tenant.Id.ToString("N")[..8]}";

            // Deactivate users
            await db.Users
                .Where(u => u.TenantId == tenant.Id && !u.IsDeleted)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.IsActive, false)
                    .SetProperty(u => u.Status, "Deactivated")
                    .SetProperty(u => u.UpdatedAtUtc, DateTime.UtcNow), ct);

            // Revoke active refresh tokens
            var userIds = await db.Users
                .Where(u => u.TenantId == tenant.Id)
                .Select(u => u.Id)
                .ToListAsync(ct);

            if (userIds.Count > 0)
                await db.RefreshTokens
                    .Where(r => r.RevokedAtUtc == null && userIds.Contains(r.UserId))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAtUtc, DateTime.UtcNow), ct);

            // Cancel subscription
            var sub = await db.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == tenant.Id, ct);
            if (sub is not null) sub.Status = "Cancelled";

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "IntelliFlowFragmentCleanup: '{OrigSlug}' → '{NewSlug}' soft-deleted OK.",
                originalSlug, tenant.Slug);
        }

        logger.LogInformation("IntelliFlowFragmentCleanup: all fragments processed.");
    }

    // ── Pre-flight FK count report ────────────────────────────────────────────

    private static async Task ReportFkCountsAsync(
        ZayraDbContext db, List<Guid> tenantIds, ILogger logger, CancellationToken ct)
    {
        foreach (var tid in tenantIds)
        {
            var tidStr = tid.ToString()[..8];
            var users     = await db.Users.CountAsync(x => x.TenantId == tid, ct);
            var companies = await db.Companies.CountAsync(x => x.TenantId == tid, ct);
            var employees = await db.Employees.CountAsync(x => x.TenantId == tid, ct);
            var payRuns   = await db.PayrollRuns.CountAsync(x => x.TenantId == tid, ct);
            var paySlips  = await db.PayrollSlips.CountAsync(x => x.TenantId == tid, ct);
            var payDeds   = await db.PayrollDeductions.CountAsync(x => x.TenantId == tid, ct);

            logger.LogInformation(
                "IntelliFlowFragmentCleanup FK counts [{TenantId}]: " +
                "users={Users} companies={Companies} employees={Employees} " +
                "payroll_runs={PayRuns} payroll_slips={PaySlips} payroll_deductions={PayDeds}",
                tidStr, users, companies, employees, payRuns, paySlips, payDeds);
        }
    }
}
