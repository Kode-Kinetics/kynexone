using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
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
}
