using FluentAssertions;
using Zayra.Api.Infrastructure.Finance;

namespace Zayra.Api.Tests;

/// <summary>WP-C1: per-currency scale and round-once-to-minor-unit (contract 2.1, 2.6; T-013, T-032).</summary>
public class CurrencyMinorUnitsTests
{
    [Theory]
    [InlineData("SAR", 2)] [InlineData("AED", 2)] [InlineData("QAR", 2)] [InlineData("USD", 2)]
    [InlineData("KWD", 3)] [InlineData("BHD", 3)] [InlineData("OMR", 3)]
    [InlineData("kwd", 3)] [InlineData(" sar ", 2)]
    [InlineData("XYZ", 2)] [InlineData("", 2)] [InlineData(null, 2)]
    public void Scale_per_currency_unknown_defaults_to_two(string? currency, int expected)
        => CurrencyMinorUnits.Scale(currency).Should().Be(expected); // T-032

    [Theory]
    [InlineData("1.005", "SAR", "1.01")]   // away from zero, not banker's
    [InlineData("2.5", "SAR", "2.50")]
    [InlineData("-1.005", "SAR", "-1.01")]
    [InlineData("1.0005", "KWD", "1.001")]
    [InlineData("-1.0005", "KWD", "-1.001")]
    [InlineData("1.0004", "KWD", "1.000")]
    public void Round_is_away_from_zero_at_currency_scale(string input, string cur, string expected)
        => CurrencyMinorUnits.Round(decimal.Parse(input), cur).Should().Be(decimal.Parse(expected)); // T-013/T-032

    [Theory]
    [InlineData("100.01", "SAR", 10001L)]
    [InlineData("100.001", "KWD", 100001L)]
    [InlineData("0.005", "SAR", 1L)]
    [InlineData("-0.005", "SAR", -1L)]
    [InlineData("0", "KWD", 0L)]
    public void ToMinor_rounds_once_then_scales(string input, string cur, long expected)
        => CurrencyMinorUnits.ToMinor(decimal.Parse(input), cur).Should().Be(expected);

    [Theory]
    [InlineData(10001L, "SAR", "100.01")]
    [InlineData(100001L, "KWD", "100.001")]
    [InlineData(-5L, "OMR", "-0.005")]
    public void FromMinor_is_exact(long minor, string cur, string expected)
        => CurrencyMinorUnits.FromMinor(minor, cur).Should().Be(decimal.Parse(expected));

    [Fact]
    public void ToMinor_FromMinor_round_trips_for_seeded_amounts()
    {
        var rng = new Random(20261025);
        foreach (var cur in new[] { "SAR", "KWD", "XYZ" })
            for (var i = 0; i < 1000; i++)
            {
                var minor = rng.NextInt64(-5_000_000_000, 5_000_000_000);
                CurrencyMinorUnits.ToMinor(CurrencyMinorUnits.FromMinor(minor, cur), cur).Should().Be(minor);
            }
    }
}
