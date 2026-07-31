using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

/// <summary>
/// Idempotent seeder for the data-driven pay-component defaults (pay_components): the system rows that
/// reproduce PayrollController.Process's current earning/deduction set byte-for-byte. Seeded as
/// DEFAULTS — never the only source (the engine falls back to the compiled sequence when the store is
/// empty). Safe to call repeatedly (up-serts absent rows only, keyed by tenant-default code+type) so it
/// survives tenant creation and re-provisioning. Does NOT SaveChanges — the caller owns the transaction
/// boundary. Deliberately mirrors <see cref="GlDriverSeeder"/>.
/// </summary>
public static class PayComponentSeeder
{
    public readonly record struct SeedResult(int Components);

    public static async Task<SeedResult> SeedTenantDefaultsAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        // IgnoreQueryFilters is intentional: system/config read — scope authorised by the caller (or the
        // seeder), the WHERE re-applies exact tenant + tenant-default (CompanyId == null) scope, and never
        // reads another tenant. We key existence on (code, component_type) because "ADJ" is both an earning
        // family and a deduction family.
        var existing = (await db.PayComponents.IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId && c.CompanyId == null)
                .Select(c => new { c.Code, c.ComponentType })
                .ToListAsync(ct))
            .Select(x => (x.Code, x.ComponentType))
            .ToHashSet();

        int components = 0;
        foreach (var seed in PayComponentCatalog.SystemComponentSeeds(tenantId))
        {
            if (existing.Contains((seed.Code, seed.ComponentType))) continue;
            db.PayComponents.Add(seed);
            components++;
        }

        return new SeedResult(components);
    }
}
