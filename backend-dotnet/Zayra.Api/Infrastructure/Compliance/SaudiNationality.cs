namespace Zayra.Api.Infrastructure.Compliance;

/// <summary>
/// The ONE answer to "is this recorded nationality Saudi?". GOSI classification, Saudisation counts,
/// the GCC readiness floor and the Saudi wage file all ask it, and they used to keep four drifting lists:
/// GOSI recognised neither "KSA" nor the Arabic forms and did not trim, so a Saudi recorded as "KSA" or
/// "سعودي" was computed as an expatriate and paid no GOSI.
///
/// <para>Match is EXACT after trimming, case-insensitive — never a substring: "Sudanese" and
/// "Saudi-born Egyptian" are not Saudi.</para>
/// </summary>
public static class SaudiNationality
{
    private static readonly HashSet<string> Spellings = new(StringComparer.OrdinalIgnoreCase)
    {
        // Country, demonym, ISO-2/ISO-3 and the common abbreviation.
        "KSA", "Saudi", "Saudi Arabia", "Saudi Arabian", "Kingdom of Saudi Arabia", "SA", "SAU",
        // Arabic: Saudi (m.), the same with a dotless final ya (سعودى, common in Egyptian-style input),
        // Saudi (f.), Saudi Arabia, and the Kingdom of Saudi Arabia.
        "سعودي", "سعودى", "سعودية", "السعودية", "المملكة العربية السعودية",
        // Kept from the GCC readiness floor's own list, so no caller loses a spelling it accepted.
        "SaudiArabia",
    };

    /// <summary>True when <paramref name="nationality"/> is a recognised spelling of Saudi.</summary>
    public static bool IsSaudi(string? nationality) =>
        !string.IsNullOrWhiteSpace(nationality) && Spellings.Contains(nationality.Trim());
}
