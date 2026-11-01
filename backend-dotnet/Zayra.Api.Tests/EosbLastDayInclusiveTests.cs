using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.CountryPack.Qatar;
using Zayra.Api.Infrastructure.CountryPack.Uae;

namespace Zayra.Api.Tests;

/// <summary>
/// PAY-01 — the service period is INCLUSIVE of the last working day. Service 2020-01-01 through
/// 2021-12-31 is two full years; measuring it as 1y11m30d paid a resigning employee nothing.
/// </summary>
public class EosbLastDayInclusiveTests
{
    private static EndOfServiceInput Input(DateOnly start, DateOnly lastDay, string reason) =>
        new(Guid.NewGuid(), Guid.NewGuid(), new SalaryBreakdown(10_000m, 0m, 0m, 0m),
            start, lastDay, reason, "Indefinite", "SAU");

    private static KsaEndOfServiceCalculator Ksa() => new(new StubRuleReader());

    [Fact]
    public async Task Ksa_Resignation_ServiceThroughLastDayOfSecondYear_IsTwoYears_AndPays()
    {
        var r = await Ksa().CalculateAsync(Input(new(2020, 1, 1), new(2021, 12, 31), "Resignation"));
        // 2.0 yrs x 0.5 x 10,000 = 10,000; one third under Art. 85 = 3,333.33
        Assert.True(r.TotalGratuity > 0m, $"Expected a non-nil Art.85 award for 2 full years, got {r.TotalGratuity}");
        Assert.Equal(3_333.33m, r.TotalGratuity);
    }

    [Fact]
    public async Task Ksa_Resignation_OneDayShortOfTwoYearsInclusive_IsStillNil()
    {
        var r = await Ksa().CalculateAsync(Input(new(2020, 1, 1), new(2021, 12, 30), "Resignation"));
        Assert.Equal(0m, r.TotalGratuity);
    }

    [Theory]
    [InlineData(2024, 12, 31, 1, 3)]   // exactly 5.0 yrs inclusive -> 1/3 (band is "up to and including 5")
    [InlineData(2025, 1, 1, 2, 3)]     // 5y + 1d -> 2/3 band
    [InlineData(2029, 12, 30, 2, 3)]   // 10y - 1d -> still 2/3
    [InlineData(2029, 12, 31, 1, 1)]   // exactly 10y inclusive -> full
    public async Task Ksa_Resignation_BandBoundaries_AreInclusiveOfLastDay(int y, int m, int d, int num, int den)
    {
        var resign = await Ksa().CalculateAsync(Input(new(2020, 1, 1), new(y, m, d), "Resignation"));
        var full   = await Ksa().CalculateAsync(Input(new(2020, 1, 1), new(y, m, d), "Termination"));
        Assert.Equal(Math.Round(full.TotalGratuity * num / den, 2), resign.TotalGratuity, 1);
    }

    [Fact]
    public async Task Ksa_Termination_FullYearsAreExactOnInclusiveLastDay()
    {
        var r = await Ksa().CalculateAsync(Input(new(2020, 1, 1), new(2022, 12, 31), "Termination"));
        Assert.Equal(15_000m, r.TotalGratuity); // 3.0 yrs x 0.5 x 10,000
    }

    [Fact]
    public void Uae_ServiceYears_CountsLastDay()
    {
        Assert.Equal(1m, Math.Round(UaeMainlandEndOfServiceCalculator.ServiceYears(new(2021, 1, 1), new(2021, 12, 31)), 6));
    }

    [Fact]
    public void Qatar_ServiceYears_CountsLastDay()
    {
        Assert.Equal(1m, Math.Round(QatarEndOfServiceCalculator.ServiceYears(new(2021, 1, 1), new(2021, 12, 31)), 6));
    }
}
