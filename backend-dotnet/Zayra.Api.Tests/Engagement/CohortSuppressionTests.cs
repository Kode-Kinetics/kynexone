using FluentAssertions;
using Zayra.Api.Infrastructure.Privacy;

namespace Zayra.Api.Tests.Engagement;

/// <summary>
/// The shared suppression rule (REGISTER §3.2.4), pure. Every number here is worked by hand in ENG_DESIGN.md §5.
/// </summary>
public sealed class CohortSuppressionTests
{
    private static CohortPopulation Population(params (string Dept, string Desig, int Count)[] groups) =>
        new(groups.SelectMany(g => Enumerable.Range(0, g.Count).Select(_ =>
            new CohortRecord(new Dictionary<string, string?> { ["department"] = g.Dept, ["designation"] = g.Desig }))));

    private static CohortFilter F(string dim, string value) => new(dim, [value]);

    [Theory]
    [InlineData(null, 5, 5)]
    [InlineData(null, 2, 3)]   // a tenant default below the floor is raised
    [InlineData(2, 5, 3)]      // a survey override below the floor is raised
    [InlineData(8, 5, 8)]
    public void EffectiveK_NeverGoesBelowTheFloor(int? requested, int tenantDefault, int expected) =>
        CohortSuppression.EffectiveK(requested, tenantDefault).Should().Be(expected);

    [Fact]
    public void Rule1_ASliceBelowKIsHidden()
    {
        var pop = Population(("A", "X", 4), ("B", "X", 20));
        var d = CohortSuppression.EvaluateView(pop, [F("department", "A")], 5);
        d.IsVisible.Should().BeFalse();
        d.Reason.Should().Be(SuppressionReasons.BelowMinimum);
        CohortSuppression.EvaluateView(pop, [], 5).IsVisible.Should().BeTrue();
    }

    [Fact]
    public void Rule2_AComplementBelowKIsHidden_ButAComplementOfZeroIsFine()
    {
        // Total 7, department A 5: "everyone minus A" is 2 people.
        var pop = Population(("A", "X", 5), ("B", "X", 2));
        var a = CohortSuppression.EvaluateView(pop, [F("department", "A")], 5);
        a.IsVisible.Should().BeFalse();
        a.Reason.Should().Be(SuppressionReasons.ComplementTooSmall);

        // Every respondent is in A: the complement is 0 and nothing is revealed.
        var only = Population(("A", "X", 6));
        CohortSuppression.EvaluateView(only, [F("department", "A")], 5).IsVisible.Should().BeTrue();
    }

    [Fact]
    public void Rule2_IntersectingFilters_AreCheckedAgainstEverySupersetView()
    {
        // A∩X = 5, A = 12 (A∖X = 7 ok), all = 30 (25 ok), but X = 6: X∖(A∩X) = 1 person.
        var pop = Population(("A", "X", 5), ("A", "Y", 7), ("B", "X", 1), ("B", "Y", 17));
        var d = CohortSuppression.EvaluateView(pop, [F("department", "A"), F("designation", "X")], 5);
        d.IsVisible.Should().BeFalse();
        d.Reason.Should().Be(SuppressionReasons.ComplementTooSmall);

        // With six in B∩X the same slice is safe.
        var safe = Population(("A", "X", 5), ("A", "Y", 7), ("B", "X", 6), ("B", "Y", 12));
        CohortSuppression.EvaluateView(safe, [F("department", "A"), F("designation", "X")], 5).IsVisible.Should().BeTrue();
    }

    [Fact]
    public void Rule3_OneHiddenSibling_HidesTheNextSmallest()
    {
        // Parent 20: A 10, B 7, C 3. C is below k; without secondary suppression 20 − 10 − 7 = 3 recovers C.
        var pop = Population(("A", "X", 10), ("B", "X", 7), ("C", "X", 3));
        var cells = CohortSuppression.EvaluateBreakdown(pop, [], "department", 5).ToDictionary(c => c.Value, c => c.Decision);
        cells["A"].IsVisible.Should().BeTrue();
        cells["B"].IsVisible.Should().BeFalse();
        cells["B"].Reason.Should().Be(SuppressionReasons.Secondary);
        cells["C"].IsVisible.Should().BeFalse();
        cells["C"].Reason.Should().Be(SuppressionReasons.BelowMinimum);
    }

    [Fact]
    public void Rule3_TheHiddenGroupAsAWhole_MustReachK()
    {
        // Parent 30: A 15, B 10, C 2, D 1. C and D are hidden (3 together, < 5): hide the next smallest, B.
        var pop = Population(("A", "X", 15), ("B", "X", 10), ("C", "X", 2), ("D", "X", 1));
        var cells = CohortSuppression.EvaluateBreakdown(pop, [], "department", 5).ToDictionary(c => c.Value, c => c.Decision);
        cells["A"].IsVisible.Should().BeTrue();
        cells["B"].IsVisible.Should().BeFalse();
        cells["C"].IsVisible.Should().BeFalse();
        cells["D"].IsVisible.Should().BeFalse();
        // Recoverable = parent − visible = 30 − 15 = 15, a group of three cells: no single small group is exposed.
    }

    [Fact]
    public void Rule3_TwoHiddenSiblingsThatTogetherReachK_NeedNothingMore()
    {
        var pop = Population(("A", "X", 15), ("B", "X", 10), ("C", "X", 3), ("D", "X", 4));
        var cells = CohortSuppression.EvaluateBreakdown(pop, [], "department", 5).ToDictionary(c => c.Value, c => c.Decision);
        cells["A"].IsVisible.Should().BeTrue();
        cells["B"].IsVisible.Should().BeTrue();
        cells["C"].IsVisible.Should().BeFalse();
        cells["D"].IsVisible.Should().BeFalse();
    }

    [Fact]
    public void AHiddenParent_HidesEveryCell()
    {
        var pop = Population(("A", "X", 2), ("B", "X", 2));
        var cells = CohortSuppression.EvaluateBreakdown(pop, [], "department", 5);
        cells.Should().OnlyContain(c => !c.Decision.IsVisible);
    }

    [Fact]
    public void AMissingAttribute_IsItsOwnSibling_AndIsSuppressedLikeOne()
    {
        var records = Enumerable.Range(0, 10).Select(_ => new CohortRecord(new Dictionary<string, string?> { ["department"] = "A" }))
            .Concat(Enumerable.Range(0, 2).Select(_ => new CohortRecord(new Dictionary<string, string?>())))
            .Concat(Enumerable.Range(0, 8).Select(_ => new CohortRecord(new Dictionary<string, string?> { ["department"] = "B" })));
        var cells = CohortSuppression.EvaluateBreakdown(new CohortPopulation(records), [], "department", 5)
            .ToDictionary(c => c.Value, c => c.Decision);
        cells.Should().ContainKey(CohortSuppression.Unknown);
        cells[CohortSuppression.Unknown].IsVisible.Should().BeFalse();
        cells["B"].IsVisible.Should().BeFalse("the next-smallest sibling covers the unknown bucket");
        cells["A"].IsVisible.Should().BeTrue();
    }

    [Fact]
    public void ABreakdownInsideAFilteredView_UsesTheViewAsParent_AndTheWholeAsAncestor()
    {
        // View = designation X (A 6, B 6 = 12). Breaking it down by department: A∩X = 6, B∩X = 6 both ≥ 5, and
        // each complement inside X is 6. But A = 6 + 1 (A∩Y = 1): (A) − (A∩X) = 1 — so A∩X is hidden, and B∩X
        // follows by secondary suppression.
        var pop = Population(("A", "X", 6), ("A", "Y", 1), ("B", "X", 6), ("B", "Y", 9));
        var cells = CohortSuppression.EvaluateBreakdown(pop, [F("designation", "X")], "department", 5)
            .ToDictionary(c => c.Value, c => c.Decision);
        cells["A"].IsVisible.Should().BeFalse();
        cells["A"].Reason.Should().Be(SuppressionReasons.ComplementTooSmall);
        cells["B"].IsVisible.Should().BeFalse();
    }

    [Fact]
    public void KBelowTheFloor_IsTreatedAsTheFloor()
    {
        var pop = Population(("A", "X", 2), ("B", "X", 10));
        CohortSuppression.EvaluateView(pop, [F("department", "A")], 1).IsVisible.Should().BeFalse();
    }
}
