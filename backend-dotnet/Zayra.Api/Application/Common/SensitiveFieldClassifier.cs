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

    [GeneratedRegex(@"\b[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}\b", RegexOptions.CultureInvariant)]
    private static partial Regex IbanShape();

    [GeneratedRegex(@"(?<![0-9])[12][0-9]{9}(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex SaudiIdShape();

    private static bool IsIbanCandidate(string token) =>
        token.StartsWith("SA", StringComparison.Ordinal) && token.Length == 24
        || Zayra.Api.Infrastructure.Payroll.IbanValidator.IsValid(token);

    /// <summary>True when the whole value (spaces ignored) is an IBAN or a Saudi ID / iqama number.</summary>
    public static bool LooksSensitive(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var compact = value.Replace(" ", string.Empty).Trim().ToUpperInvariant();
        return IbanShape().IsMatch(compact) && IbanShape().Match(compact).Length == compact.Length && IsIbanCandidate(compact)
               || SaudiIdShape().IsMatch(compact) && compact.Length == 10;
    }

    /// <summary>
    /// Masks every IBAN- or Saudi-ID-shaped token INSIDE free text ("changed from SA03…7519" → "changed from ***7519"),
    /// leaving everything else as it was.
    /// </summary>
    public static string MaskEmbedded(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var masked = IbanShape().Replace(text, m => IsIbanCandidate(m.Value) ? SensitiveValueMask.MaskId(m.Value) : m.Value);
        return SaudiIdShape().Replace(masked, m => SensitiveValueMask.MaskId(m.Value));
    }
}
