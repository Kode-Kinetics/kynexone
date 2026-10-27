using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Tests;

/// <summary>Cost-centre accounting v1, WP-A (door: setup assistant): a code an AI draft proposes is made safe before it is ever applied.</summary>
public class CostCentreCodeSanitiseTests
{
    [Theory]
    [InlineData("=1+1", "1_1")]
    [InlineData("-lead", "LEAD")]
    [InlineData("Sales & Marketing", "SALES_MARKETING")]
    [InlineData("  ops  ", "OPS")]
    [InlineData("CC_Finance", "CC_FINANCE")]
    [InlineData("@SUM(A1)", "SUM_A1")]
    public void Sanitise_YieldsAValidUpperCaseCode(string raw, string expected)
    {
        var code = CostCentreCode.Sanitise(raw, "FALLBACK");
        code.Should().Be(expected);
        CostCentreCode.IsValid(code).Should().BeTrue();
    }

    [Fact]
    public void Sanitise_UsesTheFallback_WhenNothingUsableRemains_AndCapsAtThirty()
    {
        CostCentreCode.Sanitise("=+-", "Finance Dept").Should().Be("FINANCE_DEPT");
        CostCentreCode.Sanitise(new string('A', 50), "x").Should().HaveLength(30);
    }
}
