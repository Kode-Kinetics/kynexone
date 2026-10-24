using System.Text;
using System.Text.RegularExpressions;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;

namespace Zayra.Api.Infrastructure.CompanySetup;

/// <summary>
/// How the wizard reads what a person typed into a registration field (data-entry audit, 2026-10-23). An Arabic or
/// Persian keyboard types Arabic-Indic (٠١٢…) or Persian (۰۱۲…) digits, and people copy numbers with spaces or dashes
/// in them; a valid number must not be refused for that. Each number is cleaned only as far as its own format allows:
/// the CR, 700 and VAT numbers are digits only, so spaces and dashes go; the MOL establishment id may hold ONE hyphen
/// (labour office - sequence), so only spaces go.
/// </summary>
public static partial class CompanySetupDataEntry
{
    /// <summary>Arabic-Indic (U+0660–0669) and Persian/Extended (U+06F0–06F9) digits as ASCII digits; nothing else changes.</summary>
    public static string AsciiDigits(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(ch switch
            {
                >= '٠' and <= '٩' => (char)('0' + (ch - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (ch - '۰')),
                _ => ch,
            });
        return sb.ToString();
    }

    private static bool IsSpace(char ch) => char.IsWhiteSpace(ch) || ch is '‎' or '‏' or '؜';
    private static bool IsDash(char ch) => ch is '-' or '‐' or '‑' or '‒' or '–' or '—' or '−' or 'ـ';

    /// <summary>A digits-only number (CR, 700, VAT): ASCII digits, spaces and dashes removed.</summary>
    public static string DigitsOnlyNumber(string? value) =>
        new(AsciiDigits(value).Where(ch => !IsSpace(ch) && !IsDash(ch)).ToArray());

    /// <summary>The MOL establishment id: ASCII digits, spaces removed, any dash written as the one ASCII hyphen its format allows.</summary>
    public static string EstablishmentId(string? value) =>
        new(AsciiDigits(value).Where(ch => !IsSpace(ch)).Select(ch => IsDash(ch) ? '-' : ch).ToArray());

    [GeneratedRegex(@"^3\d{13}3$")]
    private static partial Regex SaudiVatPattern();

    /// <summary>A Saudi VAT registration number (ZATCA TIN): 15 digits, starting and ending with 3.</summary>
    public static bool IsSaudiVatNumber(string value) => SaudiVatPattern().IsMatch(value);

    public const string VatInvalidCode = "vat_number_invalid";
    public const string VatInvalidMessage = "A Saudi VAT number is 15 digits that start and end with 3 (for example 300000000000003).";
    public const string VatInvalidMessageAr = "الرقم الضريبي السعودي 15 رقماً يبدأ بالرقم 3 وينتهي به (مثال: 300000000000003).";

    public const string CountryInvalidMessage = "Choose the company's country from the list.";
    public const string CountryInvalidMessageAr = "اختر دولة الشركة من القائمة.";

    public const string CoordinatesMissingCode = "location_coordinates_missing";

    /// <summary>0,0 is a point in the Gulf of Guinea, never a real work site: a form left at zero, so the location has no
    /// geofence yet. Treated as missing, never written as a geofence.</summary>
    public static bool IsZeroPoint(decimal? latitude, decimal? longitude) => latitude == 0m && longitude == 0m;

    /// <summary>The coordinates as the wizard keeps them: (0,0) is "none".</summary>
    public static (decimal? Latitude, decimal? Longitude) Coordinates(decimal? latitude, decimal? longitude) =>
        IsZeroPoint(latitude, longitude) ? (null, null) : (latitude, longitude);

    /// <summary>Arabic text for the MOL establishment id and 700 number problems the wage-file rules raise in English.</summary>
    public static string? ArabicFor(string code) => code switch
    {
        KsaWageFileRules.Codes.EstablishmentMissing => "أدخل رقم المنشأة لدى وزارة الموارد البشرية (يظهر في قوى). لا يُستخدم رقم افتراضي.",
        KsaWageFileRules.Codes.EstablishmentInvalid => "رقم المنشأة لدى وزارة الموارد البشرية من 2 إلى 15 خانة: رقم مكتب العمل والرقم التسلسلي، أرقام فقط، ويمكن الفصل بينهما بشرطة واحدة (مثال: 7-1234567).",
        KsaWageFileRules.Codes.EstablishmentPlaceholder => "رقم المنشأة كله أصفار، وهو رقم افتراضي وليس منشأة حقيقية. أدخل الرقم الظاهر في قوى.",
        KsaWageFileRules.Codes.NationalUnifiedNoMissing => "الرقم الوطني الموحد للمنشأة (10 أرقام، يظهر في قوى) مطلوب.",
        KsaWageFileRules.Codes.NationalUnifiedNoInvalid => "الرقم الوطني الموحد 10 أرقام بالضبط، كما يظهر في قوى.",
        _ => null,
    };
}
