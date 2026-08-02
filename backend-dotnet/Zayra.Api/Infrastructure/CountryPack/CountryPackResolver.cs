using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.CountryPack;

namespace Zayra.Api.Infrastructure.CountryPack;

// Resolves the correct strategy implementation for a given country + jurisdiction.
// Lookup order: exact jurisdiction key (e.g. "ARE:UAE-DIFC") → country-wide key
// ("ARE") → non-keyed fallback (Default pack).
// Business logic calls the resolver — it never branches on country with if/switch.

public sealed class CountryPackResolver : ICountryPackResolver
{
    private readonly IServiceProvider _sp;
    public CountryPackResolver(IServiceProvider sp) => _sp = sp;

    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => Resolve<IStatutoryDeductionCalculator>(cc, j);

    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j)
        => Resolve<IEndOfServiceCalculator>(cc, j);

    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j)
        => Resolve<IWageProtectionExporter>(cc, j);

    public INationalizationTracker ResolveNationalizationTracker(string cc, string j)
        => Resolve<INationalizationTracker>(cc, j);

    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j)
        => Resolve<ILocalizationProfile>(cc, j);

    public ICountryPackDescriptor ResolveDescriptor(string cc, string j)
        => Resolve<ICountryPackDescriptor>(cc, j);

    public IIdentityDocumentFormat ResolveIdentityDocumentFormat(string cc, string j)
        => Resolve<IIdentityDocumentFormat>(cc, j);

    private T Resolve<T>(string countryCode, string jurisdiction) where T : class
    {
        // Pack registrations are keyed by ISO-3 ("SAU", "ARE"); canonical stored data is
        // ISO-2 ("SA", "AE"). Try the raw value first (backward compat for ISO-3 data),
        // then the explicit ISO-2 → ISO-3 mapping layer. Never mutate stored values here.
        var exact = _sp.GetKeyedService<T>($"{countryCode}:{jurisdiction}")
                    ?? _sp.GetKeyedService<T>(countryCode);
        if (exact is not null) return exact;

        var iso3 = Application.Common.CountryCodeStandard.ToIso3(countryCode);
        if (iso3 is not null && !iso3.Equals(countryCode, StringComparison.OrdinalIgnoreCase))
        {
            var mapped = _sp.GetKeyedService<T>($"{iso3}:{jurisdiction}")
                         ?? _sp.GetKeyedService<T>(iso3);
            if (mapped is not null) return mapped;
        }

        return _sp.GetRequiredService<T>();  // non-keyed default pack
    }
}

// Retained for test use: allows constructing a resolver with explicit strategy
// instances without needing a full DI container.
public sealed record KeyedService<T>(string Key, T Service);
