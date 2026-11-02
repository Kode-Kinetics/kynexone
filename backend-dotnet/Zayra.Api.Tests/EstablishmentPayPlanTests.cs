using FluentAssertions;
using Zayra.Api.Application.Establishment;

namespace Zayra.Api.Tests;

/// <summary>Pure-logic tests for the establishment pay-range reconciliation (SME-reviewed rules).</summary>
public class EstablishmentPayPlanTests
{
    private static readonly Guid Lvl = Guid.NewGuid();
    private static readonly Guid Lvl2 = Guid.NewGuid();
    private static readonly Guid CoA = Guid.NewGuid();
    private static readonly Guid CoB = Guid.NewGuid();

    private static PayGrade G(decimal min, decimal mid, decimal max, string cur = "SAR", Guid? co = null,
        bool deleted = false, bool active = true) =>
        new(Guid.NewGuid(), co, deleted, active, min, mid, max, cur);

    private static PayDesignation D(Guid? level, PayGrade? g) => new(level, g?.Id);

    private static DepartmentPayPlan Plan(decimal budget, int approved, PayLevelInput[] levels,
        PayDesignation[] desigs, PayGrade[] grades, Guid? co = null, string? cur = "SAR") =>
        EstablishmentPayPlan.Plan(budget, approved, co, cur, levels, desigs, grades);

    private static PayLevelInput L(Guid id, int? heads) => new(id, heads);

    // ── Band ────────────────────────────────────────────────────────────────

    [Fact]
    public void Band_SingleDesignation_MinMidMaxEqualGrade()
    {
        var g = G(1000, 1500, 2000);
        var r = EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { D(Lvl, g) }, new[] { g });
        r.Band.Should().Be(new PayBand(1000, 1500, 2000, "SAR"));
    }

    [Fact]
    public void Band_MinOfMins_AverageOfMids_MaxOfMaxes()
    {
        var g1 = G(1000, 1500, 2000);
        var g2 = G(2000, 3000, 4000);
        var r = EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR",
            new[] { D(Lvl, g1), D(Lvl, g2) }, new[] { g1, g2 });
        r.Band.Should().Be(new PayBand(1000, 2250, 4000, "SAR"));
    }

    [Fact]
    public void Band_MidZero_UsesMidpointOfMinMax()
    {
        var g = G(1000, 0, 3000);
        EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { D(Lvl, g) }, new[] { g })
            .Band!.Mid.Should().Be(2000);
    }

    [Fact]
    public void Band_MidOutsideRange_IsClamped()
    {
        var hi = G(1000, 9000, 3000);
        var lo = G(1000, 10, 3000);
        EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { D(Lvl, hi) }, new[] { hi }).Band!.Mid.Should().Be(3000);
        EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { D(Lvl, lo) }, new[] { lo }).Band!.Mid.Should().Be(1000);
    }

    [Fact]
    public void Band_MinGreaterThanMax_GradeInvalid_LevelUnpriced()
    {
        var g = G(3000, 2000, 1000);
        var r = EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { D(Lvl, g) }, new[] { g });
        r.Band.Should().BeNull();
        r.UnpricedDesignations.Should().Be(1);
    }

    [Fact]
    public void Band_AllZeroGrade_Unpriced()
    {
        var g = G(0, 0, 0);
        EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { D(Lvl, g) }, new[] { g }).Band.Should().BeNull();
    }

    [Fact]
    public void Band_DeletedOrInactiveGrade_IsUnpriced_NotDropped()
    {
        var del = G(1000, 1500, 2000, deleted: true);
        var off = G(1000, 1500, 2000, active: false);
        var ok = G(500, 600, 700);
        var r = EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR",
            new[] { D(Lvl, del), D(Lvl, off), D(Lvl, ok) }, new[] { del, off, ok });
        r.Band.Should().Be(new PayBand(500, 600, 700, "SAR"));
        r.PricedDesignations.Should().Be(1);
        r.UnpricedDesignations.Should().Be(2);
    }

    [Fact]
    public void Band_GradeIdNull_CountsUnpriced()
    {
        var ok = G(500, 600, 700);
        var r = EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR",
            new[] { D(Lvl, ok), new PayDesignation(Lvl, null) }, new[] { ok });
        r.UnpricedDesignations.Should().Be(1);
        r.Band.Should().NotBeNull();
    }

    [Fact]
    public void Band_UnknownGradeId_CountsUnpriced()
    {
        var r = EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR",
            new[] { new PayDesignation(Lvl, Guid.NewGuid()) }, Array.Empty<PayGrade>());
        r.Band.Should().BeNull();
        r.UnpricedDesignations.Should().Be(1);
    }

    [Fact]
    public void Band_NoDesignationsForLevel_Unpriced()
    {
        var g = G(1, 2, 3);
        EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { D(Lvl2, g) }, new[] { g }).Band.Should().BeNull();
    }

    [Fact]
    public void Band_DesignationWithoutLevel_Ignored()
    {
        var g = G(1, 2, 3);
        EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { D(null, g) }, new[] { g }).Band.Should().BeNull();
    }

    // ── Company scope ───────────────────────────────────────────────────────

    [Fact]
    public void Scope_GradeOfAnotherCompany_NotUsed()
    {
        var other = G(1000, 1500, 2000, co: CoB);
        var r = EstablishmentPayPlan.ComputeBand(Lvl, CoA, "SAR", new[] { D(Lvl, other) }, new[] { other });
        r.Band.Should().BeNull();
        r.UnpricedDesignations.Should().Be(1);
    }

    [Fact]
    public void Scope_NullCompanyLegacyGrade_UsableByAnyCompany()
    {
        var legacy = G(1000, 1500, 2000, co: null);
        EstablishmentPayPlan.ComputeBand(Lvl, CoA, "SAR", new[] { D(Lvl, legacy) }, new[] { legacy })
            .Band.Should().NotBeNull();
    }

    [Fact]
    public void Scope_NullDepartmentCompany_UsesOnlySharedGrades_NeverMixesCompanies()
    {
        var a = G(1000, 1500, 2000, co: CoA);
        var b = G(5000, 6000, 7000, co: CoB);
        var shared = G(100, 200, 300, co: null);
        var r = EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR",
            new[] { D(Lvl, a), D(Lvl, b), D(Lvl, shared) }, new[] { a, b, shared });
        r.Band.Should().Be(new PayBand(100, 200, 300, "SAR"));
        r.UnpricedDesignations.Should().Be(2);
    }

    [Fact]
    public void Scope_NullDepartmentCompany_OnlyCompanyGrades_Unpriced()
    {
        var a = G(1000, 1500, 2000, co: CoA);
        var b = G(5000, 6000, 7000, co: CoB);
        var r = EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { D(Lvl, a), D(Lvl, b) }, new[] { a, b });
        r.Band.Should().BeNull();
        r.UnpricedDesignations.Should().Be(2);
    }

    [Fact]
    public void Scope_DesignationOfAnotherCompany_DoesNotContribute()
    {
        var s1 = G(1000, 1500, 2000);
        var s2 = G(5000, 6000, 7000);
        var ds = new[] { new PayDesignation(Lvl, s1.Id, CoA), new PayDesignation(Lvl, s2.Id, CoB) };
        var gs = new[] { s1, s2 };
        EstablishmentPayPlan.ComputeBand(Lvl, CoA, "SAR", ds, gs).Band.Should().Be(new PayBand(1000, 1500, 2000, "SAR"));
        EstablishmentPayPlan.ComputeBand(Lvl, CoB, "SAR", ds, gs).Band.Should().Be(new PayBand(5000, 6000, 7000, "SAR"));
        var other = EstablishmentPayPlan.ComputeBand(Lvl, CoA, "SAR", new[] { ds[1] }, gs);
        other.Band.Should().BeNull();
        other.UnpricedDesignations.Should().Be(0, "a foreign designation is out of scope, not unpriced");
    }

    [Fact]
    public void Scope_SharedDesignation_CountsForAnyCompany_NullDeptOnlySharedDesignations()
    {
        var g = G(1000, 1500, 2000);
        var shared = new PayDesignation(Lvl, g.Id, null);
        var ofA = new PayDesignation(Lvl, g.Id, CoA);
        EstablishmentPayPlan.ComputeBand(Lvl, CoB, "SAR", new[] { shared }, new[] { g }).Band.Should().NotBeNull();
        EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { ofA }, new[] { g }).Band.Should().BeNull();
        EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { shared }, new[] { g }).Band.Should().NotBeNull();
    }

    [Fact]
    public void Rounding_FractionalMid_RoundedToTwoDecimals_AwayFromZero()
    {
        var g1 = G(1000, 1500.5m, 2000);
        var g2 = G(1000, 1500.25m, 2000);
        var r = EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { D(Lvl, g1), D(Lvl, g2) }, new[] { g1, g2 });
        r.Band!.Mid.Should().Be(1500.38m);   // 1500.375 rounds away from zero
    }

    [Fact]
    public void Rounding_PlanFiguresAreTwoDecimals()
    {
        var g = G(1000, 1500.5m, 2000);
        var p = Plan(10000, 3, new[] { L(Lvl, 3) }, new[] { D(Lvl, g) }, new[] { g });
        p.AvgPerHead.Should().Be(3333.33m);
        p.PlannedMid.Should().Be(4501.50m);
        p.UnallocatedBudget.Should().Be(5498.50m);
    }

    [Fact]
    public void Scope_SameLevel_DifferentBandsPerCompany()
    {
        var a = G(1000, 1500, 2000, co: CoA);
        var b = G(5000, 6000, 7000, co: CoB);
        var ds = new[] { D(Lvl, a), D(Lvl, b) };
        var gs = new[] { a, b };
        EstablishmentPayPlan.ComputeBand(Lvl, CoA, "SAR", ds, gs).Band.Should().Be(new PayBand(1000, 1500, 2000, "SAR"));
        EstablishmentPayPlan.ComputeBand(Lvl, CoB, "SAR", ds, gs).Band.Should().Be(new PayBand(5000, 6000, 7000, "SAR"));
    }

    // ── Currency ────────────────────────────────────────────────────────────

    [Fact]
    public void Currency_MixedWithinLevel_UnpricedAndMismatch()
    {
        var sar = G(1000, 1500, 2000, "SAR");
        var usd = G(1000, 1500, 2000, "USD");
        var r = EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { D(Lvl, sar), D(Lvl, usd) }, new[] { sar, usd });
        r.Band.Should().BeNull();
        r.CurrencyMismatch.Should().BeTrue();
    }

    [Fact]
    public void Currency_DiffersFromCompanyDefault_UnpricedAndMismatch()
    {
        var usd = G(1000, 1500, 2000, "USD");
        var r = EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { D(Lvl, usd) }, new[] { usd });
        r.Band.Should().BeNull();
        r.CurrencyMismatch.Should().BeTrue();
    }

    [Fact]
    public void Currency_CaseInsensitive_NoMismatch()
    {
        var g = G(1000, 1500, 2000, "sar");
        var r = EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { D(Lvl, g) }, new[] { g });
        r.CurrencyMismatch.Should().BeFalse();
        r.Band!.Currency.Should().Be("SAR");
    }

    [Fact]
    public void Currency_NoCompanyCurrency_UsesGradeCurrency()
    {
        var g = G(1000, 1500, 2000, "KWD");
        EstablishmentPayPlan.ComputeBand(Lvl, null, null, new[] { D(Lvl, g) }, new[] { g })
            .Band!.Currency.Should().Be("KWD");
    }

    [Fact]
    public void Currency_Mismatch_FlagsDepartment_AndNeverSums()
    {
        var usd = G(1000, 1500, 2000, "USD");
        var p = Plan(10000, 5, new[] { L(Lvl, 5) }, new[] { D(Lvl, usd) }, new[] { usd });
        p.Flags.Should().Contain("currencyMismatch");
        p.PlannedMid.Should().Be(0);
        p.UnpricedHeads.Should().Be(5);
    }

    // ── Plan ────────────────────────────────────────────────────────────────

    [Fact]
    public void Plan_OwnerExample_Budget10000_Approved5_AvgPerHead2000()
    {
        var p = Plan(10000, 5, Array.Empty<PayLevelInput>(), Array.Empty<PayDesignation>(), Array.Empty<PayGrade>());
        p.AvgPerHead.Should().Be(2000);
    }

    [Fact]
    public void Plan_NoBudget_NotComparable_NoAvg()
    {
        var g = G(1000, 1500, 2000);
        var p = Plan(0, 5, new[] { L(Lvl, 2) }, new[] { D(Lvl, g) }, new[] { g });
        p.AvgPerHead.Should().BeNull();
        p.Flags.Should().Equal("notComparable");
    }

    [Fact]
    public void Plan_ApprovedZero_NoAvg()
    {
        Plan(10000, 0, Array.Empty<PayLevelInput>(), Array.Empty<PayDesignation>(), Array.Empty<PayGrade>())
            .AvgPerHead.Should().BeNull();
    }

    [Fact]
    public void Plan_NoLevels_NotComparable()
    {
        var p = Plan(10000, 5, Array.Empty<PayLevelInput>(), Array.Empty<PayDesignation>(), Array.Empty<PayGrade>());
        p.Flags.Should().Equal("notComparable");
        p.UnallocatedBudget.Should().Be(10000);
        p.PlannedMid.Should().Be(0);
    }

    [Fact]
    public void Plan_SumsBudgetedHeadsTimesBand()
    {
        var g = G(1000, 1500, 2000);
        var p = Plan(5000, 3, new[] { L(Lvl, 3) }, new[] { D(Lvl, g) }, new[] { g });
        p.PlannedMin.Should().Be(3000);
        p.PlannedMid.Should().Be(4500);
        p.PlannedMax.Should().Be(6000);
        p.UnallocatedBudget.Should().Be(500);
    }

    [Fact]
    public void Plan_FrozenLevel_PricedAtZero_NotUnpriced()
    {
        var g = G(1000, 1500, 2000);
        var p = Plan(5000, 3, new[] { L(Lvl, 0), L(Lvl2, 2) }, new[] { D(Lvl, g), D(Lvl2, g) }, new[] { g });
        p.PlannedMid.Should().Be(3000);
        p.UnpricedHeads.Should().Be(0);
        p.UncontrolledLevels.Should().Be(0);
    }

    [Fact]
    public void Plan_UncontrolledLevel_ExcludedAndCounted()
    {
        var g = G(1000, 1500, 2000);
        var p = Plan(5000, 3, new[] { L(Lvl, null), L(Lvl2, 2) }, new[] { D(Lvl, g), D(Lvl2, g) }, new[] { g });
        p.UncontrolledLevels.Should().Be(1);
        p.PlannedMid.Should().Be(3000);
    }

    [Fact]
    public void Plan_UnpricedLevelHeads_CountedAndExcludedFromPlan()
    {
        var g = G(1000, 1500, 2000);
        var p = Plan(5000, 5, new[] { L(Lvl, 2), L(Lvl2, 3) }, new[] { D(Lvl, g), new PayDesignation(Lvl2, null) }, new[] { g });
        p.UnpricedHeads.Should().Be(3);
        p.PlannedMid.Should().Be(3000);
    }

    [Fact]
    public void Plan_AllocatedGreaterThanApproved_StillComputes()
    {
        var g = G(1000, 1000, 1000);
        var p = Plan(3000, 2, new[] { L(Lvl, 4) }, new[] { D(Lvl, g) }, new[] { g });
        p.PlannedMid.Should().Be(4000);
        p.UnallocatedBudget.Should().Be(-1000);
        p.AvgPerHead.Should().Be(1500);
    }

    // ── Flags ───────────────────────────────────────────────────────────────

    [Fact]
    public void Flag_OverBudget_StrictlyGreater_EqualIsNot()
    {
        var g = G(1000, 1000, 1000);
        Plan(2000, 2, new[] { L(Lvl, 2) }, new[] { D(Lvl, g) }, new[] { g }).Flags.Should().NotContain("overBudget");
        Plan(1999, 2, new[] { L(Lvl, 2) }, new[] { D(Lvl, g) }, new[] { g }).Flags.Should().Contain("overBudget");
    }

    [Fact]
    public void Flag_BelowMinimum_WhenMinExceedsBudget()
    {
        var g = G(1000, 1500, 2000);
        var p = Plan(1500, 2, new[] { L(Lvl, 2) }, new[] { D(Lvl, g) }, new[] { g });
        p.Flags.Should().Contain("belowMinimum").And.Contain("overBudget");
        Plan(2000, 2, new[] { L(Lvl, 2) }, new[] { D(Lvl, g) }, new[] { g }).Flags.Should().NotContain("belowMinimum");
    }

    [Fact]
    public void Flag_UnderBudget_ExactlyTwentyFivePercentCounts()
    {
        var g = G(1000, 1000, 1000);
        // budget 4000, planned 3000 => unallocated 1000 = exactly 25%.
        Plan(4000, 3, new[] { L(Lvl, 3) }, new[] { D(Lvl, g) }, new[] { g }).Flags.Should().Contain("underBudget");
        // budget 4001, planned 3000 => 1001 >= 1000.25 yes; budget 3999 => 999 < 999.75 no.
        Plan(3999, 3, new[] { L(Lvl, 3) }, new[] { D(Lvl, g) }, new[] { g }).Flags.Should().NotContain("underBudget");
        EstablishmentPayPlan.UnderBudgetThreshold.Should().Be(0.25m);
    }

    [Fact]
    public void Flag_NoPricedHeads_NotComparable_SuppressesOthers()
    {
        var p = Plan(5000, 3, new[] { L(Lvl, 3) }, new[] { new PayDesignation(Lvl, null) }, Array.Empty<PayGrade>());
        p.Flags.Should().Equal("notComparable");
        p.UnpricedHeads.Should().Be(3);
    }

    [Fact]
    public void Flag_FrozenOnly_NoPricedHeads_NotComparable()
    {
        var g = G(1000, 1000, 1000);
        Plan(5000, 3, new[] { L(Lvl, 0) }, new[] { D(Lvl, g) }, new[] { g }).Flags.Should().Equal("notComparable");
    }

    // ── Determinism ─────────────────────────────────────────────────────────

    [Fact]
    public void Result_IndependentOfInputOrdering()
    {
        var g1 = G(1000, 1500, 2000);
        var g2 = G(2000, 2500, 4000);
        var g3 = G(700, 0, 900);
        var ds = new[] { D(Lvl, g1), D(Lvl, g2), D(Lvl2, g3), new PayDesignation(Lvl2, null) };
        var gs = new[] { g1, g2, g3 };
        var ls = new[] { L(Lvl, 3), L(Lvl2, 2) };
        var a = Plan(9000, 6, ls, ds, gs);
        var b = Plan(9000, 6, ls.Reverse().ToArray(), ds.Reverse().ToArray(), gs.Reverse().ToArray());
        b.Should().BeEquivalentTo(a);
    }

    [Fact]
    public void Status_Priced_NoRange_CurrencyMismatch()
    {
        var sar = G(1000, 1500, 2000, "SAR");
        var usd = G(1000, 1500, 2000, "USD");
        EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { D(Lvl, sar) }, new[] { sar }).Status.Should().Be("priced");
        EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { D(Lvl, usd) }, new[] { usd }).Status.Should().Be("currencyMismatch");
        EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { D(Lvl, sar), D(Lvl, usd) }, new[] { sar, usd }).Status.Should().Be("currencyMismatch");
        EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", Array.Empty<PayDesignation>(), new[] { sar }).Status.Should().Be("noRange");
        var bad = G(3000, 2000, 1000);
        EstablishmentPayPlan.ComputeBand(Lvl, null, "SAR", new[] { D(Lvl, bad), new PayDesignation(Lvl, null) }, new[] { bad }).Status.Should().Be("noRange");
    }
}
