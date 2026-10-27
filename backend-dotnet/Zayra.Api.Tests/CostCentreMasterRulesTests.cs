using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Tests;

/// <summary>Cost-centre accounting v1, WP-A: the pure master-data rules (contract 1.1, 2.9): code format and the parent tree.</summary>
public class CostCentreMasterRulesTests
{
    [Theory]
    [InlineData("OPS", true)]
    [InlineData("a", true)]
    [InlineData("CC-100.A_b", true)]
    [InlineData("9X", true)]
    [InlineData("=1+1", false)]
    [InlineData("+cmd", false)]
    [InlineData("@SUM(A1)", false)]
    [InlineData("-lead", false)]
    [InlineData(".lead", false)]
    [InlineData("has space", false)]
    [InlineData("tab\t", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("ÄÖ", false)]
    public void T011_CodeFormat(string? code, bool valid) => CostCentreCode.IsValid(code).Should().Be(valid);

    [Fact]
    public void T011_ThirtyCharactersPass_ThirtyOneFail()
    {
        CostCentreCode.IsValid(new string('A', 30)).Should().BeTrue();
        CostCentreCode.IsValid(new string('A', 31)).Should().BeFalse();
    }

    [Fact]
    public void T011_GlSegmentUsesTheSameFormat() =>
        (CostCentreCode.IsValid("SEG-1"), CostCentreCode.IsValidGlSegment("=SEG"), CostCentreCode.IsValidGlSegment("SEG.1")).Should().Be((true, false, true));

    [Theory]
    [InlineData("COGS", true)] [InlineData("SELLING", true)] [InlineData("G_AND_A", true)] [InlineData("R_AND_D", true)]
    [InlineData("OPERATIONS", true)] [InlineData("CAPITALISABLE", true)] [InlineData("cogs", false)] [InlineData("X", false)]
    public void FunctionalClass_IsOneOfTheSix(string value, bool known) => CostCentreFunctionalClasses.IsKnown(value).Should().Be(known);

    private static Dictionary<Guid, Guid?> Chain(int length, out Guid[] ids)
    {
        ids = Enumerable.Range(0, length).Select(_ => Guid.NewGuid()).ToArray();
        var map = new Dictionary<Guid, Guid?>();
        for (var i = 0; i < length; i++) map[ids[i]] = i == 0 ? null : ids[i - 1];
        return map;
    }

    [Fact]
    public void T010_SelfParent_IsRefused()
    {
        var map = Chain(1, out var ids);
        CostCentreHierarchy.Check(ids[0], ids[0], map).Should().Be("cost_centre_parent_self");
    }

    [Fact]
    public void T010_ACycle_IsRefused()
    {
        var map = Chain(3, out var ids); // ids[2] -> ids[1] -> ids[0]
        CostCentreHierarchy.Check(ids[0], ids[2], map).Should().Be("cost_centre_parent_cycle");
        CostCentreHierarchy.Check(ids[0], ids[1], map).Should().Be("cost_centre_parent_cycle");
    }

    [Fact]
    public void T010_ADepthOfTen_IsAllowed_ElevenIsRefused()
    {
        var map = Chain(10, out var ids);
        var leaf = Guid.NewGuid(); map[leaf] = null;
        CostCentreHierarchy.Check(leaf, ids[8], map).Should().BeNull("the new node would be the tenth level");
        CostCentreHierarchy.Check(leaf, ids[9], map).Should().Be("cost_centre_parent_too_deep");
    }

    [Fact]
    public void T010_MovingASubtreeUnderADeepNode_CountsTheSubtreeHeightToo()
    {
        var map = Chain(9, out var ids);
        var top = Guid.NewGuid(); var below = Guid.NewGuid();
        map[top] = null; map[below] = top; // a two-level subtree
        CostCentreHierarchy.Check(top, ids[7], map).Should().BeNull("8 + 2 = 10 levels");
        CostCentreHierarchy.Check(top, ids[8], map).Should().Be("cost_centre_parent_too_deep", "9 + 2 = 11 levels");
    }

    [Fact]
    public void T010_NoParent_IsAlwaysFine()
    {
        var map = Chain(2, out var ids);
        CostCentreHierarchy.Check(ids[1], null, map).Should().BeNull();
    }
}
