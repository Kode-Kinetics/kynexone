namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// ISO 13616 IBAN validator (mod-97 check).  Used to gate WPS/SIF bank-file
/// export so that no payment record with an invalid or missing IBAN is exported.
/// </summary>
public static class IbanValidator
{
    public static bool IsValid(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban)) return false;
        iban = iban.Replace(" ", "").ToUpperInvariant();
        if (iban.Length is < 15 or > 34) return false;
        if (!iban.All(c => char.IsLetterOrDigit(c))) return false;
        // ISO 13616 delegates length and BBAN structure to each country. Saudi IBANs are exactly
        // 24 characters: SA + 2 numeric check digits + 2 numeric bank-code digits + an 18-character
        // account body. A shorter SA value can still be made to satisfy mod-97, so checksum alone is
        // not sufficient and previously allowed 22-character demo accounts into WPS readiness.
        if (iban.StartsWith("SA", StringComparison.Ordinal) && !HasSaudiStructure(iban)) return false;

        // Move the first 4 chars to the end, map letters → numbers, then mod-97 must equal 1.
        var rearranged = iban[4..] + iban[..4];
        var numeric = string.Concat(rearranged.Select(c => char.IsLetter(c) ? (c - 'A' + 10).ToString() : c.ToString()));

        var remainder = 0;
        foreach (var ch in numeric)
            remainder = (remainder * 10 + (ch - '0')) % 97;

        return remainder == 1;
    }

    /// <summary>True only for a 24-character Saudi IBAN with valid country structure and mod-97.</summary>
    public static bool IsSaudiIban(string? iban)
    {
        var cleaned = (iban ?? string.Empty).Replace(" ", "").ToUpperInvariant();
        return HasSaudiStructure(cleaned) && IsValid(cleaned);
    }

    private static bool HasSaudiStructure(string iban) =>
        iban.Length == 24
        && iban.StartsWith("SA", StringComparison.Ordinal)
        && iban.Skip(2).Take(4).All(char.IsDigit)
        && iban.Skip(6).All(char.IsLetterOrDigit);

    /// <summary>
    /// Returns the given IBAN with its two check digits recomputed so it satisfies the ISO 13616
    /// mod-97 checksum, preserving the country code and BBAN (account body). Used to keep seeded
    /// demo IBANs valid without hand-computing check digits — pass a structurally-shaped IBAN
    /// (2 letters + 2 digits + BBAN) and any wrong check digits are replaced with correct ones.
    /// Input that is too short to carry a BBAN is returned uppercased/unspaced unchanged.
    /// </summary>
    public static string WithValidCheckDigits(string? iban)
    {
        var cleaned = (iban ?? string.Empty).Replace(" ", "").ToUpperInvariant();
        if (cleaned.Length < 5) return cleaned;
        var country = cleaned[..2];
        var bban = cleaned[4..];

        // Move country code + "00" placeholder to the end, map letters → numbers, mod-97.
        var rearranged = bban + country + "00";
        var numeric = string.Concat(rearranged.Select(c => char.IsLetter(c) ? (c - 'A' + 10).ToString() : c.ToString()));
        var remainder = 0;
        foreach (var ch in numeric)
            remainder = (remainder * 10 + (ch - '0')) % 97;

        var check = 98 - remainder;
        return $"{country}{check:D2}{bban}";
    }
}
