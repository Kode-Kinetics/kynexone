using FluentAssertions;
using Zayra.Api.Application.Employees;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Who is Saudi, GCC or other is decided by the ISO code HR confirmed, not by how the nationality happened to be typed. A confirmed
/// SA is Saudi whatever the free text says; a confirmed other code is not Saudi even if the text says so; a proposed or missing code
/// leaves the free text to decide; and a nationality that matches nothing is flagged on save instead of silently counting as a foreigner.
/// </summary>
public sealed class NationalityCodeClassificationTests
{
    private const string Confirmed = NationalityMappingStates.Confirmed;

    [Theory]
    [InlineData("Suadi", "SA", true)]
    [InlineData("", "SA", true)]
    [InlineData("Saudi", "EG", false)]
    [InlineData("Saudi", "sa", true)]
    public void IsSaudi_AConfirmedCodeDecides(string text, string code, bool expected) =>
        SaudiNationality.IsSaudi(text, code, Confirmed).Should().Be(expected);

    [Theory]
    [InlineData("Saudi", "EG", NationalityMappingStates.ProposedAuto, true)]
    [InlineData("Suadi", "SA", NationalityMappingStates.ProposedAuto, false)]
    [InlineData("Saudi", null, NationalityMappingStates.Unset, true)]
    [InlineData("Suadi", null, NationalityMappingStates.Unresolved, false)]
    public void IsSaudi_AProposedOrMissingCodeLeavesTheTextToDecide(string text, string? code, string state, bool expected) =>
        SaudiNationality.IsSaudi(text, code, state).Should().Be(expected);

    [Theory]
    [InlineData("Suadi", "SA", GosiClassifications.Saudi)]
    [InlineData("Saudi", "IN", GosiClassifications.NonSaudi)]
    [InlineData("Kuwaiti", "KW", GosiClassifications.GCC)]
    [InlineData("Whatever", "KW", GosiClassifications.GCC)]
    [InlineData("Indian", "IN", GosiClassifications.NonSaudi)]
    [InlineData("Saudi", "AE", GosiClassifications.GCC)]
    public void GosiClassification_AConfirmedCodeDecides(string text, string code, string expected) =>
        GosiCalculationService.DeriveClassification(text, code, Confirmed).Should().Be(expected);

    [Theory]
    [InlineData("Saudi", null, NationalityMappingStates.Unset, GosiClassifications.Saudi)]
    [InlineData("Kuwaiti", null, NationalityMappingStates.Unset, GosiClassifications.GCC)]
    [InlineData("Indian", "SA", NationalityMappingStates.ProposedAuto, GosiClassifications.NonSaudi)]
    public void GosiClassification_WithoutAConfirmedCode_IsTheTextRuleAsBefore(string text, string? code, string state, string expected) =>
        GosiCalculationService.DeriveClassification(text, code, state).Should().Be(expected);

    private static EmployeeCreateRequest Request(string? nationality, string? code = null) =>
        Zayra.Api.Tests.Onboarding.EmployeeInputValidatorTests.Req(nationality: nationality, nationalityCode: code);

    private static EmployeeInputValidationResult Validate(EmployeeCreateRequest request, EmployeeGuardMode mode = EmployeeGuardMode.Create) =>
        EmployeeInputValidator.Validate(request, new EmployeeInputValidationContext("SA", new DateOnly(2026, 10, 26), mode, TimeZoneInfo.Utc));

    [Fact]
    public void ATypedNationalityThatMatchesNothing_AndHasNoCode_IsWarnedAbout()
    {
        var result = Validate(Request("Suadi"));
        result.Warnings.Should().Contain(w => w.Code == EmployeeWarningCodes.NationalityUnresolved && w.Field == "nationality");
    }

    [Theory]
    [InlineData("Saudi", null)]
    [InlineData("Indian", null)]
    [InlineData("anything", "IN")]
    [InlineData(null, null)]
    public void ARecognisedOrCodedOrBlankNationality_IsNotWarnedAbout(string? text, string? code) =>
        Validate(Request(text, code)).Warnings.Should().NotContain(w => w.Code == EmployeeWarningCodes.NationalityUnresolved);

    [Fact]
    public void AutosavingADraft_DoesNotNagAboutHalfTypedNationality() =>
        Validate(Request("Sau"), EmployeeGuardMode.Draft).Warnings.Should().NotContain(w => w.Code == EmployeeWarningCodes.NationalityUnresolved);
}
