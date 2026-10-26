using FluentAssertions;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests.Onboarding;

public class ImportRowValidationTests
{
    private static ResolvedImportRow Run(Dictionary<string, string> row, string country = "SA") =>
        ImportRowValidation.Apply(new ResolvedImportRow { CompanyCountryCode = country, CompanyId = Guid.NewGuid() }, row);

    private static Dictionary<string, string> Row(params (string k, string v)[] cells)
    {
        var d = new Dictionary<string, string> { ["FullName"] = "Omar Khan", ["Nationality"] = "Indian" };
        foreach (var (k, v) in cells) d[k] = v;
        return d;
    }

    [Fact]
    public void Import_AppliesSameValidators_NoBypass()
    {
        Run(Row(("Nationality", "Saudi"), ("IdNumber", "1000000009"))).Errors.Should().ContainSingle().Which.Should().StartWith("IdNumber");
        Run(Row(("IqamaNumber", "2000000007"))).Errors.Should().NotBeEmpty();
        Run(Row(("Nationality", "Saudi"), ("IqamaNumber", TestIds.ValidIqama(5)))).Errors.Should().NotBeEmpty();   // class disagreement
        Run(Row(("FullName", new string('x', 151)))).Errors.Should().NotBeEmpty();
        Run(Row(("DateOfBirth", DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd")))).Errors.Should().NotBeEmpty();
        Run(Row(("HousingAllowance", "-1"))).Errors.Should().NotBeEmpty();
        Run(Row(("BasicSalary", "99999999999"))).Errors.Should().NotBeEmpty();
        Run(Row(("JoiningDate", "1900-01-01"))).Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void ValidRow_HasNoErrors_AndResolvesNationality()
    {
        var r = Run(Row(("Nationality", "سعودي"), ("IdNumber", TestIds.ValidNationalId(7))));
        r.Errors.Should().BeEmpty();
        r.NationalityCode.Should().Be("SA");
        r.NationalityMappingState.Should().Be(NationalityMappingStates.Confirmed);
    }

    [Fact]
    public void UnmappedNationality_LandsAsUnresolved_WithGap_NotAnError()
    {
        var r = Run(Row(("Nationality", "Suadi")));
        r.Errors.Should().BeEmpty();
        r.NationalityCode.Should().BeNull();
        r.NationalityMappingState.Should().Be(NationalityMappingStates.Unresolved);
        r.Gaps.Should().Contain(g => g.Type == "data:nationality_unresolved");
    }

    [Fact]
    public void Floors_AndUnreadableContact_LandWithWarningsAndGaps()
    {
        var back = Run(Row(("JoiningDate", DateTime.UtcNow.AddDays(-60).ToString("yyyy-MM-dd")), ("Phone", "0412345678")));
        back.Errors.Should().BeEmpty();
        back.Gaps.Select(g => g.Type).Should().Contain(["data:joining_backdated", "data:phone_invalid"]);
        back.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public void NoName_IsLeftToTheCaller()
    {
        var r = Run(new Dictionary<string, string> { ["FullName"] = "", ["IdNumber"] = "1" });
        r.Errors.Should().BeEmpty();
    }
}
