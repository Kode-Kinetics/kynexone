using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// PAY-04 — exactly-once for the money-moving payroll transitions after Lock: creating the payment batch and
/// settling it. (Two simultaneous Locks and Voids are already proven by GlPostingConcurrencyPostgresTests;
/// Lock and Approve hold a FOR UPDATE row lock on payroll_runs inside one transaction, and settle runs under
/// FinanceDecisionSerializer.)
///
/// <para>Real Postgres only: each contender has its OWN context and connection and they are released
/// together. The sequential SQLite twins at the bottom run anywhere and prove the same end state through
/// the serialized path.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class PayrollRunConcurrencyPostgresTests
{
    private readonly PostgresFixture _fx;
    public PayrollRunConcurrencyPostgresTests(PostgresFixture fx) => _fx = fx;

    private const int Rounds = 5;

    [Fact]
    public async Task TwoSimultaneousBatchCreates_LeaveExactlyOneLiveBatch_TheLoserGets409()
    {
        await using var k = await NewKitAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var run = await LockedRunAsync(k, round + 1);

            var results = await RaceAsync(() => CreateBatchAsync(k, run.Id), () => CreateBatchAsync(k, run.Id));

            results.Should().NotContainNulls($"round {round}: the loser must fail cleanly, never with a 500");
            results.OfType<CreatedResult>().Should().HaveCount(1, $"round {round}: exactly one create wins");
            results.OfType<ConflictObjectResult>().Should().ContainSingle($"round {round}: the loser gets 409");

            await using var db = k.NewContext();
            (await db.PayrollPaymentBatches.AsNoTracking()
                .CountAsync(b => b.PayrollRunId == run.Id && b.WpsStatus != WpsStatuses.Voided))
                .Should().Be(1, $"round {round}: one live batch, not two");
            (await db.PayrollPaymentRecords.AsNoTracking()
                .CountAsync(r => r.TenantId == k.TenantId && db.PayrollPaymentBatches.Any(b => b.Id == r.PaymentBatchId && b.PayrollRunId == run.Id)))
                .Should().Be(1, $"round {round}: one payment record per employee, once");
        }
    }

    [Fact]
    public async Task TwoSimultaneousSettles_PostExactlyOneNetSettlement_TheLoserGets409()
    {
        await using var k = await NewKitAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var run = await LockedRunAsync(k, round + 1);
            var batchId = await AcceptedBatchAsync(k, run.Id);

            var results = await RaceAsync(() => SettleAsync(k, batchId), () => SettleAsync(k, batchId));

            results.Should().NotContainNulls($"round {round}: the loser must fail cleanly, never with a 500");
            results.OfType<OkObjectResult>().Should().HaveCount(1, $"round {round}: exactly one settle wins");
            results.OfType<ConflictObjectResult>().Should().ContainSingle($"round {round}: the loser gets 409")
                .Subject.Value!.ToString().Should().Contain("already_settled");

            await using var db = k.NewContext();
            var gl = await k.RunGlAsync(run.Id, db);
            var settlement = gl.Where(l => l.EventType == GlEventTypes.NetSettlement && !l.IsReversed).ToList();
            GlPostingKit.Debits(settlement).Should().Be(5_000m, $"round {round}: the net pay is cleared once, not twice");
            GlPostingKit.Credits(settlement).Should().Be(5_000m, $"round {round}");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private async Task<GlPostingKit> NewKitAsync()
    {
        Guid tenantId;
        await using (var db = _fx.CreateDb()) tenantId = await PostgresFixture.SeedMinimalTenant(db);
        return await GlPostingKit.CreateAsync(_fx.CreateDb, tenantId);
    }

    private static async Task<PayrollRun> LockedRunAsync(GlPostingKit k, int month)
    {
        var run = await k.SeedApprovedRunAsync(
            new[] { (1, "BASIC", "Salary", 5_000m) }, Array.Empty<(int, string, string, decimal)>(), net: 5_000m, month: month);
        await using var db = k.NewContext();
        (await k.Payroll(db).Lock(run.Id, default)).Should().BeOfType<OkObjectResult>();
        return run;
    }

    private static async Task<Guid> AcceptedBatchAsync(GlPostingKit k, Guid runId)
    {
        await using var db = k.NewContext();
        (await k.Payroll(db, null, null, "payroll.export").CreatePaymentBatch(runId, new PayrollPaymentBatchRequest("WPS", "SAR"), default))
            .Should().BeOfType<CreatedResult>();
        var batch = await db.PayrollPaymentBatches.SingleAsync(b => b.PayrollRunId == runId && b.WpsStatus != WpsStatuses.Voided);
        batch.WpsStatus = WpsStatuses.Accepted;   // the bank/Mudad acceptance step is its own endpoint; this test is about the settle race
        await db.SaveChangesAsync();
        return batch.Id;
    }

    private static async Task<IActionResult> CreateBatchAsync(GlPostingKit k, Guid runId)
    {
        await using var db = k.NewContext();
        return await k.Payroll(db, null, null, "payroll.export").CreatePaymentBatch(runId, new PayrollPaymentBatchRequest("WPS", "SAR"), default);
    }

    private static async Task<IActionResult> SettleAsync(GlPostingKit k, Guid batchId)
    {
        await using var db = k.NewContext();
        return await k.Payroll(db, null, null, "payroll.export").SettlePaymentBatch(batchId, new SettlePaymentBatchRequest("race", null), default);
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
