using Zayra.Api.Application.CountryPack;

namespace Zayra.Api.Infrastructure.CountryPack;

// Safe no-op implementations — returned by the resolver when no country-specific
// pack matches.  Each real pack replaces exactly one of these per (country, jurisdiction).

public sealed class DefaultStatutoryDeductionCalculator : IStatutoryDeductionCalculator
{
    public Task<StatutoryDeductionResult> CalculateAsync(StatutoryDeductionInput input, CancellationToken ct = default)
        => Task.FromResult(new StatutoryDeductionResult(0m, 0m, Array.Empty<StatutoryDeductionLine>()));
}

public sealed class DefaultEndOfServiceCalculator : IEndOfServiceCalculator
{
    /// <summary>
    /// S1 — a zero here is NOT an answer, it is the absence of one, and a settlement screen showing
    /// "KWD 0.00 indemnity" is worse than one that refuses. Kuwait Law 6/2010 Art. 51 alone is 15 days
    /// per year rising to a month after five, on the TOTAL wage, with its own resignation scale; Oman
    /// and Bahrain have both moved expatriate end-of-service to funded monthly schemes. None of that is
    /// modelled. The WPS path already refuses rather than emitting an empty file
    /// (<c>PayrollController</c>'s <c>no_wps_pack_configured</c> guard); this is the missing half of
    /// that asymmetry, so callers can make the same refusal.
    /// </summary>
    public const string NoPackNotice =
        "[NO-PACK] No end-of-service pack is configured for this company's country/jurisdiction, so the " +
        "gratuity is reported as ZERO — that is the absence of an answer, not an answer. Do not settle a " +
        "leaver on this figure. End-of-service in Kuwait, Oman and Bahrain is substantial and structurally " +
        "different from the KSA/UAE/Qatar packs (Bahrain and Oman have moved expatriate end-of-service to " +
        "funded monthly contribution schemes), and none of it is implemented. Compute the indemnity outside " +
        "the product and record it as an explicit other-dues line.";

    public Task<EndOfServiceResult> CalculateAsync(EndOfServiceInput input, CancellationToken ct = default)
        => Task.FromResult(new EndOfServiceResult(0m, "default-no-op", Array.Empty<EndOfServiceBreakdown>())
        {
            Notices = new[] { NoPackNotice },
            AppliedWageBase = 0m,
        });
}

public sealed class DefaultWageProtectionExporter : IWageProtectionExporter
{
    public Task<WageProtectionExportResult> ExportAsync(WageProtectionExportInput input, CancellationToken ct = default)
        => Task.FromResult(new WageProtectionExportResult(Array.Empty<byte>(), string.Empty, "none", 0));
}

public sealed class DefaultNationalizationTracker : INationalizationTracker
{
    public Task<NationalizationResult> GetStatusAsync(NationalizationInput input, CancellationToken ct = default)
        => Task.FromResult(new NationalizationResult(0d, 0d, input.TotalHeadcount, input.NationalHeadcount,
            NationalizationComplianceStatus.NotApplicable, "default"));
}

public sealed class DefaultLocalizationProfile : ILocalizationProfile
{
    public LocalizationProfile GetProfile()
        => new("USD", "$", "en", false, "yyyy-MM-dd", "Gregorian");
}

public sealed class DefaultCountryPackDescriptor : ICountryPackDescriptor
{
    public PackDescriptor GetDescriptor() => new(
        CountryCode:              "N/A",
        CountryNameEn:            "Not configured",
        CountryNameAr:            string.Empty,
        SocialInsuranceScheme:    "None",
        SocialInsuranceDescription: "No statutory deduction pack is configured for this company.",
        EosbFormula:              "No end-of-service pack configured.",
        WpsFormat:                "none",
        WpsFormatLabel:           "No WPS format configured.",
        NationalizationScheme:    "None");
}
