using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// P0 — double posting. Lock and Void each read the run, checked "already done?" and wrote a full
/// journal with no transaction and no row lock, so two simultaneous requests both posted: a month
/// accrued twice, or reversed twice. Real Postgres only (an in-memory provider has no row locks, so a
/// race test there proves nothing); each contender has its OWN context and connection, they are released
/// together from a barrier, and every scenario repeats over freshly seeded runs.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class GlPostingConcurrencyPostgresTests
{
    private readonly PostgresFixture _fx;
    public GlPostingConcurrencyPostgresTests(PostgresFixture fx) => _fx = fx;

    private const int Rounds = 5;

    [Fact]
    public async Task TwoSimultaneousLocks_PostExactlyOneAccrual_TheLoserGets409()
    {
        await using var k = await NewKitAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var run = await k.SeedApprovedRunAsync(
                new[] { (1, "BASIC", "Salary", 5_000m), (2, "BASIC", "Salary", 4_000m) },
                new[] { (1, "ATTENDANCE", "Attendance", 100m) },
                net: 8_900m, month: round + 1);

            var results = await RaceAsync(() => LockAsync(k, run.Id), () => LockAsync(k, run.Id));

            results.Should().NotContainNulls($"round {round}: the loser must fail cleanly, never with a 500");
            results.OfType<OkObjectResult>().Should().HaveCount(1, $"round {round}: exactly one Lock wins");
            results.OfType<ConflictObjectResult>().Should().ContainSingle($"round {round}: the loser gets 409")
                .Subject.Value!.ToString().Should().Contain("run_already_locked");

            await using var db = k.NewContext();
            var gl = await k.RunGlAsync(run.Id, db);
            // One accrual = BASIC (both employees, one group) + the attendance contra + net pay.
            gl.Where(l => l.EventType == GlEventTypes.Accrual).Should().HaveCount(3,
                $"round {round}: one accrual journal, not two");
            GlPostingKit.Debits(gl).Should().Be(9_000m, $"round {round}: payroll cost booked once");
            GlPostingKit.Credits(gl).Should().Be(9_000m, $"round {round}");
            (await db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).Status.Should().Be("Locked");
            (await db.PayrollAuditLogs.AsNoTracking().CountAsync(a => a.TenantId == k.TenantId
                && a.Action == "payroll.run.locked" && a.EntityId == run.Id.ToString()))
                .Should().Be(1, $"round {round}: one lock, one audit entry");
        }
    }

    [Fact]
    public async Task TwoSimultaneousVoids_ReverseTheAccrualExactlyOnce()
    {
        await using var k = await NewKitAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var run = await k.SeedApprovedRunAsync(
                new[] { (1, "BASIC", "Salary", 5_000m) }, new[] { (1, "LEAVE", "Leave", 250m) },
                net: 4_750m, month: round + 1);
            await using (var lockDb = k.NewContext())
                (await k.Payroll(lockDb).Lock(run.Id, default)).Should().BeOfType<OkObjectResult>();

            var results = await RaceAsync(() => VoidAsync(k, run.Id), () => VoidAsync(k, run.Id));

            results.Should().NotContainNulls($"round {round}: the loser must fail cleanly, never throw");
            results.Count(r => r!.IsVoided).Should().Be(1, $"round {round}: exactly one void wins");
            results.Count(r => r!.IsAlreadyVoid).Should().Be(1, $"round {round}: the loser is told it is already voided");

            await using var db = k.NewContext();
            var gl = await k.RunGlAsync(run.Id, db);
            var accrual = gl.Where(l => l.EventType == GlEventTypes.Accrual).ToList();
            var contras = gl.Where(l => l.EventType == GlEventTypes.Void).ToList();
            contras.Should().HaveCount(accrual.Count, $"round {round}: one contra per accrual line, not two");
            GlPostingKit.Debits(gl).Should().Be(GlPostingKit.Credits(gl), $"round {round}");
            accrual.Should().OnlyContain(l => l.IsReversed);
            (await db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).Status.Should().Be("Voided");
        }
    }

    private async Task<GlPostingKit> NewKitAsync()
    {
        Guid tenantId;
        await using (var db = _fx.CreateDb()) tenantId = await PostgresFixture.SeedMinimalTenant(db);
        return await GlPostingKit.CreateAsync(_fx.CreateDb, tenantId);
    }

    private static async Task<IActionResult> LockAsync(GlPostingKit k, Guid runId)
    {
        await using var db = k.NewContext();
        return await k.Payroll(db).Lock(runId, default);
    }

    private static async Task<VoidRunResult> VoidAsync(GlPostingKit k, Guid runId)
    {
        await using var db = k.NewContext();
        return await new PayrollVoidService(db).VoidAsync(runId, k.TenantId, null, "race", "concurrency test", default);
    }

    /// <summary>Both contenders start, warm up, and are released together. A throw yields null.</summary>
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
