using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Finance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Shared fixture for the advance audit-remediation tests (LON-02, LON-07, LON-08, LON-10).</summary>
internal static class AdvanceTestKit
{
    internal sealed record Fx(Guid TenantId, Guid CompanyId, int EmployeeId, Guid EmployeePublicId, Guid EmployeeUserId);

    internal static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>One SAR company, one employee on a 10,000 monthly wage; policy optional (null = no active policy).</summary>
    internal static async Task<Fx> SeedAsync(ZayraDbContext db, bool withPolicy = true, int maxInstallments = 3)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "Advance Co", CountryCode = "SAU", DefaultCurrency = "SAR", IsActive = true };
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "ADV-001", FullName = "Huda Saleh", EnglishName = "Huda Saleh",
            Status = EmployeeStatuses.Active, JoiningDate = DateTime.UtcNow.AddMonths(-24), UserAccountId = userId,
        };
        db.AddRange(company, employee);
        if (withPolicy)
            db.AdvancePolicies.Add(new AdvancePolicy
            {
                TenantId = tenantId, PolicyName = "Standard", MaxPercentageOfSalary = 50, MaxAdvancesPerYear = 2,
                MinServiceMonths = 6, AllowInstallments = true, MaxInstallments = maxInstallments, CooldownMonths = 3,
                RequiresApproval = true, IsActive = true,
            });
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = employee.Id, SalaryStructureId = Guid.NewGuid(),
            BasicSalary = 8_000m, HousingAllowance = 2_000m, Currency = "SAR",
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-1)), IsActive = true,
        });
        await db.SaveChangesAsync();
        return new Fx(tenantId, company.Id, employee.Id, employee.PublicId, userId);
    }

    internal static async Task<Guid> SeedPendingAsync(ZayraDbContext db, Fx f, decimal requested, int installments = 2)
    {
        var adv = new SalaryAdvance
        {
            TenantId = f.TenantId, CompanyId = f.CompanyId, EmployeeId = f.EmployeePublicId, EmployeeIntId = f.EmployeeId,
            EmployeeName = "Huda Saleh", AdvanceNumber = $"ADV-P-{Guid.NewGuid():N}"[..14], RequestedAmount = requested,
            RepaymentType = "Installments", Installments = installments, Status = "Pending", CreatedBy = Guid.NewGuid(),
        };
        db.SalaryAdvances.Add(adv);
        await db.SaveChangesAsync();
        return adv.Id;
    }

    internal static AdvancesController Advances(ZayraDbContext db, Fx f, Guid userId, string[] roles,
        IDataScopeService? scope = null, string[]? perms = null)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", f.TenantId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "Advance Tester"),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange((perms ?? Array.Empty<string>()).Select(p => new Claim("permission", p)));
        return new AdvancesController(db, scope ?? new FixedScope(null))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
            },
        };
    }

    internal sealed class FixedScope(int[]? allowed) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(allowed is null
                ? new DataScope { Level = DataScopeLevel.Organization }
                : new DataScope { Level = DataScopeLevel.Department, AllowedEmployeeIds = allowed });
    }
}

/// <summary>LON-02 (terms: installments sum to principal, hard cap) and LON-08 (policy upsert validated + audited).</summary>
public class AdvanceTermsValidationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Approve_InstallmentAmounts_SumExactlyToThePrincipal_WithTheRemainderInTheLast()
    {
        await using var db = AdvanceTestKit.CreateDb();
        var f = await AdvanceTestKit.SeedAsync(db);
        var advanceId = await AdvanceTestKit.SeedPendingAsync(db, f, requested: 1_000m, installments: 3);

        var result = await AdvanceTestKit.Advances(db, f, Guid.NewGuid(), new[] { "HR Manager" })
            .Approve(advanceId, new AdvanceApproveRequest(1_000m, 3, null), Ct);

        Assert.IsType<OkObjectResult>(result);
        var rows = await db.AdvanceInstallments.Where(x => x.AdvanceId == advanceId).OrderBy(x => x.InstallmentNumber).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(decimal.Round(r.AmountDue, 2), r.AmountDue));
        Assert.Equal(1_000m, rows.Sum(r => r.AmountDue));
        Assert.Equal(new[] { 333.33m, 333.33m, 333.34m }, rows.Select(r => r.AmountDue).ToArray());
    }

    [Fact]
    public async Task Create_WithoutAPolicy_RefusesMoreThanTheSixtyInstallmentHardCap()
    {
        await using var db = AdvanceTestKit.CreateDb();
        var f = await AdvanceTestKit.SeedAsync(db, withPolicy: false);

        var result = await AdvanceTestKit.Advances(db, f, Guid.NewGuid(), new[] { "HR Manager" }).Create(
            new CreateAdvanceRequest(f.EmployeePublicId, "Huda Saleh", 6_100m, "Installments", 61, null, null, f.EmployeeId), Ct);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.SalaryAdvances.ToListAsync());
    }

    [Fact]
    public async Task Create_AndApprove_WhenThePolicyDoesNotRequireApproval_StillEnforceEveryCap()
    {
        await using var db = AdvanceTestKit.CreateDb();
        var f = await AdvanceTestKit.SeedAsync(db);
        var policy = await db.AdvancePolicies.SingleAsync();
        policy.RequiresApproval = false;
        await db.SaveChangesAsync();

        // Over the 50% salary cap (5,000) and over the installment cap (3).
        var overCap = await AdvanceTestKit.Advances(db, f, Guid.NewGuid(), new[] { "HR Manager" }).Create(
            new CreateAdvanceRequest(f.EmployeePublicId, "Huda Saleh", 9_000m, "Installments", 2, null, null, f.EmployeeId), Ct);
        var overInstallments = await AdvanceTestKit.Advances(db, f, Guid.NewGuid(), new[] { "HR Manager" }).Create(
            new CreateAdvanceRequest(f.EmployeePublicId, "Huda Saleh", 1_000m, "Installments", 4, null, null, f.EmployeeId), Ct);

        Assert.IsType<BadRequestObjectResult>(overCap);
        Assert.IsType<BadRequestObjectResult>(overInstallments);
        Assert.Empty(await db.SalaryAdvances.ToListAsync());
    }

    // ── LON-08 policy upsert ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1, 3, 2, 6, 3)]      // negative cap
    [InlineData(101, 3, 2, 6, 3)]     // more than the whole salary
    [InlineData(50, 0, 2, 6, 3)]      // zero installments allowed
    [InlineData(50, 61, 2, 6, 3)]     // above the hard cap
    [InlineData(50, 3, -1, 6, 3)]     // negative per-year count
    [InlineData(50, 3, 2, -6, 3)]     // negative service months
    [InlineData(50, 3, 2, 6, -3)]     // negative cooldown
    public async Task UpsertPolicy_OutOfRange_IsRejected_AndNothingChanges(
        int pct, int maxInstallments, int perYear, int minService, int cooldown)
    {
        await using var db = AdvanceTestKit.CreateDb();
        var f = await AdvanceTestKit.SeedAsync(db);

        var result = await AdvanceTestKit.Advances(db, f, Guid.NewGuid(), new[] { "HR Manager" }).UpsertPolicy(
            new AdvancePolicyRequest("Tampered", pct, perYear, minService, true, maxInstallments, cooldown, true), Ct);

        Assert.IsType<BadRequestObjectResult>(result);
        var policy = await db.AdvancePolicies.SingleAsync();
        Assert.Equal("Standard", policy.PolicyName);
        Assert.Equal(50m, policy.MaxPercentageOfSalary);
        Assert.Empty(await db.AdvanceAuditLogs.ToListAsync());
    }

    [Fact]
    public async Task UpsertPolicy_Valid_WritesAnAuditRowWithOldAndNewValues()
    {
        await using var db = AdvanceTestKit.CreateDb();
        var f = await AdvanceTestKit.SeedAsync(db);
        var actor = Guid.NewGuid();

        var result = await AdvanceTestKit.Advances(db, f, actor, new[] { "HR Manager" }).UpsertPolicy(
            new AdvancePolicyRequest("Standard", 40, 2, 6, true, 4, 3, true), Ct);

        Assert.IsType<OkObjectResult>(result);
        var audit = Assert.Single(await db.AdvanceAuditLogs.ToListAsync());
        Assert.Equal("AdvancePolicyChanged", audit.Action);
        Assert.Equal(actor, audit.PerformedBy);
        Assert.Contains("50", audit.OldValuesJson);
        Assert.Contains("40", audit.NewValuesJson);
    }
}
