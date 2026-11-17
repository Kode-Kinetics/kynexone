using System.Text.Json;
using FluentAssertions;
using Zayra.Api.Infrastructure.Entitlements;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Models;
using static Zayra.Api.Tests.ReleaseA.Bd;

namespace Zayra.Api.Tests.ReleaseA;

/// <summary>
/// A run processed while a change waited for Finance paid the last confirmed terms. Once the change is confirmed, the run is stale whenever its month's
/// allowance is no longer what it paid: other terms, or no allowance in effect that month at all (switched off or ended). It is processed again, never
/// approved as it stands.
/// </summary>
public partial class CashAllowanceRependKeepsPayingTests
{
    private static async Task<IActionResult> ApproveAsync(World w, PayrollRun run)
    {
        await ReconTestSupport.SignOffAsync(w.Db, run.Id);
        w.Db.ChangeTracker.Clear();
        var result = await PayrollProrationArrearsTests.Ctrl(w.Db, w.Tid).Approve(run.Id, new PayrollDecisionRequest("ok"), CancellationToken.None);
        w.Db.ChangeTracker.Clear();
        return result;
    }

    private static string? ErrorOf(IActionResult r) =>
        JsonSerializer.SerializeToElement((r as ObjectResult)?.Value, Web).TryGetProperty("error", out var e) ? e.GetString() : null;

    [Theory]
    [InlineData("off")]
    [InlineData("end")]
    public async Task ARunThatPaidAnAllowanceSwitchedOffOrEndedSince_IsStaleAtApproval(string change)
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        await w.GiveAsync(w.A, 500m);
        var edit = change == "end"
            ? new UpdateBenefitRequest(await w.VersionAsync(), "Car", null) { EffectiveTo = new DateOnly(2026, 5, 31) }
            : new UpdateBenefitRequest(await w.VersionAsync(), "Car", null) { IsActive = false };
        Component(await w.As(w.Hr).UpdateBenefit(w.Service, Car, edit, default));
        var june = await w.RunAsync(2026, 6);
        (await w.Process(june)).Should().BeOfType<OkObjectResult>();
        Status(await w.As(w.FinanceB, finance: true).ConfirmBenefit(w.Service, Car, null, default)).Should200("confirmed while June is only processed");

        var refused = await ApproveAsync(w, june);

        refused.Should().BeOfType<ConflictObjectResult>(Describe(refused));
        ErrorOf(refused).Should().Be("payroll_run_stale_definition");
        (await w.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == june.Id)).Status.Should().Be("Processed");
    }

    [Fact]
    public async Task ARunWhoseAllowanceIsStillInEffect_IsApproved()
    {
        await using var w = await World.Create();
        await w.AddConfirmedCarAsync();
        await w.GiveAsync(w.A, 500m);
        var june = await w.RunAsync(2026, 6);
        (await w.Process(june)).Should().BeOfType<OkObjectResult>();

        (await ApproveAsync(w, june)).Should().BeOfType<OkObjectResult>();
    }
}
