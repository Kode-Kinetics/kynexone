using FluentAssertions;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// <c>Employee.IdType</c> reaches the database in more than one spelling: the hire wizard derives "NationalId", while the profile
/// screen and the CSV import keep the displayed label ("National ID"). Every reader compares through
/// <see cref="GovernmentIdTypes.Canonical"/>, so no spelling makes a Saudi national's ID disappear.
/// </summary>
public class GovernmentIdTypeTests
{
    [Theory]
    [InlineData("NationalId", GovernmentIdTypes.NationalId)]
    [InlineData("National ID", GovernmentIdTypes.NationalId)]
    [InlineData("national id", GovernmentIdTypes.NationalId)]
    [InlineData("NATIONAL_ID", GovernmentIdTypes.NationalId)]
    [InlineData("National ID (Hawiyya)", GovernmentIdTypes.NationalId)]
    [InlineData("National Identity", GovernmentIdTypes.NationalId)]
    [InlineData("Iqama", GovernmentIdTypes.Iqama)]
    [InlineData("IQAMA", GovernmentIdTypes.Iqama)]
    [InlineData("Iqama (residence permit)", GovernmentIdTypes.Iqama)]
    [InlineData("Border Number", GovernmentIdTypes.BorderNumber)]
    [InlineData("BorderNumber", GovernmentIdTypes.BorderNumber)]
    [InlineData("Passport", GovernmentIdTypes.Passport)]
    public void Canonical_MapsEverySpellingToOneValue(string stored, string expected)
        => GovernmentIdTypes.Canonical(stored).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Canonical_OfNothing_IsNull(string? stored) => GovernmentIdTypes.Canonical(stored).Should().BeNull();

    [Fact]
    public void Canonical_KeepsAnotherCountrysTypeAsStored() => GovernmentIdTypes.Canonical(" CivilId ").Should().Be("CivilId");

    [Fact]
    public void Qiwa_IsToldTheCanonicalType_NotTheDisplayedLabel()
    {
        var e = new Employee { IdNumber = "1000000008", IdType = "National ID" };
        QiwaIntegrationService.PrimaryIdentity(e).Should().Be((GovernmentIdTypes.NationalId, "1000000008"));
    }
}
