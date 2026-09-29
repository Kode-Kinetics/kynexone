using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// S1/A4 — GCC nationals working in Saudi Arabia.
///
/// <para>The calculator recognised only SAU/SA/Saudi, so a Bahraini fell through to the expatriate
/// branch and received 2% occupational hazard and nothing else. The validation engine derives a
/// distinct "GCC" bucket from the SAME nationality string and raises a BLOCKING error demanding
/// GOSI Annuities and SANED for it — contributions the calculator structurally could not produce.
/// The run could not complete and could not be re-processed: the customer could not pay their
/// Bahraini engineer.</para>
/// </summary>
public class StatutoryGccNationalTests
{
    private static StatutoryDeductionInput Input(string nationality, decimal basic = 10_000m, decimal housing = 4_000m)
        => new(Guid.NewGuid(), Guid.NewGuid(), new SalaryBreakdown(basic, housing, 0m, 0m),
               nationality, "Indefinite", 2026, 1);

    private static StubRuleReader BaseRules() => new StubRuleReader()
        .Set("gosi.saudi_employee_rate", 0.09m)
        .Set("gosi.saudi_employer_rate", 0.09m)
        .Set("gosi.saned_rate", 0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar", 45_000m);

    [Theory]
    [InlineData("Bahraini", "BH")]
    [InlineData("KW", "KW")]
    [InlineData("Omani", "OM")]
    [InlineData("Qatari", "QA")]
    [InlineData("Emirati", "AE")]
    public void Classification_And_HomeState_AgreeOnWhoIsGcc(string nationality, string expectedHome)
    {
        Assert.Equal(GosiClassifications.GCC, GosiCalculationService.DeriveClassification(nationality));
        Assert.Equal(expectedHome, GosiCalculationService.DeriveGccHomeState(nationality));
    }

    [Theory]
    [InlineData("SAU")]
    [InlineData("Saudi")]
    [InlineData("IND")]
    public void HomeState_IsNullForAnyoneWhoIsNotAGccNational(string nationality)
        => Assert.Null(GosiCalculationService.DeriveGccHomeState(nationality));

    [Fact]
    public async Task GccNational_IsNotGivenExpatTreatment()
    {
        // The old behaviour: a Bahraini got GOSI-OH-ER at 2% and nothing else — a confident, wrong
        // answer. With no home-state rates configured the honest output is NOTHING, so the validator
        // can block with a reason instead of the payslip quietly claiming compliance.
        var calc = new KsaDeductionCalculator(BaseRules());

        var result = await calc.CalculateAsync(Input("Bahraini"));

        Assert.Empty(result.Lines);
        Assert.Equal(0m, result.TotalEmployeeDeduction);
        Assert.Equal(0m, result.TotalEmployerContribution);
        // Specifically: NOT the expat occupational-hazard line the old code emitted.
        Assert.DoesNotContain(result.Lines, l => l.Code == "GOSI-OH-ER");
    }

    [Fact]
    public async Task GccNational_ContributesAtHomeStateRates_WhenConfigured()
    {
        // Covered wage = 10,000 + 4,000 = 14,000.
        // Home-state (BH) rates configured at 7% employee / 12% employer.
        //   Employee: 14,000 × 7% = 980
        //   Employer: capped at the Saudi employer rate of 9% → 14,000 × 9% = 1,260
        //   Excess 12% − 9% = 3% → 14,000 × 3% = 420, borne by the EMPLOYEE per the scheme
        //   Occupational hazard: 14,000 × 2% = 280, employer
        var calc = new KsaDeductionCalculator(BaseRules()
            .Set("gosi.gcc.BH.employee_rate", 0.07m)
            .Set("gosi.gcc.BH.employer_rate", 0.12m));

        var result = await calc.CalculateAsync(Input("Bahraini"));

        Assert.Equal(980m + 420m, result.TotalEmployeeDeduction);
        Assert.Equal(1_260m + 280m, result.TotalEmployerContribution);
        Assert.Contains(result.Lines, l => l.Code == "GOSI-GCC-BH-EE");
        Assert.Contains(result.Lines, l => l.Code == "GOSI-GCC-BH-ER" && l.EmployerAmount == 1_260m);
        Assert.Contains(result.Lines, l => l.Code == "GOSI-OH-ER" && l.EmployerAmount == 280m);
    }

    [Fact]
    public async Task GccNational_EmployerShareIsCappedAtTheSaudiRate()
    {
        // A home state cheaper than Saudi: the employer pays the home rate, not the Saudi rate, and
        // nothing spills onto the employee.
        var calc = new KsaDeductionCalculator(BaseRules()
            .Set("gosi.gcc.OM.employee_rate", 0.07m)
            .Set("gosi.gcc.OM.employer_rate", 0.0575m));

        var result = await calc.CalculateAsync(Input("Omani"));

        Assert.Equal(Math.Round(14_000m * 0.07m, 2), result.TotalEmployeeDeduction);
        Assert.Equal(Math.Round(14_000m * 0.0575m, 2) + 280m, result.TotalEmployerContribution);
    }

    [Fact]
    public async Task PartiallyConfiguredHomeState_StillRefusesRatherThanGuessing()
    {
        var calc = new KsaDeductionCalculator(BaseRules().Set("gosi.gcc.KW.employee_rate", 0.08m));

        var result = await calc.CalculateAsync(Input("Kuwaiti"));

        Assert.Empty(result.Lines);
    }

    [Fact]
    public async Task SaudiAndExpatTreatmentAreUnchanged()
    {
        var calc = new KsaDeductionCalculator(BaseRules());

        var saudi = await calc.CalculateAsync(Input("SAU"));
        Assert.Equal(Math.Round(14_000m * 0.0975m, 2), saudi.TotalEmployeeDeduction);
        Assert.Equal(5, saudi.Lines.Count);

        var expat = await calc.CalculateAsync(Input("IND"));
        Assert.Equal(0m, expat.TotalEmployeeDeduction);
        Assert.Equal(280m, expat.TotalEmployerContribution);
        Assert.Single(expat.Lines);
    }
}
