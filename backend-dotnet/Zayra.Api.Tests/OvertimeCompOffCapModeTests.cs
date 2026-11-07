using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Policy;
using Zayra.Api.Models;
using static Zayra.Api.Tests.OvertimeFixKit;

namespace Zayra.Api.Tests;

/// <summary>overtime.compoff_cap_mode: WholeDays (default, rounds the cap UP to a whole day) or Fractional (hours / 8).</summary>
public class OvertimeCompOffCapModeTests
{
    private static readonly string[] Adm = { "Admin" };

    private static async Task<(World W, OvertimeRequest R)> ArrangeAsync(int approvedMinutes, string? mode)
    {
        var w = await SeedAsync();
        if (mode is not null)
            w.Db.SystemSettings.Add(new SystemSetting
            {
                TenantId = w.TenantId, Category = PolicyRegistry.StorageCategory,
                SettingKey = PolicyRegistry.OvertimeCompoffCapMode, SettingValue = mode,
            });
        var r = await AddRequestAsync(w, status: "Approved", minutes: approvedMinutes, approvedMinutes: approvedMinutes);
        await w.Db.SaveChangesAsync();
        return (w, r);
    }

    private static Task<ActionResult<OvertimeCompOffConversion>> Convert(World w, OvertimeRequest r, decimal days) =>
        Controller(w, Guid.NewGuid(), Adm).CreateCompOffConversion(new CompOffConversionRequest(r.Id, days), CancellationToken.None);

    [Fact]
    public async Task Fractional_AllowsExactlyHoursOverEight()
    {
        var (w, r) = await ArrangeAsync(120, "Fractional");   // 2 h = 0.25 day
        (await Convert(w, r, 0.25m)).Result.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task Fractional_RefusesAboveHoursOverEight_WhereWholeDaysWouldAllowOneDay()
    {
        var (w, r) = await ArrangeAsync(120, "Fractional");
        (await Convert(w, r, 1m)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await Convert(w, r, 0.26m)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("WholeDays")]
    public async Task WholeDays_IsTheDefault_AndStillRoundsAPartDayUpToOne(string? mode)
    {
        var (w, r) = await ArrangeAsync(120, mode);
        (await Convert(w, r, 1m)).Result.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task OtherTenantsSetting_DoesNotLeakIn()
    {
        var (w, r) = await ArrangeAsync(120, null);
        w.Db.SystemSettings.Add(new SystemSetting
        {
            TenantId = Guid.NewGuid(), Category = PolicyRegistry.StorageCategory,
            SettingKey = PolicyRegistry.OvertimeCompoffCapMode, SettingValue = "Fractional",
        });
        await w.Db.SaveChangesAsync();
        (await Convert(w, r, 1m)).Result.Should().BeOfType<CreatedResult>();
    }

    // ── Grants are made at the precision the tables keep (2 decimals), rounded DOWN, so what is persisted never exceeds the cap ──

    private static async Task<(World W, OvertimeRequest R)> ArrangeWithCashAsync(int approvedMinutes, string mode)
    {
        var (w, r) = await ArrangeAsync(approvedMinutes, mode);
        w.Db.OvertimePayrollImpacts.Add(new OvertimePayrollImpact
        {
            TenantId = w.TenantId, OvertimeRequestId = r.Id, EmployeeId = r.EmployeeId, Minutes = approvedMinutes, Amount = 100m,
            ApprovedMultiplier = 1.5m, Status = "PendingPayroll",
        });
        await w.Db.SaveChangesAsync();
        return (w, r);
    }

    [Fact]
    public async Task Fractional_SixtyMinutes_GrantsTwelveHundredthsRoundedDown_NotThirteen()
    {
        var (w, r) = await ArrangeAsync(60, "Fractional");   // 1 h / 8 = 0.125 day; the tables keep 2 decimals

        (await Convert(w, r, 0.125m)).Result.Should().BeOfType<CreatedResult>();

        (await w.Db.OvertimeCompOffConversions.AsNoTracking().SingleAsync()).CompOffDays.Should().Be(0.12m, "0.125 persisted as 0.13 would exceed the cap");
        (await w.Db.CompOffCredits.AsNoTracking().SingleAsync()).DaysEarned.Should().Be(0.12m);
    }

    [Fact]
    public async Task Fractional_SixtyMinutes_CapIsTheRoundedDownFigure_SoThirteenHundredthsIsRefused()
    {
        var (w, r) = await ArrangeAsync(60, "Fractional");

        (await Convert(w, r, 0.13m)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await Convert(w, r, 0.12m)).Result.Should().BeOfType<CreatedResult>();
    }

    [Theory]
    [InlineData(1, 0.0021)]    // the exact cap for one minute, which the tables would store as 0.00
    [InlineData(1, 0.01)]      // nothing at all can be granted for a minute
    [InlineData(120, 0.004)]   // under the smallest unit that is kept
    public async Task Fractional_AGrantThatRoundsToZero_IsRefusedWithAClearCode_AndTheCashIsUntouched(int approvedMinutes, double days)
    {
        var (w, r) = await ArrangeWithCashAsync(approvedMinutes, "Fractional");

        var result = await Convert(w, r, (decimal)days);

        var refused = result.Result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        JsonSerializer.SerializeToElement(refused.Value).GetProperty("code").GetString().Should().Be("compoff_below_minimum");
        (await w.Db.CompOffCredits.CountAsync()).Should().Be(0);
        (await w.Db.OvertimeCompOffConversions.CountAsync()).Should().Be(0);
        (await w.Db.OvertimePayrollImpacts.AsNoTracking().SingleAsync(x => x.OvertimeRequestId == r.Id)).Status
            .Should().Be("PendingPayroll", "cash is never withdrawn for zero leave");
    }

    [Fact]
    public async Task WholeDays_AFractionOfADayThatTheTablesWouldStoreAsZero_IsRefused_ButANormalDayIsUnchanged()
    {
        var (w, r) = await ArrangeWithCashAsync(120, "WholeDays");

        (await Convert(w, r, 0.004m)).Result.Should().BeOfType<UnprocessableEntityObjectResult>();
        (await w.Db.OvertimePayrollImpacts.AsNoTracking().SingleAsync(x => x.OvertimeRequestId == r.Id)).Status.Should().Be("PendingPayroll");
        (await Convert(w, r, 1m)).Result.Should().BeOfType<CreatedResult>();
        (await w.Db.OvertimeCompOffConversions.AsNoTracking().SingleAsync()).CompOffDays.Should().Be(1m);
    }
}
