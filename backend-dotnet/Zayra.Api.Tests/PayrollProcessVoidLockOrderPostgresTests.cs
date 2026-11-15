using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll;
using Kit = Zayra.Api.Tests.DebtRacePostgresKit;

namespace Zayra.Api.Tests;

/// <summary>
/// Payroll Process and a run void on the same run. A run in a processable status (Draft, Failed) can be voided: a void takes the
/// run row FOR UPDATE first, then (for a cascade over a live child run) the debt locks of the chain. Process used to take the
/// debt locks first and write the run row last, the opposite order, so a void cascading over a Draft correction run could
/// deadlock with the Process of that child (SQLSTATE 40P01), and a Process that had read the run before a void committed would
/// then write "Processed" over "Voided". Process now takes the run row FOR UPDATE as the first step of its transaction and
/// re-checks the status under the lock; the one lock order is documented on <see cref="FinanceDecisionSerializer.AcquireDebtLocksAsync"/>.
/// Settle needs a Locked run, which Process refuses, so settle and Process cannot meet on one run.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class PayrollProcessVoidLockOrderPostgresTests(PostgresFixture fixture)
{
    /// <summary>The void holds the Draft run (done, not committed) when a Process of it arrives. Process must wait for the void,
    /// then refuse the voided run: nothing is processed, the run stays Voided and the loan keeps its balance.</summary>
    [Fact]
    public async Task ProcessArrivingWhileAVoidHoldsTheRun_WaitsThenRefusesTheVoidedRun_AndDoesNotResurrectIt()
    {
        var b = await Kit.SeedBorrowerAsync(fixture, 100m, 2);
        var runId = await Kit.SeedDraftRunAsync(fixture, b, b.Today.Year, b.Today.Month);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var processDb = fixture.CreateDb();
        var processPid = await Kit.OpenAndPidAsync(processDb, timeout.Token);
        await using var voidDb = fixture.CreateDb();

        PayrollProcessOutcome? outcome = null;
        var waited = false;
        await voidDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await voidDb.Database.BeginTransactionAsync(timeout.Token);
            var voided = await new PayrollVoidService(voidDb)
                .VoidAsync(runId, b.Tenant, Guid.NewGuid(), "Payroll Manager", "Created in error", timeout.Token);
            Assert.True(voided.IsVoided, $"{voided.ResultKind} {voided.BlockCode} {voided.BlockMessage}");

            var processing = Task.Run(() => Kit.ProcessAsync(processDb, b, runId, null, timeout.Token));
            waited = await Kit.WaitForLockAsync(fixture, processPid, advisoryOnly: false, processing, timeout.Token);
            await tx.CommitAsync(timeout.Token);
            outcome = await processing;
        });

        Assert.True(waited, "Process must wait for the run the void holds, before the void commits");
        Assert.Equal(400, outcome!.StatusCode);
        Assert.Contains("run_voided", System.Text.Json.JsonSerializer.Serialize(outcome.Body));
        await using var verify = fixture.CreateDb();
        Assert.Equal("Voided", (await verify.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == runId)).Status);
        Assert.Empty(await verify.PayrollSlips.AsNoTracking().Where(s => s.RunId == runId).ToListAsync());
        var loan = await verify.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == b.LoanId);
        Assert.Equal((100m, 0m), (loan.OutstandingBalance, loan.TotalRepaid));
    }

    /// <summary>
    /// The deadlock, forced. A Locked parent run took the loan's first instalment; a live Draft child run amends it. Someone holds
    /// the loan lock (a cash payoff, say) while a Process of the child and a cascading void of the parent both queue behind it,
    /// the Process first. When the loan is released the Process gets it: it must already hold the child run, or it would wait for
    /// the run row the void holds while the void waits for the loan lock the Process holds. Both must finish, no 40P01.
    /// </summary>
    [Fact]
    public async Task ProcessOfADraftChild_AndACascadingVoidOfItsParent_QueuedOnOneLoanLock_DoNotDeadlock()
    {
        var b = await Kit.SeedBorrowerAsync(fixture, 100m, 2);
        var parent = await Kit.SeedDraftRunAsync(fixture, b, b.Today.Year, b.Today.Month);
        await using (var db = fixture.CreateDb())
        {
            var took = await Kit.ProcessAsync(db, b, parent, null, CancellationToken.None);
            Assert.True(took.StatusCode == 200, System.Text.Json.JsonSerializer.Serialize(took.Body));
            await db.PayrollRuns.Where(r => r.Id == parent).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Locked"));
        }
        var next = b.Today.AddMonths(1);
        var child = await Kit.SeedDraftRunAsync(fixture, b, next.Year, next.Month);
        await using (var db = fixture.CreateDb())
            await db.PayrollRuns.Where(r => r.Id == child).ExecuteUpdateAsync(s => s.SetProperty(r => r.ParentRunId, parent));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var holderDb = fixture.CreateDb();
        await using var processDb = fixture.CreateDb();
        var processPid = await Kit.OpenAndPidAsync(processDb, timeout.Token);
        await using var voidDb = fixture.CreateDb();
        var voidPid = await Kit.OpenAndPidAsync(voidDb, timeout.Token);

        PayrollProcessOutcome? processed = null;
        VoidRunResult? voided = null;
        Exception? failure = null;
        await holderDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var holder = await holderDb.Database.BeginTransactionAsync(timeout.Token);
            await FinanceDecisionSerializer.AcquireAsync(holderDb, FinanceDecisionSerializer.ScopeLoan, b.Tenant, b.LoanId, timeout.Token);

            var processing = Task.Run(() => Kit.ProcessAsync(processDb, b, child, null, timeout.Token));
            Assert.True(await Kit.WaitForLockAsync(fixture, processPid, advisoryOnly: false, processing, timeout.Token),
                "the Process of the child queues behind the loan lock");
            var voiding = Task.Run(() => new PayrollVoidService(voidDb).VoidAsync(parent, b.Tenant, Guid.NewGuid(), "Payroll Manager",
                "Wrong month", timeout.Token, new PayrollVoidOptions { Cascade = true, ExpectedChildRunIds = new[] { child } }));
            Assert.True(await Kit.WaitForLockAsync(fixture, voidPid, advisoryOnly: false, voiding, timeout.Token),
                "the cascading void queues behind the same loan lock");
            await holder.CommitAsync(timeout.Token);

            try { processed = await processing; voided = await voiding; }
            catch (Exception ex) { failure = ex; }
        });

        Assert.True(failure is null, $"neither side may fail on a deadlock: {failure}");
        Assert.Equal(200, processed!.StatusCode);
        Assert.True(voided!.IsVoided, $"{voided.ResultKind} {voided.BlockCode} {voided.BlockMessage}");
        await using var verify = fixture.CreateDb();
        var statuses = await verify.PayrollRuns.AsNoTracking().Where(r => r.Id == parent || r.Id == child)
            .ToDictionaryAsync(r => r.Id, r => r.Status);
        Assert.Equal("Voided", statuses[parent]);
        Assert.Equal("Voided", statuses[child]);
        // Both runs' instalments came back: the loan owes what it owed before either run.
        var loan = await verify.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == b.LoanId);
        Assert.Equal((100m, 0m), (loan.OutstandingBalance, loan.TotalRepaid));
    }
}
