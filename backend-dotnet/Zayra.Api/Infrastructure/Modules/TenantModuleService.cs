using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Modules;

/// <summary>
/// The effective module state for one tenant, after the catalog's locks have been applied to
/// whatever is stored in <c>tenant_feature_flags</c>.
/// </summary>
public sealed class TenantModuleState
{
    /// <summary>The tenant's ISO-3166 alpha-2 country, from tenant localisation. Null when unset.</summary>
    public required string? CountryCode { get; init; }

    /// <summary>Raw stored state: keys explicitly set to <c>IsEnabled = false</c>.</summary>
    public required IReadOnlySet<string> StoredDisabledKeys { get; init; }

    /// <summary>
    /// The keys that are actually off, after locks. A stored <c>false</c> on a module the tenant
    /// is not permitted to disable is ignored rather than honoured — that is the whole point of
    /// the lock, and historically the reason a KSA tenant could switch off GOSI filing by
    /// switching off Qiwa.
    /// </summary>
    public required IReadOnlySet<string> EffectiveDisabledKeys { get; init; }

    /// <summary>
    /// Absent key = enabled. This preserves the pre-existing backend default (fail-open): a tenant
    /// with no rows at all has every module, and a key nobody has ever written cannot accidentally
    /// lock people out of a module they are paying for.
    /// </summary>
    public bool IsEnabled(string moduleKey) => !EffectiveDisabledKeys.Contains(moduleKey);
}

public interface ITenantModuleService
{
    Task<TenantModuleState> GetStateAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Drop the cached state for a tenant. Call after any module toggle.</summary>
    void Invalidate(Guid tenantId);
}

/// <summary>
/// Reads <c>tenant_feature_flags</c> and the tenant's country once, applies
/// <see cref="ModuleCatalog"/>'s locks, and caches the result for the whole tenant.
///
/// <para>Every enforcement layer resolves through this one type, so the API guard, the notification
/// dispatcher, the dashboard and the module API cannot disagree about whether a module is on.</para>
/// </summary>
public sealed class TenantModuleService : ITenantModuleService
{
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Per-tenant cache generation. Invalidation bumps this rather than removing a fixed key.
    ///
    /// <para>Removing a key is not enough under concurrency, and the failure is nasty: a read that
    /// began before the write can finish afterwards and re-insert the pre-toggle state, which then
    /// sticks for the full TTL. Observed in the browser — the module switch flipped, the write
    /// succeeded, and the navigation kept showing the module for two minutes, which is
    /// indistinguishable to a user from a switch that does nothing.</para>
    ///
    /// <para>With a generation in the key, a late writer stores under the superseded key and every
    /// subsequent reader looks under the new one.</para>
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, long> Generations = new();

    private readonly ZayraDbContext _db;
    private readonly IMemoryCache _cache;

    public TenantModuleService(ZayraDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    private static long GenerationOf(Guid tenantId) => Generations.GetValueOrDefault(tenantId, 0);

    internal static string CacheKey(Guid tenantId) => $"modules:{tenantId}:{GenerationOf(tenantId)}";

    public void Invalidate(Guid tenantId) => Invalidate(_cache, tenantId);

    public static void Invalidate(IMemoryCache cache, Guid tenantId)
    {
        // Drop the current entry AND move the generation on, so an in-flight read cannot restore it.
        cache.Remove(CacheKey(tenantId));
        Generations.AddOrUpdate(tenantId, 1, (_, v) => v + 1);
    }

    public async Task<TenantModuleState> GetStateAsync(Guid tenantId, CancellationToken ct = default)
    {
        // Captured once: the generation may move while the database read is in flight, and the
        // result must then be stored against the generation it was actually read for.
        var generation = GenerationOf(tenantId);
        var cacheKey = $"modules:{tenantId}:{generation}";

        if (_cache.TryGetValue(cacheKey, out TenantModuleState? cached) && cached is not null)
            return cached;

        var storedDisabled = await _db.TenantFeatureFlags
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && !f.IsEnabled)
            .Select(f => f.FeatureKey)
            .ToListAsync(ct);

        var countryCode = await _db.TenantLocalizationSettings
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .Select(l => l.CountryCode)
            .FirstOrDefaultAsync(ct);

        var state = Build(storedDisabled, countryCode);

        // Only cache if the generation still holds. If a write landed while this read was in
        // flight, this result is already stale and must not be published.
        if (GenerationOf(tenantId) == generation)
            _cache.Set(cacheKey, state, CacheTtl);

        return state;
    }

    /// <summary>
    /// Pure construction of the effective state. Exposed so the lock semantics can be tested
    /// without a database — the in-memory/Postgres divergence that has bitten this codebase before
    /// cannot reach a function that touches neither.
    /// </summary>
    public static TenantModuleState Build(IEnumerable<string> storedDisabledKeys, string? countryCode)
    {
        var stored = new HashSet<string>(storedDisabledKeys, StringComparer.Ordinal);

        // A stored `false` only counts if the tenant is permitted to disable that module.
        // Resolving the dependency (`ObligationArisesFrom`) against the STORED set rather than the
        // effective one keeps this a single pass and cannot oscillate: an obligation's origin is
        // itself never Statutory in the catalog.
        bool StoredEnabled(string key) => !stored.Contains(key);

        var effective = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in stored)
        {
            var module = ModuleCatalog.TryGet(key);
            if (module is null)
            {
                // Not a catalog module (a retired key, a sub-feature, or the demo seeder's version
                // stamp). It gates nothing, so carrying it through changes no behaviour — but it
                // must not be dropped either, or the admin API could not show that it is set.
                effective.Add(key);
                continue;
            }

            var decision = ModuleCatalog.CanDisable(module, countryCode, StoredEnabled);
            if (decision.IsAllowed) effective.Add(key);
            // else: locked — the stored `false` is deliberately not honoured.
        }

        return new TenantModuleState
        {
            CountryCode = countryCode,
            StoredDisabledKeys = stored,
            EffectiveDisabledKeys = effective,
        };
    }
}
