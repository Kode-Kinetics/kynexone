using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Exactly-once statutory remittance. "Remit GOSI" used to read the already-remitted probe and insert the
/// journal with no transaction, no row lock and no posting key, so two simultaneous calls both posted
/// DR 2101/2106 / CR 1000: the payment went out twice and the journal still balanced, so nothing flagged it.
/// Real Postgres: each contender has its OWN context and connection and they are released together.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class StatutoryRemitConcurrencyPostgresTests
{
    private readonly PostgresFixture _fx;
    public StatutoryRemitConcurrencyPostgresTests(PostgresFixture fx) => _fx = fx;

    private const int Rounds = 5;

    [Fact]
    public async Task TwoSimultaneousGosiRemits_PostExactlyOnePayment_TheLoserIsRefused()
    {
        await using var k = await NewKitAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var run = await LockedRunWithGosiAsync(k, round + 1);

            var results = await RaceAsync(() => RemitAsync(k, run.Id, "GOSI"), () => RemitAsync(k, run.Id, "GOSI"));

            results.Should().NotContainNulls($"round {round}: the loser must fail cleanly, never with a 500");
            results.OfType<OkObjectResult>().Should().HaveCount(1, $"round {round}: exactly one remit wins");
            results.OfType<ConflictObjectResult>().Should().ContainSingle($"round {round}: the loser gets 409");

            await using var db = k.NewContext();
            var remit = (await k.RunGlAsync(run.Id, db)).Where(l => l.EventType == GlEventTypes.Remit("GOSI") && !l.IsReversed).ToList();
            GlPostingKit.Credits(remit).Should().Be(900m, $"round {round}: the GOSI payment leaves the bank once, not twice");
            GlPostingKit.Debits(remit).Should().Be(900m, $"round {round}");
        }
    }

    [Fact]
    public async Task TwoSimultaneousAllRemits_PostEachGroupOnce()
    {
        await using var k = await NewKitAsync();
        var run = await LockedRunWithGosiAsync(k, 1);

        var results = await RaceAsync(() => RemitAsync(k, run.Id, "All"), () => RemitAsync(k, run.Id, "All"));

        results.Should().NotContainNulls();
        results.OfType<OkObjectResult>().Should().HaveCount(1);
        results.OfType<ConflictObjectResult>().Should().ContainSingle();
        await using var db = k.NewContext();
        var remit = (await k.RunGlAsync(run.Id, db)).Where(l => l.EventType.StartsWith(GlEventTypes.RemitPrefix) && !l.IsReversed).ToList();
        GlPostingKit.Credits(remit).Should().Be(900m);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private async Task<GlPostingKit> NewKitAsync()
    {
        Guid tenantId;
        await using (var db = _fx.CreateDb()) tenantId = await PostgresFixture.SeedMinimalTenant(db);
        return await GlPostingKit.CreateAsync(_fx.CreateDb, tenantId);
    }

    private static async Task<PayrollRun> LockedRunWithGosiAsync(GlPostingKit k, int month)
    {
        // 600 employee share + 300 employer share of GOSI, withheld/accrued on one 5,000 salary.
        var run = await k.SeedApprovedRunAsync(
            new[] { (1, "BASIC", "Salary", 5_000m) },
            new[] { (1, "GOSI-ANN-EE", "Statutory", 600m), (1, "GOSI-ANN-ER", "Statutory", 300m) },
            net: 4_400m, month: month);
        await using var db = k.NewContext();
        (await k.Payroll(db).Lock(run.Id, default)).Should().BeOfType<OkObjectResult>();
        return run;
    }

    private static async Task<IActionResult> RemitAsync(GlPostingKit k, Guid runId, string group)
    {
        await using var db = k.NewContext();
        return await k.Payroll(db, null, null, "payroll.export").RemitStatutory(runId, new RemitStatutoryRequest(group, "race"), default);
    }

    private static async Task<T?[]> RaceAsync<T>(Func<Task<T>> first, Func<Task<T>> second) where T : class
    {
        var ready = new SemaphoreSlim(0, 2);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<T?> Run(Func<Task<T>> contender)
        {
            await Task.Yield();
            ready.Release();
            await go.Task;
            try { return await contender(); }
            catch { return null; }
        }
        var a = Task.Run(() => Run(first));
        var b = Task.Run(() => Run(second));
        await ready.WaitAsync();
        await ready.WaitAsync();
        go.SetResult();
        return await Task.WhenAll(a, b);
    }
}
