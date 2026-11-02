using Zayra.Api.Application.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Owner decision 2026-10-25: a configured end-of-service rate below the Art. 84 floor is honoured once an admin
/// acknowledged it, and the settlement shows what the statute would have paid. A tenant that never acknowledged one
/// is paid exactly as before (the floor applies).
/// </summary>
public class EosbBelowFloorCalculationTests
{
    private static EndOfServiceInput Input(string reason = "Termination") =>
        new(Guid.NewGuid(), Guid.NewGuid(), new SalaryBreakdown(12_000m, 0m, 0m, 0m), new(2014, 1, 1), new(2025, 12, 31), reason, "Indefinite", "SAU");

    private static Task<EndOfServiceResult> Run(EndOfServiceInput i) => new KsaEndOfServiceCalculator(new StubRuleReader()).CalculateAsync(i);

    [Fact]
    public async Task AcknowledgedBelowFloorRates_AreHonoured_AndTheStatutoryFigureIsShownBesideThem()
    {
        var statutory = await Run(Input());
        var low = await Run(Input() with { Policy = new EosbPolicyOverride(10m, 20m, 0, true, true) });

        // 12 years: 5 yrs at 10 days + 7 yrs at 20 days, on a /30 day-rate of the 12,000 wage.
        Assert.Equal(Math.Round(5m * (10m / 30m) * 12_000m, 2) + Math.Round(7m * (20m / 30m) * 12_000m, 2), low.TotalGratuity);
        Assert.True(low.TotalGratuity < statutory.TotalGratuity);
        Assert.Equal(statutory.TotalGratuity, low.StatutoryTotalGratuity);
        Assert.Contains(low.Breakdown, b => b.Amount == 0m
            && b.Label == $"Configured below the statutory minimum (Art. 84): statutory would be {statutory.TotalGratuity:N2}");
        Assert.Contains(low.Notices, n => n.Contains("[ACK-KSA]") && n.Contains("claim the difference"));
    }

    [Fact]
    public async Task UnacknowledgedBelowFloorRates_StillPayTheFloor_ExactlyAsBefore()
    {
        var statutory = await Run(Input());
        var low = await Run(Input() with { Policy = new EosbPolicyOverride(10m, 20m, 0) });

        Assert.Equal(statutory.TotalGratuity, low.TotalGratuity);
        Assert.Null(low.StatutoryTotalGratuity);
        Assert.DoesNotContain(low.Breakdown, b => b.Label.Contains("Configured below"));
        Assert.Contains(low.Notices, n => n.Contains("[CERT-KSA]") && n.Contains("NOT been acknowledged"));
    }

    [Fact]
    public async Task OnlyTheAcknowledgedTierIsHonoured()
    {
        var statutory = await Run(Input());
        var mixed = await Run(Input() with { Policy = new EosbPolicyOverride(10m, 20m, 0, true, false) });

        // tier 1 honoured at 10 days; tier 2 (not acknowledged) paid at the 30-day floor
        Assert.Equal(Math.Round(5m * (10m / 30m) * 12_000m, 2) + Math.Round(7m * 1m * 12_000m, 2), mixed.TotalGratuity);
        Assert.Equal(statutory.TotalGratuity, mixed.StatutoryTotalGratuity);
    }

    [Fact]
    public async Task RatesAtOrAboveTheFloor_NeverShowTheBelowMinimumLine_EvenIfMarkedAcknowledged()
    {
        var r = await Run(Input() with { Policy = new EosbPolicyOverride(15m, 30m, 0, true, true) });
        Assert.Null(r.StatutoryTotalGratuity);
        Assert.DoesNotContain(r.Breakdown, b => b.Label.Contains("Configured below"));
        var enhanced = await Run(Input() with { Policy = new EosbPolicyOverride(21m, 30m, 0, true, true) });
        Assert.Null(enhanced.StatutoryTotalGratuity);
    }

    [Fact]
    public async Task TheStatutoryComparison_AppliesTheSameResignationReduction()
    {
        // Resignation after 12 years: ⅔/full band applies to both figures, so the comparison is like for like.
        var statutory = await Run(Input("Resignation"));
        var low = await Run(Input("Resignation") with { Policy = new EosbPolicyOverride(10m, 20m, 0, true, true) });
        Assert.Equal(statutory.TotalGratuity, low.StatutoryTotalGratuity);
        Assert.True(low.TotalGratuity < statutory.TotalGratuity);
    }

    [Fact]
    public void AcknowledgementIsMatchedToTheExactRate_NotJustTheCode()
    {
        var json = Zayra.Api.Application.Compliance.FloorAckStore.Serialize([new("eosb_tier1_below_minimum", "eosbYears1To5Rate", 10m, 15m, "Labor Law Art. 84", "why not", "Zed", null, DateTime.UtcNow)]);
        Assert.True(KsaEosbFloorChecks.IsAcknowledged(json, KsaEosbFloorChecks.Tier1, 10m));
        Assert.False(KsaEosbFloorChecks.IsAcknowledged(json, KsaEosbFloorChecks.Tier1, 9m));
        Assert.False(KsaEosbFloorChecks.IsAcknowledged(json, KsaEosbFloorChecks.Tier2, 10m));
        Assert.False(KsaEosbFloorChecks.IsAcknowledged(null, KsaEosbFloorChecks.Tier1, 10m));
    }
}
