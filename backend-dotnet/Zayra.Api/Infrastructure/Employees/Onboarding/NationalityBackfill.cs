using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// One idempotent pass that maps each employee's free-text nationality to the ISO catalog WITHOUT changing
/// behaviour. Only rows still in state Unset are touched, so re-running is a no-op.
/// <list type="bullet">
/// <item>Maps exactly and the class equals what today's readers compute: code set, state Confirmed.</item>
/// <item>Maps exactly but the class would CHANGE: the code is stored as a proposal, state ProposedAuto, and nothing
/// classifies off it until HR confirms (with an audit entry).</item>
/// <item>Blank or unmapped ("Suadi"): state Unresolved, no code. Blank is unknown, not expatriate.</item>
/// </list>
/// </summary>
public static class NationalityBackfill
{
    private const int Batch = 500;

    public static async Task<int> RunAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var rows = await ScopedBypass.NullableTenantWide(db.Employees, tenantId,
                    "Per-tenant nationality backfill needs every company's employees; the tenant predicate is re-applied.")
                .Where(e => !e.IsDeleted && e.NationalityMappingState == NationalityMappingStates.Unset)
                .OrderBy(e => e.Id).Take(Batch).ToListAsync(ct);
            if (rows.Count == 0) return total;
            foreach (var e in rows) Classify(e);
            await db.SaveChangesAsync(ct);
            total += rows.Count;
        }
    }

    /// <summary>All tenants that still have Unset rows (bounded sweep), one tenant at a time.</summary>
    public static async Task<int> RunAllAsync(ZayraDbContext db, CancellationToken ct)
    {
        var total = 0;
        var tenants = await ScopedBypass.SystemWideDistinct(db.Employees, 1000,
                "Startup nationality backfill sweeps every tenant that still has unmapped employees; each pass is tenant-pinned.",
                e => !e.IsDeleted && e.TenantId != null && e.NationalityMappingState == NationalityMappingStates.Unset,
                e => e.TenantId)
            .ToListAsync(ct);
        foreach (var t in tenants.Where(t => t is not null)) total += await RunAsync(db, t!.Value, ct);
        return total;
    }

    internal static void Classify(Employee e)
    {
        var text = e.Nationality;
        var entry = NationalityCatalog.TryResolve(text);
        if (entry is null)
        {
            e.NationalityCode = null;
            e.NationalityMappingState = NationalityMappingStates.Unresolved;
            return;
        }
        e.NationalityCode = entry.Iso2;
        e.NationalityMappingState = NationalityCatalog.LegacyClassOf(text) == entry.Class
            ? NationalityMappingStates.Confirmed
            : NationalityMappingStates.ProposedAuto;
    }
}
