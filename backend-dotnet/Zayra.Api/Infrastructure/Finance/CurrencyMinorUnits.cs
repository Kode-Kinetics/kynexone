namespace Zayra.Api.Infrastructure.Finance;

/// <summary>
/// Currency minor-unit scale and rounding (cost-accounting contract 2.6). SAR/AED/QAR = 2 dp;
/// KWD/BHD/OMR = 3 dp; any other or missing currency = 2 dp. Rounding is half away from zero and is
/// applied once, at the employee x component row; everything downstream is exact integer arithmetic.
/// </summary>
public static class CurrencyMinorUnits
{
    public static int Scale(string? currency) =>
        (currency ?? "").Trim().ToUpperInvariant() switch
        {
            "KWD" or "BHD" or "OMR" => 3,
            _ => 2,
        };

    public static decimal Round(decimal amount, string? currency) =>
        Math.Round(amount, Scale(currency), MidpointRounding.AwayFromZero);

    public static long ToMinor(decimal amount, string? currency) =>
        (long)Math.Round(amount * Pow10(Scale(currency)), 0, MidpointRounding.AwayFromZero);

    public static decimal FromMinor(long minor, string? currency) =>
        new decimal(minor) / Pow10(Scale(currency));

    private static decimal Pow10(int scale) => scale == 3 ? 1000m : 100m;
}
