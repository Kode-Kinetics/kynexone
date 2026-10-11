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
        if (jur.Length == 0 && cc.Length > 0) jur = MainlandFor(cc) ?? string.Empty;
        return (cc, jur);
    }
}
