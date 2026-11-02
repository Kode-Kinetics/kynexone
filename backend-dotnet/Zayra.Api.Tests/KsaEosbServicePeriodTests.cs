using FluentAssertions;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// The service period counts BOTH the first and the last day served, and the Art. 85 resignation
/// bands switch at exactly 2, 5 and 10 years (two years but under five = one third; five but under
/// ten = two thirds).
/// </summary>
public sealed class KsaEosbServicePeriodTests
{
    [Theory]
    [InlineData("2024-01-01", "2025-12-31", 24, 0)]   // contract-year convention: exactly 2 years
    [InlineData("2020-01-01", "2024-12-31", 60, 0)]   // exactly 5 years across a leap year
    [InlineData("2015-01-01", "2024-12-31", 120, 0)]  // exactly 10 years
    [InlineData("2024-01-01", "2025-12-30", 23, 30)]  // the day before the anniversary
    [InlineData("2024-01-31", "2024-02-29", 1, 1)]    // month-end start, leap February end: 31 Jan + 1 month clamps to 29 Feb
    [InlineData("2023-01-31", "2023-02-28", 1, 1)]    // month-end start, non-leap February end (was a negative day count)
    [InlineData("2024-03-15", "2024-03-15", 0, 1)]    // a single day served
    // Feb-29 starter, convention (L5): the anniversary month-add clamps 29 Feb to 28 Feb, so a last day of 27 Feb is exactly 5 years.
    // The true anniversary is a day or two away; the product convention is "calendar months, a clamped day counts as reached".
    [InlineData("2020-02-29", "2025-02-27", 60, 0)]
    public void ServicePeriod_CountsBothEndDays(string start, string end, int months, int days)
    {
        var (m, d) = KsaEndOfServiceCalculator.ServicePeriod(DateOnly.Parse(start), DateOnly.Parse(end));
        (m, d).Should().Be((months, days));
    }

    private static async Task<decimal> Resign(string start, string end)
    {
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var input = new EndOfServiceInput(
            Guid.NewGuid(), Guid.NewGuid(), new SalaryBreakdown(10_000m, 0m, 0m, 0m),
            DateOnly.Parse(start), DateOnly.Parse(end), "Resignation", "Unlimited", "SAU");
        return (await calc.CalculateAsync(input)).TotalGratuity;
    }

    [Fact]
    public async Task Resignation_OneDayUnderTwoYears_GetsNothing()
        => (await Resign("2024-01-01", "2025-12-30")).Should().Be(0m);

    [Fact]
    public async Task Resignation_ExactlyTwoYears_GetsOneThird()
        // Full award 2 x 0.5 x 10,000 = 10,000; one third = 3,333.33. Before the fix the last day
        // was dropped, the period read 1y 11m 30d and the award was nil.
        => (await Resign("2024-01-01", "2025-12-31")).Should().Be(3_333.33m);

    [Fact]
    public async Task Resignation_ExactlyFiveYears_GetsTwoThirds()
        // Full award 5 x 0.5 x 10,000 = 25,000; two thirds = 16,666.67 (was one third, 8,333.33).
        => (await Resign("2020-01-01", "2024-12-31")).Should().Be(16_666.67m);

    [Fact]
    public async Task Resignation_OneDayUnderFiveYears_StillOneThird()
        => (await Resign("2020-01-01", "2024-12-30")).Should().BeLessThan(8_400m).And.BeGreaterThan(8_300m);

    [Fact]
    public async Task Resignation_ExactlyTenYears_GetsFullAward()
        // 5 x 0.5 x 10,000 + 5 x 1 x 10,000 = 75,000.
        => (await Resign("2015-01-01", "2024-12-31")).Should().Be(75_000m);

    [Fact]
    public async Task Resignation_OneDayUnderTenYears_StillTwoThirds()
        => (await Resign("2015-01-01", "2024-12-30")).Should().BeLessThan(50_000m);

    [Fact]
    public async Task TheSettlementShowsTheYearsTheAwardWasMeasuredOn()
    {
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var input = new EndOfServiceInput(
            Guid.NewGuid(), Guid.NewGuid(), new SalaryBreakdown(10_000m, 0m, 0m, 0m),
            DateOnly.Parse("2024-01-01"), DateOnly.Parse("2025-12-31"), "Termination", "Unlimited", "SAU");

        var result = await calc.CalculateAsync(input);

        // 731 days / 365 would read 2.0027; the award is measured on 24 whole months = exactly 2.0000.
        result.ServiceYearsAwarded.Should().Be(2m);
    }
}
