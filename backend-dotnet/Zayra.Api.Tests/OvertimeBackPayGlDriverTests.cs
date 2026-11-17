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

/// <summary>
/// The same rule on a tenant that HAS gl_drivers rows (every tenant created through GlDriverSeeder). The persisted catch-all
/// EARN:OTHER is seeded `Any` and claimed ADJ_OVERTIME_BACK_PAY before the compiled switch was consulted, so late-approved
/// overtime posted to 5099 Other Earnings while on-time overtime posted to 5005. Found by OvertimeMoneyProofTests.
/// </summary>
public class OvertimeBackPayPersistedDriverTests
{
    private static IReadOnlyList<GlDriverRow> SeededDrivers() =>
        Zayra.Api.Models.PayrollGlCatalog.SystemDriverSeeds(Guid.NewGuid())
            .Select(d => new GlDriverRow(d.Key, d.Category, d.PostingSide, d.MatchSource, d.MatchMode,
                d.MatchComponentCode, d.EmitsEmployerExpensePair, d.PairedExpenseDriverKey, d.IsSystem, d.SortOrder))
            .ToList();

    [Fact]
    public void OnASeededTenant_OvertimeBackPay_PostsToTheOvertimeDriver_NotOtherEarnings() =>
        PayrollRunProcessor.EarningDriverKeyFor(SeededDrivers(), "ADJ_OVERTIME_BACK_PAY", "Adjustment").Should().Be("EARN:OVERTIME");

    [Fact]
    public void OnASeededTenant_AnyOtherAdjustment_StillPostsToOtherEarnings() =>
        PayrollRunProcessor.EarningDriverKeyFor(SeededDrivers(), "ADJ_SPOT_AWARD", "Adjustment").Should().Be("EARN:OTHER");

    [Fact]
    public void ATenantDriverThatClaimsBackPayByItsCode_StillWins()
    {
        var drivers = SeededDrivers().Append(new GlDriverRow("EARN:OT_BACKPAY", "Earning", "DR", null, "Exact", "ADJ_OVERTIME_BACK_PAY", false, null, false, 5)).ToList();
        PayrollRunProcessor.EarningDriverKeyFor(drivers, "ADJ_OVERTIME_BACK_PAY", "Adjustment").Should().Be("EARN:OT_BACKPAY");
    }
}
