using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;
using static Zayra.Api.Tests.ReleaseA.Bd;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>Re-check of the split at confirmation: a confirmed switch-off leaves no inactive version behind, a refusal names a way out that works, and the
/// closed version says why it ends where it does.</summary>
public partial class CashAllowanceRependKeepsPayingTests
{
    private static readonly System.Text.RegularExpressions.Regex Arabic = new("[؀-ۿ]");

    private static JsonElement BodyOf(IActionResult r) => JsonSerializer.SerializeToElement((r as ObjectResult)?.Value, Web);

    [Fact]
    public async Task ASwitchOffConfirmedAcrossACommittedMonth_LeavesOneActiveVersion_ThatCanBeSwitchedBackOnAndRedated()
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        await w.GiveAsync(w.A, 500m);
        Component(await w.As(w.Hr).UpdateBenefit(w.Service, Car, new UpdateBenefitRequest(await w.VersionAsync(), "Car", null) { IsActive = false }, default));
        var june = await w.RunAsync(2026, 6);
        (await w.Process(june)).Should().BeOfType<OkObjectResult>();
        await w.MarkAsync(june, "Approved");
        Status(await w.As(w.FinanceB, finance: true).ConfirmBenefit(w.Service, Car, null, default)).Should200("");

        var rows = await w.Db.PayComponents.AsNoTracking().Where(x => x.TenantId == w.Tid && x.Code == Car).ToListAsync();
        rows.Should().ContainSingle("a switch-off adds no version: the confirmed one simply ends with the committed month");
        (rows[0].IsActive, rows[0].EffectiveTo).Should().Be((true, (DateOnly?)new DateOnly(2026, 6, 30)));
        (await w.InEffectAsync(July1)).Should().BeNull("off from July");

        // Switched back on from July (re-dated: the end is lifted): allowed, and waits for Finance like any money change.
        var back = await w.As(w.Hr).UpdateBenefit(w.Service, Car, new UpdateBenefitRequest(await w.VersionAsync(), "Car", null) { IsActive = true, EffectiveTo = null }, default);
        Status(back).Should200(Describe(back));
        Status(await w.As(w.FinanceA, finance: true).ConfirmBenefit(w.Service, Car, null, default)).Should200("");
        (await w.InEffectAsync(July1)).Should().NotBeNull("back on from July");
        (await w.InEffectAsync(June1))!.GlDriverKey.Should().Be(CarDriver, "June keeps what it was paid on");
    }
}
