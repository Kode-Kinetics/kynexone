using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// KSA Nitaqat: banding, weighted headcount, scenario arithmetic, refusals and isolation.
///
/// Band boundaries are tested AT the exact threshold and one tick either side, because
/// an off-by-one at a band edge is the difference between an establishment believing it
/// can issue work visas and discovering at the Qiwa counter that it cannot.
/// </summary>
public class KsaNitaqatTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid CompanyA = Guid.NewGuid();
    private static readonly Guid CompanyB = Guid.NewGuid();

    private static readonly DateOnly AsOf = new(2026, 6, 1);
    private static readonly DateTime Eff = new(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ═══════════════════════════════════════════════════════════════════════════
    //  1. Band boundaries — AT the threshold, and one tick either side
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Floors are INCLUSIVE: an establishment sitting exactly on the Medium Green floor
    /// is Medium Green, not Low Green. MHRSD publishes "not less than", so the boundary
    /// belongs to the higher band.
    /// </summary>
    [Theory]
    // exactly on each floor → that band
    [InlineData(10.00, NitaqatBands.LowGreen)]
    [InlineData(18.00, NitaqatBands.MediumGreen)]
    [InlineData(26.00, NitaqatBands.HighGreen)]
    [InlineData(36.00, NitaqatBands.Platinum)]
    // one tick below each floor → the band beneath
    [InlineData(9.9999, NitaqatBands.Red)]
    [InlineData(17.9999, NitaqatBands.LowGreen)]
    [InlineData(25.9999, NitaqatBands.MediumGreen)]
    [InlineData(35.9999, NitaqatBands.HighGreen)]
    // one tick above each floor → still that band
    [InlineData(10.0001, NitaqatBands.LowGreen)]
    [InlineData(36.0001, NitaqatBands.Platinum)]
    // the extremes
    [InlineData(0.00, NitaqatBands.Red)]
    [InlineData(100.00, NitaqatBands.Platinum)]
    public void ResolveBand_IsInclusiveAtEveryFloor(double achieved, string expected)
    {
        var grid = Thresholds("RETAIL", "SmallC", low: 10m, medium: 18m, high: 26m, platinum: 36m);

        NitaqatCalculationService.ResolveBand(grid, (decimal)achieved).Should().Be(expected);
    }

    [Fact]
    public void ResolveBand_BelowEveryPublishedFloor_IsRed_NotUnknown()
    {
        // Red is the residual, never a stored row. An establishment below the lowest
        // published floor must land somewhere concrete, because "no band" and "Red"
        // have completely different consequences.
        var grid = Thresholds("RETAIL", "Giant", low: 20m, medium: 28m, high: 37m, platinum: 48m);

        NitaqatCalculationService.ResolveBand(grid, 19.9999m).Should().Be(NitaqatBands.Red);
    }

    [Fact]
    public void BandRanking_IsMonotonicAndYellowIsGone()
    {
        // Yellow was abolished in the 2021 balanced-Nitaqat revision. The old
        // KsaNationalizationTracker still calls its AtRisk bucket "Yellow" in a comment;
        // this model must not reintroduce it.
        NitaqatBands.Ascending.Should().ContainInOrder(
            NitaqatBands.Red, NitaqatBands.LowGreen, NitaqatBands.MediumGreen,
            NitaqatBands.HighGreen, NitaqatBands.Platinum);
        NitaqatBands.Ascending.Should().NotContain("Yellow");
        NitaqatBands.RankOf("Yellow").Should().Be(-1);

        NitaqatBands.RestrictsServices(NitaqatBands.Red).Should().BeTrue();
        NitaqatBands.RestrictsServices(NitaqatBands.LowGreen).Should().BeTrue();
        NitaqatBands.RestrictsServices(NitaqatBands.MediumGreen).Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  2. Weighted headcount — hand-worked examples
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// HAND-WORKED. Roster:
    ///   6 × full-time Saudi above the wage floor   → 6.0 Saudi units,  6.0 total units
    ///   1 × Saudi with a disability (4×)           → 4.0 Saudi units,  1.0 total units
    ///   2 × part-time Saudi (0.5)                  → 1.0 Saudi units,  1.0 total units
    ///   1 × Saudi at SAR 3,500 (half wage floor)   → 0.5 Saudi units,  1.0 total units
    ///   1 × Saudi at SAR 2,000 (below wage floor)  → 0.0 Saudi units,  1.0 total units
    ///  10 × non-Saudi                              → 0.0 Saudi units, 10.0 total units
    ///                                       TOTALS = 11.5 Saudi units, 20.0 total units
    ///  ⇒ 57.5%.  Plain headcount would say 11 Saudi / 21 heads = 52.38%.
    ///
    /// The two answers differ by more than five percentage points on a twenty-person
    /// establishment. That gap is the entire reason NationalizationInput's two ints
    /// cannot carry this.
    /// </summary>
    [Fact]
    public async Task WeightedHeadcount_MatchesTheHandWorkedExample_AndDivergesFromPlainHeadcount()
    {
        await using var db = NewDb(nameof(WeightedHeadcount_MatchesTheHandWorkedExample_AndDivergesFromPlainHeadcount));
        SeedReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        SeedProfile(db, TenantA, CompanyA, "RETAIL");
        SeedGrid(db, "RETAIL", "SmallB", low: 10m, medium: 18m, high: 26m, platinum: 36m);

        var ids = new List<int>();
        for (var i = 0; i < 6; i++) ids.Add(AddEmployee(db, TenantA, CompanyA, "Saudi", "FullTime", 8000m));
        var disabled = AddEmployee(db, TenantA, CompanyA, "Saudi", "FullTime", 9000m);
        for (var i = 0; i < 2; i++) ids.Add(AddEmployee(db, TenantA, CompanyA, "Saudi", "Part-Time", 8000m));
        AddEmployee(db, TenantA, CompanyA, "Saudi", "FullTime", 3500m);   // half floor
        AddEmployee(db, TenantA, CompanyA, "Saudi", "FullTime", 2000m);   // below floor
        for (var i = 0; i < 10; i++) AddEmployee(db, TenantA, CompanyA, "Indian", "FullTime", 5000m);

        db.NitaqatEmployeeWeightOverrides.Add(new NitaqatEmployeeWeightOverride
        {
            TenantId = TenantA, CompanyId = CompanyA, EmployeeId = disabled,
            Category = NitaqatWeightCategories.Disability,
            Justification = "Medical certificate on file, verified by HR 2026-01-14.",
        });
        await db.SaveChangesAsync();

        var result = await Svc(db).GetStandingAsync(TenantA, CompanyA, AsOf);

        result.Ok.Should().BeTrue(result.Refusal?.Message);
        var s = result.Standing!;

        s.SaudiWeighted.Should().Be(11.5m);
        s.TotalWeighted.Should().Be(20.0m);
        s.AchievedPercent.Should().Be(57.50m);

        // The unweighted numbers are also reported, so the weighting can be audited.
        s.RawSaudiHeadcount.Should().Be(11);
        s.RawTotalHeadcount.Should().Be(21);

        // And they are genuinely different from the weighted answer.
        var plain = decimal.Round(11m / 21m * 100m, 2);
        plain.Should().Be(52.38m);
        s.AchievedPercent.Should().NotBe(plain);

        // Every line of the working is returned.
        s.Breakdown.Sum(b => b.NumeratorTotal).Should().Be(11.5m);
        s.Breakdown.Sum(b => b.DenominatorTotal).Should().Be(20.0m);
        s.Breakdown.Sum(b => b.Heads).Should().Be(21);
    }

    [Fact]
    public void WageFloorCategories_StepAtTheExactBoundaries()
    {
        var none = new Dictionary<int, string>();

        // Floors: full 4000, half 3000.
        NitaqatCalculationService.ResolveCategory(1, GosiClassifications.Saudi, 4000m, 4000m, 3000m, none)
            .Should().Be(NitaqatWeightCategories.Standard, "exactly on the full floor counts as a full unit");
        NitaqatCalculationService.ResolveCategory(1, GosiClassifications.Saudi, 3999.99m, 4000m, 3000m, none)
            .Should().Be(NitaqatWeightCategories.HalfWageFloor);
        NitaqatCalculationService.ResolveCategory(1, GosiClassifications.Saudi, 3000m, 4000m, 3000m, none)
            .Should().Be(NitaqatWeightCategories.HalfWageFloor, "exactly on the half floor counts as half a unit");
        NitaqatCalculationService.ResolveCategory(1, GosiClassifications.Saudi, 2999.99m, 4000m, 3000m, none)
            .Should().Be(NitaqatWeightCategories.BelowWageFloor);

        // The wage floor is a Saudi rule; it must not silently reclassify an expatriate.
        NitaqatCalculationService.ResolveCategory(1, GosiClassifications.NonSaudi, 1000m, 4000m, 3000m, none)
            .Should().Be(NitaqatWeightCategories.Standard);

        // An unknown wage is not a reason to stop counting somebody.
        NitaqatCalculationService.ResolveCategory(1, GosiClassifications.Saudi, null, 4000m, 3000m, none)
            .Should().Be(NitaqatWeightCategories.Standard);
    }

    [Fact]
    public void EmploymentTypeNormalisation_SurvivesTheRepositorysInconsistentSpellings()
    {
        // Seeders in this repo write "FullTime", "Full-Time", "Full-time", "PART_TIME"…
        foreach (var t in new[] { "PartTime", "Part-Time", "part time", "PART_TIME", "part-time" })
            NitaqatCalculationService.NormaliseBasis(t).Should().Be(NitaqatCountBasis.PartTime, t);

        foreach (var t in new[] { "FullTime", "Full-Time", "Contract", "Temporary", "", null })
            NitaqatCalculationService.NormaliseBasis(t).Should().Be(NitaqatCountBasis.FullTime, t ?? "null");
    }

    [Fact]
    public async Task GccNationals_AreCountedConservativelyAsDenominatorOnly()
    {
        // The safe direction. If MHRSD in fact excludes GCC nationals from both sides,
        // this UNDER-states the customer's Saudization, which causes over-hiring of
        // Saudis. Counting them in the numerator would OVER-state it, which produces a
        // confident green against a real Red — the failure that freezes a visa quota.
        await using var db = NewDb(nameof(GccNationals_AreCountedConservativelyAsDenominatorOnly));
        SeedReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        SeedProfile(db, TenantA, CompanyA, "RETAIL");
        SeedGrid(db, "RETAIL", "SmallB", 10m, 18m, 26m, 36m);

        for (var i = 0; i < 5; i++) AddEmployee(db, TenantA, CompanyA, "Saudi", "FullTime", 8000m);
        for (var i = 0; i < 5; i++) AddEmployee(db, TenantA, CompanyA, "Bahraini", "FullTime", 8000m);
        await db.SaveChangesAsync();

        var s = (await Svc(db).GetStandingAsync(TenantA, CompanyA, AsOf)).Standing!;

        s.SaudiWeighted.Should().Be(5m);
        s.TotalWeighted.Should().Be(10m, "GCC nationals sit in the denominator under the conservative default");
        s.AchievedPercent.Should().Be(50m);

        // And the caller is told this is not a verified rule.
        s.UnverifiedInputs.Should().Contain(x => x.Contains("GCC"));
        s.AllInputsVerified.Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  3. Fail loud — never a silent default
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UnconfiguredSector_RefusesWithANamedReason_AndNeverGuessesABand()
    {
        await using var db = NewDb(nameof(UnconfiguredSector_RefusesWithANamedReason_AndNeverGuessesABand));
        SeedReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        // Deliberately NO NitaqatEstablishmentProfile.
        for (var i = 0; i < 20; i++) AddEmployee(db, TenantA, CompanyA, "Saudi", "FullTime", 8000m);
        await db.SaveChangesAsync();

        var result = await Svc(db).GetStandingAsync(TenantA, CompanyA, AsOf);

        result.Ok.Should().BeFalse();
        result.Standing.Should().BeNull("a wrong band is worse than no band — a customer acts on it");
        result.Refusal!.Reason.Should().Be(NitaqatRefusalReasons.ActivityNotSet);
        result.Refusal.Message.Should().Contain("economic activity");
        result.Refusal.Remedy.Should().NotBeNullOrWhiteSpace("a refusal without a remedy is a dead end");

        // Note this establishment is 100% Saudi — the most flattering possible position.
        // It STILL gets no band, because the band depends on a target we do not have.
    }

    [Fact]
    public async Task ActivityWithNoPublishedThresholds_RefusesRatherThanFallingBackToAnyGrid()
    {
        // The real MHRSD activities ship catalogued with NO thresholds. An establishment
        // mapped to one must refuse, not silently borrow the illustrative grid.
        await using var db = NewDb(nameof(ActivityWithNoPublishedThresholds_RefusesRatherThanFallingBackToAnyGrid));
        SeedReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        SeedProfile(db, TenantA, CompanyA, "CONSTRUCTION");
        SeedGrid(db, "RETAIL", "SmallB", 10m, 18m, 26m, 36m);  // a DIFFERENT activity's grid exists
        for (var i = 0; i < 12; i++) AddEmployee(db, TenantA, CompanyA, "Saudi", "FullTime", 8000m);
        await db.SaveChangesAsync();

        var result = await Svc(db).GetStandingAsync(TenantA, CompanyA, AsOf);

        result.Ok.Should().BeFalse();
        result.Refusal!.Reason.Should().Be(NitaqatRefusalReasons.ThresholdsMissing);
        result.Refusal.Message.Should().Contain("CONSTRUCTION");
    }

    [Fact]
    public async Task ThresholdsForTheWrongSizeTier_DoNotLeakAcross()
    {
        // The matrix is two-dimensional. A grid published for Giant must not band a Small B.
        await using var db = NewDb(nameof(ThresholdsForTheWrongSizeTier_DoNotLeakAcross));
        SeedReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        SeedProfile(db, TenantA, CompanyA, "RETAIL");
        SeedGrid(db, "RETAIL", "Giant", 20m, 28m, 37m, 48m);   // wrong tier for a 12-person shop
        for (var i = 0; i < 12; i++) AddEmployee(db, TenantA, CompanyA, "Saudi", "FullTime", 8000m);
        await db.SaveChangesAsync();

        var result = await Svc(db).GetStandingAsync(TenantA, CompanyA, AsOf);

        result.Ok.Should().BeFalse();
        result.Refusal!.Reason.Should().Be(NitaqatRefusalReasons.ThresholdsMissing);
    }

    [Fact]
    public async Task NonSaudiCompany_RefusesRatherThanComputingASaudiBandForADubaiEntity()
    {
        await using var db = NewDb(nameof(NonSaudiCompany_RefusesRatherThanComputingASaudiBandForADubaiEntity));
        SeedReference(db);
        SeedCompany(db, TenantA, CompanyA, "AE");
        SeedProfile(db, TenantA, CompanyA, "RETAIL");
        SeedGrid(db, "RETAIL", "SmallB", 10m, 18m, 26m, 36m);
        for (var i = 0; i < 12; i++) AddEmployee(db, TenantA, CompanyA, "Saudi", "FullTime", 8000m);
        await db.SaveChangesAsync();

        var result = await Svc(db).GetStandingAsync(TenantA, CompanyA, AsOf);

        result.Ok.Should().BeFalse();
        result.Refusal!.Reason.Should().Be(NitaqatRefusalReasons.NotKsa);
    }

    [Fact]
    public async Task EmptyEstablishment_RefusesRatherThanReportingZeroPercentRed()
    {
        // 0/0 is not 0%. An establishment with no workforce has no ratio, and reporting
        // "Red, 0%" would be a fabricated compliance failure.
        await using var db = NewDb(nameof(EmptyEstablishment_RefusesRatherThanReportingZeroPercentRed));
        SeedReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        SeedProfile(db, TenantA, CompanyA, "RETAIL");
        SeedGrid(db, "RETAIL", "SmallB", 10m, 18m, 26m, 36m);
        await db.SaveChangesAsync();

        var result = await Svc(db).GetStandingAsync(TenantA, CompanyA, AsOf);

        result.Ok.Should().BeFalse();
        result.Refusal!.Reason.Should().Be(NitaqatRefusalReasons.NoWorkforce);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  4. Scenario arithmetic, pinned on a fixture
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// FIXTURE: 12 Saudis + 38 non-Saudis = 50 units, 24.00% Saudization.
    /// Tier MediumA (50–99). Grid: Low 12 / Medium 20 / High 29 / Platinum 40.
    /// ⇒ band = MediumGreen (24 ≥ 20, &lt; 29).
    ///
    /// HAND-WORKED SCENARIOS, all with weights of 1.0 on both sides:
    ///  • To HighGreen (29%): n ≥ (0.29·50 − 12)/(1 − 0.29) = 2.5/0.71 = 3.521 → 4 hires.
    ///    Check: (12+4)/(50+4) = 16/54 = 29.63% ≥ 29%. And 3 hires gives 15/53 = 28.30% < 29%.
    ///  • Expat headroom in MediumGreen (floor 20%): m ≤ 12/0.20 − 50 = 60 − 50 = 10.
    ///    Check: 12/60 = 20.00% (still in), 12/61 = 19.67% (out).
    ///  • Saudi leavers before breaching 20%: k ≤ (12 − 0.20·50)/(1 − 0.20) = 2/0.8 = 2.5 → 2.
    ///    Check: (12−2)/(50−2) = 10/48 = 20.83% (in), (12−3)/(50−3) = 9/47 = 19.15% (out).
    /// </summary>
    [Fact]
    public async Task Scenarios_AreArithmeticallyExactOnTheFixture()
    {
        await using var db = NewDb(nameof(Scenarios_AreArithmeticallyExactOnTheFixture));
        SeedReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        SeedProfile(db, TenantA, CompanyA, "RETAIL");
        SeedGrid(db, "RETAIL", "MediumA", low: 12m, medium: 20m, high: 29m, platinum: 40m);

        for (var i = 0; i < 12; i++) AddEmployee(db, TenantA, CompanyA, "Saudi", "FullTime", 8000m);
        for (var i = 0; i < 38; i++) AddEmployee(db, TenantA, CompanyA, "Egyptian", "FullTime", 5000m);
        await db.SaveChangesAsync();

        var s = (await Svc(db).GetStandingAsync(TenantA, CompanyA, AsOf)).Standing!;

        s.SizeTierCode.Should().Be("MediumA");
        s.TotalWeighted.Should().Be(50m);
        s.AchievedPercent.Should().Be(24.00m);
        s.Band.Should().Be(NitaqatBands.MediumGreen);
        s.CurrentBandFloorPercent.Should().Be(20m);

        s.NextBandUp!.Band.Should().Be(NitaqatBands.HighGreen);
        s.NextBandUp.RequiredPercent.Should().Be(29m);
        s.NextBandUp.PercentGap.Should().Be(5.00m);
        s.Scenario.SaudiHiresToNextBand.Should().Be(4);

        s.Scenario.ExpatHiresBeforeDowngrade.Should().Be(10);
        s.Scenario.SaudiLeaversBeforeDowngrade.Should().Be(2);
        s.Scenario.BandBelow.Should().Be(NitaqatBands.LowGreen);

        s.RestrictsServices.Should().BeFalse();
        s.ConsequenceSummary.Should().Contain("Green");
    }

    [Fact]
    public void SaudiHiresToReach_AgreesWithItsOwnAnswerWhenTheHiresAreApplied()
    {
        // Property check on the hire maths: the returned n must clear the target and
        // n-1 must not. Run across a spread of positions so an off-by-one cannot hide.
        foreach (var (saudi, total, target) in new[]
                 {
                     (12m, 50m, 29m), (1m, 100m, 40m), (49m, 100m, 50m),
                     (0m, 10m, 25m), (33m, 100m, 33.3m), (7m, 13m, 60m),
                 })
        {
            var (n, infeasible) = NitaqatCalculationService.SaudiHiresToReach(saudi, total, target, 1m, 1m);
            infeasible.Should().BeNull();
            n.Should().NotBeNull();

            var after = NitaqatCalculationService.Percent(saudi + n!.Value, total + n.Value);
            after.Should().BeGreaterThanOrEqualTo(target, $"n={n} must reach {target}%");

            if (n > 0)
            {
                var justShort = NitaqatCalculationService.Percent(saudi + n.Value - 1, total + n.Value - 1);
                justShort.Should().BeLessThan(target, $"n-1={n - 1} must NOT reach {target}%");
            }
        }
    }

    [Fact]
    public void SaudiHiresToReach_IsZeroWhenAlreadyThere_AndInfeasibleAtOneHundredPercent()
    {
        NitaqatCalculationService.SaudiHiresToReach(30m, 100m, 25m, 1m, 1m)
            .Should().Be(((int?)0, (string?)null));

        // A 100% target cannot be reached by hiring while any expatriate remains: each
        // Saudi hire adds exactly as much to the denominator as to the numerator.
        var (hires, infeasible) = NitaqatCalculationService.SaudiHiresToReach(30m, 100m, 100m, 1m, 1m);
        hires.Should().BeNull();
        infeasible.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ExpatHeadroom_IsZeroWhenAlreadyOnTheFloor_AndUnboundedInRed()
    {
        // Exactly on the floor: one more expatriate breaches it.
        NitaqatCalculationService.ExpatHiresBeforeBreaching(20m, 100m, 20m, 1m).Should().Be(0);
        // Comfortably above: 25/100 at a 20% floor tolerates 25 more heads (25/125 = 20%).
        NitaqatCalculationService.ExpatHiresBeforeBreaching(25m, 100m, 20m, 1m).Should().Be(25);
        // Below the floor already: no headroom, not a negative number.
        NitaqatCalculationService.ExpatHiresBeforeBreaching(10m, 100m, 20m, 1m).Should().Be(0);
        // Red has no floor to breach.
        NitaqatCalculationService.ExpatHiresBeforeBreaching(5m, 100m, 0m, 1m).Should().BeNull();
    }

    [Fact]
    public async Task HireImpact_WarnsWhenAPendingExpatriateHireCostsABand()
    {
        // 12/50 = 24%, MediumGreen floor 20%, headroom 10 expatriates.
        await using var db = NewDb(nameof(HireImpact_WarnsWhenAPendingExpatriateHireCostsABand));
        SeedReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        SeedProfile(db, TenantA, CompanyA, "RETAIL");
        SeedGrid(db, "RETAIL", "MediumA", 12m, 20m, 29m, 40m);
        for (var i = 0; i < 12; i++) AddEmployee(db, TenantA, CompanyA, "Saudi", "FullTime", 8000m);
        for (var i = 0; i < 38; i++) AddEmployee(db, TenantA, CompanyA, "Egyptian", "FullTime", 5000m);
        await db.SaveChangesAsync();

        var svc = Svc(db);

        var safe = await svc.GetHireImpactAsync(TenantA, CompanyA, "Egyptian", 10, AsOf);
        safe.Ok.Should().BeTrue();
        safe.Classification.Should().Be(GosiClassifications.NonSaudi);
        safe.ProjectedPercent.Should().Be(20.00m);
        safe.BandChanges.Should().BeFalse("ten expatriates lands exactly on the floor, which is still in-band");

        var costly = await svc.GetHireImpactAsync(TenantA, CompanyA, "Egyptian", 11, AsOf);
        costly.BandChanges.Should().BeTrue();
        costly.BandImproves.Should().BeFalse();
        costly.ProjectedBand.Should().Be(NitaqatBands.LowGreen);
        costly.Summary.Should().Contain("WARNING");
        costly.Summary.Should().Contain("Iqama transfer", "a band change must state what it costs");

        var good = await svc.GetHireImpactAsync(TenantA, CompanyA, "Saudi", 4, AsOf);
        good.BandChanges.Should().BeTrue();
        good.BandImproves.Should().BeTrue();
        good.ProjectedBand.Should().Be(NitaqatBands.HighGreen);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  5. Tenant and company isolation
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AnotherTenantsEmployees_NeverEnterTheCount()
    {
        await using var db = NewDb(nameof(AnotherTenantsEmployees_NeverEnterTheCount));
        SeedReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        SeedProfile(db, TenantA, CompanyA, "RETAIL");
        SeedGrid(db, "RETAIL", "SmallB", 10m, 18m, 26m, 36m);

        // Tenant A: 5 Saudi + 5 expat = 50%.
        for (var i = 0; i < 5; i++) AddEmployee(db, TenantA, CompanyA, "Saudi", "FullTime", 8000m);
        for (var i = 0; i < 5; i++) AddEmployee(db, TenantA, CompanyA, "Indian", "FullTime", 5000m);
        // Tenant B, same company GUID reused deliberately: 100 expatriates that must not leak.
        for (var i = 0; i < 100; i++) AddEmployee(db, TenantB, CompanyA, "Indian", "FullTime", 5000m);
        await db.SaveChangesAsync();

        var s = (await Svc(db).GetStandingAsync(TenantA, CompanyA, AsOf)).Standing!;

        s.TotalWeighted.Should().Be(10m, "tenant B's 100 expatriates must not appear in tenant A's denominator");
        s.AchievedPercent.Should().Be(50m);
    }

    [Fact]
    public async Task AnotherCompanysEmployees_NeverEnterTheCount()
    {
        // Nitaqat bands an ESTABLISHMENT. A group tenant with a compliant Riyadh entity
        // and a non-compliant Jeddah entity has two bands, not one average.
        await using var db = NewDb(nameof(AnotherCompanysEmployees_NeverEnterTheCount));
        SeedReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        SeedCompany(db, TenantA, CompanyB, "SA");
        SeedProfile(db, TenantA, CompanyA, "RETAIL");
        SeedProfile(db, TenantA, CompanyB, "RETAIL");
        SeedGrid(db, "RETAIL", "SmallB", 10m, 18m, 26m, 36m);

        for (var i = 0; i < 9; i++) AddEmployee(db, TenantA, CompanyA, "Saudi", "FullTime", 8000m);
        for (var i = 0; i < 1; i++) AddEmployee(db, TenantA, CompanyA, "Indian", "FullTime", 5000m);
        for (var i = 0; i < 1; i++) AddEmployee(db, TenantA, CompanyB, "Saudi", "FullTime", 8000m);
        for (var i = 0; i < 9; i++) AddEmployee(db, TenantA, CompanyB, "Indian", "FullTime", 5000m);
        await db.SaveChangesAsync();

        var svc = Svc(db);
        var a = (await svc.GetStandingAsync(TenantA, CompanyA, AsOf)).Standing!;
        var b = (await svc.GetStandingAsync(TenantA, CompanyB, AsOf)).Standing!;

        a.AchievedPercent.Should().Be(90m);
        a.Band.Should().Be(NitaqatBands.Platinum);
        b.AchievedPercent.Should().Be(10m);
        b.Band.Should().Be(NitaqatBands.LowGreen);
        b.RestrictsServices.Should().BeTrue();
    }

    [Fact]
    public async Task ATenantsThresholdOverride_BeatsThePlatformDefault_AndOnlyForThatTenant()
    {
        await using var db = NewDb(nameof(ATenantsThresholdOverride_BeatsThePlatformDefault_AndOnlyForThatTenant));
        SeedReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        SeedCompany(db, TenantB, CompanyB, "SA");
        SeedProfile(db, TenantA, CompanyA, "RETAIL");
        SeedProfile(db, TenantB, CompanyB, "RETAIL");
        SeedGrid(db, "RETAIL", "SmallB", low: 10m, medium: 18m, high: 26m, platinum: 36m);

        // Tenant A has loaded the real MHRSD figure: Medium Green is actually 45%.
        db.NitaqatBandThresholds.Add(new NitaqatBandThreshold
        {
            TenantId = TenantA, ActivityCode = "RETAIL", SizeTierCode = "SmallB",
            Band = NitaqatBands.MediumGreen, BandRank = NitaqatBands.RankOf(NitaqatBands.MediumGreen),
            MinSaudizationPercent = 45m, EffectiveFrom = Eff,
            SourceNote = "MHRSD Nitaqat table, Retail / Small B, checked 2026-05-02.", IsVerified = true,
        });

        foreach (var (t, c) in new[] { (TenantA, CompanyA), (TenantB, CompanyB) })
        {
            for (var i = 0; i < 4; i++) AddEmployee(db, t, c, "Saudi", "FullTime", 8000m);
            for (var i = 0; i < 16; i++) AddEmployee(db, t, c, "Indian", "FullTime", 5000m);
        }
        await db.SaveChangesAsync();

        var svc = Svc(db);
        var a = (await svc.GetStandingAsync(TenantA, CompanyA, AsOf)).Standing!;
        var b = (await svc.GetStandingAsync(TenantB, CompanyB, AsOf)).Standing!;

        a.AchievedPercent.Should().Be(20m);
        b.AchievedPercent.Should().Be(20m);

        a.Band.Should().Be(NitaqatBands.LowGreen, "tenant A's own 45% Medium Green floor applies");
        b.Band.Should().Be(NitaqatBands.MediumGreen, "tenant B still sees the 18% platform default");
    }

    [Fact]
    public async Task VerificationStatus_IsReportedRatherThanAssumed()
    {
        await using var db = NewDb(nameof(VerificationStatus_IsReportedRatherThanAssumed));
        SeedReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        SeedProfile(db, TenantA, CompanyA, "RETAIL");
        SeedGrid(db, "RETAIL", "SmallB", 10m, 18m, 26m, 36m, verified: false);
        for (var i = 0; i < 5; i++) AddEmployee(db, TenantA, CompanyA, "Saudi", "FullTime", 8000m);
        for (var i = 0; i < 5; i++) AddEmployee(db, TenantA, CompanyA, "Indian", "FullTime", 5000m);
        await db.SaveChangesAsync();

        var s = (await Svc(db).GetStandingAsync(TenantA, CompanyA, AsOf)).Standing!;

        s.AllInputsVerified.Should().BeFalse();
        s.UnverifiedInputs.Should().NotBeEmpty(
            "an unverified threshold must be named to the user, not buried in a code comment");
    }

    [Fact]
    public async Task WhenQiwaDisagrees_TheProductSaysSoRatherThanQuietlyWinning()
    {
        await using var db = NewDb(nameof(WhenQiwaDisagrees_TheProductSaysSoRatherThanQuietlyWinning));
        SeedReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        SeedProfile(db, TenantA, CompanyA, "RETAIL", qiwaBand: NitaqatBands.Red);
        SeedGrid(db, "RETAIL", "SmallB", 10m, 18m, 26m, 36m);
        for (var i = 0; i < 9; i++) AddEmployee(db, TenantA, CompanyA, "Saudi", "FullTime", 8000m);
        AddEmployee(db, TenantA, CompanyA, "Indian", "FullTime", 5000m);
        await db.SaveChangesAsync();

        var s = (await Svc(db).GetStandingAsync(TenantA, CompanyA, AsOf)).Standing!;

        s.Band.Should().Be(NitaqatBands.Platinum);
        s.QiwaReportedBand.Should().Be(NitaqatBands.Red);
        s.DisagreesWithQiwa.Should().BeTrue("MHRSD computes from its own register and is authoritative");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  6. Trend
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Trend_AccumulatesOnePointPerDay_AndIsIdempotentWithinADay()
    {
        await using var db = NewDb(nameof(Trend_AccumulatesOnePointPerDay_AndIsIdempotentWithinADay));
        SeedReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        SeedProfile(db, TenantA, CompanyA, "RETAIL");
        SeedGrid(db, "RETAIL", "SmallB", 10m, 18m, 26m, 36m);
        for (var i = 0; i < 5; i++) AddEmployee(db, TenantA, CompanyA, "Saudi", "FullTime", 8000m);
        for (var i = 0; i < 5; i++) AddEmployee(db, TenantA, CompanyA, "Indian", "FullTime", 5000m);
        await db.SaveChangesAsync();

        var svc = Svc(db);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        await svc.GetStandingAndRecordAsync(TenantA, CompanyA, today);
        await svc.GetStandingAndRecordAsync(TenantA, CompanyA, today);
        await svc.GetStandingAndRecordAsync(TenantA, CompanyA, today);

        var stored = await db.NitaqatStandingSnapshots
            .Where(s => s.TenantId == TenantA && s.CompanyId == CompanyA).ToListAsync();

        stored.Should().HaveCount(1, "three reads on one day are one trend point, not three");
        stored[0].AchievedPercent.Should().Be(50m);
        stored[0].SaudiWeighted.Should().Be(5m);
    }

    [Fact]
    public async Task Trend_WarnsBeforeADowngrade_NotAfterIt()
    {
        await using var db = NewDb(nameof(Trend_WarnsBeforeADowngrade_NotAfterIt));
        SeedReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        SeedProfile(db, TenantA, CompanyA, "RETAIL");
        SeedGrid(db, "RETAIL", "SmallB", 10m, 18m, 26m, 36m);

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        // Was 30% (HighGreen), now 27% — still HighGreen (floor 26), but the same
        // 3-point slide again lands at 24%, below the floor.
        foreach (var (offset, pct) in new[] { (60, 30m), (0, 27m) })
        {
            db.NitaqatStandingSnapshots.Add(new NitaqatStandingSnapshot
            {
                TenantId = TenantA, CompanyId = CompanyA,
                AsOfDate = today.AddDays(-offset),
                ActivityCode = "RETAIL", SizeTierCode = "SmallB",
                SaudiWeighted = pct, TotalWeighted = 100m, AchievedPercent = pct,
                Band = NitaqatBands.HighGreen, BandRank = NitaqatBands.RankOf(NitaqatBands.HighGreen),
                RawSaudiHeadcount = (int)pct, RawTotalHeadcount = 100,
            });
        }
        await db.SaveChangesAsync();

        var trend = await Svc(db).GetTrendAsync(TenantA, CompanyA, 90);

        trend.Ok.Should().BeTrue();
        trend.Points.Should().HaveCount(2);
        trend.ChangePercentagePoints.Should().Be(-3.00m);
        trend.Direction.Should().Be("Declining");
        trend.ProjectedBandWarning.Should().NotBeNullOrWhiteSpace(
            "the point of a trend is to see the downgrade coming");
        trend.ProjectedBandWarning.Should().Contain("HighGreen");
    }

    [Fact]
    public async Task Trend_WithASinglePoint_ReportsNoDirectionRatherThanInventingOne()
    {
        await using var db = NewDb(nameof(Trend_WithASinglePoint_ReportsNoDirectionRatherThanInventingOne));
        db.NitaqatStandingSnapshots.Add(new NitaqatStandingSnapshot
        {
            TenantId = TenantA, CompanyId = CompanyA,
            AsOfDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            ActivityCode = "RETAIL", SizeTierCode = "SmallB",
            SaudiWeighted = 5m, TotalWeighted = 10m, AchievedPercent = 50m,
            Band = NitaqatBands.Platinum, BandRank = 4, RawSaudiHeadcount = 5, RawTotalHeadcount = 10,
        });
        await db.SaveChangesAsync();

        var trend = await Svc(db).GetTrendAsync(TenantA, CompanyA, 180);

        trend.Points.Should().HaveCount(1);
        trend.ChangePercentagePoints.Should().BeNull();
        trend.Direction.Should().BeNull();
        trend.ProjectedBandWarning.Should().BeNull();
    }

    [Fact]
    public async Task Trend_IsScopedToTheTenantAndCompany()
    {
        await using var db = NewDb(nameof(Trend_IsScopedToTheTenantAndCompany));
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        foreach (var (t, c) in new[] { (TenantA, CompanyA), (TenantB, CompanyA), (TenantA, CompanyB) })
        {
            db.NitaqatStandingSnapshots.Add(new NitaqatStandingSnapshot
            {
                TenantId = t, CompanyId = c, AsOfDate = today,
                ActivityCode = "RETAIL", SizeTierCode = "SmallB",
                SaudiWeighted = 1m, TotalWeighted = 2m, AchievedPercent = 50m,
                Band = NitaqatBands.Platinum, BandRank = 4, RawSaudiHeadcount = 1, RawTotalHeadcount = 2,
            });
        }
        await db.SaveChangesAsync();

        (await Svc(db).GetTrendAsync(TenantA, CompanyA, 30)).Points.Should().HaveCount(1);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  7. The seeder's honesty contract
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Seeder_ShipsNoThresholdsForRealActivities_SoTheyMustRefuse()
    {
        // The regression this guards: somebody "helpfully" fills the MHRSD grid with
        // plausible invented numbers and a customer acts on them.
        await using var db = NewDb(nameof(Seeder_ShipsNoThresholdsForRealActivities_SoTheyMustRefuse));
        await Zayra.Api.Infrastructure.Seed.NitaqatReferenceSeeder.SeedAsync(
            db, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var thresholds = await db.NitaqatBandThresholds.ToListAsync();
        var activities = await db.NitaqatActivities.ToListAsync();

        activities.Should().HaveCountGreaterThan(5);
        thresholds.Select(t => t.ActivityCode).Distinct()
            .Should().BeEquivalentTo(new[] { "GENERAL_UNVERIFIED" },
                "only the self-describing illustrative activity may ship with a grid");

        thresholds.Should().OnlyContain(t => !t.IsVerified);
        thresholds.Should().OnlyContain(t => t.SourceNote.Contains("ILLUSTRATIVE"));

        // Every real activity says, on the row itself, that it has no thresholds.
        activities.Where(a => a.Code != "GENERAL_UNVERIFIED").Should()
            .OnlyContain(a => a.SourceNote.Contains("NO BAND THRESHOLDS"));
    }

    [Fact]
    public async Task Seeder_IsIdempotent()
    {
        await using var db = NewDb(nameof(Seeder_IsIdempotent));
        var log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        await Zayra.Api.Infrastructure.Seed.NitaqatReferenceSeeder.SeedAsync(db, log);
        var first = await db.NitaqatBandThresholds.CountAsync()
                  + await db.NitaqatWeightRules.CountAsync()
                  + await db.NitaqatSizeTiers.CountAsync()
                  + await db.NitaqatActivities.CountAsync();

        await Zayra.Api.Infrastructure.Seed.NitaqatReferenceSeeder.SeedAsync(db, log);
        var second = await db.NitaqatBandThresholds.CountAsync()
                   + await db.NitaqatWeightRules.CountAsync()
                   + await db.NitaqatSizeTiers.CountAsync()
                   + await db.NitaqatActivities.CountAsync();

        second.Should().Be(first);
    }

    [Fact]
    public async Task Seeder_TierBoundariesHaveNoGapsOrOverlaps()
    {
        // A gap means an establishment of some size gets no tier and therefore no band —
        // caught here rather than by the customer whose headcount happens to land in it.
        await using var db = NewDb(nameof(Seeder_TierBoundariesHaveNoGapsOrOverlaps));
        await Zayra.Api.Infrastructure.Seed.NitaqatReferenceSeeder.SeedAsync(
            db, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var tiers = (await db.NitaqatSizeTiers.ToListAsync()).OrderBy(t => t.Rank).ToList();
        tiers.Should().NotBeEmpty();
        tiers[0].MinWorkforce.Should().Be(1);
        tiers[^1].MaxWorkforce.Should().BeNull("the largest tier must be open-ended");

        for (var i = 1; i < tiers.Count; i++)
            tiers[i].MinWorkforce.Should().Be(tiers[i - 1].MaxWorkforce!.Value + 1,
                $"{tiers[i - 1].Code} must abut {tiers[i].Code} exactly");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  8. The tracker this service replaces for reporting
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Documents, as an executable claim, WHY the country-pack tracker could not be
    /// extended: its input has already discarded the weighting by the time it is called,
    /// so the two answers differ on the same establishment and the tracker's is wrong.
    /// </summary>
    [Fact]
    public async Task TheOldTracker_CannotReachTheSameAnswer_BecauseItsInputHasAlreadyLostTheWeighting()
    {
        await using var db = NewDb(nameof(TheOldTracker_CannotReachTheSameAnswer_BecauseItsInputHasAlreadyLostTheWeighting));
        SeedReference(db);
        SeedCompany(db, TenantA, CompanyA, "SA");
        SeedProfile(db, TenantA, CompanyA, "RETAIL");
        SeedGrid(db, "RETAIL", "SmallB", 10m, 18m, 26m, 36m);

        // 4 full-time Saudis, one of whom has a disability (4×), plus 16 expatriates.
        var disabled = AddEmployee(db, TenantA, CompanyA, "Saudi", "FullTime", 9000m);
        for (var i = 0; i < 3; i++) AddEmployee(db, TenantA, CompanyA, "Saudi", "FullTime", 8000m);
        for (var i = 0; i < 16; i++) AddEmployee(db, TenantA, CompanyA, "Indian", "FullTime", 5000m);
        db.NitaqatEmployeeWeightOverrides.Add(new NitaqatEmployeeWeightOverride
        {
            TenantId = TenantA, CompanyId = CompanyA, EmployeeId = disabled,
            Category = NitaqatWeightCategories.Disability,
            Justification = "Disability certificate verified by HR, reference DC-2026-0041.",
        });
        await db.SaveChangesAsync();

        var s = (await Svc(db).GetStandingAsync(TenantA, CompanyA, AsOf)).Standing!;

        // Weighted: (3 × 1) + (1 × 4) = 7 Saudi units over 20 total units = 35%.
        s.SaudiWeighted.Should().Be(7m);
        s.AchievedPercent.Should().Be(35m);
        s.Band.Should().Be(NitaqatBands.HighGreen);

        // The country-pack tracker is handed plain integers. The BEST it can be told is
        // (20, 4) — four Saudi heads — which is 20%, and against its single 0.35 default
        // it reports NonCompliant. Same establishment, opposite conclusion.
        var tracker = new Zayra.Api.Infrastructure.CountryPack.Ksa.KsaNationalizationTracker(
            new NullRuleReader());
        var legacy = await tracker.GetStatusAsync(
            new NationalizationInput(TenantA, CompanyA, s.RawTotalHeadcount, s.RawSaudiHeadcount));

        legacy.TargetRatio.Should().Be(0.35d, "the hardcoded directional default");
        legacy.CurrentRatio.Should().BeApproximately(0.20d, 0.0001d);
        legacy.Status.Should().Be(NationalizationComplianceStatus.NonCompliant);

        // And it has no band at all — only four generic statuses.
        legacy.SchemeLabel.Should().Be("Nitaqat");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Fixtures
    // ═══════════════════════════════════════════════════════════════════════════

    private static ZayraDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(name).Options);

    private static NitaqatCalculationService Svc(ZayraDbContext db) =>
        new(db, new FixedRuleReader(fullFloor: 4000m, halfFloor: 3000m));

    private static IReadOnlyList<NitaqatBandThreshold> Thresholds(
        string activity, string tier, decimal low, decimal medium, decimal high, decimal platinum) =>
        new[]
        {
            (NitaqatBands.LowGreen, low), (NitaqatBands.MediumGreen, medium),
            (NitaqatBands.HighGreen, high), (NitaqatBands.Platinum, platinum),
        }
        .Select(x => new NitaqatBandThreshold
        {
            ActivityCode = activity, SizeTierCode = tier, Band = x.Item1,
            BandRank = NitaqatBands.RankOf(x.Item1), MinSaudizationPercent = x.Item2,
            EffectiveFrom = Eff,
        })
        .ToList();

    private static void SeedGrid(
        ZayraDbContext db, string activity, string tier,
        decimal low, decimal medium, decimal high, decimal platinum, bool verified = false)
    {
        foreach (var t in Thresholds(activity, tier, low, medium, high, platinum))
        {
            t.TenantId = null;
            t.IsVerified = verified;
            t.SourceNote = "Test fixture.";
            db.NitaqatBandThresholds.Add(t);
        }
    }

    /// <summary>Platform-default size tiers, activities and weight rules.</summary>
    private static void SeedReference(ZayraDbContext db)
    {
        var tiers = new (string Code, int Min, int? Max, int Rank)[]
        {
            ("VerySmall", 1, 5, 1), ("SmallA", 6, 9, 2), ("SmallB", 10, 24, 3), ("SmallC", 25, 49, 4),
            ("MediumA", 50, 99, 5), ("MediumB", 100, 199, 6), ("MediumC", 200, 499, 7),
            ("Large", 500, 2999, 8), ("Giant", 3000, null, 9),
        };
        foreach (var t in tiers)
            db.NitaqatSizeTiers.Add(new NitaqatSizeTier
            {
                TenantId = null, Code = t.Code, NameEn = t.Code, MinWorkforce = t.Min,
                MaxWorkforce = t.Max, Rank = t.Rank, EffectiveFrom = Eff, SourceNote = "Test fixture.",
            });

        foreach (var code in new[] { "RETAIL", "CONSTRUCTION", "GENERAL_UNVERIFIED" })
            db.NitaqatActivities.Add(new NitaqatActivity
            {
                TenantId = null, Code = code, NameEn = code, IsActive = true, SourceNote = "Test fixture.",
            });

        var weights = new (string Code, string Cls, string Basis, string Cat, decimal Num, decimal Den, int Prec, bool Ver)[]
        {
            ("SAUDI_FULLTIME",    GosiClassifications.Saudi,    NitaqatCountBasis.FullTime, NitaqatWeightCategories.Standard,       1m,   1m, 10, true),
            ("NONSAUDI_STANDARD", GosiClassifications.NonSaudi, NitaqatCountBasis.Any,      NitaqatWeightCategories.Standard,       0m,   1m, 10, true),
            ("SAUDI_PARTTIME",    GosiClassifications.Saudi,    NitaqatCountBasis.PartTime, NitaqatWeightCategories.Standard,     0.5m, 0.5m, 20, false),
            ("SAUDI_DISABILITY",  GosiClassifications.Saudi,    NitaqatCountBasis.Any,      NitaqatWeightCategories.Disability,     4m,   1m, 40, false),
            ("SAUDI_HALF_WAGE",   GosiClassifications.Saudi,    NitaqatCountBasis.Any,      NitaqatWeightCategories.HalfWageFloor, 0.5m,  1m, 30, false),
            ("SAUDI_BELOW_WAGE",  GosiClassifications.Saudi,    NitaqatCountBasis.Any,      NitaqatWeightCategories.BelowWageFloor,  0m,  1m, 30, false),
            ("GCC_STANDARD",      GosiClassifications.GCC,      NitaqatCountBasis.Any,      NitaqatWeightCategories.Standard,        0m,  1m, 10, false),
        };
        foreach (var w in weights)
            db.NitaqatWeightRules.Add(new NitaqatWeightRule
            {
                TenantId = null, RuleCode = w.Code, Classification = w.Cls, CountBasis = w.Basis,
                Category = w.Cat, NumeratorWeight = w.Num, DenominatorWeight = w.Den,
                Precedence = w.Prec, EffectiveFrom = Eff, IsVerified = w.Ver, SourceNote = "Test fixture.",
            });
    }

    private static void SeedCompany(ZayraDbContext db, Guid tenantId, Guid companyId, string country) =>
        db.Companies.Add(new Company
        {
            Id = companyId, TenantId = tenantId, CountryCode = country,
            TradeName = $"Co-{companyId.ToString()[..4]}", LegalNameEn = "Test Co", IsActive = true,
        });

    private static void SeedProfile(
        ZayraDbContext db, Guid tenantId, Guid companyId, string activity, string? qiwaBand = null) =>
        db.NitaqatEstablishmentProfiles.Add(new NitaqatEstablishmentProfile
        {
            TenantId = tenantId, CompanyId = companyId, ActivityCode = activity,
            QiwaReportedBand = qiwaBand ?? string.Empty, IsActive = true,
        });

    private static int _nextEmployeeId = 1;

    private static int AddEmployee(
        ZayraDbContext db, Guid tenantId, Guid companyId, string nationality, string type, decimal salary)
    {
        var id = Interlocked.Increment(ref _nextEmployeeId);
        db.Employees.Add(new Employee
        {
            Id = id, TenantId = tenantId, CompanyId = companyId,
            Nationality = nationality, EmploymentType = type, Salary = salary,
            Status = EmployeeStatuses.Active,
            JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FullName = $"Test Employee {id}",
        });
        return id;
    }

    private sealed class FixedRuleReader : IStatutoryRuleReader
    {
        private readonly decimal _full, _half;
        public FixedRuleReader(decimal fullFloor, decimal halfFloor) { _full = fullFloor; _half = halfFloor; }

        public Task<decimal?> GetDecimalAsync(
            string countryCode, string jurisdiction, string ruleKey,
            DateOnly effectiveDate, Guid? tenantId = null, CancellationToken ct = default)
            => Task.FromResult<decimal?>(ruleKey switch
            {
                NitaqatCalculationService.RuleKeyWageFloorFull => _full,
                NitaqatCalculationService.RuleKeyWageFloorHalf => _half,
                _ => null,
            });

        public Task<string?> GetStringAsync(
            string countryCode, string jurisdiction, string ruleKey,
            DateOnly effectiveDate, Guid? tenantId = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class NullRuleReader : IStatutoryRuleReader
    {
        public Task<decimal?> GetDecimalAsync(
            string countryCode, string jurisdiction, string ruleKey,
            DateOnly effectiveDate, Guid? tenantId = null, CancellationToken ct = default)
            => Task.FromResult<decimal?>(null);

        public Task<string?> GetStringAsync(
            string countryCode, string jurisdiction, string ruleKey,
            DateOnly effectiveDate, Guid? tenantId = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }
}
