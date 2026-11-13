using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostingLockKit;

namespace Zayra.Api.Tests;

/// <summary>
/// The cost-centre approval hash seals cost-centre and posting-rule decisions. How a loan instalment SPLITS between the receivable and the
/// 2107 clearing liability follows the live receivable balance (it moves whenever any loan is paid out or settled), so the split is not
/// sealed; the instalment TOTAL is. A balance change between Approve and Lock must not send the run back; a changed instalment still must.
/// </summary>
public class LoanRecoveryApprovalHashTests
{
    private const string LoanRecv = "1400 - Employee Loans Receivable";

    private static async Task<(CostingLockKit K, PayrollRun Run)> ApprovedAsync()
    {
        var k = await CostingLockKit.CreateAsync();
        await k.AddCc("CC-200", "SELLING");
        await k.AddDept("D-SAL", "CC-200");
        await k.AddEmployee("E1", "D-SAL");
        var loan = new EmployeeLoan
        {
            TenantId = k.TenantId, CompanyId = k.CompanyId, EmployeeId = Guid.NewGuid(), EmployeeIntId = k.Emp["E1"].Id, LoanNumber = "LN-H",
            Status = "Active", ApprovedAmount = 2_000m, OutstandingBalance = 2_000m, InstallmentAmount = 500m, ApprovedInstallments = 4,
            Currency = "SAR", RepaymentMethod = "PayrollDeduction",
        };
        k.Db.EmployeeLoans.Add(loan);
        k.Db.FinanceGlEntries.Add(Entry(k, "Loan", loan.Id, "Disbursement", LoanRecv, "1000 - Cash/Bank", 2_000m));
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 5_000m) }, new[] { D("E1", "LOAN_EMI", "Loan", 500m) });
        k.Db.PayrollRunConsumptions.Add(new PayrollRunConsumption
        {
            TenantId = k.TenantId, CompanyId = k.CompanyId, PayrollRunId = run.Id, ArtifactType = PayrollConsumptionArtifacts.Loan,
            ArtifactId = loan.Id, EmployeeId = k.Emp["E1"].Id, Amount = 500m, PriorStatus = "Active", PriorOutstandingBalance = 2_000m, PriorTotalRepaid = 0m,
        });
        await k.Db.SaveChangesAsync();
        (await k.ApproveAsync(run.Id)).Should().BeOfType<OkObjectResult>();
        (await k.Db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).CostCenterResolutionHash.Should().NotBeNullOrEmpty();
        return (k, run);
    }

    private static FinanceGlEntry Entry(CostingLockKit k, string module, Guid source, string eventType, string debit, string credit, decimal amount) => new()
    {
        TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = module, SourceEntityId = source, EventType = eventType,
        DebitAccount = debit, CreditAccount = credit, Amount = amount, Currency = "SAR",
        EntryDate = new DateOnly(2026, 8, 1), Period = "2026-08", Description = $"{module} {eventType}",
    };

    [Fact]
    public async Task AReceivableBalanceThatMovesAfterApproval_DoesNotSendTheRunBack()
    {
        var (k, run) = await ApprovedAsync();
        await using var _ = k;
        // Another loan is settled in cash after the approval: the account now carries 200, so the instalment splits 200 / 300 instead of 500 / 0.
        k.Db.FinanceGlEntries.Add(Entry(k, "Loan", Guid.NewGuid(), "Repayment", "1000 - Cash/Bank", LoanRecv, 1_800m));
        await k.Db.SaveChangesAsync();

        var result = await k.LockAsync(run.Id);

        result.Should().BeOfType<OkObjectResult>(Status(result) + " " + Err(result));
        var gl = await k.GlAsync(run.Id);
        gl.Where(l => l.CreditAccount == LoanRecv && l.DebitAccount == string.Empty).Sum(l => l.Amount).Should().Be(200m);
        gl.Where(l => l.CreditAccount.StartsWith("2107 ")).Sum(l => l.Amount).Should().Be(300m);
    }

    [Fact]
    public async Task AChangedInstalment_StillSendsTheRunBack()
    {
        var (k, run) = await ApprovedAsync();
        await using var _ = k;
        var deduction = await k.Db.PayrollDeductions.SingleAsync(d => d.PayrollRunId == run.Id && d.ComponentCode == "LOAN_EMI");
        deduction.Amount = 400m;
        await k.Db.SaveChangesAsync();

        var result = await k.LockAsync(run.Id);

        Status(result).Should().Be(409);
        Err(result).Should().Be("cost_centre_changed_after_approval");
    }
}
