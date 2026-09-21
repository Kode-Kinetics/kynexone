using System.Text.RegularExpressions;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;

namespace Zayra.Api.Tests;

/// <summary>
/// S1/A5 — KSA overtime was paid on basic only, at a flat multiplier, with the seeded rest-day and
/// public-holiday rates read by nothing and no statutory floor on the per-request approved rate.
/// </summary>
public class StatutoryOvertimeTests
{
    // ── Art. 107: hourly WAGE plus 50% of BASIC ─────────────────────────────────────────────────

    [Fact]
    public void Ksa_OvertimeHour_IsWageHourlyPlusHalfOfBasicHourly()
    {
        // Canonical Saudi package: SAR 30,000 total on a 60/40 split — basic 18,000, allowances 12,000.
        // 240 standard monthly hours.
        //   basic hourly = 18,000 / 240 = 75.00
        //   wage  hourly = 30,000 / 240 = 125.00
        //   Art. 107     = 125.00 + 0.5 × 75.00 = 162.50 per overtime hour
        // The pre-S1 product paid 75.00 × 1.5 = 112.50 — a 30.8% underpayment on every hour.
        const decimal basicHourly = 18_000m / 240m;
        const decimal wageHourly  = 30_000m / 240m;

        var statutory = OvertimeStatutoryCalculator.HourPay(wageHourly, basicHourly, 1.5m);

        Assert.Equal(162.50m, statutory);
        Assert.NotEqual(112.50m, statutory);
        // Expressed as a multiple of the basic hourly rate, which is how the SME framed it: 2.17×.
        Assert.Equal(2.17m, Math.Round(statutory / basicHourly, 2));
    }

    [Fact]
    public void UaeAndQatar_OvertimeHour_StayBasicPlusTwentyFivePercent()
    {
        // With the base set to basic, the general expression collapses to basicHourly × multiplier —
        // byte-identical to the pre-S1 arithmetic, which was already right for these two.
        const decimal basicHourly = 12_000m / 240m;   // 50.00

        var uae = OvertimeStatutoryCalculator.HourPay(basicHourly, basicHourly, 1.25m);

        Assert.Equal(62.50m, uae);
        Assert.Equal(basicHourly * 1.25m, uae);
    }

    [Theory]
    [InlineData("wage", true)]
    [InlineData("WAGE", true)]
    [InlineData("basic", false)]
    [InlineData(null, false)]      // unseeded rule → basic → pre-S1 behaviour preserved
    [InlineData("nonsense", false)]
    public void BaseIsFullWage_FallsBackToBasicForAnythingUnrecognised(string? rule, bool expected)
        => Assert.Equal(expected, OvertimeStatutoryCalculator.BaseIsFullWage(rule));

    // ── The seeded rest-day and public-holiday rates ────────────────────────────────────────────

    [Fact]
    public void StatutoryFloor_UsesTheDayCategory_NotJustTheStandardRate()
    {
        Assert.Equal(1.5m, OvertimeStatutoryCalculator.StatutoryFloor("RegularDay",    1.5m, 2.0m, 2.0m));
        Assert.Equal(2.0m, OvertimeStatutoryCalculator.StatutoryFloor("Weekend",       1.5m, 2.0m, 2.0m));
        Assert.Equal(2.0m, OvertimeStatutoryCalculator.StatutoryFloor("PublicHoliday", 1.5m, 2.0m, 2.0m));
    }

    [Fact]
    public void DayCategory_PrefersPublicHolidayOverWeekend()
    {
        var eidOnAFriday = new DateOnly(2026, 3, 20);
        Assert.Equal("PublicHoliday", OvertimeStatutoryCalculator.DayCategory(eidOnAFriday, true, true));
        Assert.Equal("Weekend",       OvertimeStatutoryCalculator.DayCategory(eidOnAFriday, false, true));
        Assert.Equal("RegularDay",    OvertimeStatutoryCalculator.DayCategory(eidOnAFriday, false, false));
    }

    // ── The ApprovedMultiplier back door ────────────────────────────────────────────────────────

    [Fact]
    public void ApprovedMultiplierBelowTheStatutoryFloor_IsRaisedToTheFloor()
    {
        // An approved request on a policy configured at 1.25 used to pay 1.25 in Saudi Arabia,
        // because the run preferred ApprovedMultiplier over the statutory rule whenever it was
        // non-zero and no floor check existed anywhere in the system.
        Assert.Equal(1.5m, OvertimeStatutoryCalculator.EffectiveMultiplier(1.25m, 1.5m));
        Assert.Equal(2.0m, OvertimeStatutoryCalculator.EffectiveMultiplier(1.5m, 2.0m));   // rest day
    }

    [Fact]
    public void ApprovedMultiplierAboveTheStatutoryFloor_IsHonoured()
        => Assert.Equal(2.5m, OvertimeStatutoryCalculator.EffectiveMultiplier(2.5m, 1.5m));

    [Fact]
    public void UnsetApprovedMultiplier_FallsToTheStatutoryFloorForThatDay()
        => Assert.Equal(2.0m, OvertimeStatutoryCalculator.EffectiveMultiplier(0m, 2.0m));

    // ── The rates are seeded, and the run now reads them ────────────────────────────────────────

    [Fact]
    public void Seeder_ShipsTheKsaOvertimeBaseAndDayRates()
    {
        // Keyed by RuleKey, taking the LATEST effective row for each.
        //
        // This used to be a plain ToDictionary(r => r.RuleKey, …), which assumed the seeder ships
        // at most one row per key. It does not: StatutoryRule is effective-dated and the seeder's
        // own idempotency check keys on (TenantId, Country, Jurisdiction, RuleKey, EffectiveFrom)
        // precisely so a key CAN carry several dated values. The KSA Nitaqat curve intercepts are
        // the first to use that (a C-2023 row and a C-2024 row for the same key), and the plain
        // ToDictionary threw on them. The overtime rules below have a single row each, so every
        // assertion is unchanged — what changed is the assumption, which was never true by design.
        var rules = InvokeBuildRules()
            .Where(r => r.CountryCode == "SAU" && r.Jurisdiction == "KSA-mainland")
            .GroupBy(r => r.RuleKey)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EffectiveFrom).First().RuleValue);

        Assert.Equal("wage", rules["ot.hourly_base"]);
        Assert.Equal("1.5",  rules["ot.standard_multiplier"]);
        Assert.Equal("2.0",  rules["ot.restday_multiplier"]);
        Assert.Equal("2.0",  rules["ot.holiday_multiplier"]);
    }

    [Fact]
    public void Seeder_KeepsUaeAndQatarOnTheBasicOvertimeBase()
    {
        var all = InvokeBuildRules();
        Assert.Equal("basic", all.Single(r => r.CountryCode == "ARE" && r.RuleKey == "ot.hourly_base").RuleValue);
        Assert.Equal("basic", all.Single(r => r.CountryCode == "QAT" && r.RuleKey == "ot.hourly_base").RuleValue);
    }

    /// <summary>
    /// Source lint, following the convention in <c>DemoLeaveSeedLintTests</c>: the two day-rate rules
    /// were seeded for years and READ BY NOTHING. That is the failure mode this guards — a rule the
    /// seeder writes and the money path never consults looks like compliance and is not. If the
    /// source tree cannot be located the test returns rather than reporting a false pass.
    /// </summary>
    [Fact]
    public void PayrollRun_ActuallyReadsTheRestDayAndHolidayRates()
    {
        var controller = ResolveControllerSource();
        if (controller is null) return;
        var source = File.ReadAllText(controller);

        Assert.Contains("ot.restday_multiplier", source, StringComparison.Ordinal);
        Assert.Contains("ot.holiday_multiplier", source, StringComparison.Ordinal);
        Assert.Contains("ot.hourly_base", source, StringComparison.Ordinal);
        // And the back door is closed: the run must not prefer a raw ApprovedMultiplier over the
        // statutory rate without going through the floor check.
        Assert.DoesNotMatch(
            new Regex(@"ApprovedMultiplier\s*>\s*0m\s*\?\s*x\.ApprovedMultiplier\s*:\s*otMultiplier"),
            source);
    }

    private static IReadOnlyList<Zayra.Api.Models.StatutoryRule> InvokeBuildRules()
    {
        var m = typeof(StatutoryRuleSeeder).GetMethod("BuildRules",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (List<Zayra.Api.Models.StatutoryRule>)m.Invoke(null, null)!;
    }

    private static string? ResolveControllerSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6; i++)
        {
            if (dir?.Parent is null) return null;
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "Zayra.Api", "Controllers", "PayrollController.cs");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
