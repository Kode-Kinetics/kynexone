namespace Zayra.Api.Application.CountryPack;

public interface ICountryPackResolver
{
    IStatutoryDeductionCalculator ResolveDeductionCalculator(string countryCode, string jurisdiction);
    IEndOfServiceCalculator ResolveEndOfServiceCalculator(string countryCode, string jurisdiction);
    IWageProtectionExporter ResolveWageProtectionExporter(string countryCode, string jurisdiction);
    INationalizationTracker ResolveNationalizationTracker(string countryCode, string jurisdiction);
    ILocalizationProfile ResolveLocalizationProfile(string countryCode, string jurisdiction);
    ICountryPackDescriptor ResolveDescriptor(string countryCode, string jurisdiction);

    /// <summary>Identity-document FORMAT pack for a jurisdiction. Default-implemented as a no-op so the
    /// many existing resolver stubs need not change; the production CountryPackResolver overrides it with
    /// the ISO-3-keyed lookup.</summary>
    IIdentityDocumentFormat ResolveIdentityDocumentFormat(string countryCode, string jurisdiction)
        => NoIdentityDocumentFormat.Instance;
}

/// <summary>Shared no-op identity format (imposes no constraint) used by the interface default.</summary>
public sealed class NoIdentityDocumentFormat : IIdentityDocumentFormat
{
    public static readonly NoIdentityDocumentFormat Instance = new();
    public (string? Pattern, string? Hint) GetFormat(string fieldKey) => (null, null);
}
