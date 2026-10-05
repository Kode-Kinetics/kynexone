using FluentAssertions;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>One Saudi-nationality normaliser behind GOSI, Saudisation, the readiness floor and the wage file.</summary>
public class SaudiNationalityTests
{
    [Theory]
    [InlineData("KSA")]
    [InlineData(" Saudi ")]
    [InlineData("سعودي")]
    [InlineData("saudi arabia")]
    [InlineData("Saudi Arabian")]
    [InlineData("SA")]
    [InlineData("sau")]
    [InlineData("سعودية")]
    [InlineData("السعودية")]
    [InlineData("Kingdom of Saudi Arabia")]
    [InlineData("سعودى")]
    [InlineData(" المملكة العربية السعودية ")]
    public void Every_saudi_spelling_classifies_as_saudi_for_gosi(string nationality)
    {
        GosiCalculationService.DeriveClassification(nationality).Should().Be(GosiClassifications.Saudi);
        SaudiNationality.IsSaudi(nationality).Should().BeTrue();
        KsaWageFileRules.IsSaudiNationality(nationality).Should().BeTrue();
        GccReadinessFloor.NormalizeNationality(nationality).Should().Be("SA");
        GosiCalculationService.DeriveGccHomeState(nationality).Should().BeNull();
    }

    [Theory]
    [InlineData("Sudanese")]
    [InlineData("Saudi-born Egyptian")]
    [InlineData("Non-Saudi")]
    [InlineData("Saudis")]
    [InlineData("")]
    [InlineData(null)]
    public void Match_is_exact_after_normalising_never_a_substring(string? nationality)
    {
        GosiCalculationService.DeriveClassification(nationality).Should().Be(GosiClassifications.NonSaudi);
        SaudiNationality.IsSaudi(nationality).Should().BeFalse();
    }

    [Theory]
    [InlineData("Bahraini", "BH")]
    [InlineData("UAE", "AE")]
    [InlineData("Kuwait", "KW")]
    [InlineData(" Bahraini ", "BH")]
    [InlineData("omani ", "OM")]
    public void Gcc_classification_is_unchanged(string nationality, string home)
    {
        GosiCalculationService.DeriveClassification(nationality).Should().Be(GosiClassifications.GCC);
        GosiCalculationService.DeriveGccHomeState(nationality).Should().Be(home);
    }
}
