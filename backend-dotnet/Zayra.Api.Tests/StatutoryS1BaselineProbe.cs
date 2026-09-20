using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.CountryPack.Uae;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// S1 evidence probe — written against the BASELINE (develop) API surface only, so it compiles both
/// before and after the fix. Each assertion states what GCC statute requires. On develop they fail;
/// on fix/s1-statutory-payroll they pass.
/// </summary>
public class StatutoryS1BaselineProbe
{
    [Fact]
    public async Task A1_KsaGratuityIsOnTheLastWage()
    {
        // 12 yrs, SAR 30,000 package (basic 18,000 / housing 7,500 / transport 4,500).
        // Art. 84 + Art. 2: award = 9.5 months × 30,000 = 285,000. Baseline pays 9.5 × 18,000 = 171,000.
        var calc = new KsaEndOfServiceCalculator(new StubRuleReader());
        var result = await calc.CalculateAsync(new EndOfServiceInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(18_000m, 7_500m, 4_500m, 0m),
            new DateOnly(2014, 1, 1), new DateOnly(2026, 1, 1),
            "Termination", "Indefinite", "SAU"));

        Assert.Equal(9.5m * 30_000m, result.TotalGratuity);
    }

    [Fact]
    public void A1_ShippedCatalogFlagsHousingIntoTheEosbBase()
    {
        var housing = PayComponentCatalog.SystemComponentSeeds(Guid.NewGuid())
            .Single(c => c.Code == "HOUSING");
        Assert.True(housing.EosbIncluded);
    }

    [Fact]
    public async Task A9_DifcPostCutOverServiceIsNotAnEmployerPayable()
    {
        // Hired after the 1 Feb 2020 DEWS cut-over: the trustee pays, the employer does not.
        var calc = new UaeDifcEndOfServiceCalculator(new StubRuleReader());
        var result = await calc.CalculateAsync(new EndOfServiceInput(
            Guid.NewGuid(), Guid.NewGuid(),
            new SalaryBreakdown(20_000m, 0m, 0m, 0m),
            new DateOnly(2021, 6, 1), new DateOnly(2026, 1, 1),
            "Termination", "Indefinite", "IND"));

        Assert.Equal(0m, result.TotalGratuity);
    }
}
