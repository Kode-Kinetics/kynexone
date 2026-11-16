using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The bonus audit report read every Bonus ledger row, every batch and the tenant-wide control-account
/// balances with no company filter, so a caller scoped to company A saw company B's ledger volume, batch
/// totals and Bonus Payable balance. A company-scoped caller now sees only their own entity's batches and
/// ledger rows, and the tenant-wide control balances are withheld. (Postgres: the report aggregates decimals
/// in SQL, which SQLite cannot do.)
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class BonusAuditReportScopePostgresTests
{
    private readonly PostgresFixture _fx;
    public BonusAuditReportScopePostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task AuditReport_ForACompanyScopedCaller_ExcludesTheOtherEntitiesBatchesLedgerAndTenantWideBalances()
    {
        Guid tid, coA, coB;
        await using (var seed = _fx.CreateDb())
        {
            tid = await PostgresFixture.SeedMinimalTenant(seed);
            coA = await CompanyAsync(seed, tid, "Entity A");
            coB = await CompanyAsync(seed, tid, "Entity B");
            var type = new BonusType { TenantId = tid, Code = "PERF", NameEn = "Performance", TaxRegion = "GCC", IsActive = true };
            seed.BonusTypes.Add(type);
            await seed.SaveChangesAsync();
            var batchA = await BatchAsync(seed, tid, type, coA, "BON-A", 1_000m);
            var batchB = await BatchAsync(seed, tid, type, coB, "BON-B", 7_000m);
            Accrual(seed, tid, batchA, 1_000m, coA);
            Accrual(seed, tid, batchB, 7_000m, coB);
            Accrual(seed, tid, batchB, 500m, null);   // an unattributed legacy row: group scope only
            await seed.SaveChangesAsync();
        }

        using var scopedDoc = await ReportAsync(tid, scopedTo: coA);
        var s = scopedDoc.RootElement;
        s.GetProperty("TotalBatches").GetInt32().Should().Be(1, "only entity A's batch is visible");
        s.GetProperty("TotalBonusAmount").GetDecimal().Should().Be(1_000m, "entity B's 7,000 must not appear");
        s.GetProperty("GlEntriesCount").GetInt32().Should().Be(1, "only entity A's ledger rows");
        s.GetProperty("ControlAccounts").GetArrayLength().Should().Be(0, "tenant-wide control balances span every entity");
        s.GetProperty("Batches").EnumerateArray().Select(b => b.GetProperty("BatchNumber").GetString()).Should().Equal("BON-A");

        // Group scope is unchanged: everything, including the control accounts.
        using var groupDoc = await ReportAsync(tid, scopedTo: null);
        var g = groupDoc.RootElement;
        g.GetProperty("TotalBatches").GetInt32().Should().Be(2);
        g.GetProperty("GlEntriesCount").GetInt32().Should().Be(3);
        g.GetProperty("ControlAccounts").GetArrayLength().Should().BeGreaterThan(0);
    }

    private async Task<JsonDocument> ReportAsync(Guid tid, Guid? scopedTo)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tid.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "scope-test"), new("permission", "payroll.read"), new("permission", "finance.gl.read"),
        };
        if (scopedTo is Guid cid)
            claims.Add(new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = EntityScopeModes.Companies, c = new[] { cid } })));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        await using var db = _fx.CreateDbWithAccessor(new HttpContextAccessor { HttpContext = http });
        var controller = new BonusesController(db, new Unrestricted()) { ControllerContext = new ControllerContext { HttpContext = http } };
        var result = await controller.AuditReport(null, CancellationToken.None);
        return JsonDocument.Parse(JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value));
    }

    private static async Task<Guid> CompanyAsync(ZayraDbContext db, Guid tid, string name)
    {
        var c = new Company { TenantId = tid, LegalNameEn = name, CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR" };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    private static async Task<BonusBatch> BatchAsync(ZayraDbContext db, Guid tid, BonusType type, Guid companyId, string number, decimal amount)
    {
        var emp = new Employee
        {
            TenantId = tid, CompanyId = companyId, EmployeeCode = $"{number}-E", FullName = $"Emp {number}", Status = "Active",
            JoiningDate = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), WorkEmail = $"{number}@scope.test", Nationality = "SAU", ContractType = "Indefinite",
        };
        db.Employees.Add(emp);
        var b = new BonusBatch
        {
            TenantId = tid, BonusTypeId = type.Id, BonusTypeName = type.NameEn, BatchNumber = number, BatchName = $"Batch {number}",
            PaymentPeriod = "2026-06", PaymentDate = new DateOnly(2026, 6, 25), Status = "Approved", EmployeeCount = 1, TotalAmount = amount,
        };
        db.BonusBatches.Add(b);
        await db.SaveChangesAsync();
        db.EmployeeBonuses.Add(new EmployeeBonus
        {
            TenantId = tid, CompanyId = companyId, BonusBatchId = b.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
            EmployeeName = emp.FullName, BonusTypeId = type.Id, BonusTypeName = type.NameEn, BasicSalary = 10_000m,
            CalculationMethod = "Fixed", CalculationValue = amount, GrossBonusAmount = amount, TaxWithheld = 0m, BonusAmount = amount,
            PaymentPeriod = "2026-06", Status = "Approved", TaxRegion = "GCC",
        });
        await db.SaveChangesAsync();
        return b;
    }

    private static void Accrual(ZayraDbContext db, Guid tid, BonusBatch batch, decimal amount, Guid? companyId) =>
        db.FinanceGlEntries.Add(new FinanceGlEntry
        {
            TenantId = tid, CompanyId = companyId, SourceModule = BonusGlLedger.SourceModule, SourceEntityId = batch.Id,
            SourceEntityRef = batch.BatchNumber, EventType = companyId is null ? GlEventTypes.BonusAccrualLegacy : GlEventTypes.BonusAccrual,
            DebitAccount = "6100 - Employee Bonus Expense", CreditAccount = "2300 - Bonus Payable", Amount = amount, Currency = "SAR",
            EntryDate = new DateOnly(2026, 6, 1), Period = "2026-06", Description = "Bonus accrual",
        });

    private sealed class Unrestricted : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
    }
}
