using FluentAssertions;
using Zayra.Api.Infrastructure.Employees;

namespace Zayra.Api.Tests;

/// <summary>PAY-08 — a national-ID prefix that contradicts the nationality is a warning, never a block.</summary>
public class IdentityNationalityConsistencyTests
{
    [Theory]
    [InlineData("Saudi", "1012345678", "")]
    [InlineData("Egyptian", "", "2123456789")]
    [InlineData("Egyptian", "2123456789", "")]
    [InlineData("Saudi", "", "")]
    [InlineData("", "1012345678", "")]            // no nationality: nothing to contradict
    [InlineData("Atlantean", "1012345678", "")]   // unknown nationality: no opinion (payroll flags that separately)
    [InlineData("Saudi", "3012345678", "")]       // prefix neither 1 nor 2: no opinion
    public void Consistent_or_undecidable_values_raise_nothing(string nationality, string idNumber, string iqama)
        => IdentityNationalityConsistency.Warnings(nationality, idNumber, iqama).Should().BeEmpty();

    [Fact]
    public void A_saudi_with_a_resident_style_id_is_warned()
        => IdentityNationalityConsistency.Warnings("Saudi", "2123456789", "")
            .Should().ContainSingle().Which.Should().Contain("starts with 1").And.Contain("2");

    [Fact]
    public void A_non_saudi_with_a_saudi_style_id_is_warned()
        => IdentityNationalityConsistency.Warnings("Egyptian", "1012345678", "")
            .Should().ContainSingle().Which.Should().Contain("starts with 2");

    [Fact]
    public void An_iqama_number_starting_with_1_is_warned_for_anyone()
        => IdentityNationalityConsistency.Warnings("Egyptian", "", "1123456789").Should().ContainSingle();

    [Fact]
    public void The_warning_never_repeats_the_identity_number()
        => IdentityNationalityConsistency.Warnings("Saudi", "2123456789", "").Single().Should().NotContain("2123456789");
}
