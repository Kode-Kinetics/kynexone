using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using Kit = Zayra.Api.Tests.DebtRacePostgresKit;

namespace Zayra.Api.Tests;

/// <summary>
/// Settling a leaver's final-settlement payment batch moves the loan and advance debt the settlement could not recover to the
/// 1420 Employee Receivable (<c>PayrollController.FinalizePaidSettlementsAsync</c>). A payroll run that includes the same
/// leaver collects an instalment from the same loan and advance. The two must never both take the debt: whichever commits
/// second must see the other, so that for each debt <c>balance + moved to 1420 + withheld by payroll == original</c>.
///
/// <para>Real Postgres only: settle and Process run on their own contexts and connections, and the test observes the waiting
/// side in <c>pg_stat_activity</c> rather than relying on timing.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class SettlementResidualDebtRacePostgresTests(PostgresFixture fixture)
{
    private const decimal LoanAmount = 100m, LoanInstalment = 50m, AdvanceAmount = 60m, AdvanceInstalment = 30m;

    /// <summary>The payroll pass holds the leaver's loan and advance (it is inside its transaction, deductions computed) when the
    /// settlement batch is settled. The settle must wait, then move only what payroll left to 1420.</summary>
    [Fact]
    public async Task SettleArrivingWhileProcessHoldsTheDebts_WaitsAndMovesOnlyWhatPayrollLeft()
    {
        var w = await SeedAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var processDb = fixture.CreateDb();
        await using var settleDb = fixture.CreateDb();
        var settlePid = await Kit.OpenAndPidAsync(settleDb, timeout.Token);

        Task<IActionResult>? settle = null;
        var settleWaited = false;
        var hooks = new PayrollProcessHooks
        {
            // Progress first fires inside the run's transaction, after the deductions' inputs were read.
            Progress = async (_, _, ct) =>
            {
                if (settle is not null) return;
                settle = Task.Run(() => Kit.Payroll(settleDb, w.B.Tenant)
                    .SettlePaymentBatch(w.BatchId, new SettlePaymentBatchRequest("BANK-OK"), timeout.Token));
                settleWaited = await Kit.WaitForLockAsync(fixture, settlePid, advisoryOnly: true, settle, ct);
            },
        };

        var outcome = await Kit.ProcessAsync(processDb, w.B, w.RegularRunId, hooks, timeout.Token);
        Assert.Equal(200, outcome.StatusCode);
        Assert.NotNull(settle);
        var settled = await settle!;
        Assert.IsType<OkObjectResult>(settled);

        Assert.True(settleWaited, "the settlement must queue behind the debt locks the payroll pass holds");
        var d = await ReadAsync(w);
        AssertConserved(d);
        Assert.Equal((LoanInstalment, LoanAmount - LoanInstalment, 0m), (d.LoanWithheld, d.LoanMoved, d.LoanBalance));
        Assert.Equal((AdvanceInstalment, AdvanceAmount - AdvanceInstalment, 0m), (d.AdvanceWithheld, d.AdvanceMoved, d.AdvanceBalance));
    }

    /// <summary>The settlement holds the leaver's debts (its unit is done, not yet committed) when the payroll pass arrives. The
    /// pass must wait, then see the debt already moved to 1420 and withhold nothing for it.</summary>
    [Fact]
    public async Task ProcessArrivingWhileSettleHoldsTheDebts_WaitsAndWithholdsNothing()
    {
        var w = await SeedAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var processDb = fixture.CreateDb();
        var processPid = await Kit.OpenAndPidAsync(processDb, timeout.Token);
        await using var settleDb = fixture.CreateDb();

        await settleDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await settleDb.Database.BeginTransactionAsync(timeout.Token);
            var settled = await Kit.Payroll(settleDb, w.B.Tenant)
                .SettlePaymentBatch(w.BatchId, new SettlePaymentBatchRequest("BANK-OK"), timeout.Token);
            Assert.IsType<OkObjectResult>(settled);

            var process = Task.Run(() => Kit.ProcessAsync(processDb, w.B, w.RegularRunId, null, timeout.Token));
            var waited = await Kit.WaitForLockAsync(fixture, processPid, advisoryOnly: false, process, timeout.Token);
            await tx.CommitAsync(timeout.Token);
            var outcome = await process;

            Assert.True(waited, "the payroll pass must reach a lock the settlement holds before the settlement commits");
            Assert.Equal(200, outcome.StatusCode);
        });

        var d = await ReadAsync(w);
        AssertConserved(d);
        Assert.Equal((0m, LoanAmount, 0m), (d.LoanWithheld, d.LoanMoved, d.LoanBalance));
        Assert.Equal((0m, AdvanceAmount, 0m), (d.AdvanceWithheld, d.AdvanceMoved, d.AdvanceBalance));
    }

    // ── assertions ───────────────────────────────────────────────────────────────────────────────

    private sealed record Debts(
        decimal LoanBalance, decimal LoanRepaid, decimal LoanMoved, decimal LoanWithheld,
        decimal AdvanceBalance, decimal AdvanceRepaid, decimal AdvanceMoved, decimal AdvanceWithheld,
        decimal ReceivableSubLedger, decimal SettlementReclassed);

    private static void AssertConserved(Debts d)
    {
        Assert.False(d.LoanWithheld > 0m && d.LoanMoved == LoanAmount,
            $"the loan was taken twice: {d.LoanWithheld} withheld by payroll and the whole {d.LoanMoved} moved to 1420");
        Assert.Equal(LoanAmount, d.LoanBalance + d.LoanMoved + d.LoanWithheld);
        Assert.Equal(AdvanceAmount, d.AdvanceBalance + d.AdvanceMoved + d.AdvanceWithheld);
        // A reclass is not a repayment: what the loan says was repaid is exactly what payroll withheld.
        Assert.Equal(d.LoanWithheld, d.LoanRepaid);
        Assert.Equal(d.AdvanceWithheld, d.AdvanceRepaid);
        // The 1420 sub-ledger and the settlement's own figure agree with the 1420 journal.
        Assert.Equal(d.LoanMoved + d.AdvanceMoved, d.ReceivableSubLedger);
        Assert.Equal(d.LoanMoved + d.AdvanceMoved, d.SettlementReclassed);
    }

    private async Task<Debts> ReadAsync(World w)
    {
        await using var db = fixture.CreateDb();
        var loan = await db.EmployeeLoans.AsNoTracking().SingleAsync(l => l.Id == w.B.LoanId);
        var advance = await db.SalaryAdvances.AsNoTracking().SingleAsync(a => a.Id == w.AdvanceId);
        var reclass = await db.FinanceGlEntries.AsNoTracking()
            .Where(g => g.TenantId == w.B.Tenant && g.EventType == GlEventTypes.SettlementResidualReclass && g.SourceEntityId == w.SettlementId)
            .Select(g => new { g.CreditAccount, g.Amount }).ToListAsync();
        var withheld = await db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == w.B.Tenant && d.PayrollRunId == w.RegularRunId)
            .Select(d => new { d.ComponentCode, d.Amount }).ToListAsync();
        var subLedger = await db.PayrollEmployeeReceivables.AsNoTracking()
            .Where(r => r.TenantId == w.B.Tenant && r.EmployeeId == w.B.EmployeeId).SumAsync(r => r.Amount);
        var settlement = await db.EmployeeFinalSettlements.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == w.SettlementId);
        Assert.Equal(FinalSettlementStatuses.Paid, settlement.Status);
        return new Debts(
            loan.OutstandingBalance, loan.TotalRepaid,
            reclass.Where(r => r.CreditAccount.StartsWith("1400", StringComparison.Ordinal)).Sum(r => r.Amount),
            withheld.Where(x => x.ComponentCode == "LOAN_EMI").Sum(x => x.Amount),
            advance.OutstandingBalance, advance.TotalRepaid,
            reclass.Where(r => r.CreditAccount.StartsWith("1410", StringComparison.Ordinal)).Sum(r => r.Amount),
            withheld.Where(x => x.ComponentCode == "ADVANCE_EMI").Sum(x => x.Amount),
            subLedger, settlement.ResidualDebtReclassed);
    }

    // ── seed ─────────────────────────────────────────────────────────────────────────────────────

    private sealed record World(Kit.Borrower B, Guid AdvanceId, Guid RegularRunId, Guid BatchId, Guid SettlementId);

    /// <summary>
    /// A leaver with a 100 SAR payroll-collected loan (two 50 SAR instalments, disbursed through the real payout path so 1400
    /// carries it) and a 60 SAR advance (two of 30, 1410 carries it); this month's Draft regular run, which collects one
    /// instalment of each; and last month's Locked settlement run whose Accepted bank batch pays the leaver's final settlement.
    /// </summary>
    private async Task<World> SeedAsync()
    {
        var b = await Kit.SeedBorrowerAsync(fixture, LoanAmount, 2);
        var advanceId = await Kit.SeedAdvanceAsync(fixture, b, AdvanceAmount, 2);
        var regularRunId = await Kit.SeedDraftRunAsync(fixture, b, b.Today.Year, b.Today.Month);
        var prior = b.Today.AddMonths(-1);

        await using var db = fixture.CreateDb();
        var settlementRun = new PayrollRun
        {
            TenantId = b.Tenant, CompanyId = b.CompanyId, Year = prior.Year, Month = prior.Month,
            RunType = PayrollRunTypes.OffCycle, Status = "Locked",
        };
        var batch = new PayrollPaymentBatch
        {
            TenantId = b.Tenant, PayrollRunId = settlementRun.Id, BatchNumber = $"SET-{Guid.NewGuid():N}"[..20],
            WpsStatus = WpsStatuses.Accepted, TotalAmount = 1_000m, Currency = "SAR",
        };
        var offboarding = new EmployeeOffboarding
        {
            TenantId = b.Tenant, EmployeeId = b.EmployeeId, SeparationType = "Resignation", Status = "InProgress", LastWorkingDay = b.Today,
        };
        var settlement = new EmployeeFinalSettlement
        {
            TenantId = b.Tenant, CompanyId = b.CompanyId, EmployeeId = b.EmployeeId, EmployeeCode = b.EmployeeCode,
            EmployeeName = "Debt Race Borrower", OffboardingId = offboarding.Id, PayrollRunId = settlementRun.Id, Currency = "SAR",
            Status = FinalSettlementStatuses.Disbursing,
        };
        db.AddRange(settlementRun, batch, offboarding, settlement);
        db.PayrollPaymentRecords.Add(new PayrollPaymentRecord
        {
            TenantId = b.Tenant, PaymentBatchId = batch.Id, EmployeeId = b.EmployeeId, Amount = 1_000m,
            Iban = "SA4420000001234567891234", Status = "Pending",
        });
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = b.Tenant, CompanyId = b.CompanyId, SourceModule = "Payroll", SourceEntityId = settlementRun.Id,
            EventType = GlEventTypes.Accrual, CreditAccount = "2100 - Salaries Payable", Description = PayrollGlDescriptions.NetPayable,
            Amount = 1_000m, Currency = "SAR", EntryDate = prior, Period = $"{prior.Year}-{prior.Month:D2}",
        });
        await db.SaveChangesAsync();
        return new World(b, advanceId, regularRunId, batch.Id, settlement.Id);
    }
}
