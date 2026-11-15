using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Infrastructure.Payroll;
using Kit = Zayra.Api.Tests.DebtRacePostgresKit;

namespace Zayra.Api.Tests;

/// <summary>
/// Voiding a payroll run gives back the loan and advance instalments it collected
/// (<see cref="PayrollVoidService.RestoreOperationalStateAsync"/>). It must give back exactly what that run took, on top of
/// whatever has happened to the balance since: a later run's collection or a cash settlement is not the void's to undo. And it
/// must take the same finance.loan / finance.advance locks every other writer of those balances takes, so a payoff racing the
/// void is applied to the restored balance, not overwritten by it (or the other way round).
///
/// <para>Real Postgres only; the racing tests observe the waiting side in <c>pg_stat_activity</c>.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class PayrollVoidDebtRestoreRacePostgresTests(PostgresFixture fixture)
{
    /// <summary>June's run took the first instalment, July's took the second and closed the loan and the advance. Voiding June
    /// gives back June's instalment only: the debt is owed again by exactly that amount, and July's collection stands.</summary>
    [Fact]
    public async Task VoidAfterALaterRunCollected_GivesBackOnlyWhatTheVoidedRunTook()
    {
        var b = await Kit.SeedBorrowerAsync(fixture, 100m, 2);
        var advanceId = await Kit.SeedAdvanceAsync(fixture, b, 60m, 2);
        var next = b.Today.AddMonths(1);
        var first = await ProcessNewRunAsync(b, b.Today.Year, b.Today.Month);
        await ProcessNewRunAsync(b, next.Year, next.Month);

        await using (var before = fixture.CreateDb())
        {
            var loan = await before.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == b.LoanId);
            var adv = await before.SalaryAdvances.AsNoTracking().SingleAsync(a => a.Id == advanceId);
            Assert.Equal((0m, 100m, "Closed"), (loan.OutstandingBalance, loan.TotalRepaid, loan.Status));
            Assert.Equal((0m, 60m, "Closed"), (adv.OutstandingBalance, adv.TotalRepaid, adv.Status));
        }

        await using (var db = fixture.CreateDb())
        {
            var result = await new PayrollVoidService(db).VoidAsync(first, b.Tenant, Guid.NewGuid(), "Payroll Manager", "Wrong month");
            Assert.True(result.IsVoided, $"{result.ResultKind} {result.BlockCode} {result.BlockMessage}");
        }

        await using var verify = fixture.CreateDb();
        var restored = await verify.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == b.LoanId);
        var restoredAdv = await verify.SalaryAdvances.AsNoTracking().SingleAsync(a => a.Id == advanceId);
        // Reopened (balance above zero again) with the second run's collection still counted as repaid.
        Assert.Equal((50m, 50m, "Active"), (restored.OutstandingBalance, restored.TotalRepaid, restored.Status));
        Assert.Equal((30m, 30m, "Active"), (restoredAdv.OutstandingBalance, restoredAdv.TotalRepaid, restoredAdv.Status));
    }

    /// <summary>
    /// Out of order, with the journal: a 1,500 advance in three instalments of 500. October's run takes 500 and is locked
    /// (its accrual credits 1410), November's takes 500 and is locked. Voiding October gives back October's 500 only: the
    /// advance owes 1,000 with 500 repaid, and 1410 on the ledger ties to the advance sub-ledger.
    /// </summary>
    [Fact]
    public async Task VoidOfAnEarlierLockedRun_GivesBackOnlyItsAdvanceInstalment_And1410TiesToTheSubLedger()
    {
        var b = await Kit.SeedBorrowerAsync(fixture, 100m, 2);
        var advanceId = await Kit.SeedAdvanceAsync(fixture, b, 1_500m, 3);
        var next = b.Today.AddMonths(1);
        var october = await ProcessNewRunAsync(b, b.Today.Year, b.Today.Month);
        await ApproveAndLockAsync(b, october);
        var november = await ProcessNewRunAsync(b, next.Year, next.Month);
        await ApproveAndLockAsync(b, november);

        await using (var db = fixture.CreateDb())
        {
            var result = await new PayrollVoidService(db).VoidAsync(october, b.Tenant, Guid.NewGuid(), "Payroll Manager", "Wrong month");
            Assert.True(result.IsVoided, $"{result.ResultKind} {result.BlockCode} {result.BlockMessage}");
        }

        await using var verify = fixture.CreateDb();
        var adv = await verify.SalaryAdvances.AsNoTracking().SingleAsync(a => a.Id == advanceId);
        Assert.Equal((1_000m, 500m, "Active"), (adv.OutstandingBalance, adv.TotalRepaid, adv.Status));
        var ledger = await verify.FinanceGlEntries.AsNoTracking()
            .Where(g => g.TenantId == b.Tenant && (g.DebitAccount.StartsWith("1410") || g.CreditAccount.StartsWith("1410")))
            .Select(g => new { g.DebitAccount, g.Amount }).ToListAsync();
        var gl1410 = ledger.Sum(g => g.DebitAccount.StartsWith("1410", StringComparison.Ordinal) ? g.Amount : -g.Amount);
        var subLedger = await verify.SalaryAdvances.AsNoTracking().Where(a => a.TenantId == b.Tenant).SumAsync(a => a.OutstandingBalance);
        Assert.Equal(subLedger, gl1410);
    }

    /// <summary>A cash payoff of the rest of the loan holds the loan (done, not committed) when the run that took the first
    /// instalment is voided. The void must wait for it, then give back only the run's instalment on top of the payoff.</summary>
    [Fact]
    public async Task VoidArrivingWhileAPayoffHoldsTheLoan_WaitsAndGivesBackOnlyTheRunsInstalment()
    {
        var (b, runId) = await SeedLockedRunThatTookOneInstalmentAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var voidDb = fixture.CreateDb();
        var voidPid = await Kit.OpenAndPidAsync(voidDb, timeout.Token);
        await using var payoffDb = fixture.CreateDb();

        await payoffDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await payoffDb.Database.BeginTransactionAsync(timeout.Token);
            var payoff = await Kit.Loans(payoffDb, b.Tenant).RecordRepayment(b.LoanId,
                new RecordLoanRepaymentRequest(50m, b.Today, "CASH-PAYOFF"), timeout.Token);
            Assert.IsType<OkObjectResult>(payoff);

            var voiding = Task.Run(() => new PayrollVoidService(voidDb)
                .VoidAsync(runId, b.Tenant, Guid.NewGuid(), "Payroll Manager", "Wrong month", timeout.Token));
            var waited = await Kit.WaitForLockAsync(fixture, voidPid, advisoryOnly: false, voiding, timeout.Token);
            await tx.CommitAsync(timeout.Token);
            var result = await voiding;

            Assert.True(waited, "the void must reach a lock the payoff holds before the payoff commits");
            Assert.True(result.IsVoided, $"{result.ResultKind} {result.BlockCode} {result.BlockMessage}");
        });

        await using var verify = fixture.CreateDb();
        var loan = await verify.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == b.LoanId);
        var cash = await verify.LoanRepayments.AsNoTracking().Where(r => r.LoanId == b.LoanId && !r.IsReversed).SumAsync(r => r.Amount);
        Assert.Equal(50m, cash);
        // The payoff's 50 stands; the voided run's 50 is owed again.
        Assert.Equal((50m, 50m, "Active"), (loan.OutstandingBalance, loan.TotalRepaid, loan.Status));
    }

    /// <summary>The void holds the loan (done, not committed) when a cash payoff of what the loan showed before the void arrives.
    /// The payoff must wait, then read the restored balance and refuse a part payment of a payroll-collected loan.</summary>
    [Fact]
    public async Task PayoffArrivingWhileAVoidHoldsTheLoan_WaitsAndIsRefusedAgainstTheRestoredBalance()
    {
        var (b, runId) = await SeedLockedRunThatTookOneInstalmentAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var payoffDb = fixture.CreateDb();
        var payoffPid = await Kit.OpenAndPidAsync(payoffDb, timeout.Token);
        await using var voidDb = fixture.CreateDb();

        IActionResult? payoffResult = null;
        await voidDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await voidDb.Database.BeginTransactionAsync(timeout.Token);
            var result = await new PayrollVoidService(voidDb)
                .VoidAsync(runId, b.Tenant, Guid.NewGuid(), "Payroll Manager", "Wrong month", timeout.Token);
            Assert.True(result.IsVoided, $"{result.ResultKind} {result.BlockCode} {result.BlockMessage}");

            var payoff = Task.Run(() => Kit.Loans(payoffDb, b.Tenant).RecordRepayment(b.LoanId,
                new RecordLoanRepaymentRequest(50m, b.Today, "CASH-PAYOFF"), timeout.Token));
            var waited = await Kit.WaitForLockAsync(fixture, payoffPid, advisoryOnly: false, payoff, timeout.Token);
            await tx.CommitAsync(timeout.Token);
            payoffResult = await payoff;
            Assert.True(waited, "the payoff must reach a lock the void holds before the void commits");
        });

        var refusal = Assert.IsAssignableFrom<ObjectResult>(payoffResult);
        Assert.Equal(409, refusal.StatusCode);
        await using var verify = fixture.CreateDb();
        var loan = await verify.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == b.LoanId);
        var cash = await verify.LoanRepayments.AsNoTracking().Where(r => r.LoanId == b.LoanId && !r.IsReversed).SumAsync(r => r.Amount);
        Assert.Equal(0m, cash);
        Assert.Equal((100m, 0m, "Active"), (loan.OutstandingBalance, loan.TotalRepaid, loan.Status));
    }

    // ── seed ─────────────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> ProcessNewRunAsync(Kit.Borrower b, int year, int month)
    {
        var runId = await Kit.SeedDraftRunAsync(fixture, b, year, month);
        await using var db = fixture.CreateDb();
        var outcome = await Kit.ProcessAsync(db, b, runId, null, CancellationToken.None);
        Assert.True(outcome.StatusCode == 200, System.Text.Json.JsonSerializer.Serialize(outcome.Body));
        return runId;
    }

    /// <summary>The approvals are stamped (they are not what these tests are about); the Lock is the real one, so its accrual
    /// journal, with the instalments credited to 1400 and 1410, is on the ledger for the void to reverse.</summary>
    private async Task ApproveAndLockAsync(Kit.Borrower b, Guid runId)
    {
        await using var db = fixture.CreateDb();
        await db.PayrollRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Approved"));
        var locked = await Kit.Payroll(db, b.Tenant).Lock(runId, CancellationToken.None);
        Assert.True(locked is OkObjectResult, System.Text.Json.JsonSerializer.Serialize((locked as ObjectResult)?.Value));
    }

    /// <summary>A 100 SAR loan in two payroll instalments; this month's run took the first (balance 50) and is Locked, so a cash
    /// payoff of the rest is allowed. The lock is stamped directly: these tests are about the loan, not the run's journal.</summary>
    private async Task<(Kit.Borrower B, Guid RunId)> SeedLockedRunThatTookOneInstalmentAsync()
    {
        var b = await Kit.SeedBorrowerAsync(fixture, 100m, 2);
        var runId = await ProcessNewRunAsync(b, b.Today.Year, b.Today.Month);
        await using var db = fixture.CreateDb();
        await db.PayrollRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Locked"));
        var loan = await db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == b.LoanId);
        Assert.Equal((50m, 50m, "Active"), (loan.OutstandingBalance, loan.TotalRepaid, loan.Status));
        return (b, runId);
    }
}
