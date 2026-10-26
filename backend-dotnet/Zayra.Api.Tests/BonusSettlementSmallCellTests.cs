using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-accounting v1, WP-C3 (AM-15, AM-36): the small-cell roll-up also covers the bonus accrual and the final-settlement accrual
/// (payroll rows and EOSB already roll up). A cost centre with fewer than K people among the employees being posted is replaced by
/// its nearest ancestor that holds at least K, else by the company default, so no ledger line reveals one person's amount.
/// </summary>
public class BonusSettlementSmallCellTests
{
    private sealed class AllowAllScope : Zayra.Api.Application.Common.IDataScopeService
    {
        public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new Zayra.Api.Application.Common.DataScope { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization });
    }

    private static BonusesController Bonuses(CostingLockKit k)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", k.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Name, "bonus-test"),
            new("permission", "payroll.write"), new("permission", "payroll.approve"),
        };
        return new BonusesController(k.Db, new AllowAllScope())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
        };
    }

    /// <summary>P (parent) with A (one person, E1) and B (three people, E2..E4).</summary>
    private static async Task<CostingLockKit> KitAsync(short k)
    {
        var kit = await CostingLockKit.CreateAsync(costing: true, preset: true);
        await kit.AddCc("CC-P", "G_AND_A");
        await kit.AddCc("CC-A", "G_AND_A", parent: "CC-P");
        await kit.AddCc("CC-B", "G_AND_A", parent: "CC-P");
        await kit.AddCc("CC-DEF", "G_AND_A");
        await kit.AddDept("D-A", "CC-A"); await kit.AddDept("D-B", "CC-B");
        await kit.AddEmployee("E1", "D-A");
        await kit.AddEmployee("E2", "D-B"); await kit.AddEmployee("E3", "D-B"); await kit.AddEmployee("E4", "D-B");
        await kit.SetDefaultCc("CC-DEF");
        (await kit.Db.Companies.SingleAsync(c => c.Id == kit.CompanyId)).CostCentreSmallCellMin = k;
        await kit.Db.SaveChangesAsync();
        return kit;
    }

    private static async Task<BonusBatch> PendingBatchAsync(CostingLockKit k, params (string Emp, decimal Gross)[] children)
    {
        var db = k.Db;
        var type = new BonusType { TenantId = k.TenantId, Code = "PERF", NameEn = "Performance", TaxRegion = "GCC", IsActive = true };
        db.BonusTypes.Add(type);
        var batch = new BonusBatch
        {
            TenantId = k.TenantId, BonusTypeId = type.Id, BonusTypeName = type.NameEn, BatchNumber = "BON-1", BatchName = "Batch 1",
            PaymentPeriod = "2026-09", PaymentDate = new DateOnly(2026, 9, 25), Status = "PendingApproval", EmployeeCount = children.Length,
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
                Status = "Draft", TaxRegion = "GCC",
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return batch;
    }

    private static Task<List<FinanceGlEntry>> LedgerAsync(CostingLockKit k, string module) =>
        k.Db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == k.TenantId && x.SourceModule == module).ToListAsync();

    [Fact]
    public async Task BonusAccrual_WithK3_RollsAOnePersonCostCentreUpToItsParent_SoNoRowIsOnePersonsAmount()
    {
        await using var k = await KitAsync(3);
        var batch = await PendingBatchAsync(k, ("E1", 777m), ("E2", 1000m), ("E3", 1000m), ("E4", 1000m));

        (await Bonuses(k).ApproveBatch(batch.Id, new BatchApproveRequest("ok"), default)).Should().BeOfType<OkObjectResult>();

        var rows = await LedgerAsync(k, "Bonus");
        rows.Select(l => (l.CostCenterCode, l.Amount)).OrderBy(x => x.CostCenterCode).Should().Equal(("CC-B", 3000m), ("CC-P", 777m));
        rows.Should().NotContain(l => l.CostCenterCode == "CC-A");
        rows.Single(l => l.CostCenterCode == "CC-P").CostCenterId.Should().Be(k.Cc["CC-P"].Id, "the rolled-up centre is what is stored (AM-15)");
    }

    [Fact]
    public async Task BonusAccrual_WithK0_StillPostsOnTheEmployeesOwnCostCentre()
    {
        await using var k = await KitAsync(0);
        var batch = await PendingBatchAsync(k, ("E1", 777m), ("E2", 1000m), ("E3", 1000m), ("E4", 1000m));

        (await Bonuses(k).ApproveBatch(batch.Id, new BatchApproveRequest("ok"), default)).Should().BeOfType<OkObjectResult>();

        (await LedgerAsync(k, "Bonus")).Select(l => (l.CostCenterCode, l.Amount)).OrderBy(x => x.CostCenterCode)
            .Should().Equal(("CC-A", 777m), ("CC-B", 3000m));
    }

    [Fact]
    public async Task BonusAccrual_WithK3_WhenNoAncestorQualifies_RollsToTheCompanyDefault()
    {
        await using var k = await KitAsync(3);
        // Only A's person and one of B's are paid: nothing in the subtree reaches three people.
        var batch = await PendingBatchAsync(k, ("E1", 777m), ("E2", 1000m));

        (await Bonuses(k).ApproveBatch(batch.Id, new BatchApproveRequest("ok"), default)).Should().BeOfType<OkObjectResult>();

        (await LedgerAsync(k, "Bonus")).Select(l => (l.CostCenterCode, l.Amount)).Should().Equal(("CC-DEF", 1777m));
    }

    [Fact]
    public async Task BonusPaidOutsidePayroll_UnaccruedRemainder_AlsoRollsUp()
    {
        await using var k = await KitAsync(3);
        var batch = await PendingBatchAsync(k, ("E1", 777m), ("E2", 1000m), ("E3", 1000m), ("E4", 1000m));
        // approve first so the batch reaches "Approved", then remove its accrual so the whole payment is an un-accrued expense
        (await Bonuses(k).ApproveBatch(batch.Id, new BatchApproveRequest("ok"), default)).Should().BeOfType<OkObjectResult>();
        await k.Db.FinanceGlEntries.IgnoreQueryFilters().Where(x => x.TenantId == k.TenantId && x.SourceModule == "Bonus").ExecuteDeleteAsync();
        k.Db.ChangeTracker.Clear();

        var res = await Bonuses(k).MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), default);

        res.Should().BeOfType<OkObjectResult>(JsonSerializer.Serialize((res as ObjectResult)?.Value));
        var expense = (await LedgerAsync(k, "Bonus")).Where(l => l.DebitAccount.StartsWith("6100")).ToList();
        expense.Select(l => l.CostCenterCode).Should().NotContain("CC-A");
        expense.Sum(l => l.Amount).Should().Be(3777m);
    }

    private static async Task<IActionResult> ApproveSettlementOfAsync(CostingLockKit k, string employeeCode)
    {
        var db = k.Db;
        var employee = k.Emp[employeeCode];
        var lastDay = new DateOnly(2026, 9, 20);
        // the final wage month is paid by an ordinary run (the leaver is on it), as the approval guard requires
        var wagesRun = await k.SeedRunAsync(new[] { CostingLockKit.E(employeeCode, "BASIC", 100m) }, Array.Empty<(string, string, string, decimal, bool)>(), month: 9);
        var offboarding = new EmployeeOffboarding
        {
            TenantId = k.TenantId, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode, EmployeeName = employee.FullName, SeparationType = "Termination",
            Status = "InProgress", NoticeDate = lastDay.AddDays(-30), NoticePeriodDays = 30, LastWorkingDay = lastDay,
        };
        var settlement = new EmployeeFinalSettlement
        {
            TenantId = k.TenantId, CompanyId = k.CompanyId, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode, EmployeeName = employee.FullName,
            OffboardingId = offboarding.Id, LastWorkingDay = lastDay, ServiceStartDate = new DateOnly(2020, 5, 1), SettlementDueDate = lastDay.AddDays(7),
            TerminationReason = "Termination", ServiceYears = 6.394521m, Currency = "SAR",
            GratuityAmount = 31156.16m, LeaveEncashmentAmount = 2000m, GrossPayable = 33156.16m, TotalDeductions = 0m, NetPayable = 33156.16m,
            WagesPaidByRunId = wagesRun.Id, WageBaseDeltaAmount = 0m,
            Status = FinalSettlementStatuses.PendingApproval,
            CreatedByUserId = Guid.NewGuid(), CreatedByName = "Maker", SubmittedByUserId = Guid.NewGuid(), SubmittedByName = "Submitter", SubmittedAtUtc = DateTime.UtcNow,
        };
        db.EmployeeOffboardings.Add(offboarding);
        db.EmployeeFinalSettlements.Add(settlement);
        db.FinalSettlementLines.AddRange(
            new FinalSettlementLine { TenantId = k.TenantId, SettlementId = settlement.Id, ComponentCode = FinalSettlementComponents.Gratuity, ComponentName = "Gratuity", LineType = FinalSettlementLineTypes.Earning, Source = FinalSettlementComponents.SettlementSource, Amount = 31156.16m, SortOrder = 0 },
            new FinalSettlementLine { TenantId = k.TenantId, SettlementId = settlement.Id, ComponentCode = FinalSettlementComponents.LeaveEncashment, ComponentName = "Leave", LineType = FinalSettlementLineTypes.Earning, Source = FinalSettlementComponents.SettlementSource, Amount = 2000m, SortOrder = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var body = JsonSerializer.Deserialize<ApproveFinalSettlementRequest>(
            """{"confirmTerminationReason":"Termination","accrualDate":"2026-09-21"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var result = await FinalSettlementMakerCheckerTests.Controller(db, k.TenantId, Guid.NewGuid()).ApproveFinalSettlement(settlement.Id, body, default);
        db.ChangeTracker.Clear();
        return result;
    }

    [Fact]
    public async Task SettlementAccrual_WithK3_RollsALeaversOnePersonCostCentreToTheCompanyDefault()
    {
        await using var k = await KitAsync(3);

        var res = await ApproveSettlementOfAsync(k, "E1");

        res.Should().BeOfType<OkObjectResult>(JsonSerializer.Serialize((res as ObjectResult)?.Value));
        var expense = (await k.Db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == k.TenantId && x.EventType == GlEventTypes.SettlementAccrual).ToListAsync())
            .Where(l => l.CostCenterId != null).ToList();
        expense.Should().NotBeEmpty();
        expense.Should().OnlyContain(l => l.CostCenterCode == "CC-DEF", "one leaver is a cell of one: nothing is posted on a centre only they sit in");
    }

    [Fact]
    public async Task SettlementAccrual_WithK0_PostsOnTheLeaversOwnCostCentre()
    {
        await using var k = await KitAsync(0);

        (await ApproveSettlementOfAsync(k, "E1")).Should().BeOfType<OkObjectResult>();

        (await k.Db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == k.TenantId && x.EventType == GlEventTypes.SettlementAccrual && x.CostCenterId != null).ToListAsync())
            .Should().OnlyContain(l => l.CostCenterCode == "CC-A");
    }

    [Fact]
    public async Task S036_NoLedgerLineOfABonusAccrualOrASettlementAccrual_CarriesAnEmployeeCodeOrName()
    {
        await using var k = await KitAsync(3);
        await k.AddEmployee("EMP-7781", "D-A");
        var batch = await PendingBatchAsync(k, ("EMP-7781", 777m), ("E2", 1000m), ("E3", 1000m), ("E4", 1000m));
        (await Bonuses(k).ApproveBatch(batch.Id, new BatchApproveRequest("ok"), default)).Should().BeOfType<OkObjectResult>();
        (await ApproveSettlementOfAsync(k, "EMP-7781")).Should().BeOfType<OkObjectResult>();

        var rows = await k.Db.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == k.TenantId).ToListAsync();

        rows.Should().NotBeEmpty();
        foreach (var l in rows)
        {
            l.Description.Should().NotContain("EMP-7781").And.NotContain("Person EMP-7781", $"{l.EventType} description");
            l.SourceEntityRef.Should().NotContain("EMP-7781").And.NotContain("Person EMP-7781", $"{l.EventType} reference");
        }
    }
}
