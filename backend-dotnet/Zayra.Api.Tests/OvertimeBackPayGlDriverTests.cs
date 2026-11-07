using FluentAssertions;
using Zayra.Api.Infrastructure.Payroll;

namespace Zayra.Api.Tests;

/// <summary>Carried-forward overtime back-pay is overtime expense, not "other earnings".</summary>
public class OvertimeBackPayGlDriverTests
{
    [Fact]
    public void OvertimeBackPayAdjustment_RoutesToTheOvertimeExpenseDriver()
    {
        var code = $"ADJ_{PayrollRunProcessor.NormalizeCode("Overtime Back Pay")}";
        PayrollRunProcessor.EarningDriverKey(code, "Adjustment").Should().Be("EARN:OVERTIME");
    }

    [Fact]
    public void OtherAdjustments_StillRouteToOtherEarnings() =>
        PayrollRunProcessor.EarningDriverKey("ADJ_SPOT_AWARD", "Adjustment").Should().Be("EARN:OTHER");
}
