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

    [Fact]
    public async Task A4_GccNationalIsNotGivenExpatTreatment()
    {
        // A Bahraini engineer in Riyadh is insured under Bahrain's scheme via the GCC Unified
        // Insurance Extension Scheme, not as an expatriate on 2% occupational hazard. Applying expat
        // treatment is a confident wrong answer, and the validation engine simultaneously blocks the
        // run demanding GOSI the calculator cannot produce.
        var rules = new StubRuleReader()
            .Set("gosi.saudi_employee_rate", 0.09m)
            .Set("gosi.saudi_employer_rate", 0.09m)
            .Set("gosi.saned_rate", 0.0075m)
            .Set("gosi.expat_occupational_hazard_rate", 0.02m)
            .Set("gosi.covered_wage_ceiling_sar", 45_000m);
        var calc = new KsaDeductionCalculator(rules);

        var result = await calc.CalculateAsync(new StatutoryDeductionInput(
            Guid.NewGuid(), Guid.NewGuid(), new SalaryBreakdown(10_000m, 4_000m, 0m, 0m),
            "Bahraini", "Indefinite", 2026, 1));

        Assert.DoesNotContain(result.Lines, l => l.Code == "GOSI-OH-ER");
    }

    [Fact]
    public void A5_KsaOvertimeBaseIsTheFullWage_AndTheDayRatesAreReadable()
    {
        // KSA Art. 107 pays the hourly WAGE plus 50% of BASIC. The product had no way to express
        // that at all: there was no ot.hourly_base rule, so basic was the only possible base.
        var build = typeof(Zayra.Api.Infrastructure.Seed.StatutoryRuleSeeder).GetMethod(
            "BuildRules", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var rules = (List<Zayra.Api.Models.StatutoryRule>)build.Invoke(null, null)!;

        var ksaBase = rules.SingleOrDefault(
            r => r.CountryCode == "SAU" && r.Jurisdiction == "KSA-mainland" && r.RuleKey == "ot.hourly_base");
        Assert.NotNull(ksaBase);
        Assert.Equal("wage", ksaBase!.RuleValue);
    }

    [Fact]
    public void A5_ThePayrollRunReadsTheSeededRestDayAndHolidayRates()
    {
        // StatutoryRuleSeeder has written ot.restday_multiplier and ot.holiday_multiplier at 2.0
        // since the pack was built, and a repo-wide grep found no reader. A rule the seeder writes
        // and the money path never consults looks like compliance and is not.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? controller = null;
        for (var i = 0; i < 6 && controller is null; i++)
        {
            if (dir?.Parent is null) break;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api", "Controllers", "PayrollController.cs");
            if (File.Exists(candidate)) controller = candidate;
        }
        if (controller is null) return;
        var source = File.ReadAllText(controller);

        Assert.Contains("ot.restday_multiplier", source, StringComparison.Ordinal);
        Assert.Contains("ot.holiday_multiplier", source, StringComparison.Ordinal);
    }
}
