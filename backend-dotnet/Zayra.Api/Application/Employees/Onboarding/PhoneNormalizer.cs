using System.Text;

namespace Zayra.Api.Application.Employees.Onboarding;

public enum PhoneKind { Mobile, Landline }

/// <summary>
/// Normalises a typed phone number to E.164 or returns null when it cannot be one. Saudi rules: a mobile is
/// +966 5 and eight digits (local 05XXXXXXXX), a landline is +966 and a 1-9 area digit plus seven digits.
/// Outside Saudi Arabia the caller must give an international prefix. The server never depends on a phone
/// library; the client's richer parsing only has to agree with this floor.
/// </summary>
public static class PhoneNormalizer
{
    public static string? Normalize(string? input, string? companyCountry, PhoneKind kind)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var sb = new StringBuilder();
        foreach (var ch in input.Trim())
        {
            if (ch >= '\u0660' && ch <= '\u0669') sb.Append((char)('0' + (ch - '\u0660')));
            else if (ch >= '\u06F0' && ch <= '\u06F9') sb.Append((char)('0' + (ch - '\u06F0')));
            else if (char.IsDigit(ch) && ch <= '9') sb.Append(ch);
            else if (ch == '+' && sb.Length == 0) sb.Append('+');
            else if (ch is ' ' or '-' or '(' or ')' or '.' or '\u00a0') continue;
            else return null;
        }
        var s = sb.ToString();
        var saudiCompany = string.Equals(CountryNormal(companyCountry), "SA", StringComparison.Ordinal);

        string e164;
        if (s.StartsWith('+')) e164 = s;
        else if (s.StartsWith("00")) e164 = "+" + s[2..];
        else if (saudiCompany && s.StartsWith("966")) e164 = "+" + s;
        else if (saudiCompany && s.StartsWith('0')) e164 = "+966" + s[1..];
        else if (saudiCompany && s.StartsWith('5') && s.Length == 9) e164 = "+966" + s;
        else return null;

        // E.164: + then 8-15 digits, no leading zero country code.
        if (e164.Length < 9 || e164.Length > 16 || e164[1] == '0' || !e164.Skip(1).All(char.IsDigit)) return null;

        if (e164.StartsWith("+966", StringComparison.Ordinal))
        {
            var national = e164[4..];
            if (kind == PhoneKind.Mobile) return national.Length == 9 && national[0] == '5' ? e164 : null;
            return national.Length == 9 && national[0] is >= '1' and <= '9' && national[0] != '5' ? e164 : null;
        }
        return e164;
    }

    private static string CountryNormal(string? c) => CountryCodeStandard_Normalize(c);

    private static string CountryCodeStandard_Normalize(string? c) =>
        Zayra.Api.Application.Common.CountryCodeStandard.NormalizeToIso2(c) ?? (c ?? string.Empty).Trim().ToUpperInvariant();
}
