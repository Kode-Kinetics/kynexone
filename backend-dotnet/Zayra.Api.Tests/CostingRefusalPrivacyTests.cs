using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;
using static Zayra.Api.Tests.CostingLockKit;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-accounting v1, WP-C3, final security and ledger review:
/// (a) a refusal body never carries an amount that equals one person's pay (the group's amount is withheld when the group holds fewer people than the
/// company's small-cell minimum, or exactly one when no minimum is set);
/// (c) an Income-typed account is profit and loss for the stamper and the Lock readiness like everywhere else, so a Required one yields per-employee blockers;
/// (f) the approval hash covers the posting currency.
/// </summary>
public class CostingRefusalPrivacyTests
{
    private static readonly (string, string, string, decimal, bool)[] NoDeductions = [];

    private static async Task<CostingLockKit> OrgAsync(short k = 0, int unresolved = 1)
    {
        var kit = await CostingLockKit.CreateAsync(costing: true, preset: true);
        await kit.AddCc("CC-200", "SELLING");
        await kit.AddDept("D-SAL", "CC-200");
        await kit.AddEmployee("E1", "D-SAL");
        for (var i = 1; i <= unresolved; i++) await kit.AddEmployee($"U{i}");
        if (k > 0) (await kit.Db.Companies.SingleAsync(c => c.Id == kit.CompanyId)).CostCentreSmallCellMin = k;
        await kit.Db.SaveChangesAsync();
        return kit;
    }

    private static async Task<JsonElement> LockRefusalAsync(CostingLockKit k, int unresolved)
    {
        var earnings = new List<(string, string, string, decimal)> { E("E1", "BASIC", 1000m) };
        for (var i = 1; i <= unresolved; i++) earnings.Add(E($"U{i}", "BASIC", 100m * i));
        var run = await k.SeedRunAsync(earnings, NoDeductions);
        var res = await k.ApproveAndLockAsync(run.Id);
        Status(res).Should().Be(422, Json(res).ToString());
        return Json(res);
    }

    private static JsonElement Basic(JsonElement body) =>
        body.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("accountCode").GetString() == "5001");

    // ── (a) amounts that equal one person's pay are withheld ─────────────────

    [Fact]
    public async Task A_AGroupOfOneEmployee_HasItsAmountWithheld_WhenNoMinimumIsSet()
    {
        await using var k = await OrgAsync(k: 0, unresolved: 1);

        var group = Basic(await LockRefusalAsync(k, 1));

        group.GetProperty("employeeCount").GetInt32().Should().Be(1);
        group.GetProperty("amount").ValueKind.Should().Be(JsonValueKind.Null, "the aggregate of one person is that person's pay");
        group.GetProperty("amountWithheld").GetBoolean().Should().BeTrue();
        group.GetProperty("message").GetString().Should().Contain("1 employee(s) have no cost centre");
    }

    [Fact]
    public async Task A_AGroupOfTwo_KeepsItsAmount_WhenNoMinimumIsSet()
    {
        await using var k = await OrgAsync(k: 0, unresolved: 2);

        var group = Basic(await LockRefusalAsync(k, 2));

        (group.GetProperty("employeeCount").GetInt32(), group.GetProperty("amount").GetDecimal(), group.GetProperty("amountWithheld").GetBoolean())
            .Should().Be((2, 300m, false));
    }

    [Theory]
    [InlineData(2, false)]   // fewer than K=3 people: withheld
    [InlineData(3, true)]    // exactly K people: shown
    public async Task A_WithAMinimumOfThree_TheAmountIsShownOnlyForGroupsOfAtLeastThree(int unresolved, bool shown)
    {
        await using var k = await OrgAsync(k: 3, unresolved: unresolved);

        var group = Basic(await LockRefusalAsync(k, unresolved));

        group.GetProperty("amountWithheld").GetBoolean().Should().Be(!shown);
        if (shown) group.GetProperty("amount").GetDecimal().Should().Be(100m * (unresolved * (unresolved + 1) / 2));
        else group.GetProperty("amount").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void A_TheRefusalBuilder_AppliesTheSameRule_ToEveryWriter()
    {
        var one = new[] { new CostBlocker(7, "E7", "company", null, null, null, PayrollCostStamper.RequiredCode, "6100", "Bonus", 1000m) };
        var two = one.Concat(new[] { new CostBlocker(8, "E8", "company", null, null, null, PayrollCostStamper.RequiredCode, "6100", "Bonus", 500m) }).ToArray();

        JsonSerializer.SerializeToElement(PayrollCostStamper.BuildRefusal(one, smallCellMin: 0).Groups.Single()).GetProperty("amountWithheld").GetBoolean().Should().BeTrue();
        JsonSerializer.SerializeToElement(PayrollCostStamper.BuildRefusal(two, smallCellMin: 0).Groups.Single()).GetProperty("amount").GetDecimal().Should().Be(1500m);
        JsonSerializer.SerializeToElement(PayrollCostStamper.BuildRefusal(two, smallCellMin: 3).Groups.Single()).GetProperty("amountWithheld").GetBoolean().Should().BeTrue();
    }

    private sealed class AllowAllScope : Zayra.Api.Application.Common.IDataScopeService
    {
        public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new Zayra.Api.Application.Common.DataScope { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization });
    }

    [Fact]
    public async Task A_TheBonusRefusal_WithholdsTheAmountOfASingleEmployee()
    {
        await using var k = await OrgAsync(k: 0, unresolved: 1);
        var type = new BonusType { TenantId = k.TenantId, Code = "PERF", NameEn = "Performance", TaxRegion = "GCC", IsActive = true };
        k.Db.BonusTypes.Add(type);
        var batch = new BonusBatch
        {
            TenantId = k.TenantId, BonusTypeId = type.Id, BonusTypeName = type.NameEn, BatchNumber = "BON-1", BatchName = "Batch 1", PaymentPeriod = "2026-09",
            PaymentDate = new DateOnly(2026, 9, 25), Status = "PendingApproval", EmployeeCount = 1, TotalAmount = 777m,
        };
        k.Db.BonusBatches.Add(batch);
        await k.Db.SaveChangesAsync();
        k.Db.EmployeeBonuses.Add(new EmployeeBonus
        {
            TenantId = k.TenantId, CompanyId = k.CompanyId, BonusBatchId = batch.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = k.Emp["U1"].Id, EmployeeName = "U1",
            BonusTypeId = type.Id, BonusTypeName = type.NameEn, BasicSalary = 10_000m, CalculationMethod = "Fixed", CalculationValue = 777m, GrossBonusAmount = 777m,
            TaxWithheld = 0m, BonusAmount = 777m, PaymentPeriod = "2026-09", Status = "Draft", TaxRegion = "GCC",
        });
        await k.Db.SaveChangesAsync();
        k.Db.ChangeTracker.Clear();
        var claims = new[]
        {
            new Claim("tenant_id", k.TenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Name, "t"),
            new Claim("permission", "payroll.write"), new Claim("permission", "payroll.approve"),
        };
        var ctrl = new BonusesController(k.Db, new AllowAllScope())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
        };

        var res = await ctrl.ApproveBatch(batch.Id, new BatchApproveRequest("ok"), default);

        Status(res).Should().Be(422);
        var group = Json(res).GetProperty("groups").EnumerateArray().Single();
        group.GetProperty("amount").ValueKind.Should().Be(JsonValueKind.Null);
        group.GetProperty("amountWithheld").GetBoolean().Should().BeTrue();
    }

    // ── (c) Income accounts are profit and loss for the stamper ──────────────

    [Fact]
    public async Task C_ARequiredIncomeAccount_YieldsPerEmployeeBlockersAtLock_NotALateGenericRefusal()
    {
        await using var k = await OrgAsync(k: 0, unresolved: 1);
        var income = new GlAccount { TenantId = k.TenantId, CompanyId = k.CompanyId, Code = "4900", Name = "Recoveries income", AccountType = "Income", CostCenterRule = "Required" };
        k.Db.GlAccounts.Add(income);
        await k.Db.SaveChangesAsync();
        k.Db.GlAccountMappings.Add(new GlAccountMapping { TenantId = k.TenantId, CompanyId = k.CompanyId, DriverKey = "EARN:BASIC", AccountId = income.Id });
        await k.Db.SaveChangesAsync();
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 1000m), E("U1", "BASIC", 400m) }, NoDeductions);

        var res = await k.ApproveAndLockAsync(run.Id);

        Status(res).Should().Be(422);
        var body = Json(res);
        body.GetProperty("error").GetString().Should().Be("cost_centre_required");
        var group = body.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("accountCode").GetString() == "4900");
        group.GetProperty("employees").EnumerateArray().Select(x => x.GetProperty("code").GetString()).Should().Equal("U1");
        (await k.GlAsync(run.Id)).Should().BeEmpty();
    }

    // ── (f) the approval hash covers the currency ────────────────────────────

    [Fact]
    public async Task F_ACompanyCurrencyChange_AfterApproval_SendsTheRunBack()
    {
        await using var k = await OrgAsync(k: 0, unresolved: 0);
        var run = await k.SeedRunAsync(new[] { E("E1", "BASIC", 1000m) }, NoDeductions);
        (await k.ApproveAsync(run.Id)).Should().BeOfType<OkObjectResult>();
        await k.Db.Companies.Where(c => c.Id == k.CompanyId).ExecuteUpdateAsync(s => s.SetProperty(c => c.DefaultCurrency, "AED"));

        var res = await k.LockAsync(run.Id);

        Status(res).Should().Be(409);
        Err(res).Should().Be("cost_centre_changed_after_approval");
        (await k.GlAsync(run.Id)).Should().BeEmpty("nothing posted in a currency nobody approved");
    }
}
