namespace Zayra.Api.Application.Common;

/// <summary>
/// Canonical country-code policy (Phase 1B decision).
///
/// CANONICAL INTERNAL FORMAT: ISO 3166-1 alpha-2 ("SA", "AE", "IN", "GB") — this is what
/// IsoReference serves, what the frontend selectors submit, and what the overwhelming
/// majority of stored data already uses. All entity CountryCode columns and all new
/// governance tables (CompanyTaxPolicy, CompanyComplianceProfile) validate as ISO-2.
///
/// The CountryPack framework is DI-keyed by ISO 3166-1 alpha-3 ("SAU", "ARE", "QAT") and
/// is left untouched (its keys are compile-time constants across pack registrations).
/// This class is the EXPLICIT MAPPING LAYER between the two: resolution accepts ISO-2
/// and maps to the pack's ISO-3 key; nothing is silently rewritten in the database.
/// Any data conversion is a documented backfill, not an implicit coercion.
/// </summary>
public static class CountryCodeStandard
{
    // ISO-2 → ISO-3 for every country in IsoReference (kept in the same order).
    private static readonly Dictionary<string, string> Iso2ToIso3 = new(StringComparer.OrdinalIgnoreCase)
    {
        // GCC
        ["SA"] = "SAU", ["AE"] = "ARE", ["KW"] = "KWT", ["QA"] = "QAT", ["BH"] = "BHR", ["OM"] = "OMN",
        // Wider MENA
        ["EG"] = "EGY", ["JO"] = "JOR", ["LB"] = "LBN", ["IQ"] = "IRQ", ["YE"] = "YEM", ["SY"] = "SYR",
        ["PS"] = "PSE", ["MA"] = "MAR", ["DZ"] = "DZA", ["TN"] = "TUN", ["LY"] = "LBY", ["SD"] = "SDN",
        ["TR"] = "TUR",
        // South Asia / SEA
        ["IN"] = "IND", ["PK"] = "PAK", ["BD"] = "BGD", ["LK"] = "LKA", ["NP"] = "NPL", ["PH"] = "PHL",
        ["ID"] = "IDN",
        // Major economies
        ["US"] = "USA", ["GB"] = "GBR", ["CA"] = "CAN", ["AU"] = "AUS", ["DE"] = "DEU", ["FR"] = "FRA",
        ["IT"] = "ITA", ["ES"] = "ESP", ["NL"] = "NLD", ["CH"] = "CHE", ["CN"] = "CHN", ["JP"] = "JPN",
        ["SG"] = "SGP", ["MY"] = "MYS", ["ZA"] = "ZAF", ["NG"] = "NGA", ["KE"] = "KEN", ["ET"] = "ETH",
    };

    private static readonly Dictionary<string, string> Iso3ToIso2 =
        Iso2ToIso3.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Normalizes any accepted input (ISO-2 or ISO-3, any casing, surrounding whitespace)
    /// to canonical upper-case ISO-2. Returns null when the value is not a recognized code
    /// — callers decide whether that is a validation failure or a legacy passthrough.
    /// </summary>
    public static string? NormalizeToIso2(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().ToUpperInvariant();
        if (v.Length == 2 && Iso2ToIso3.ContainsKey(v)) return v;
        if (v.Length == 3 && Iso3ToIso2.TryGetValue(v, out var iso2)) return iso2.ToUpperInvariant();
        return null;
    }

    /// <summary>Maps canonical ISO-2 (or already-ISO-3) input to the CountryPack ISO-3 key; null if unknown.</summary>
    public static string? ToIso3(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().ToUpperInvariant();
        if (v.Length == 3 && Iso3ToIso2.ContainsKey(v)) return v;
        if (v.Length == 2 && Iso2ToIso3.TryGetValue(v, out var iso3)) return iso3.ToUpperInvariant();
        return null;
    }

    /// <summary>True when the value is a recognized ISO-2 or ISO-3 country code.</summary>
    public static bool IsValid(string? value) => NormalizeToIso2(value) is not null;

    /// <summary>
    /// Validation gate for canonical fields: empty is allowed (not yet configured);
    /// non-empty must be a recognized code.
    /// </summary>
    public static bool IsValidOrEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) || IsValid(value);
}
