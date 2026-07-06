namespace Zayra.Api.Application.Common;

/// <summary>
/// Standards-based reference data: ISO 3166-1 alpha-2 countries and ISO 4217 currencies.
/// Served read-only so entity "country code" / "currency" fields can validate against a
/// canonical list instead of accepting free text (e.g. "KSA" → the ISO code is "SA").
/// Regionally complete for the GCC/MENA market this product serves, plus major economies.
/// </summary>
public static class IsoReference
{
    public record Country(string Code, string Name, string Currency);
    public record Currency(string Code, string Name, string Symbol);

    public static readonly IReadOnlyList<Country> Countries = new[]
    {
        // GCC
        new Country("SA", "Saudi Arabia", "SAR"),
        new Country("AE", "United Arab Emirates", "AED"),
        new Country("KW", "Kuwait", "KWD"),
        new Country("QA", "Qatar", "QAR"),
        new Country("BH", "Bahrain", "BHD"),
        new Country("OM", "Oman", "OMR"),
        // Wider MENA
        new Country("EG", "Egypt", "EGP"),
        new Country("JO", "Jordan", "JOD"),
        new Country("LB", "Lebanon", "LBP"),
        new Country("IQ", "Iraq", "IQD"),
        new Country("YE", "Yemen", "YER"),
        new Country("SY", "Syria", "SYP"),
        new Country("PS", "Palestine", "ILS"),
        new Country("MA", "Morocco", "MAD"),
        new Country("DZ", "Algeria", "DZD"),
        new Country("TN", "Tunisia", "TND"),
        new Country("LY", "Libya", "LYD"),
        new Country("SD", "Sudan", "SDG"),
        new Country("TR", "Türkiye", "TRY"),
        // South Asia (large expat workforce)
        new Country("IN", "India", "INR"),
        new Country("PK", "Pakistan", "PKR"),
        new Country("BD", "Bangladesh", "BDT"),
        new Country("LK", "Sri Lanka", "LKR"),
        new Country("NP", "Nepal", "NPR"),
        new Country("PH", "Philippines", "PHP"),
        new Country("ID", "Indonesia", "IDR"),
        // Major economies
        new Country("US", "United States", "USD"),
        new Country("GB", "United Kingdom", "GBP"),
        new Country("CA", "Canada", "CAD"),
        new Country("AU", "Australia", "AUD"),
        new Country("DE", "Germany", "EUR"),
        new Country("FR", "France", "EUR"),
        new Country("IT", "Italy", "EUR"),
        new Country("ES", "Spain", "EUR"),
        new Country("NL", "Netherlands", "EUR"),
        new Country("CH", "Switzerland", "CHF"),
        new Country("CN", "China", "CNY"),
        new Country("JP", "Japan", "JPY"),
        new Country("SG", "Singapore", "SGD"),
        new Country("MY", "Malaysia", "MYR"),
        new Country("ZA", "South Africa", "ZAR"),
        new Country("NG", "Nigeria", "NGN"),
        new Country("KE", "Kenya", "KES"),
        new Country("ET", "Ethiopia", "ETB"),
    };

    public static readonly IReadOnlyList<Currency> Currencies = new[]
    {
        new Currency("SAR", "Saudi Riyal", "ر.س"),
        new Currency("AED", "UAE Dirham", "د.إ"),
        new Currency("KWD", "Kuwaiti Dinar", "د.ك"),
        new Currency("QAR", "Qatari Riyal", "ر.ق"),
        new Currency("BHD", "Bahraini Dinar", "ب.د"),
        new Currency("OMR", "Omani Rial", "ر.ع."),
        new Currency("EGP", "Egyptian Pound", "ج.م"),
        new Currency("JOD", "Jordanian Dinar", "د.ا"),
        new Currency("USD", "US Dollar", "$"),
        new Currency("EUR", "Euro", "€"),
        new Currency("GBP", "Pound Sterling", "£"),
        new Currency("INR", "Indian Rupee", "₹"),
        new Currency("PKR", "Pakistani Rupee", "₨"),
        new Currency("PHP", "Philippine Peso", "₱"),
        new Currency("CNY", "Chinese Yuan", "¥"),
        new Currency("JPY", "Japanese Yen", "¥"),
    };

    private static readonly HashSet<string> CountryCodeSet =
        Countries.Select(c => c.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsValidCountryCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && CountryCodeSet.Contains(code.Trim());
}
