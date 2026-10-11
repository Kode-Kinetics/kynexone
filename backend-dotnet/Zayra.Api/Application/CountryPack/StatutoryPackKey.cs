using Zayra.Api.Application.Common;

namespace Zayra.Api.Application.CountryPack;

/// <summary>
/// The (country, jurisdiction) key statutory rules are stored under, from what a company holds.
///
/// <para>Companies store the canonical ISO-2 code ("SA") and usually no jurisdiction; the seeded
/// statutory rules, the overrides written against them and the country packs are keyed by ISO-3
/// plus a mainland jurisdiction ("SAU" / "KSA-mainland"). Reading rules with the company's raw
/// values found nothing, so payroll quietly used its hard-coded fallbacks and the Statutory Rates
/// tab showed "no rules" for every Saudi company. Every statutory read and override write goes
/// through this one mapping instead.</para>
/// </summary>
public static class StatutoryPackKey
{
    /// <summary>The jurisdiction a company with none set is on: the country's mainland labour law.</summary>
    public static string? MainlandFor(string iso3) => iso3.ToUpperInvariant() switch
    {
        CountryCodes.Saudi => Jurisdictions.KsaMainland,
        CountryCodes.UAE => Jurisdictions.UAEMainland,
        CountryCodes.Qatar => Jurisdictions.QatarMainland,
        _ => null,
    };

    /// <summary>
    /// ISO-2 or ISO-3 → ISO-3; a blank jurisdiction → the country's mainland one. Unknown codes and
    /// explicit jurisdictions (e.g. "UAE-DIFC") pass through unchanged.
    /// </summary>
    public static (string CountryCode, string Jurisdiction) Normalize(string? countryCode, string? jurisdiction)
    {
        var raw = (countryCode ?? string.Empty).Trim().ToUpperInvariant();
        var cc = CountryCodeStandard.ToIso3(raw) ?? raw;
        var jur = (jurisdiction ?? string.Empty).Trim();
        if (cc.Length > 0 && MainlandFor(cc) is { } mainland && IsMainlandAlias(raw, cc, jur)) jur = mainland;
        return (cc, jur);
    }

    /// <summary>
    /// Jurisdiction spellings that mean "the country's mainland labour law" rather than a distinct legal
    /// zone. Real data holds all of them: blank (company form), the bare country code ("SA"/"SAU", e2e
    /// provisioning), "KSA", and "&lt;cc&gt;-default" (the setup assistant writes "SA-default"). Left
    /// unmapped they match no seeded rule and payroll silently falls back to hard-coded constants.
    /// Explicit zones such as "UAE-DIFC" or "UAE-ADGM" are distinct law and pass through unchanged.
    /// </summary>
    private static bool IsMainlandAlias(string rawCountry, string iso3, string jur)
    {
        if (jur.Length == 0) return true;
        var j = jur.ToUpperInvariant();
        if (j == rawCountry || j == iso3 || j.EndsWith("-DEFAULT", StringComparison.Ordinal) || j.EndsWith("-MAINLAND", StringComparison.Ordinal))
            return true;
        var iso2 = CountryCodeStandard.NormalizeToIso2(iso3);
        if (iso2 is not null && (j == iso2 || j == iso2 + "-DEFAULT")) return true;
        return iso3 switch
        {
            CountryCodes.Saudi => j is "KSA" or "SAUDI" or "SAUDI ARABIA",
            CountryCodes.UAE => j is "UAE" or "EMIRATES",
            _ => false,
        };
    }
}
