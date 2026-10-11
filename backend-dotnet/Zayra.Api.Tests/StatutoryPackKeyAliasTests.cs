using FluentAssertions;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.Payroll;
using Xunit;

namespace Zayra.Api.Tests;

/// <summary>
/// Real company rows spell "mainland" many ways: blank (company form), the bare country code (e2e
/// provisioning), "KSA", "SA-default" (setup assistant). Every one of them must reach the seeded
/// KSA-mainland pack; before this, all but blank matched no rule and payroll silently used fallbacks.
/// </summary>
public class StatutoryPackKeyAliasTests
{
    [Theory]
    [InlineData("SA", "")]
    [InlineData("SA", null)]
    [InlineData("SA", "SA")]
    [InlineData("SA", "SAU")]
    [InlineData("SA", "KSA")]
    [InlineData("SA", "SA-default")]
    [InlineData("sa", "sa-default")]
    [InlineData("SAU", "KSA-mainland")]
    [InlineData("SAU", "")]
    public void EverySaudiMainlandSpelling_MapsToTheSeededPack(string country, string? jurisdiction)
        => StatutoryPackKey.Normalize(country, jurisdiction).Should().Be(("SAU", Jurisdictions.KsaMainland));

    [Theory]
    [InlineData("AE", "")]
    [InlineData("AE", "AE-default")]
    [InlineData("AE", "UAE")]
    public void UaeMainlandSpellings_MapToUaeMainland(string country, string jurisdiction)
        => StatutoryPackKey.Normalize(country, jurisdiction).Should().Be(("ARE", Jurisdictions.UAEMainland));

    [Theory]
    [InlineData("AE", "UAE-DIFC")]
    [InlineData("AE", "UAE-ADGM")]
    public void DistinctLegalZones_PassThrough(string country, string jurisdiction)
        => StatutoryPackKey.Normalize(country, jurisdiction).Jurisdiction.Should().Be(jurisdiction);

    [Theory]
    [InlineData(208, 240, 208)]   // contractual policy more generous than statute: policy wins
    [InlineData(240, 240, 240)]
    [InlineData(260, 240, 240)]   // policy less generous than statute: statute wins
    [InlineData(208, null, 208)]  // no rule: policy stands
    [InlineData(0, null, 1)]      // never divide by zero
    public void OvertimeDivisor_IsTheMoreGenerousOfPolicyAndStatute(int policy, int? rule, int expected)
        => OvertimeStatutoryContextResolver.EffectiveMonthlyHours(policy, rule).Should().Be(expected);
}
