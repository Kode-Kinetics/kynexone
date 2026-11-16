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
    public async Task TwoSimultaneousSettles_PostExactlyOneNetSettlement_TheLoserIsRefused()
    {
        await using var k = await NewKitAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var run = await LockedRunAsync(k, round + 1);
            var batchId = await AcceptedBatchAsync(k, run.Id);

            var results = await RaceAsync(() => SettleAsync(k, batchId), () => SettleAsync(k, batchId));

            results.Should().NotContainNulls($"round {round}: the loser must fail cleanly, never with a 500");
            results.OfType<OkObjectResult>().Should().HaveCount(1, $"round {round}: exactly one settle wins");
            // The settle runs under the per-batch advisory lock, so the loser reads the batch AFTER the winner committed:
            // it is already Paid, and the status guard (400 batch_not_accepted) refuses it before the already_settled
            // idempotency check (409) is reached. Either way it is a refusal that posts nothing.
            results.OfType<BadRequestObjectResult>().Should().ContainSingle($"round {round}: the loser is refused, not a second success")
                .Subject.Value!.ToString().Should().Contain("batch_not_accepted");

            await using var db = k.NewContext();
            var gl = await k.RunGlAsync(run.Id, db);
            var settlement = gl.Where(l => l.EventType == GlEventTypes.NetSettlement && !l.IsReversed).ToList();
            GlPostingKit.Debits(settlement).Should().Be(5_000m, $"round {round}: the net pay is cleared once, not twice");
            GlPostingKit.Credits(settlement).Should().Be(5_000m, $"round {round}");
        }
    }

    /// <summary>
    /// Settle used to be serialized per BATCH while Void is serialized per RUN (FOR UPDATE on the run row), so a
    /// settle that read the run as Locked could post its cash journal after a void had committed. A void is in
    /// flight here (the test holds the run row lock): the settle must WAIT for it, then see the voided run and
    /// refuse, posting nothing. Before the fix the settle ran straight through and posted DR 2100 / CR 1000 on a
    /// run that was being voided.
    /// </summary>
    [Fact]
    public async Task SettleWaitsForAnInFlightRunLock_ThenRefusesAVoidedRun_PostingNothing()
    {
        await using var k = await NewKitAsync();
        var run = await LockedRunAsync(k, 1);
        var batchId = await AcceptedBatchAsync(k, run.Id);

        Task<IActionResult> settle = null!;
        await using (var holder = k.NewContext())
        {
            await holder.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var tx = await holder.Database.BeginTransactionAsync();
                var locked = await holder.PayrollRuns.TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag)
                    .SingleAsync(r => r.Id == run.Id);
                settle = Task.Run(() => SettleAsync(k, batchId));

                var finishedEarly = await Task.WhenAny(settle, Task.Delay(TimeSpan.FromSeconds(3))) == settle;
                finishedEarly.Should().BeFalse("settle must queue behind the run row lock a void holds, not run alongside it");

                locked.Status = "Voided";
                await holder.SaveChangesAsync();
                await tx.CommitAsync();
            });
        }

        var result = await settle;
        result.Should().BeOfType<BadRequestObjectResult>().Which.Value!.ToString().Should().Contain("run_not_locked");

        await using var db = k.NewContext();
        (await k.RunGlAsync(run.Id, db)).Where(l => l.EventType == GlEventTypes.NetSettlement).Should().BeEmpty(
            "nothing is settled against a voided run");
        (await db.PayrollPaymentBatches.AsNoTracking().SingleAsync(b => b.Id == batchId)).WpsStatus.Should().Be(WpsStatuses.Accepted);
    }

    /// <summary>The cleared amount is asserted against what the batch says it pays. A batch whose total no longer
    /// matches the net-pay liability it would clear is refused, never settled for "whatever the ledger holds".</summary>
    [Fact]
    public async Task Settle_RefusesWhenTheClearedAmountDiffersFromTheBatchTotal_PostingNothing()
    {
        await using var k = await NewKitAsync();
        var run = await LockedRunAsync(k, 1);
        var batchId = await AcceptedBatchAsync(k, run.Id);
        await using (var tamper = k.NewContext())
        {
            var b = await tamper.PayrollPaymentBatches.SingleAsync(x => x.Id == batchId);
            b.TotalAmount = 4_000m;   // the bank file says 4,000; the liability this would clear is 5,000
            await tamper.SaveChangesAsync();
        }

        var result = await SettleAsync(k, batchId);

        result.Should().BeOfType<UnprocessableEntityObjectResult>().Which.Value!.ToString().Should().Contain("settlement_amount_mismatch");
        await using var db = k.NewContext();
        (await k.RunGlAsync(run.Id, db)).Where(l => l.EventType == GlEventTypes.NetSettlement).Should().BeEmpty();
        (await db.PayrollPaymentBatches.AsNoTracking().SingleAsync(b => b.Id == batchId)).WpsStatus.Should().Be(WpsStatuses.Accepted);
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
