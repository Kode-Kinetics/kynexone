using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>LON-06: bonus batch maker-checker, input range checks, and a relational (transactional) approve/pay path.</summary>
public sealed class BonusBatchSodTests : IAsyncDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly ZayraDbContext _db;
    private readonly Guid _tenant = Guid.NewGuid();
    private Employee _emp = null!;
    private BonusType _type = null!;

    public BonusBatchSodTests()
    {
        _conn.Open();
        _db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync() { await _db.DisposeAsync(); await _conn.DisposeAsync(); }

    private async Task SeedAsync()
    {
        var company = new Company { TenantId = _tenant, LegalNameEn = "Bonus Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland", IsActive = true, DefaultCurrency = "SAR" };
        _db.Companies.Add(company);
        await _db.SaveChangesAsync();
        _emp = new Employee
        {
            TenantId = _tenant, CompanyId = company.Id, EmployeeCode = "B-1", FullName = "Emp B1", Status = "Active",
            JoiningDate = new DateTime(2023, 1, 1), WorkEmail = "b1@test.com", Nationality = "SAU", ContractType = "Indefinite",
        };
        _type = new BonusType
        {
            TenantId = _tenant, Code = "PERF", NameEn = "Performance", TaxRegion = "GCC", IsActive = true,
            IsIncludedInGosiBase = false, IsIncludedInWps = false, IsIncludedInEosb = false,
        };
        _db.AddRange(_emp, _type);
        await _db.SaveChangesAsync();
    }

    /// <summary>A second Finance login (payroll.approve through an Allow override). The tenant row is never seeded in this world, so the tenant FK is not enforced for this one save.</summary>
    private async Task<Zayra.Api.Domain.Entities.User> SeedFinanceUserAsync()
    {
        var user = new Zayra.Api.Domain.Entities.User { TenantId = _tenant, Email = $"fin-{Guid.NewGuid():N}@test.local", NormalizedEmail = $"FIN-{Guid.NewGuid():N}@TEST.LOCAL", FullName = "Finance", IsGroupScope = true };
        _db.Users.Add(user);
        _db.UserPermissionOverrides.Add(new UserPermissionOverride { TenantId = _tenant, UserId = user.Id, PermissionKey = "payroll.approve", Effect = "Allow" });
        await _db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        try { await _db.SaveChangesAsync(); }
        finally { await _db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON"); }
        _db.ChangeTracker.Clear();
        return user;
    }

    private BonusesController As(Guid user)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", _tenant.ToString()), new(ClaimTypes.NameIdentifier, user.ToString()), new(ClaimTypes.Name, "bonus-user"),
            new("permission", "payroll.write"), new("permission", "payroll.approve"), new("permission", "payroll.read"),
        };
        return new BonusesController(_db, new OrgScope())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
        };
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }

    private async Task<BonusBatch> PendingBatchAsync(Guid? createdBy, Guid? submittedBy)
    {
        var batch = new BonusBatch
        {
            TenantId = _tenant, BonusTypeId = _type.Id, BonusTypeName = _type.NameEn, BatchNumber = $"BON-{Guid.NewGuid():N}"[..12],
            BatchName = "Q3", PaymentPeriod = "2026-06", PaymentDate = new DateOnly(2026, 6, 25), Status = "PendingApproval",
            EmployeeCount = 1, TotalAmount = 500m, CreatedBy = createdBy,
        };
        _db.BonusBatches.Add(batch);
        _db.BonusAuditLogs.Add(new BonusAuditLog
        {
            TenantId = _tenant, BonusBatchId = batch.Id, EntityType = "BonusBatch", Action = "BatchSubmitted", PerformedBy = submittedBy,
        });
        await _db.SaveChangesAsync();
        _db.EmployeeBonuses.Add(new EmployeeBonus
        {
            TenantId = _tenant, CompanyId = _emp.CompanyId, BonusBatchId = batch.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = _emp.Id,
            EmployeeName = _emp.FullName, BonusTypeId = _type.Id, BonusTypeName = _type.NameEn, BasicSalary = 10_000m,
            CalculationMethod = "Fixed", CalculationValue = 500m, GrossBonusAmount = 500m, TaxWithheld = 0m, BonusAmount = 500m,
            PaymentPeriod = "2026-06", Status = "Draft", TaxRegion = "GCC",
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return batch;
    }

    private async Task<BonusBatch> DraftBatchAsync()
    {
        var batch = new BonusBatch
        {
            TenantId = _tenant, BonusTypeId = _type.Id, BonusTypeName = _type.NameEn, BatchNumber = $"BON-{Guid.NewGuid():N}"[..12],
            BatchName = "Draft", PaymentPeriod = "2026-06", PaymentDate = new DateOnly(2026, 6, 25), Status = "Draft",
        };
        _db.BonusBatches.Add(batch);
        await _db.SaveChangesAsync();
        return batch;
    }

    // ── Maker-checker ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApproveBatch_ByTheBatchCreator_IsRefused_AndNothingAccrues()
    {
        await SeedAsync();
        var maker = Guid.NewGuid();
        var batch = await PendingBatchAsync(createdBy: maker, submittedBy: Guid.NewGuid());

        var result = await As(maker).ApproveBatch(batch.Id, new BatchApproveRequest("self"), Ct);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("PendingApproval", (await _db.BonusBatches.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await _db.FinanceGlEntries.ToListAsync());
        Assert.Empty(await _db.BonusApprovals.ToListAsync());
    }

    [Fact]
    public async Task ApproveBatch_ByTheSubmitter_IsRefused_AndNothingAccrues()
    {
        await SeedAsync();
        var submitter = Guid.NewGuid();
        var batch = await PendingBatchAsync(createdBy: Guid.NewGuid(), submittedBy: submitter);

        var result = await As(submitter).ApproveBatch(batch.Id, new BatchApproveRequest("self"), Ct);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("PendingApproval", (await _db.BonusBatches.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await _db.FinanceGlEntries.ToListAsync());
    }

    [Fact]
    public async Task ApproveBatch_ByAnIndependentApprover_AccruesOnce_AndMarkPaidThenWorks()
    {
        await SeedAsync();
        var batch = await PendingBatchAsync(createdBy: Guid.NewGuid(), submittedBy: Guid.NewGuid());
        var approver = As((await SeedFinanceUserAsync()).Id);
        // An independent payer must exist: the approver cannot also release the payment.
        var payerUser = await SeedFinanceUserAsync();
        var payer = As(payerUser.Id);

        Assert.IsType<OkObjectResult>(await approver.ApproveBatch(batch.Id, new BatchApproveRequest("ok"), Ct));
        Assert.Single(await _db.FinanceGlEntries.Where(x => x.EventType == "BonusAccrual").ToListAsync());
        // A replay by anyone is refused and does not accrue again.
        Assert.IsType<BadRequestObjectResult>(await approver.ApproveBatch(batch.Id, new BatchApproveRequest("again"), Ct));
        Assert.Single(await _db.FinanceGlEntries.Where(x => x.EventType == "BonusAccrual").ToListAsync());

        Assert.IsType<OkObjectResult>(await payer.MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), Ct));
        Assert.IsType<ConflictObjectResult>(await payer.MarkBatchPaid(batch.Id, new MarkBatchPaidRequest(null), Ct));
    }

    // ── Input ranges ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Fixed", 10_000, -500)]                   // negative fixed value
    [InlineData("PercentageSalary", 10_000, -10)]         // negative percentage
    [InlineData("PercentageSalary", -10_000, 10)]         // negative basic salary
    [InlineData("PercentageSalary", 10_000, 5_000)]       // absurd percentage
    [InlineData("Fixed", 10_000, 9_999_999_999_999)]      // beyond the column
    [InlineData("Fixed", 10_000, 0)]                      // a zero bonus is not a bonus
    public async Task AddEmployeeBonus_WithOutOfRangeValues_IsRefused_AndTheBatchTotalDoesNotMove(string method, long basic, long value)
    {
        await SeedAsync();
        var batch = await DraftBatchAsync();

        var result = await As(Guid.NewGuid()).AddEmployeeBonus(batch.Id,
            new AddEmployeeBonusRequest(_emp.PublicId, _emp.FullName, null, basic, method, value, null, _emp.Id), Ct);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await _db.EmployeeBonuses.ToListAsync());
        Assert.Equal(0m, (await _db.BonusBatches.AsNoTracking().SingleAsync()).TotalAmount);
    }

    [Fact]
    public async Task UpdateEmployeeBonus_ToANegativeValue_IsRefused_AndTheBonusIsUntouched()
    {
        await SeedAsync();
        var batch = await DraftBatchAsync();
        var ctrl = As(Guid.NewGuid());
        Assert.IsType<OkObjectResult>(await ctrl.AddEmployeeBonus(batch.Id,
            new AddEmployeeBonusRequest(_emp.PublicId, _emp.FullName, null, 10_000m, "Fixed", 500m, null, _emp.Id), Ct));
        var bonus = await _db.EmployeeBonuses.SingleAsync();

        var result = await ctrl.UpdateEmployeeBonus(batch.Id, bonus.Id, new UpdateEmployeeBonusRequest(null, -9_000m, null, null), Ct);

        Assert.IsType<BadRequestObjectResult>(result);
        _db.ChangeTracker.Clear();
        Assert.Equal(500m, (await _db.EmployeeBonuses.AsNoTracking().SingleAsync()).BonusAmount);
        Assert.Equal(500m, (await _db.BonusBatches.AsNoTracking().SingleAsync()).TotalAmount);
    }

    [Fact]
    public async Task BulkAdd_WithANegativeOverride_IsRefused()
    {
        await SeedAsync();
        var batch = await DraftBatchAsync();
        _emp.Salary = 10_000m;
        await _db.SaveChangesAsync();

        var result = await As(Guid.NewGuid()).BulkAddEmployees(batch.Id, new BulkAddRequest(null, null, null, null, -300m), Ct);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await _db.EmployeeBonuses.ToListAsync());
    }

    [Fact]
    public async Task AddEmployeeBonus_WithinRange_StillWorks()
    {
        await SeedAsync();
        var batch = await DraftBatchAsync();

        var result = await As(Guid.NewGuid()).AddEmployeeBonus(batch.Id,
            new AddEmployeeBonusRequest(_emp.PublicId, _emp.FullName, null, 10_000m, "PercentageSalary", 10m, null, _emp.Id), Ct);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1_000m, (await _db.EmployeeBonuses.AsNoTracking().SingleAsync()).BonusAmount);
    }
}
