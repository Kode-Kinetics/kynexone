using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Seed;

/// <summary>
/// Idempotent seeder for the Phase-2 GL defaults: the tenant-default chart of accounts, the
/// tenant-default driver→account mappings, the 17 system posting drivers (gl_drivers), and the
/// client-configurable rate allow-list (client_rate_definitions). Seeded as DEFAULTS — never the
/// only source. Safe to call repeatedly (up-serts absent rows only) so it survives tenant creation
/// and re-provisioning. Does NOT SaveChanges — the caller owns the transaction boundary.
/// </summary>
public static class GlDriverSeeder
{
    public readonly record struct SeedResult(int Accounts, int Mappings, int Drivers, int RateDefinitions);

    public static async Task<SeedResult> SeedTenantDefaultsAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        // ── Chart of accounts (tenant defaults, CompanyId == null) ──────────────
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var existingCodes = (await db.GlAccounts.IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && a.CompanyId == null)
            .Select(a => a.Code).ToListAsync(ct)).ToHashSet();
        var codeToId = new Dictionary<string, Guid>();
        int accounts = 0;
        foreach (var d in PayrollGlCatalog.Drivers)
        {
            if (codeToId.ContainsKey(d.DefaultCode) || existingCodes.Contains(d.DefaultCode)) continue;
            var acct = new GlAccount { TenantId = tenantId, CompanyId = null, Code = d.DefaultCode, Name = d.DefaultName, AccountType = d.AccountType };
            db.GlAccounts.Add(acct);
            codeToId[d.DefaultCode] = acct.Id;
            accounts++;
        }
        await db.SaveChangesAsync(ct);

        // ── Tenant-default driver→account mappings ──────────────────────────────
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var allAccounts = await db.GlAccounts.IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && a.CompanyId == null)
            .ToDictionaryAsync(a => a.Code, a => a.Id, ct);
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var existingDrivers = (await db.GlAccountMappings.IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.CompanyId == null)
            .Select(m => m.DriverKey).ToListAsync(ct)).ToHashSet();
        int mappings = 0;
        foreach (var d in PayrollGlCatalog.Drivers)
        {
            if (existingDrivers.Contains(d.Key)) continue;
            if (!allAccounts.TryGetValue(d.DefaultCode, out var aid)) continue;
            db.GlAccountMappings.Add(new GlAccountMapping { TenantId = tenantId, CompanyId = null, DriverKey = d.Key, AccountId = aid });
            mappings++;
        }

        // ── 17 system posting drivers (gl_drivers) ──────────────────────────────
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var existingDriverKeys = (await db.GlDrivers.IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId && d.CompanyId == null)
            .Select(d => d.Key).ToListAsync(ct)).ToHashSet();
        int drivers = 0;
        foreach (var seed in PayrollGlCatalog.SystemDriverSeeds(tenantId))
        {
            if (existingDriverKeys.Contains(seed.Key)) continue;
            db.GlDrivers.Add(seed);
            drivers++;
        }

        // ── Client-configurable rate allow-list (client_rate_definitions) ───────
        // IgnoreQueryFilters is intentional: system/config read — scope authorised above (or seeder), WHERE re-applies exact tenant+company scope; never reads another tenant.
        var existingRateKeys = (await db.ClientRateDefinitions.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId)
            .Select(r => r.RateKey).ToListAsync(ct)).ToHashSet();
        int rateDefs = 0;
        foreach (var def in StatutoryRateGuard.DefaultClientRateDefinitions(tenantId))
        {
            if (existingRateKeys.Contains(def.RateKey)) continue;
            db.ClientRateDefinitions.Add(def);
            rateDefs++;
        }

        return new SeedResult(accounts, mappings, drivers, rateDefs);
    }
}
