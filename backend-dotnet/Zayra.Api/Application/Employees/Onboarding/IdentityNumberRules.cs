using System.Text;

namespace Zayra.Api.Application.Employees.Onboarding;

/// <summary>Compliance-record field keys the KSA identity rules apply to.</summary>
public static class IdentityKinds
{
    public const string IdNumber = "id_number";
    public const string IqamaNumber = "iqama_number";
    public const string BorderNumber = "border_number";

    public static bool IsKsaIdentity(string? fieldKey) =>
        fieldKey is not null && (fieldKey.Equals(IdNumber, StringComparison.OrdinalIgnoreCase)
            || fieldKey.Equals(IqamaNumber, StringComparison.OrdinalIgnoreCase)
            || fieldKey.Equals(BorderNumber, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Outcome of checking one identity number. <see cref="Normalised"/> is ASCII digits, trimmed.</summary>
public sealed record IdentityCheckResult(string Normalised, IReadOnlyList<string> HardErrors, IReadOnlyList<string> Warnings);

/// <summary>
/// Format, check digit and nationality agreement for KSA identity numbers. Format and check digit are hard
/// (the checksum alone is waivable with a reason by an authorised caller, see the guard); a class that
/// disagrees with the nationality is hard for a Saudi holding an Iqama-prefixed number and a warning for
/// the reverse. The Border Number checksum is advisory, so it never produces an error here.
/// </summary>
public static class IdentityNumberRules
{
    public static string NormalizeDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (ch >= '\u0660' && ch <= '\u0669') sb.Append((char)('0' + (ch - '\u0660')));
            else if (ch >= '\u06F0' && ch <= '\u06F9') sb.Append((char)('0' + (ch - '\u06F0')));
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    public static IdentityCheckResult Check(string? raw, string kind, NationalityClass? nationalityClass)
    {
        var value = NormalizeDigits(raw);
        var hard = new List<string>();
        var warnings = new List<string>();
        if (value.Length == 0) return new IdentityCheckResult(value, hard, warnings);

        var lead = kind switch
        {
            IdentityKinds.IdNumber => '1',
            IdentityKinds.IqamaNumber => '2',
            IdentityKinds.BorderNumber => '3',
            _ => '\0',
        };
        var tenDigits = value.Length == 10 && value.All(c => c is >= '0' and <= '9');
        if (!tenDigits || (lead != '\0' && value[0] != lead))
        {
            hard.Add(EmployeeFieldErrors.IdFormatInvalid);
            // A Saudi holding an Iqama-prefixed number is also a class disagreement, whichever field it was typed into.
            if (tenDigits && nationalityClass == NationalityClass.Saudi && value[0] == '2') hard.Add(EmployeeFieldErrors.IdPrefixNationalityMismatch);
            return new IdentityCheckResult(value, hard, warnings);
        }

        if (kind != IdentityKinds.BorderNumber && !SaudiIdChecksum.IsValid(value))
            hard.Add(EmployeeFieldErrors.IdChecksumInvalid);

        if (nationalityClass == NationalityClass.Saudi && value[0] == '2')
            hard.Add(EmployeeFieldErrors.IdPrefixNationalityMismatch);
        else if (nationalityClass is NationalityClass.Gcc or NationalityClass.Expat && value[0] == '1')
            warnings.Add(EmployeeWarningCodes.IdPrefixNationalityMismatch);

        return new IdentityCheckResult(value, hard, warnings);
    }
}
