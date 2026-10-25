namespace Zayra.Api.Infrastructure.Finance;

/// <summary>Currency minor-unit scale and rounding (contract 2.6).</summary>
public static class CurrencyMinorUnits
{
    public static int Scale(string? currency) => 0;

    public static decimal Round(decimal amount, string? currency) => amount;

    public static long ToMinor(decimal amount, string? currency) => 0;

    public static decimal FromMinor(long minor, string? currency) => 0m;
}
