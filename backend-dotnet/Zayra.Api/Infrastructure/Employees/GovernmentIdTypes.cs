namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// The canonical values of <c>Employee.IdType</c>. The column reaches the database in more than one spelling: the hire wizard derives
/// <see cref="NationalId"/> (<see cref="EmployeeManagementService.PrimaryIdType"/>), while the profile screen and the CSV import keep the
/// displayed label ("National ID", "Iqama (residence permit)"). Every reader compares through <see cref="Canonical"/>, never against a
/// literal, so no spelling makes an ID disappear from a wage file.
/// </summary>
public static class GovernmentIdTypes
{
    public const string NationalId = "NationalId";
    public const string Iqama = "Iqama";
    public const string BorderNumber = "BorderNumber";
    public const string Passport = "Passport";

    /// <summary>
    /// The canonical type for any stored spelling, English or Arabic. The value is reduced to its letters (case-insensitive, Arabic
    /// alef / taa marbuta / alef maqsura forms folded, a leading "ال" dropped from each word) and matched by PREFIX, so "Passport No.",
    /// "Passport #", "Border No", "Iqama No" and "جواز السفر" all land on their type. A type this list does not name (another
    /// country's card, e.g. "CivilId") is returned trimmed, as stored. Null for an empty value.
    /// </summary>
    public static string? Canonical(string? idType)
    {
        if (string.IsNullOrWhiteSpace(idType)) return null;
        var key = LetterKey(idType);
        if (key == "idnumber") return NationalId;
        foreach (var (prefix, canonical) in Prefixes)
            if (key.StartsWith(prefix, StringComparison.Ordinal)) return canonical;
        return idType.Trim();
    }

    /// <summary>Letter-key prefixes, in the form <see cref="LetterKey"/> produces.</summary>
    private static readonly (string Prefix, string Canonical)[] Prefixes =
    [
        ("nationalid", NationalId), ("saudinationalid", NationalId), ("hawiyya", NationalId),
        ("هويهوطنيه", NationalId),                       // هوية وطنية / الهوية الوطنية
        ("iqama", Iqama), ("اقامه", Iqama),               // إقامة / اقامة / الإقامة
        ("border", BorderNumber), ("رقمحدود", BorderNumber), // رقم الحدود
        ("passport", Passport), ("جوازسفر", Passport),    // جواز سفر / جواز السفر
    ];

    /// <summary>The letters of each word, lower-cased, with Arabic letter forms folded and a leading definite article removed.</summary>
    private static string LetterKey(string value)
    {
        var key = new System.Text.StringBuilder(value.Length);
        foreach (var word in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var letters = new string(word.Where(char.IsLetter).Select(FoldArabic).ToArray()).ToLowerInvariant();
            if (letters.Length > 3 && letters.StartsWith("ال", StringComparison.Ordinal)) letters = letters[2..];
            key.Append(letters);
        }
        return key.ToString();
    }

    private static char FoldArabic(char c) => c switch
    {
        'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
        'ة' => 'ه',
        'ى' => 'ي',
        _ => c,
    };

    /// <summary>True when the stored type names a Saudi National ID or an Iqama, in any spelling.</summary>
    public static bool IsNationalIdOrIqama(string? idType) => Canonical(idType) is NationalId or Iqama;
}
