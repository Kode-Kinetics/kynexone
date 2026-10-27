using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost accounting: the bonus accrual and pay-out honour the resolved expense account's cost-centre rule and the company's fixed cost
/// centre mapping, the way payroll does. Real <see cref="BonusesController"/> against the relational in-memory kit.
/// </summary>
public class BonusCostAccountRuleTests
{
    private static async Task<CostingLockKit> KitAsync()
    {
        var k = await CostingLockKit.CreateAsync(true, true);
        await k.AddCc("CC-110", "G_AND_A", segment: "4110");
        await k.AddCc("CC-200", "SELLING");
        await k.AddCc("CC-300", "COGS");
        await k.AddDept("D-FIN", "CC-110");
        await k.AddDept("D-SAL", "CC-200");
        await k.AddDept("D-OPS");
        await k.AddEmployee("E1", "D-SAL");
        await k.AddEmployee("E2", "D-FIN");
        await k.AddEmployee("E3", "D-OPS");
        return k;
    }

    private static BonusesController Bonuses(CostingLockKit k)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", k.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Name, "bonus-test"),
            new("permission", "payroll.write"), new("permission", "payroll.approve"),
        };
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new BonusesController(k.Db, new AllowAllScope()) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private sealed class AllowAllScope : Zayra.Api.Application.Common.IDataScopeService
    {
        public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new Zayra.Api.Application.Common.DataScope { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization });
    }

    private static async Task<BonusBatch> BatchAsync(CostingLockKit k, string status, params (string Emp, decimal Gross)[] children)
    {
        var db = k.Db;
        var type = new BonusType { TenantId = k.TenantId, Code = "PERF", NameEn = "Performance", TaxRegion = "GCC", IsActive = true };
        db.BonusTypes.Add(type);
        var batch = new BonusBatch
        {
            TenantId = k.TenantId, BonusTypeId = type.Id, BonusTypeName = type.NameEn, BatchNumber = "BON-1", BatchName = "Batch 1",
            PaymentPeriod = "2026-09", PaymentDate = new DateOnly(2026, 9, 25), Status = status, EmployeeCount = children.Length,
            TotalAmount = children.Sum(c => c.Gross),
        };
        db.BonusBatches.Add(batch);
        await db.SaveChangesAsync();
        foreach (var (emp, gross) in children)
            db.EmployeeBonuses.Add(new EmployeeBonus
            {
                TenantId = k.TenantId, CompanyId = k.CompanyId, BonusBatchId = batch.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = k.Emp[emp].Id,
                EmployeeName = emp, BonusTypeId = type.Id, BonusTypeName = type.NameEn, BasicSalary = 10_000m, CalculationMethod = "Fixed",
                CalculationValue = gross, GrossBonusAmount = gross, TaxWithheld = 0m, BonusAmount = gross, PaymentPeriod = "2026-09",
                Status = status == "Approved" ? "Approved" : "Draft", TaxRegion = "GCC",
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return batch;
    }

    private static async Task<GlAccount> BonusExpenseAsync(CostingLockKit k, string rule)
    {
        var (code, _) = Zayra.Api.Infrastructure.Payroll.GlAccountResolver.Resolve("EARN:BONUS", new Dictionary<string, (string, string)>(),
            new Dictionary<string, (string, string)>());
        var acct = await k.Db.GlAccounts.FirstAsync(a => a.TenantId == k.TenantId && a.Code == code);
        acct.CostCenterRule = rule;
        await k.Db.SaveChangesAsync();
        return acct;
    }

    private static async Task<List<FinanceGlEntry>> LedgerAsync(CostingLockKit k, Guid batchId)
    {
        return await k.Db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == k.TenantId && x.SourceModule == "Bonus" && x.SourceEntityId == batchId).ToListAsync();
    }

    private static async Task FixBonusCentreAsync(CostingLockKit k, GlAccount expense, string cc)
    {
        k.Db.GlAccountMappings.Add(new GlAccountMapping
        { TenantId = k.TenantId, CompanyId = k.CompanyId, DriverKey = "EARN:BONUS", AccountId = expense.Id, SegmentCostCenterId = k.Cc[cc].Id });
        await k.Db.SaveChangesAsync();
        k.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task AnAccrual_OnAnExpenseAccountThatDoesNotAcceptACostCentre_PostsUncosted_AndApproves()
    {
        await using var k = await KitAsync();
        await BonusExpenseAsync(k, "NotAllowed");
        k.Db.ChangeTracker.Clear();
        var batch = await BatchAsync(k, "PendingApproval", ("E1", 1000m), ("E2", 500m));

        (await Bonuses(k).ApproveBatch(batch.Id, new BatchApproveRequest("ok"), default)).Should().BeOfType<OkObjectResult>();

        var gl = await LedgerAsync(k, batch.Id);
        gl.Should().OnlyContain(l => l.CostCenterId == null && l.CostCenterCode == null);
        gl.Sum(l => l.Amount).Should().Be(1500m);
        gl.Should().OnlyContain(l => l.DebitAccount != "" && l.CreditAccount != "", "every accrual row is two-sided, so DR equals CR");
        gl.Select(l => l.PostingKey).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task AFixedCostCentreOnTheBonusMapping_BeatsTheEmployeesCentre_ForTheAccrual()
    {
        await using var k = await KitAsync();
        var expense = await BonusExpenseAsync(k, "Required");
        await FixBonusCentreAsync(k, expense, "CC-300");
        var batch = await BatchAsync(k, "PendingApproval", ("E1", 1000m), ("E2", 500m));

        (await Bonuses(k).ApproveBatch(batch.Id, new BatchApproveRequest("ok"), default)).Should().BeOfType<OkObjectResult>();

        var gl = await LedgerAsync(k, batch.Id);
        gl.Should().ContainSingle().Which.Should().Match<FinanceGlEntry>(l => l.CostCenterCode == "CC-300" && l.Amount == 1500m);
    }

    [Fact]
    public async Task AnEmployeeWithNoCentre_IsNotRefusedAsUnresolved_WhenTheFixedMappingSuppliesTheCentre()
    {
        await using var k = await KitAsync();
        var expense = await BonusExpenseAsync(k, "Required");
        await FixBonusCentreAsync(k, expense, "CC-300");
        var batch = await BatchAsync(k, "PendingApproval", ("E3", 700m));   // D-OPS has no centre anywhere

        (await Bonuses(k).ApproveBatch(batch.Id, new BatchApproveRequest("ok"), default)).Should().BeOfType<OkObjectResult>();

        (await LedgerAsync(k, batch.Id)).Should().ContainSingle().Which.CostCenterCode.Should().Be("CC-300");
    }

    [Fact]
    public async Task AFixedCostCentreOnTheBonusMapping_AlsoGovernsThePayOutRemainder()
    {
        await using var k = await KitAsync();
        var expense = await BonusExpenseAsync(k, "Required");
        await FixBonusCentreAsync(k, expense, "CC-300");
        var batch = await BatchAsync(k, "Approved", ("E1", 1000m), ("E2", 500m));   // never accrued: the whole pay-out is un-accrued expense

        (await Bonuses(k).MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), default)).Should().BeOfType<OkObjectResult>();

        var gl = (await LedgerAsync(k, batch.Id)).Where(l => l.EventType == GlEventTypes.BonusPayment).ToList();
        gl.Should().NotBeEmpty();
        gl.Should().OnlyContain(l => l.CostCenterCode == "CC-300");
        gl.Sum(l => l.Amount).Should().Be(1500m);
    }

    [Fact]
    public async Task APayOutRemainder_OnAnAccountThatDoesNotAcceptACostCentre_PostsUncosted()
    {
        await using var k = await KitAsync();
        await BonusExpenseAsync(k, "NotAllowed");
        k.Db.ChangeTracker.Clear();
        var batch = await BatchAsync(k, "Approved", ("E1", 1000m));

        (await Bonuses(k).MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), default)).Should().BeOfType<OkObjectResult>();

        var gl = (await LedgerAsync(k, batch.Id)).Where(l => l.EventType == GlEventTypes.BonusPayment).ToList();
        gl.Should().NotBeEmpty().And.OnlyContain(l => l.CostCenterId == null);
        gl.Sum(l => l.Amount).Should().Be(1000m);
    }
}
