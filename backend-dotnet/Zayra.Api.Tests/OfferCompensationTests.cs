using System.Text.Json;
using FluentAssertions;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Infrastructure.Recruitment;

namespace Zayra.Api.Tests;

public class OfferCompensationTests
{
    private static readonly IReadOnlySet<string> Required = new HashSet<string> { "BASIC" };
    internal static OfferComponentInput Cash(string code, decimal amount, string frequency = "Monthly") =>
        new(code, code, null, "Cash", amount, null, frequency, true, null, null, 0);

    [Fact]
    public void RecurringAndOneTimeTotalsAreSeparateAndExcludedBenefitsDoNotAddCash()
    {
        var inputs = new[] { Cash("BASIC", 1000), Cash("HOUSING", 200), Cash("ANNUAL", 1200, "Annual"),
            Cash("JOINING", 500, "OneTime"), Cash("EXCLUDED", 99) with { Included = false, ExclusionReason = "Not offered" },
            new OfferComponentInput("MEDICAL", "Medical", null, "Benefit", null, "Family coverage", null, true, null, null, 5) };
        var snapshot = OfferCompensation.Calculate(inputs, "SAR", 2, Required);
        snapshot.MonthlyCash.Should().Be(1200);
        snapshot.AnnualRecurringCash.Should().Be(15600);
        snapshot.OneTimeCash.Should().Be(500);
        snapshot.Components.Single(c => c.Code == "BASIC").Required.Should().BeTrue();
        snapshot.Components.Single(c => c.Code == "MEDICAL").Required.Should().BeFalse();
        // No presentation/design parameter participates in calculating compensation.
        JsonSerializer.Deserialize<OfferCompensationSnapshot>(JsonSerializer.Serialize(snapshot))!.MonthlyCash.Should().Be(1200);
    }

    [Theory]
    [InlineData("Annual")]
    [InlineData("OneTime")]
    [InlineData("Weekly")]
    public void BasicMustBeMonthly(string frequency) =>
        FluentActions.Invoking(() => OfferCompensation.Calculate([Cash("BASIC", 1000, frequency)], "SAR", 2, Required)).Should().Throw<ArgumentException>();

    [Fact]
    public void BasicCannotBeMissingExcludedOrDescriptive()
    {
        foreach (var inputs in new[] { Array.Empty<OfferComponentInput>(), new[] { Cash("OTHER", 10) },
            new[] { Cash("BASIC", 10) with { Included = false, ExclusionReason = "Skip" } },
            new[] { Cash("BASIC", 10) with { Kind = "Benefit", Amount = null, Frequency = null, Description = "Basic" } } })
            FluentActions.Invoking(() => OfferCompensation.Calculate(inputs, "SAR", 2, Required)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DuplicateCodesAreCaseInsensitive() => FluentActions.Invoking(() =>
        OfferCompensation.Calculate([Cash("BASIC", 1000), Cash("basic", 1000)], "SAR", 2, Required)).Should().Throw<ArgumentException>();

    [Theory]
    [InlineData(0, "1.1")]
    [InlineData(2, "1.001")]
    [InlineData(3, "1.0001")]
    public void ExcessPrecisionIsRejectedWithoutRounding(int precision, string value) => FluentActions.Invoking(() =>
        OfferCompensation.Calculate([Cash("BASIC", 1000), Cash("EXTRA", decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture))], "SAR", precision, Required))
        .Should().Throw<ArgumentException>();

    [Theory]
    [InlineData("-1")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("9999999999999999.999")]
    public void NegativeOrOverflowTotalsAreRejected(string value) => FluentActions.Invoking(() =>
        OfferCompensation.Calculate([Cash("BASIC", decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture))], "SAR", 3, Required)).Should().Throw<ArgumentException>();

    [Fact]
    public void PolicyRequirementsAreServerDerivedAndBasicSupportsPayrollPrecision()
    {
        FluentActions.Invoking(() => OfferCompensation.Calculate([Cash("BASIC", 1000)], "SAR", 2, new HashSet<string> { "HOUSING" })).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => OfferCompensation.Calculate([Cash("BASIC", 1000.001m)], "KWD", 3, Required)).Should().Throw<ArgumentException>();
        OfferCompensation.Calculate([Cash("BASIC", 1000), Cash("EXTRA", 1.001m)], "KWD", 3, Required).MonthlyCash.Should().Be(1001.001m);
    }

    [Theory]
    [InlineData("{\"code\":\"BASIC\",\"required\":false}")]
    [InlineData("{\"code\":\"BASIC\",\"currency\":\"USD\"}")]
    [InlineData("{\"code\":\"BASIC\",\"structureField\":\"BasicSalary\"}")]
    public void CallerCannotSupplyRequiredFlagsCurrencyOrPayrollMappings(string json) => FluentActions.Invoking(() =>
        JsonSerializer.Deserialize<OfferComponentInput>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))).Should().Throw<JsonException>();

    [Fact]
    public void LegacyConversionProducesTheSameFourAmountsExactlyOnce()
    {
        var snapshot = OfferCompensation.Calculate(OfferCompensation.FromLegacy(1000, 200, 50, 25), "SAR", 2, Required);
        snapshot.MonthlyCash.Should().Be(1275);
        snapshot.AnnualRecurringCash.Should().Be(15300);
        snapshot.BasicSalary.Should().Be(1000);
        snapshot.OtherAllowances.Should().Be(25);
        OfferCompensation.ValidateLegacy(snapshot, 1000, 200, 50, 25);
        FluentActions.Invoking(() => OfferCompensation.ValidateLegacy(snapshot, 900, null, null, null)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MandatoryBasicMustFitEmployeeSalaryCapacityButOptionalCashCanUseOfferCapacity()
    {
        FluentActions.Invoking(() => OfferCompensation.Calculate([Cash("BASIC", 10_000_000_000m)], "SAR", 2, Required))
            .Should().Throw<ArgumentException>();
        OfferCompensation.Calculate([Cash("BASIC", 1000), Cash("EXTRA", 10_000_000_000m)], "SAR", 2, Required)
            .MonthlyCash.Should().Be(10_000_001_000m);
    }
}
