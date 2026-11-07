using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Tests;

/// <summary>
/// OVT-05. Payroll priced every overtime impact with ONE policy (the tenant's current default) instead of the
/// policy that was applied to the request. Two requests filed under different policies therefore paid the same.
/// </summary>
public class OvertimeImpactPolicyPricingTests
{
    // 60/40 package: basic 18,000, wage 30,000.
    private const decimal Basic = 18_000m, Wage = 30_000m;

    private static OvertimePolicyTerms Fixed200 => new("FixedHourlyRate", 200m, 240);
    private static OvertimePolicyTerms Statutory => new("BasicSalary", 0m, 240);

    [Fact]
    public void TwoRequestsUnderDifferentPolicies_ArePricedDifferently()
    {
        var onFixed = Guid.NewGuid();
        var onBasic = Guid.NewGuid();
        var fixedPolicy = Guid.NewGuid();
        var basicPolicy = Guid.NewGuid();
        var requestPolicy = new Dictionary<Guid, Guid?> { [onFixed] = fixedPolicy, [onBasic] = basicPolicy };
        var policies = new Dictionary<Guid, OvertimePolicyTerms> { [fixedPolicy] = Fixed200, [basicPolicy] = Statutory };
        // The tenant's CURRENT default is the statutory one.
        var fallback = Statutory;

        decimal Price(Guid requestId) => OvertimeImpactPricing.HourRate(
            OvertimeImpactPricing.TermsFor(requestId, requestPolicy, policies, fallback),
            monthlyHoursRuleOverride: null, baseIsFullWage: true, fullBasic: Basic, fullWage: Wage, multiplier: 1.5m).HourPay;

        Price(onFixed).Should().Be(300m, "200/h x 1.5: the fixed-rate policy the request was filed under");
        Price(onBasic).Should().Be(162.50m, "Art. 107: 125 + 75 x 0.5");
    }

    [Fact]
    public void RequestPolicy_WinsOverTheCurrentDefault_EvenWhenThatPolicyIsNoLongerTheDefault()
    {
        var req = Guid.NewGuid();
        var pid = Guid.NewGuid();
        var terms = OvertimeImpactPricing.TermsFor(req,
            new Dictionary<Guid, Guid?> { [req] = pid },
            new Dictionary<Guid, OvertimePolicyTerms> { [pid] = Fixed200 },
            fallback: Statutory);
        terms.Should().Be(Fixed200);
    }

    [Fact]
    public void RequestWithNoPolicyOrAnUnknownOne_FallsBackToTheDefault()
    {
        var noPolicy = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var map = new Dictionary<Guid, Guid?> { [noPolicy] = null, [unknown] = Guid.NewGuid() };
        var policies = new Dictionary<Guid, OvertimePolicyTerms>();
        OvertimeImpactPricing.TermsFor(noPolicy, map, policies, Statutory).Should().Be(Statutory);
        OvertimeImpactPricing.TermsFor(unknown, map, policies, Statutory).Should().Be(Statutory);
        OvertimeImpactPricing.TermsFor(Guid.NewGuid(), map, policies, Statutory).Should().Be(Statutory);
    }

    [Fact]
    public void MonthlyHoursDivisor_IsTheRequestPolicys_NotTheDefaults()
    {
        // 208 h policy: wage hourly = 30000/208, basic hourly = 18000/208.
        var terms = new OvertimePolicyTerms("BasicSalary", 0m, 208);
        var rate = OvertimeImpactPricing.HourRate(terms, null, baseIsFullWage: true, Basic, Wage, 1.5m);
        rate.BaseHourly.Should().Be(Wage / 208m);
        rate.UpliftBasisHourly.Should().Be(Basic / 208m);
    }
}
