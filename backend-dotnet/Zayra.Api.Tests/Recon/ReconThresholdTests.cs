using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

namespace Zayra.Api.Tests.Recon;

public class ReconThresholdTests
{
    private static ReconThresholds T(decimal pct = 5m, decimal min = 0m) => new(pct, min, ReconThresholds.SourceDefault);

    [Fact]
    public void TheDefault_IsFivePercent_AndNoMinimumAmount()
    {
        ReconThresholds.Default.Pct.Should().Be(5m);
        ReconThresholds.Default.MinAmount.Should().Be(0m);
        ReconThresholds.Default.Source.Should().Be("default");
    }

    [Theory]
    [InlineData(1000, 50.00, false)]   // exactly 5% is not over the line
    [InlineData(1000, 50.01, true)]
    [InlineData(1000, -50.01, true)]   // symmetrical
    [InlineData(1000, -50.00, false)]
    [InlineData(1000, 0, false)]
    public void APercentageThreshold_IsStrictlyGreaterThan(double prior, double delta, bool flagged) =>
        T().IsFlagged((decimal)prior, (decimal)delta).Should().Be(flagged);

    [Theory]
    [InlineData(1000, 60, false)]   // 5% = 50, but the minimum is 100 and the larger of the two wins
    [InlineData(1000, 100, false)]
    [InlineData(1000, 100.01, true)]
    [InlineData(10000, 600, true)]  // 5% = 500 now beats the minimum
    public void TheLargerOfPercentageAndMinimumAmount_IsTheThreshold(double prior, double delta, bool flagged) =>
        T(5m, 100m).IsFlagged((decimal)prior, (decimal)delta).Should().Be(flagged);

    [Theory]
    [InlineData(0, 0.01, 0, true)]
    [InlineData(0, 0, 0, false)]
    [InlineData(0, 10, 10, false)]
    [InlineData(0, 10.01, 10, true)]
    [InlineData(0, -10.01, 10, true)]
    public void WithNothingInThePriorPeriod_ThePercentageIsMeaningless_OnlyTheMinimumApplies(double prior, double delta, double min, bool flagged) =>
        T(5m, (decimal)min).IsFlagged((decimal)prior, (decimal)delta).Should().Be(flagged);

    [Fact]
    public void DeltaPct_IsZeroWithoutAPrior_AndTwoDecimalsOtherwise()
    {
        ReconThresholds.DeltaPct(0m, 500m).Should().Be(0m);
        ReconThresholds.DeltaPct(3m, 1m).Should().Be(33.33m);
        ReconThresholds.DeltaPct(-3m, 1m).Should().Be(-33.33m);
        ReconThresholds.DeltaPct(1000m, -50m).Should().Be(-5m);
    }

    [Fact]
    public void FromRules_UsesTheCompanyValues_WhenSet_AndTheDefaultWhenNot()
    {
        var company = ReconThresholds.FromRules("7.5", "250");
        company.Should().Be(new ReconThresholds(7.5m, 250m, "company"));

        ReconThresholds.FromRules(null, null).Should().Be(ReconThresholds.Default);
        ReconThresholds.FromRules("9", null).Should().Be(new ReconThresholds(9m, 0m, "company"));
        ReconThresholds.FromRules(null, "40").Should().Be(new ReconThresholds(5m, 40m, "company"));
    }

    [Theory]
    [InlineData("0.05")]
    [InlineData("101")]
    [InlineData("abc")]
    [InlineData("-1")]
    public void FromRules_FallsBackToTheDefaultPct_ForAValueOutsideTheLegalRange(string pct) =>
        ReconThresholds.FromRules(pct, null).Pct.Should().Be(5m);

    [Theory]
    [InlineData("-5")]
    [InlineData("x")]
    public void FromRules_FallsBackToZero_ForAnIllegalMinimumAmount(string min) =>
        ReconThresholds.FromRules(null, min).MinAmount.Should().Be(0m);
}
