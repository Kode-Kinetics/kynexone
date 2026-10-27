using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostingLockKit;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-accounting v1, WP-C3, T-025 (decision 3, AM-17): on the oracle company's real September run (bonus accrual cleared by payroll, employer
/// GOSI, contra deductions, loan recovery) the allocation report, built from stamps and the ledger, reconciles per cost centre:
/// <c>journalNet == earnings − contra-expense deductions + employer statutory − clearedAgainstAccrual</c>, and <c>reconciles = true</c>.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class CostCenterAllocationReconcilesPostgresTests
{
    private readonly PostgresFixture _fx;
    public CostCenterAllocationReconcilesPostgresTests(PostgresFixture fx) => _fx = fx;

    private async Task<OracleWorld> M1Async()
    {
        var w = await OracleWorld.CreateAsync(_fx);
        w.Reg1Seed(await w.SeedRunAsync("inputs/09_payroll_register_m1_regular.csv", "Regular", 9));
        await w.ApproveBonusBatchAsync();
        await w.ApproveSettlementAsync();
        await w.ConsumeBonusesAsync(w.Reg1.Id);
        (await w.K.ApproveAndLockAsync(w.Reg1.Id)).Should().BeOfType<OkObjectResult>();
        return w;
    }

    [Fact]
    public async Task T025_AfterLock_PerCostCentre_JournalNet_EqualsStampedEarningsMinusContraPlusEmployerMinusCleared_AndReconcilesIsTrue()
    {
        await using var w = await M1Async();
        w.K.Db.ChangeTracker.Clear();
        var res = await w.K.Payroll().CostCenterAllocation(w.Reg1.Id, default);
        res.Should().BeOfType<OkObjectResult>(Json(res).ToString());
        var report = Json(res);

        report.GetProperty("basis").GetString().Should().Be("Stamped");
        report.GetProperty("reconciles").GetBoolean().Should().BeTrue();

        // Independent expectation from the ledger rows themselves: P&L side of the run's own journal, per cost-centre code.
        var ledger = (await w.AllLedgerAsync()).Where(l => l.SourceModule == "Payroll" && l.SourceEntityId == w.Reg1.Id && l.ReversalOfEntryId == null).ToList();
        static bool Pnl(string account) => account.StartsWith('5') || account.StartsWith('6');
        var expectedNet = new Dictionary<string, decimal>();
        foreach (var l in ledger)
        {
            if (!string.IsNullOrWhiteSpace(l.DebitAccount) && Pnl(l.DebitAccount)) expectedNet[l.CostCenterCode ?? "—"] = expectedNet.GetValueOrDefault(l.CostCenterCode ?? "—") + l.Amount;
            if (!string.IsNullOrWhiteSpace(l.CreditAccount) && Pnl(l.CreditAccount)) expectedNet[l.CostCenterCode ?? "—"] = expectedNet.GetValueOrDefault(l.CostCenterCode ?? "—") - l.Amount;
        }

        var cleared = 0m;
        foreach (var b in report.GetProperty("allocations").EnumerateArray())
        {
            var code = b.GetProperty("costCenterCode").GetString()!;
            var journalNet = b.GetProperty("journalNet").GetDecimal();
            journalNet.Should().Be(expectedNet.GetValueOrDefault(code), $"{code}: the report's journal figure is the ledger's");
            var formula = b.GetProperty("grossSalary").GetDecimal() - b.GetProperty("contraDeductions").GetDecimal() + b.GetProperty("employerCost").GetDecimal()
                          - b.GetProperty("clearedAgainstAccrual").GetDecimal() + b.GetProperty("roundingAdjustment").GetDecimal();
            journalNet.Should().Be(formula, $"{code}: journalNet == earnings − contra + employer − clearedAgainstAccrual (+ rounding)");
            cleared += b.GetProperty("clearedAgainstAccrual").GetDecimal();
        }
        cleared.Should().Be(2899.99m, "the bonus accrual the payroll cleared is shown separately, not as cost of this run (the oracle's A-BON-1)");
    }

    [Fact]
    public async Task T025_AfterLock_TheReportIsUnchangedByLaterMasterDataChanges()
    {
        await using var w = await M1Async();
        w.K.Db.ChangeTracker.Clear();
        var before = Json(await w.K.Payroll().CostCenterAllocation(w.Reg1.Id, default)).GetRawText();

        await w.K.Db.CostCenters.Where(c => c.TenantId == w.TenantId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, "renamed after lock"));
        await w.K.Db.Employees.Where(e => e.TenantId == w.TenantId).ExecuteUpdateAsync(s => s.SetProperty(e => e.CostCenterId, (Guid?)null).SetProperty(e => e.CostCenterIsOverride, false));
        w.K.Db.ChangeTracker.Clear();

        Json(await w.K.Payroll().CostCenterAllocation(w.Reg1.Id, default)).GetRawText().Should().Be(before);
    }
}
