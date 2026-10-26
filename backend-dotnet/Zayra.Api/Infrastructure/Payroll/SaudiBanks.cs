namespace Zayra.Api.Infrastructure.Payroll;

public sealed record SaudiBank(string Code, string NameEn, string NameAr);

/// <summary>
/// CD29: Saudi bank names by the 2-digit bank code at IBAN positions 5-6. The table is the nine codes in the onboarding
/// rulebook (SAMA list); a code that is not here resolves to null, never to a guess. Codes are VERIFY items.
/// </summary>
public static class SaudiBanks
{
    public static readonly IReadOnlyList<SaudiBank> All =
    [
        new("80", "Al Rajhi Bank", "مصرف الراجحي"),
        new("10", "Saudi National Bank", "البنك الأهلي السعودي"),
        new("20", "Riyad Bank", "بنك الرياض"),
        new("45", "Saudi Awwal Bank (SABB)", "البنك السعودي الأول (ساب)"),
        new("55", "Banque Saudi Fransi", "البنك السعودي الفرنسي"),
        new("05", "Alinma Bank", "مصرف الإنماء"),
        new("15", "Bank Albilad", "بنك البلاد"),
        new("60", "Bank Aljazira", "بنك الجزيرة"),
        new("30", "Arab National Bank", "البنك العربي الوطني"),
    ];

    public static string? NameEn(string? code) => Find(code)?.NameEn;

    public static SaudiBank? Find(string? code) =>
        code is { Length: 2 } ? All.FirstOrDefault(b => b.Code == code) : null;

    /// <summary>The bank behind a Saudi IBAN, or null for a non-Saudi IBAN or an unlisted code.</summary>
    public static SaudiBank? FromIban(string? iban)
    {
        var cleaned = (iban ?? string.Empty).Replace(" ", "").ToUpperInvariant();
        return cleaned.Length == 24 && cleaned.StartsWith("SA", StringComparison.Ordinal) ? Find(cleaned.Substring(4, 2)) : null;
    }
}
