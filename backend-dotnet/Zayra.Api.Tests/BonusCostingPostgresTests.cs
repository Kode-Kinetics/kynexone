using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Cost-accounting v1, WP-C2: the bonus accrual divides its expense per cost centre (T-020), and cancelling the batch contras it
/// per origin row under migration M2's rules (WP-E finding 2c): a contra never exceeds the row it reverses, copies that row's cost
/// centre, and flags the row reversed only when it closes it out. Real <see cref="BonusesController"/>, real PostgreSQL with M2.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class BonusCostingPostgresTests
{
    private readonly PostgresFixture _fx;
    public BonusCostingPostgresTests(PostgresFixture fx) => _fx = fx;

    private async Task<CostingLockKit> KitAsync()
    {
        Guid tenantId;
        await using (var db = _fx.CreateDb()) tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var k = await CostingLockKit.CreateAsync(true, true, _fx.CreateDb, tenantId);
        await k.AddCc("CC-110", "G_AND_A", segment: "4110");
        await k.AddCc("CC-200", "SELLING");
        await k.AddDept("D-FIN", "CC-110");
        await k.AddDept("D-SAL", "CC-200");
        await k.AddEmployee("E1", "D-SAL");
        await k.AddEmployee("E2", "D-FIN");
        await k.AddEmployee("E3", "D-FIN");
        return k;
    }

    private BonusesController Bonuses(CostingLockKit k, ZayraDbContext db)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", k.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Name, "bonus-test"),
            new("permission", "payroll.write"), new("permission", "payroll.approve"),
        };
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new BonusesController(db, new AllowAllScope()) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private sealed class AllowAllScope : Zayra.Api.Application.Common.IDataScopeService
    {
        public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new Zayra.Api.Application.Common.DataScope { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization });
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

    private async Task<List<FinanceGlEntry>> BonusLedgerAsync(CostingLockKit k, Guid batchId)
    {
        await using var read = k.K.NewContext();
        return await read.FinanceGlEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == k.TenantId && x.SourceModule == "Bonus" && x.SourceEntityId == batchId).ToListAsync();
    }

    [Fact]
    public async Task T020_ApprovingAcrossTwoCostCentres_PostsOneTwoSidedRowPerCostCentre_ThePayableSideStaysUncosted()
    {
        await using var k = await KitAsync();
        var batch = await PendingBatchAsync(k, ("E1", 1000m), ("E2", 1000m), ("E3", 500m));

        (await Bonuses(k, k.Db).ApproveBatch(batch.Id, new BatchApproveRequest("ok"), default)).Should().BeOfType<OkObjectResult>();

        var gl = await BonusLedgerAsync(k, batch.Id);
        gl.Select(l => (l.CostCenterCode, l.Amount, Expense: l.DebitAccount.Split(' ')[0], Payable: l.CreditAccount.Split(' ')[0]))
            .OrderBy(x => x.CostCenterCode).Should().Equal(("CC-110", 1500m, "6100", "2300"), ("CC-200", 1000m, "6100", "2300"));
        gl.Should().OnlyContain(l => l.PostingKey != null);
        gl.Single(l => l.CostCenterCode == "CC-110").CostCenterGlSegment.Should().Be("4110");
    }

    [Fact]
    public async Task ACostedBonusAccrual_SplitsAnUnevenTotalByLargestRemainder_NoHalalaLost()
    {
        // 2,899.99 over three equal weights (the oracle's contrived batch): 966.67 / 966.66 / 966.66, the spare unit to the lowest code.
        var ctx = new GlResolutionContext(new Dictionary<string, (string, string)>(), new Dictionary<string, (string, string)>(), Array.Empty<GlDriverRow>())
        {
            CostAccountingEnabled = true,
            CostCentres = new Dictionary<Guid, GlCostCentreInfo>(),
        };
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        ctx = ctx with { };
        var centres = new Dictionary<Guid, GlCostCentreInfo>
        {
            [a] = new("CC-110", "A", null, null, null, true), [b] = new("CC-200", "B", null, null, null, true), [c] = new("CC-300", "C", null, null, null, true),
        };
        ctx = new GlResolutionContext(ctx.Overrides, ctx.DriverDefaults, ctx.Drivers) { CostAccountingEnabled = true, CostCentres = centres };

        var lines = BonusCostSplit.BuildAccrual(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "BON-1", "B1", "2026-09", "SAR", 2899.99m,
            new[] { (1, 1000m), (2, 1000m), (3, 1000m) }, new Dictionary<int, Guid?> { [1] = a, [2] = b, [3] = c }, ctx, new DateOnly(2026, 9, 1), null, "t");

        lines.Select(l => (l.CostCenterCode, l.Amount)).Should().Equal(("CC-110", 966.67m), ("CC-200", 966.66m), ("CC-300", 966.66m));
        lines.Sum(l => l.Amount).Should().Be(2899.99m);
    }

    [Fact]
    public async Task CancellingAFullyAccruedBatch_ContrasEachCostCentreRow_AndPassesTheLedgerTriggers()
    {
        await using var k = await KitAsync();
        var batch = await PendingBatchAsync(k, ("E1", 1000m), ("E2", 1000m), ("E3", 500m));
        (await Bonuses(k, k.Db).ApproveBatch(batch.Id, new BatchApproveRequest("ok"), default)).Should().BeOfType<OkObjectResult>();
        k.Db.ChangeTracker.Clear();

        await using (var db = k.K.NewContext())
            (await Bonuses(k, db).RejectBatch(batch.Id, new RejectRequest("cancelled by finance"), default)).Should().BeOfType<OkObjectResult>();

        var gl = await BonusLedgerAsync(k, batch.Id);
        var accrual = gl.Where(l => l.EventType == GlEventTypes.BonusAccrual).ToList();
        var contras = gl.Where(l => l.EventType == GlEventTypes.BonusAccrualReversal).ToList();
        accrual.Should().OnlyContain(l => l.IsReversed);
        contras.Should().HaveCount(2);
        foreach (var o in accrual)
        {
            var c = contras.Single(x => x.ReversalOfEntryId == o.Id);
            (c.Amount, c.CostCenterId, c.CostCenterCode, c.CostCenterGlSegment).Should().Be((o.Amount, o.CostCenterId, o.CostCenterCode, o.CostCenterGlSegment));
        }
    }

    [Fact]
    public async Task CancellingAPartlyClearedBatch_SplitsTheRemainderAcrossTheOriginRows_LargestFirst_AndFlagsOnlyTheRowItCloses()
    {
        await using var k = await KitAsync();
        var batch = await PendingBatchAsync(k, ("E1", 1000m), ("E2", 1000m), ("E3", 500m));   // CC-110 1500, CC-200 1000
        (await Bonuses(k, k.Db).ApproveBatch(batch.Id, new BatchApproveRequest("ok"), default)).Should().BeOfType<OkObjectResult>();
        k.Db.ChangeTracker.Clear();
        // 700 of the payable has already been cleared (a payroll clearing line): 1,800 is left, which exceeds neither row alone... but
        // exceeds the smaller one, so the contra must span both.
        await using (var seed = k.K.NewContext())
        {
            seed.FinanceGlEntries.Add(new FinanceGlEntry
            {
                TenantId = k.TenantId, CompanyId = k.CompanyId, SourceModule = "Payroll", SourceEntityId = Guid.NewGuid(),
                SourceEntityRef = BonusGlLedger.BatchRef(batch.Id), EventType = GlEventTypes.BonusPayrollClearing,
                DebitAccount = "2300 - Bonus Payable", Amount = 700m, Currency = "SAR", Period = "2026-09", Description = "payroll clearing",
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = k.K.NewContext())
            (await Bonuses(k, db).RejectBatch(batch.Id, new RejectRequest("partial"), default)).Should().BeOfType<OkObjectResult>();

        var gl = await BonusLedgerAsync(k, batch.Id);
        var accrual = gl.Where(l => l.EventType == GlEventTypes.BonusAccrual).OrderByDescending(l => l.Amount).ToList();
        var contras = gl.Where(l => l.EventType == GlEventTypes.BonusAccrualReversal).ToList();
        contras.Sum(c => c.Amount).Should().Be(1800m);
        contras.Single(c => c.ReversalOfEntryId == accrual[0].Id).Amount.Should().Be(1500m);
        contras.Single(c => c.ReversalOfEntryId == accrual[1].Id).Amount.Should().Be(300m);
        accrual[0].IsReversed.Should().BeTrue("its contra closes it out");
        accrual[1].IsReversed.Should().BeFalse("a partial contra leaves the row visible to the payable sub-ledger");
        contras.Should().OnlyContain(c => c.CostCenterId != null);
    }
}
