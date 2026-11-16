using FluentAssertions;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;

namespace Zayra.Api.Tests;

/// <summary>
/// L1.3 — GOSI contributions round HALF-UP (away from zero) per component, per employee, per side, to two
/// decimals, instead of .NET's default banker's rounding. The mode is an effective-dated platform rule
/// (<c>gosi.rounding_mode</c>: HALF_UP default, HALF_EVEN) so a different mode proven by a client's GOSI invoice
/// is a rule change, not a code change. The modes differ only when wage x rate ends in exactly half a halala,
/// and then by 0.01 per component.
/// </summary>
public class GosiRoundingModeTests
{
    private const string ModeKey = "gosi.rounding_mode";

    private static StubRuleReader Rules(string? mode = null)
    {
        var r = new StubRuleReader()
            .Set("gosi.saudi_employee_rate", 0.09m)
            .Set("gosi.saudi_employer_rate", 0.09m)
            .Set("gosi.saned_rate", 0.0075m)
            .Set("gosi.expat_occupational_hazard_rate", 0.02m)
            .Set("gosi.covered_wage_ceiling_sar", 45_000m);
        if (mode is not null) r.Set(ModeKey, mode);
        return r;
    }

    private static StatutoryDeductionInput Person(decimal basic, decimal housing, string nationality, DateOnly? firstRegisteredOn) =>
        new(Guid.Empty, Guid.Empty, new SalaryBreakdown(basic, housing, 0m, 0m), nationality, "Indefinite", 2026, 10)
        {
            SocialInsuranceFirstRegisteredOn = firstRegisteredOn,
        };

    private static decimal Line(StatutoryDeductionResult r, string code)
    {
        var l = r.Lines.Single(x => x.Code == code);
        return code.EndsWith("-EE", StringComparison.Ordinal) ? l.EmployeeAmount : l.EmployerAmount;
    }

    // Spec 1.1 — Saudi, existing subscriber, covered wage 12,500.50.
    [Fact]
    public async Task Saudi_ExistingCohort_12500_50_RoundsEachComponentHalfUp()
    {
        var r = await new KsaDeductionCalculator(Rules()).CalculateAsync(Person(12_500.50m, 0m, "Saudi", new DateOnly(2016, 5, 10)));

        Line(r, "GOSI-ANN-EE").Should().Be(1_125.05m, "12,500.50 x 9% = 1,125.045, half-up");
        Line(r, "GOSI-ANN-ER").Should().Be(1_125.05m);
        Line(r, "GOSI-SANED-EE").Should().Be(93.75m, "12,500.50 x 0.75% = 93.75375");
        Line(r, "GOSI-SANED-ER").Should().Be(93.75m);
        Line(r, "GOSI-OH-ER").Should().Be(250.01m);
        r.TotalEmployeeDeduction.Should().Be(1_218.80m);
        r.TotalEmployerContribution.Should().Be(1_468.81m);
    }

    [Fact]
    public async Task NoRuleRow_DefaultsToHalfUp()
    {
        var r = await new KsaDeductionCalculator(Rules(mode: null)).CalculateAsync(Person(12_500.50m, 0m, "Saudi", new DateOnly(2016, 5, 10)));
        Line(r, "GOSI-ANN-EE").Should().Be(1_125.05m);
    }

    [Theory]
    [InlineData("HALF_UP", "1125.05")]
    [InlineData("half_up", "1125.05")]
    [InlineData("HALF_EVEN", "1125.04")]
    [InlineData(" half_even ", "1125.04")]
    [InlineData("SOMETHING_ELSE", "1125.05")] // an unrecognised value never silently switches to banker's
    public async Task RuleRow_SelectsTheMode(string mode, string annuityEe)
    {
        var r = await new KsaDeductionCalculator(Rules(mode)).CalculateAsync(Person(12_500.50m, 0m, "Saudi", new DateOnly(2016, 5, 10)));
        Line(r, "GOSI-ANN-EE").Should().Be(decimal.Parse(annuityEe, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task HalfEvenRow_Spec16_ProvesTheSwitchWorks_TotalsFollow()
    {
        var r = await new KsaDeductionCalculator(Rules("HALF_EVEN")).CalculateAsync(Person(12_500.50m, 0m, "Saudi", new DateOnly(2016, 5, 10)));
        r.TotalEmployeeDeduction.Should().Be(1_218.79m);
        r.TotalEmployerContribution.Should().Be(1_468.80m);
    }

    // Spec 1.2 — Saudi new entrant (annuities 10% for October 2026), wage 7,500.05.
    [Fact]
    public async Task Saudi_NewEntrant_7500_05_RoundsHalfUp()
    {
        var rules = Rules()
            .Set("gosi.new_entrant_employee_rate", 0.10m)
            .Set("gosi.new_entrant_employer_rate", 0.10m);
        var r = await new KsaDeductionCalculator(rules).CalculateAsync(Person(7_500.05m, 0m, "Saudi", new DateOnly(2025, 2, 1)));

        Line(r, "GOSI-ANN-EE").Should().Be(750.01m, "7,500.05 x 10% = 750.005");
        Line(r, "GOSI-ANN-ER").Should().Be(750.01m);
        Line(r, "GOSI-SANED-EE").Should().Be(56.25m);
        Line(r, "GOSI-OH-ER").Should().Be(150.00m, "7,500.05 x 2% = 150.001");
        r.TotalEmployeeDeduction.Should().Be(806.26m);
        r.TotalEmployerContribution.Should().Be(956.26m);
    }

    // Spec 1.3 — expat, occupational hazards only.
    [Fact]
    public async Task Expat_7500_25_OccupationalHazardsRoundsHalfUp()
    {
        var r = await new KsaDeductionCalculator(Rules()).CalculateAsync(Person(7_500.25m, 0m, "Indian", null));
        Line(r, "GOSI-OH-ER").Should().Be(150.01m, "7,500.25 x 2% = 150.005");
        r.TotalEmployerContribution.Should().Be(150.01m);
        r.TotalEmployeeDeduction.Should().Be(0m);
    }

    // Spec 1.4 / 1.5 — a clamped wage and a wage with no half-halala case are unchanged.
    [Theory]
    [InlineData(52_000, 4_387.50, 5_287.50)]
    [InlineData(12_500, 1_218.75, 1_468.75)]
    public async Task ClampedOrExactWages_AreUnchanged(int wage, double ee, double er)
    {
        var r = await new KsaDeductionCalculator(Rules()).CalculateAsync(Person(wage, 0m, "Saudi", new DateOnly(2016, 5, 10)));
        r.TotalEmployeeDeduction.Should().Be((decimal)ee);
        r.TotalEmployerContribution.Should().Be((decimal)er);
    }

    // GCC national: same mode (the home-state employee line is rounded too).
    [Fact]
    public async Task GccNational_HomeStateLines_RoundHalfUp_AndHalfEvenWhenTheRowSaysSo()
    {
        StubRuleReader Gcc(string? mode) => Rules(mode)
            .Set("gosi.gcc.BH.employee_rate", 0.09m)
            .Set("gosi.gcc.BH.employer_rate", 0.09m);

        var up = await new KsaDeductionCalculator(Gcc(null)).CalculateAsync(Person(12_500.50m, 0m, "Bahraini", null));
        up.Lines.Single(l => l.Code == "GOSI-GCC-BH-EE").EmployeeAmount.Should().Be(1_125.05m);
        up.Lines.Single(l => l.Code == "GOSI-GCC-BH-ER").EmployerAmount.Should().Be(1_125.05m);

        var even = await new KsaDeductionCalculator(Gcc("HALF_EVEN")).CalculateAsync(Person(12_500.50m, 0m, "Bahraini", null));
        even.Lines.Single(l => l.Code == "GOSI-GCC-BH-EE").EmployeeAmount.Should().Be(1_125.04m);
    }

    // The GOSI preview and the readiness report call the same engine (GosiCalculationService.CalculateAsync).
    [Fact]
    public async Task Preview_UsesTheSameRoundingAsThePayslipEngine()
    {
        var preview = await GosiCalculationService.CalculateAsync(
            Rules(), "Saudi", 12_500.50m, 0m, new DateOnly(2026, 10, 1), new DateOnly(2016, 5, 10));
        preview.EmployeeTotal.Should().Be(1_218.80m);
        preview.EmployerTotal.Should().Be(1_468.81m);
    }

    [Fact]
    public void RuleIsSeededAsPlatformRows_HalfEvenBeforeGoLive_HalfUpFromIt()
    {
        // The go-live dates themselves are pinned in GosiRoundingGoLiveTests.
        var rows = StatutoryRuleSeeder.BuildRules().Where(r => r.RuleKey == ModeKey).OrderBy(r => r.EffectiveFrom).ToList();
        rows.Select(r => r.RuleValue).Should().Equal("HALF_EVEN", "HALF_UP");
        rows.Should().OnlyContain(r => r.DataType == "string");
        rows.Should().OnlyContain(r => r.TenantId == null, "GOSI figures are platform rows; a company cannot set its own");
        rows.Should().OnlyContain(r => r.CountryCode == CountryCodes.Saudi);
    }

    [Fact]
    public void ATenantCannotWriteTheRoundingModeEither()
    {
        GosiStatutoryValues.IsStatutory(ModeKey).Should().BeTrue(
            "tenant rows are never read for GOSI, so a saved tenant value would look applied and do nothing");
        GosiStatutoryValues.TenantWriteRefusal(ModeKey).Should().NotBeNull();
    }
}
