using System.Text;
using System.Text.RegularExpressions;

namespace Zayra.Api.Application.Common;

/// <summary>
/// Decides whether a field or a value is an identity / banking number (or pay) that must never be stored or
/// shown in full outside its own column. Two independent tests, because data that arrives from outside this
/// model (a legacy migration package, a third-party response) uses names nobody here chose:
/// <list type="bullet">
/// <item><b>By name</b>, after normalising ("IBAN Number", "Iqama No", "National ID", "Bank Account No" →
/// "ibannumber", "iqamano", "nationalid", "bankaccountno").</item>
/// <item><b>By value shape</b>: a Saudi IBAN (SA + 22), any IBAN that passes ISO 13616 mod-97, or a 10-digit
/// Saudi national ID / iqama number (starting 1 or 2).</item>
/// </list>
/// Masking keeps the last 4 characters (<see cref="SensitiveValueMask.MaskId"/>), so a reviewer can still
/// correlate a value with its source document.
/// </summary>
public static partial class SensitiveFieldClassifier
{
    // Substrings of a normalised field name that mark an identity or banking number.
    private static readonly string[] IdentifierNameParts =
    [
        "iban", "iqama", "nationalid", "passport", "accountno", "accountnumber", "bankaccount", "muqeem",
        "borderno", "bordernumber", "idnumber", "idno", "civilid", "emiratesid", "residencyno", "residencynumber",
        "visano", "visanumber", "visafile", "workpermitno", "workpermitnumber", "laborcard", "labourcard",
        "gosiref", "gosino", "gosinumber", "molid", "wpsbank", "qiwaemployeeref", "qiwacontract",
    ];

    // Whole normalised names that are identifiers but too short (or too generic) to match as substrings.
    private static readonly HashSet<string> IdentifierNames = new(StringComparer.Ordinal)
    {
        "qid", "nid", "ssn", "nin", "nationalidentity", "identitynumber", "identityno",
    };

    // A name about an identifier rather than the identifier itself ("Passport Expiry Date", "ID Type").
    private static readonly string[] MetadataNameParts = ["date", "expiry", "expires", "issued", "type", "status", "verified"];

    /// <summary>Lower-case, letters and digits only: "Bank Account No." → "bankaccountno".</summary>
    public static string Normalise(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>True when the field name denotes an identity or banking number.</summary>
    public static bool IsIdentifierName(string? name)
    {
        var n = Normalise(name);
        if (n.Length == 0) return false;
        if (IdentifierNames.Contains(n)) return true;
        if (MetadataNameParts.Any(m => n.Contains(m, StringComparison.Ordinal))) return false;
        return IdentifierNameParts.Any(p => n.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>True when the field name denotes pay ("Salary", "Basic Salary", "Gross Salary", …).</summary>
    public static bool IsSalaryName(string? name) => Normalise(name).Contains("salary", StringComparison.Ordinal);

    // An IBAN written as one token, in any case: "SA0380000000608010167519", "sa03800000…".
    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]{2}[0-9]{2}[A-Za-z0-9]{11,30}(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex IbanToken();

    // An IBAN written in space-separated groups of 4: "SA03 8000 0000 6080 1016 7519". The match may run into the
    // next word ("… 7519 to"), so the evaluator trims trailing groups until what is left is an IBAN.
    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]{2}[0-9]{2}(?: [A-Za-z0-9]{1,4}){3,8}(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex IbanGrouped();

    [GeneratedRegex(@"(?<![0-9])[0-9]{13,24}(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex AccountNumberRun();

    [GeneratedRegex(@"(?<![0-9])[12][0-9]{9}(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex SaudiIdShape();

    // A passport number only where the text says it is one: "passport P9876543", "Passport No: A12345678",
    // "جواز السفر X1234567", or the number followed by the keyword. A letter plus 7–9 letters/digits, with a digit.
    [GeneratedRegex(@"(?<kw>passport|جواز(?: السفر)?)(?<gap>[^A-Za-z0-9]{0,4}(?:(?:no|number|num|nbr)\.?)?[^A-Za-z0-9]{0,4})(?<tok>[A-Za-z][A-Za-z0-9]{7,9})(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PassportAfterKeyword();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<tok>[A-Za-z][A-Za-z0-9]{7,9})(?<gap>[^A-Za-z0-9]{1,4})(?<kw>passport|جواز)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PassportBeforeKeyword();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private static bool IsIbanCandidate(string token)
    {
        var t = token.ToUpperInvariant();
        return t.StartsWith("SA", StringComparison.Ordinal) && t.Length == 24 && t.Skip(2).Take(2).All(char.IsDigit)
               || Zayra.Api.Infrastructure.Payroll.IbanValidator.IsValid(t);
    }

    /// <summary>
    /// Arabic-Indic (٠-٩) and Eastern Arabic (۰-۹) digits as Latin, and every whitespace run as one space, so the
    /// shape rules see "٢٠٩٨٧٦٥٤٣٢" and "SA03  8000 0000" as what they are.
    /// </summary>
    public static string NormaliseForShape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            sb.Append(ch switch
            {
                >= '٠' and <= '٩' => (char)('0' + (ch - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (ch - '۰')),
                _ => ch,
            });
        return Whitespace().Replace(sb.ToString(), " ");
    }

    /// <summary>True when the whole value is an IBAN, a 13–24 digit account number or a Saudi ID / iqama number.</summary>
    public static bool LooksSensitive(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var compact = NormaliseForShape(value).Replace(" ", string.Empty);
        return IbanToken().Match(compact) is { Success: true } m && m.Length == compact.Length && IsIbanCandidate(compact)
               || AccountNumberRun().Match(compact) is { Success: true } a && a.Length == compact.Length
               || SaudiIdShape().Match(compact) is { Success: true } id && id.Length == compact.Length;
    }

    /// <summary>
    /// Masks each IBAN (any case, joined or in groups of 4), 13–24 digit account number, Saudi ID / iqama number
    /// (Latin or Arabic digits) and keyword-anchored passport number INSIDE free text, keeping the last 4 of each:
    /// "moved from sa03 8000 0000 6080 1016 7519 to cash" → "moved from ***7519 to cash". Only the match is masked.
    /// Text with nothing to mask is returned exactly as given (no digit or whitespace normalisation).
    /// </summary>
    public static string MaskEmbedded(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var normalised = NormaliseForShape(text);
        var changed = false;

        string Mask(string value) { changed = true; return SensitiveValueMask.MaskId(value.Replace(" ", string.Empty)); }

        var masked = IbanGrouped().Replace(normalised, m =>
        {
            var groups = m.Value.Split(' ');
            for (var take = groups.Length; take >= 4; take--)
            {
                var candidate = string.Concat(groups.Take(take));
                if (IsIbanCandidate(candidate))
                    return Mask(candidate) + (take < groups.Length ? " " + string.Join(' ', groups.Skip(take)) : string.Empty);
            }
            return m.Value;
        });
        masked = IbanToken().Replace(masked, m => IsIbanCandidate(m.Value) ? Mask(m.Value) : m.Value);
        masked = AccountNumberRun().Replace(masked, m => Mask(m.Value));
        masked = SaudiIdShape().Replace(masked, m => Mask(m.Value));
        masked = PassportAfterKeyword().Replace(masked, m => m.Groups["tok"].Value.Any(char.IsDigit)
            ? m.Groups["kw"].Value + m.Groups["gap"].Value + Mask(m.Groups["tok"].Value) : m.Value);
        masked = PassportBeforeKeyword().Replace(masked, m => m.Groups["tok"].Value.Any(char.IsDigit)
            ? Mask(m.Groups["tok"].Value) + m.Groups["gap"].Value + m.Groups["kw"].Value : m.Value);
        return changed ? masked : text;
    }
}
