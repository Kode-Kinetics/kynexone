using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Modules;

/// <summary>
/// Features that are OFF unless a tenant has an explicit <c>tenant_feature_flags</c> row with IsEnabled = true —
/// the reverse of every <see cref="ModuleCatalog"/> module, where an absent row means on.
///
/// <para><b>Why a separate list.</b> A module defaults on so a forgotten row never locks a paying tenant out of
/// what they bought. A feature still being rolled out must default the other way: a live tenant (Evostel) must
/// not see Release A screens or reach its APIs until the platform team switches it on. Keys here are refused by
/// the tenant-facing module API (they are in <see cref="ModuleCatalog.NonModuleKeys"/>), so only the platform
/// flag endpoint can enable them.</para>
///
/// <para><b>Enforcement.</b> <see cref="TenantModuleService"/> reports an opt-in key as disabled unless enabled,
/// so the frontend's <c>isFeatureEnabled</c> and every server check read one answer.
/// <c>FeatureFlagGuardFilter</c> refuses the API prefixes below with 403 <c>feature_not_enabled</c>; an endpoint
/// outside them (e.g. a deductions statement under /api/payroll) carries <see cref="Filters.RequireOptInFeatureAttribute"/>.</para>
/// </summary>
public static class OptInFeatures
{
    /// <summary>API prefixes each opt-in feature owns. Longest prefix wins.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> RoutePrefixes =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [FeatureKeys.ReleaseA] =
            [
                "/api/entitlements",       // R1 matrix, R2 HR package view
                "/api/contracts",          // R4–R6 renewal case and chain
                "/api/ess/package",        // R2 employee package
                "/api/ess/deductions",     // R3 employee deductions
                "/api/ess/renewal-offer",  // R6 employee acceptance
            ],
        };

    public static IReadOnlyCollection<string> Keys => RoutePrefixes.Keys.ToArray();

    public static bool IsOptIn(string? key) => key is not null && RoutePrefixes.ContainsKey(key);

    private static readonly (string Prefix, string Key)[] Index =
        RoutePrefixes.SelectMany(kv => kv.Value.Select(p => (Prefix: p, kv.Key)))
            .OrderByDescending(x => x.Prefix.Length)
            .ToArray();

    /// <summary>The opt-in feature that owns an API path, or NULL when none does.</summary>
    public static string? ResolveApiPath(string path)
    {
        foreach (var (prefix, key) in Index)
        {
            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return key;
        }
        return null;
    }
}
