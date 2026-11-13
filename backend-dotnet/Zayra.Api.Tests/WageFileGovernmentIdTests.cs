using FluentAssertions;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The ONE answer to "which government ID does a Saudi wage file carry for this person", shared by the WPS validator, the WPS
/// register writer and the bank export: the National ID / Iqama on file (IdNumber unless it is recorded as a passport or border
/// number, and IqamaNumber), and, with neither, the Border Number inside the company's window when the company allows it.
/// </summary>
public class WageFileGovernmentIdTests
{
    private static Employee Emp(string idNumber = "", string idType = "", string iqama = "") =>
        new() { Id = 7, IdNumber = idNumber, IdType = idType, IqamaNumber = iqama };

    private static Dictionary<int, BorderNumberRule.WageFileBorderId> Border(bool allowed) =>
        new() { [7] = new BorderNumberRule.WageFileBorderId("3123456789", allowed, 90) };

    [Theory]
    [InlineData("1012345678", "NationalId", "")]
    [InlineData("1012345678", "National ID", "")]
    [InlineData("1012345678", "", "")]
    [InlineData("", "", "2123456785")]
    [InlineData("2123456785", "Iqama", "2123456785")]
    public void Resolve_ReadsTheNationalIdOrTheIqama(string idNumber, string idType, string iqama)
    {
        var r = WageFileGovernmentId.Resolve(Emp(idNumber, idType, iqama), null);
        r.Outcome.Should().Be(WageFileIdOutcome.Resolved);
        r.Value.Should().Be(string.IsNullOrEmpty(idNumber) ? iqama : idNumber);
    }

    [Fact]
    public void Resolve_TwoDifferentIds_IsAConflict_AndWritesNothing()
    {
        var r = WageFileGovernmentId.Resolve(Emp("1012345678", "NationalId", "2123456785"), null);
        (r.Outcome, r.Value).Should().Be((WageFileIdOutcome.Conflict, (string?)null));
    }

    [Theory]
    [InlineData("Passport")]
    [InlineData("Border Number")]
    public void Resolve_APassportOrBorderNumberInIdNumber_IsNotAGovernmentId(string idType)
        => WageFileGovernmentId.Resolve(Emp("A12345678", idType), null).Outcome.Should().Be(WageFileIdOutcome.Missing);

    [Fact]
    public void Resolve_NoIqamaYet_UsesTheBorderNumber_OnlyWhenTheCompanyAllowsIt()
    {
        WageFileGovernmentId.Resolve(Emp(), Border(allowed: true)).Should().Be(new WageFileId("3123456789", WageFileIdOutcome.BorderNumberUsed));
        WageFileGovernmentId.Resolve(Emp(), Border(allowed: false)).Should().Be(new WageFileId(null, WageFileIdOutcome.BorderNumberNotAllowed));
        WageFileGovernmentId.Resolve(Emp(), null).Should().Be(new WageFileId(null, WageFileIdOutcome.Missing));
    }
}
