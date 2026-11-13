using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A receivable that was written off stays written off when the run that part-recovered it is voided. The void re-debits 1420 by
/// exactly what the run took, so that amount becomes a NEW outstanding receivable; the written-off row is left alone. Before this,
/// the void restored the old row to Outstanding at its full amount, so the sub-ledger said 1,000 while 1420 carried 400.
///
/// Scenario: a 1,000 residual, run X nets 400, 600 is written off (1420 = 0), X is voided (1420 = 400).
/// </summary>
public class WrittenOffReceivableVoidTests
{
    private const string Receivable1420 = "1420 - Employee Overpayment Receivable";
    private const string WriteOffExpense = "5114 - Employee Receivable Write-off Expense";

    private sealed record Scenario(PayrollRun Run, PayrollEmployeeReceivable Row);

    private static async Task<Scenario> SeedAsync(GlPostingKit k, string rowStatus)
    {
        k.Db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = "FinalSettlement", SourceEntityId = Guid.NewGuid(),
            EventType = GlEventTypes.SettlementResidualReclass, DebitAccount = Receivable1420, CreditAccount = "1400 - Employee Loans Receivable",
            Amount = 1_000m, Currency = "SAR", EntryDate = new DateOnly(2026, 8, 31), Period = "2026-08", Description = "Residual reclass",
        });
        await k.Db.SaveChangesAsync();

        var run = await k.SeedApprovedRunAsync(
            new[] { (1, "BASIC", "Salary", 5_000m) },
            new[] { (1, PayrollRecoveryComponents.ReceivableRecovery, PayrollRecoveryComponents.RecoverySource, 400m) },
            net: 4_600m);
        (await k.Payroll().Lock(run.Id, default)).Should().BeOfType<OkObjectResult>();

        // The row as the netting left it (recovered 400 of 1,000), then as a write-off of the remaining 600 leaves it.
        var row = new PayrollEmployeeReceivable
        {
            TenantId = k.TenantId, CompanyId = k.CompanyId, EmployeeId = 1, EmployeeCode = "E1", SourceRunId = Guid.NewGuid(),
            EventType = GlEventTypes.SettlementResidualReclass, Period = "2026-08", Amount = 1_000m, RecoveredAmount = 400m,
            RecoveredByRunId = run.Id, Status = rowStatus,
        };
        k.Db.PayrollEmployeeReceivables.Add(row);
        k.Db.PayrollRunConsumptions.Add(new PayrollRunConsumption
        {
            TenantId = k.TenantId, CompanyId = k.CompanyId, PayrollRunId = run.Id, ArtifactType = PayrollConsumptionArtifacts.EmployeeReceivable,
            ArtifactId = row.Id, EmployeeId = 1, Amount = 400m, PriorStatus = PayrollReceivableStatuses.Outstanding, PriorAmountPaid = 0m,
        });
        if (rowStatus == PayrollReceivableStatuses.WrittenOff)
            k.Db.FinanceGlEntries.AddRange(
                WriteOff(k, WriteOffExpense, string.Empty), WriteOff(k, string.Empty, Receivable1420));
        await k.Db.SaveChangesAsync();
        k.Db.ChangeTracker.Clear();
        return new Scenario(run, row);
    }

    private static FinanceGlEntry WriteOff(GlPostingKit k, string debit, string credit) => new()
    {
        TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = "GlAdjustment", SourceEntityId = Guid.NewGuid(),
        EventType = GlEventTypes.ReceivableWriteOff, DebitAccount = debit, CreditAccount = credit, Amount = 600m, Currency = "SAR",
        EntryDate = new DateOnly(2026, 9, 20), Period = "2026-09", Description = "Employee receivable written off",
    };

    private static async Task VoidAsync(GlPostingKit k, Guid runId)
    {
        var result = await new PayrollVoidService(k.Db).VoidAsync(runId, k.TenantId, Guid.NewGuid(), "finance", "wrong month");
        result.IsVoided.Should().BeTrue();
        k.Db.ChangeTracker.Clear();
    }

    private static async Task<decimal> Ledger1420Async(GlPostingKit k) =>
        GlControlAccounts.Balance(await k.Db.FinanceGlEntries.AsNoTracking().Where(x => x.TenantId == k.TenantId).ToListAsync(), Receivable1420);

    [Fact]
    public async Task VoidingTheRunThatPartRecovered_AWrittenOffReceivable_DoesNotResurrectIt()
    {
        await using var k = await GlPostingKit.CreateAsync();
        var s = await SeedAsync(k, PayrollReceivableStatuses.WrittenOff);
        (await Ledger1420Async(k)).Should().Be(0m, "1,000 recognised - 400 netted - 600 written off");

        await VoidAsync(k, s.Run.Id);

        (await Ledger1420Async(k)).Should().Be(400m, "the void re-debits what the run netted");
        var rows = await k.Db.PayrollEmployeeReceivables.AsNoTracking().Where(r => r.TenantId == k.TenantId).ToListAsync();
        rows.Single(r => r.Id == s.Row.Id).Status.Should().Be(PayrollReceivableStatuses.WrittenOff, "a write-off is final");
        var live = rows.Where(r => r.Status == PayrollReceivableStatuses.Outstanding).ToList();
        live.Should().ContainSingle("the 400 the void gave back is a new receivable");
        live[0].Outstanding.Should().Be(400m);
        live[0].EmployeeId.Should().Be(1);
        live[0].CompanyId.Should().Be(k.CompanyId);
        live.Sum(r => r.Outstanding).Should().Be(await Ledger1420Async(k), "sub-ledger ties to 1420");
    }

    [Fact]
    public async Task ReopeningTheRun_ReplaysTheWitnessWithTheSameRule()
    {
        await using var k = await GlPostingKit.CreateAsync();
        var s = await SeedAsync(k, PayrollReceivableStatuses.WrittenOff);
        var run = await k.Db.PayrollRuns.SingleAsync(r => r.Id == s.Run.Id);

        // Reopen and void share ONE replay (RestoreOperationalStateAsync); the ledger is untouched on this path.
        await new PayrollVoidService(k.Db).RestoreOperationalStateAsync(k.TenantId, run, run.Id, default);
        await k.Db.SaveChangesAsync();
        k.Db.ChangeTracker.Clear();

        var rows = await k.Db.PayrollEmployeeReceivables.AsNoTracking().Where(r => r.TenantId == k.TenantId).ToListAsync();
        rows.Single(r => r.Id == s.Row.Id).Status.Should().Be(PayrollReceivableStatuses.WrittenOff);
        rows.Where(r => r.Status == PayrollReceivableStatuses.Outstanding).Should().ContainSingle().Which.Outstanding.Should().Be(400m);
    }

    [Fact]
    public async Task VoidingTheRun_OfAnOrdinaryPartRecoveredReceivable_StillRestoresTheSameRow()
    {
        await using var k = await GlPostingKit.CreateAsync();
        var s = await SeedAsync(k, PayrollReceivableStatuses.Outstanding);

        await VoidAsync(k, s.Run.Id);

        var rows = await k.Db.PayrollEmployeeReceivables.AsNoTracking().Where(r => r.TenantId == k.TenantId).ToListAsync();
        rows.Should().ContainSingle("nothing was written off, so the original row is simply restored");
        rows[0].Id.Should().Be(s.Row.Id);
        rows[0].RecoveredAmount.Should().Be(0m);
        rows[0].Status.Should().Be(PayrollReceivableStatuses.Outstanding);
    }
}
