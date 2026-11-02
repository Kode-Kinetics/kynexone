using FluentAssertions;
using Zayra.Api.Application.Onboarding;

namespace Zayra.Api.Tests.Onboarding;

/// <summary>
/// A hire template may pre-fill the form but never money: a salary or bank figure can only reach a new hire through the request's
/// own salary breakdown, which is staged as a pending first package and approved by a second person. Unknown keys are refused, so a
/// salary key in a template's defaults is an error, not a silent pay path.
/// </summary>
public class HireTemplateCarriesNoPayTests
{
    [Theory]
    [InlineData("{\"basicSalary\":9000}")]
    [InlineData("{\"salary\":9000}")]
    [InlineData("{\"salaryBreakdown\":{\"basicSalary\":9000}}")]
    [InlineData("{\"iban\":\"SA0380000000608010167519\"}")]
    [InlineData("{\"bankName\":\"Bank\"}")]
    [InlineData("{\"gradeId\":\"00000000-0000-0000-0000-000000000001\"}")]
    public void ADefaultsDocumentWithAPayKey_IsRefused(string json)
    {
        var parsed = HireTemplateDefaults.Parse(json);
        parsed.Ok.Should().BeFalse();
        parsed.Errors.Should().Contain(e => e.StartsWith("unknown_key:", StringComparison.Ordinal));
    }

    [Fact]
    public void TheAllowedDefaults_AreNonPayOnly()
    {
        var parsed = HireTemplateDefaults.Parse("{\"employmentType\":\"Full-time\",\"probationDays\":90,\"managerRule\":\"none\"}");
        parsed.Ok.Should().BeTrue();
        parsed.Values!.ProbationDays.Should().Be(90);
    }
}
