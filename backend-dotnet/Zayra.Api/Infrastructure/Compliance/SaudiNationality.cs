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
        // Free-text form HR users commonly type ("Saudi National"); owner-approved alias.
        "Saudi National", "Saudi Citizen", "KSA National",
        // Arabic free-text forms: "Saudi by nationality" (m./f.), ta marbuta typed as ha, and the definite article.
        "سعودي الجنسية", "سعودية الجنسية", "سعوديه", "السعودي",
        // Arabic: Saudi (m.), the same with a dotless final ya (سعودى, common in Egyptian-style input),
        // Saudi (f.), Saudi Arabia, and the Kingdom of Saudi Arabia.
        "سعودي", "سعودى", "سعودية", "السعودية", "المملكة العربية السعودية",
        // Kept from the GCC readiness floor's own list, so no caller loses a spelling it accepted.
        "SaudiArabia",
    };

    /// <summary>
    /// Is this PERSON Saudi? The ISO code HR confirmed is the record: when it is set and confirmed it decides, whatever the free text
    /// says ("Suadi" with a confirmed SA is Saudi; "Saudi" with a confirmed EG is not). A code that is only proposed, or no code, leaves
    /// the free-text spelling to decide, exactly as before.
    /// </summary>
    public static bool IsSaudi(string? nationality, string? nationalityCode, string? mappingState) =>
        IsSaudi(EmployeeNationality.Effective(nationality, nationalityCode, mappingState));

    /// <summary>True when <paramref name="nationality"/> is a recognised spelling of Saudi.</summary>
    public static bool IsSaudi(string? nationality) =>
        !string.IsNullOrWhiteSpace(nationality) && Spellings.Contains(Fold(nationality));

    /// <summary>
    /// Trim, collapse runs of whitespace to one space, and drop Arabic tatweel and diacritics so that
    /// typing variants of an accepted spelling match. Still an EXACT match on the folded text.
    /// </summary>
    private static string Fold(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value.Trim())
        {
            if (ch == '\u0640' || (ch >= '\u064B' && ch <= '\u065F') || ch == '\u0670') continue; // tatweel, tashkeel
            if (char.IsWhiteSpace(ch)) { pendingSpace = true; continue; }
            if (pendingSpace && sb.Length > 0) sb.Append(' ');
            pendingSpace = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }
}

/// <summary>
/// The nationality value every classifier reads for a person on file. When HR has CONFIRMED an ISO code it is the record, and it is passed
/// on as the code itself ("SA", "KW", "IN"), which the Saudi, GCC and GOSI matchers all already understand; otherwise (no code, or only a
/// proposed one) it is the free text as typed, so nobody is reclassified until HR confirms.
/// </summary>
public static class EmployeeNationality
{
    public static string? Effective(string? nationality, string? nationalityCode, string? mappingState) =>
        string.Equals(mappingState, Zayra.Api.Models.NationalityMappingStates.Confirmed, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(nationalityCode)
            ? nationalityCode.Trim().ToUpperInvariant()
            : nationality;

    public static string? Effective(Zayra.Api.Models.Employee? employee) =>
        employee is null ? null : Effective(employee.Nationality, employee.NationalityCode, employee.NationalityMappingState);
}
