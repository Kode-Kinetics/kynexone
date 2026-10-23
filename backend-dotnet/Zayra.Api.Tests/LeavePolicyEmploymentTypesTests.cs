using FluentAssertions;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Infrastructure.Setup;

namespace Zayra.Api.Tests;

/// <summary>
/// A leave policy's Employment Type is a list ("Full-Time,Part-Time"), stored comma-separated in the
/// existing column. A blank list still means every employment type, and a single legacy value still
/// reads as a one-item list.
/// </summary>
public sealed class LeavePolicyEmploymentTypesTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("  ", "")]
    [InlineData("Full-Time", "Full-Time")]
    [InlineData(" Full-Time , Part-Time ,, full-time ", "Full-Time,Part-Time")]
    public void Normalize_TrimsDropsBlanksAndDuplicates(string? input, string expected) =>
        LeavePolicyScope.NormalizeEmploymentTypes(input).Should().Be(expected);

    [Fact]
    public void SameAndKey_IgnoreOrderAndCase()
    {
        LeavePolicyScope.SameEmploymentTypes("Full-Time,Part-Time", "part-time, FULL-TIME").Should().BeTrue();
        LeavePolicyScope.SameEmploymentTypes("Full-Time,Part-Time", "Full-Time").Should().BeFalse();
        LeavePolicyScope.SameEmploymentTypes("", null).Should().BeTrue();
        LeavePolicyScope.EmploymentTypesKey("Part-Time,Full-Time").Should().Be(LeavePolicyScope.EmploymentTypesKey("full-time,part-time"));
    }

    [Theory]
    // blank covers everything, and overlaps everything
    [InlineData("", "Full-Time", true, true)]
    // a superset covers its subset
    [InlineData("Full-Time,Part-Time", "Part-Time", true, true)]
    [InlineData("Part-Time", "Full-Time,Part-Time", false, true)]
    // disjoint lists neither cover nor overlap
    [InlineData("Full-Time,Part-Time", "Contract,Intern", false, false)]
    // a named list never covers "everyone"
    [InlineData("Full-Time", "", false, true)]
    public void CoverAndOverlap(string broader, string narrower, bool covers, bool overlaps)
    {
        LeavePolicyScope.CoversEmploymentTypes(broader, narrower).Should().Be(covers);
        LeavePolicyScope.EmploymentTypesOverlap(broader, narrower).Should().Be(overlaps);
    }

    [Fact]
    public void SetupConflict_PartialOverlapIsAConflict_SubsetAndDisjointAreNot()
    {
        // Full-Time+Part-Time vs Part-Time+Contract: a part-timer falls under both, neither is broader.
        SetupAssistantService.LeaveScopesConflict(null, null, "Full-Time,Part-Time", null, null, "Part-Time,Contract").Should().BeTrue();
        // Strict subset: the narrower policy is a deliberate override, not a conflict.
        SetupAssistantService.LeaveScopesConflict(null, null, "Full-Time,Part-Time", null, null, "Part-Time").Should().BeFalse();
        // Separate populations.
        SetupAssistantService.LeaveScopesConflict(null, null, "Full-Time,Part-Time", null, null, "Contract").Should().BeFalse();
    }

    [Fact]
    public void Admits_AnyListedType_ByCodeOrLabel()
    {
        var list = ReferenceList.FromValues(ReferenceList.EmploymentTypeType, [("INTERN", "Intern", "")]);

        LeavePolicyScope.AdmitsEmploymentType("Full-Time,Part-Time", "PART_TIME", list).Should().BeTrue();
        LeavePolicyScope.AdmitsEmploymentType("FULL_TIME,PART_TIME", "Full-Time", list).Should().BeTrue();
        LeavePolicyScope.AdmitsEmploymentType("Full-Time,Part-Time", "INTERN", list).Should().BeFalse();
        LeavePolicyScope.AdmitsEmploymentType("", "INTERN", list).Should().BeTrue();
    }
}
