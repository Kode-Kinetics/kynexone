using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Modules;

/// <summary>
/// Features that are ON unless the platform team switches them off for a tenant (an explicit <c>tenant_feature_flags</c> row with
/// IsEnabled = false) — the third kind next to a <see cref="ModuleCatalog"/> module (on, and Tenant Admin may switch it) and an
/// <see cref="OptInFeatures"/> feature (off until the platform switches it on).
///
/// <para>These are not modules: a Tenant Admin cannot switch them (they are in <see cref="ModuleCatalog.NonModuleKeys"/>), and each
/// is read by the code that owns it rather than by a route guard. The platform console shows them with their real default, so a
/// tenant with no row reads as ON.</para>
/// </summary>
public static class PlatformDefaultOnFeatures
{
    public static readonly IReadOnlySet<string> Keys = new HashSet<string>(StringComparer.Ordinal)
    {
        // Saudi clients must be able to produce the bank instruction file; the switch exists only as a kill switch.
        FeatureKeys.SaudiBankExport,
    };

    public static bool IsDefaultOn(string? key) => key is not null && Keys.Contains(key);
}
