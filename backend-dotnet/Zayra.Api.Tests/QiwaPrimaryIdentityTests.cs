using FluentAssertions;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The Qiwa data check reads the person's primary government ID the way the wage file does: the ID number when one is recorded, else the Iqama
/// (and, for someone who has no Iqama yet, the Border Number). An expat whose only identity is an Iqama is not "missing id_number".
/// </summary>
public sealed class QiwaPrimaryIdentityTests
{
    private static Employee Ready() => new()
    {
        SaudiOrNonSaudi = "NonSaudi", Nationality = "Pakistani", OccupationCode = "2511", EstablishmentId = "1-1",
        WorkLocationId = "L1", ContractReference = "C1",
    };

    [Fact]
    public void AnExpatWithAnIqama_IsNotMissingTheIdNumberOrType()
    {
        var e = Ready(); e.IqamaNumber = "2123456785";
        QiwaIntegrationService.MissingQiwaFields(e).Should().BeEmpty();
        QiwaIntegrationService.PrimaryIdentity(e).Should().Be(("Iqama", "2123456785"));
    }

    [Fact]
    public void AnExpatWithOnlyABorderNumber_UsesIt()
    {
        var e = Ready(); e.BorderNumber = "3123456785";
        QiwaIntegrationService.MissingQiwaFields(e).Should().BeEmpty();
        QiwaIntegrationService.PrimaryIdentity(e).Should().Be(("BorderNumber", "3123456785"));
    }

    [Fact]
    public void ARecordedIdNumberAndType_AreStillUsedAsTheyAre()
    {
        var e = Ready(); e.SaudiOrNonSaudi = "Saudi"; e.IdNumber = "1000000008"; e.IdType = "NationalId"; e.IqamaNumber = "2123456785";
        QiwaIntegrationService.PrimaryIdentity(e).Should().Be(("NationalId", "1000000008"));
    }

    [Fact]
    public void ANoIdentityAtAll_IsStillFlagged()
    {
        var missing = QiwaIntegrationService.MissingQiwaFields(Ready());
        missing.Should().Contain(["id_type", "id_number"]);
    }
}
