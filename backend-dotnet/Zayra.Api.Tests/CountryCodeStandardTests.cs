using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack;

namespace Zayra.Api.Tests;

/// <summary>
/// Phase 1B country-code standardization (K11). Canonical internal format is ISO-2;
/// the CountryPack framework keeps its ISO-3 DI keys behind an explicit mapping layer.
/// </summary>
public class CountryCodeStandardTests
{
    [Theory]
    [InlineData("SA", "SA")]
    [InlineData("SAU", "SA")]
    [InlineData("AE", "AE")]
    [InlineData("ARE", "AE")]
    [InlineData("IN", "IN")]
    [InlineData("IND", "IN")]
    [InlineData("GB", "GB")]
    [InlineData("GBR", "GB")]
    [InlineData("sa", "SA")]
    [InlineData(" sau ", "SA")]
    public void NormalizeToIso2_AcceptsBothForms_ReturnsCanonicalIso2(string input, string expected)
        => CountryCodeStandard.NormalizeToIso2(input).Should().Be(expected);

    [Theory]
    [InlineData("SA", "SAU")]
    [InlineData("AE", "ARE")]
    [InlineData("IN", "IND")]
    [InlineData("GB", "GBR")]
    [InlineData("QAT", "QAT")] // already ISO-3 passes through
    public void ToIso3_MapsCanonicalToCountryPackKey(string input, string expected)
        => CountryCodeStandard.ToIso3(input).Should().Be(expected);

    [Theory]
    [InlineData("KSA")]   // common free-text mistake — NOT an ISO code
    [InlineData("UAE")]   // ditto
    [InlineData("XX")]
    [InlineData("Saudi")]
    [InlineData("")]
    [InlineData(null)]
    public void FreeTextAndUnknownCodes_AreRejected(string? input)
    {
        CountryCodeStandard.NormalizeToIso2(input).Should().BeNull();
        CountryCodeStandard.IsValid(input).Should().BeFalse();
    }

    [Fact]
    public void EmptyIsAllowedByTheOrEmptyGate_ButGarbageIsNot()
    {
        CountryCodeStandard.IsValidOrEmpty("").Should().BeTrue("not-yet-configured is a valid state");
        CountryCodeStandard.IsValidOrEmpty(null).Should().BeTrue();
        CountryCodeStandard.IsValidOrEmpty("KSA").Should().BeFalse();
    }

    [Fact]
    public void EveryIsoReferenceCountry_HasAnIso3Mapping()
    {
        foreach (var country in IsoReference.Countries)
            CountryCodeStandard.ToIso3(country.Code).Should().NotBeNull(
                $"IsoReference country {country.Code} ({country.Name}) must map to an ISO-3 CountryPack key");
    }

    // ── The mapping layer in action: ISO-2 company data reaches ISO-3 packs ─────

    [Fact]
    public void CountryPackResolver_ResolvesIso2Input_ThroughIso3MappingLayer()
    {
        var ksaPack = new StubEosCalculator();
        var defaultPack = new StubEosCalculator();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IEndOfServiceCalculator>("SAU", ksaPack);
        services.AddSingleton<IEndOfServiceCalculator>(defaultPack);
        var resolver = new CountryPackResolver(services.BuildServiceProvider());

        // Canonical ISO-2 stored on Company must reach the ISO-3-keyed pack, not Default.
        resolver.ResolveEndOfServiceCalculator("SA", "mainland").Should().BeSameAs(ksaPack);
        // Legacy ISO-3 data keeps working unchanged.
        resolver.ResolveEndOfServiceCalculator("SAU", "mainland").Should().BeSameAs(ksaPack);
        // Unknown country falls back to the Default pack (fail-closed 422 happens upstream).
        resolver.ResolveEndOfServiceCalculator("ZZ", "mainland").Should().BeSameAs(defaultPack);
    }

    private sealed class StubEosCalculator : IEndOfServiceCalculator
    {
        public Task<EndOfServiceResult> CalculateAsync(EndOfServiceInput input, CancellationToken ct = default)
            => throw new NotSupportedException("resolution-only stub");
    }
}
