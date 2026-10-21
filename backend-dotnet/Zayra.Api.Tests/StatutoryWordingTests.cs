using FluentAssertions;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.CountryPack.Ksa;

namespace Zayra.Api.Tests;

/// <summary>
/// Words only — no rate or rule changes. The settlement refusal said gratuity was paid "on the BASIC wage only",
/// which stopped being true when the KSA base became basic + housing (+ transport by default); the KSA descriptor
/// described only the pre-July-2024 9% / 9% GOSI schedule.
/// </summary>
public class StatutoryWordingTests
{
    [Fact]
    public void TheWageBaseRefusal_NamesTheAppliedBase_AndWhatIsExcluded_FromTheSettlementsOwnSnapshot()
    {
        const string snapshot = """
            {"basicWage":10000,"monthlyGross":13300,"appliedWageBase":12500,"configuredEosbWage":0,
             "packageHousing":2000,"packageTransport":500,"packageOther":800,"currency":"SAR"}
            """;

        var (text, applied, full) = PayrollController.DescribeEosbWageBase(snapshot, "SAR");

        text.Should().Contain("12,500.00 SAR (basic + housing + transport, as applied by the country pack)")
            .And.Contain("not the full package of 13,300.00 SAR")
            .And.Contain("800.00 SAR of other allowances (food, mobile, other) is excluded");
        text.Should().NotContainEquivalentOf("basic wage only");
        applied.Should().Be(12500m);
        full.Should().Be(13300m);
    }

    [Fact]
    public void TheWageBaseRefusal_SaysTransportIsExcluded_WhenTheBaseDoesNotReachIt()
    {
        // 'eosb.include_transport' switched off: the applied base is basic + housing only.
        const string snapshot = """{"basicWage":10000,"monthlyGross":13300,"appliedWageBase":12000,"packageHousing":2000,"packageTransport":500}""";

        var (text, _, _) = PayrollController.DescribeEosbWageBase(snapshot, "SAR");

        text.Should().Contain("(basic + housing, as applied").And.Contain("1,300.00 SAR of transport and other allowances");
    }

    [Fact]
    public void AnOldSnapshotWithoutTheBase_FallsBackToANeutralPhrase()
    {
        PayrollController.DescribeEosbWageBase("{}", "SAR").Text.Should().Be("the wage base the country pack applied, not the full package");
    }

    [Fact]
    public void TheKsaDescriptor_DescribesBothGosiCohorts_AndTheLastWageBase()
    {
        var d = new KsaDescriptor().GetDescriptor();

        d.SocialInsuranceDescription.Should().Contain("before 3 July 2024").And.Contain("9% EE + 9% ER")
            .And.Contain("on or after 3 July 2024").And.Contain("11% each side from July 2028").And.Contain("M/273");
        d.EosbFormula.Should().Contain("Art. 84").And.Contain("basic + housing").And.Contain("transport by default")
            .And.NotContain("month basic/yr");
    }
}
