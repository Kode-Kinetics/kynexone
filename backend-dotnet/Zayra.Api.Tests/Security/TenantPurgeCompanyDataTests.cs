using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;
using Zayra.Api.Tests.Platform;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Phase 2 (spec 10): tenant purge previously hard-deleted a fixed table list and
/// orphaned companies, employees, payroll and every other operational table. The sweep
/// is now derived from the EF model. Runs against real Postgres because ExecuteDelete
/// is not InMemory-runnable.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class TenantPurgeCompanyDataTests : PlatformTestBase
{
    private readonly PostgresFixture _fx;
    public TenantPurgeCompanyDataTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task PurgeTenant_ErasesCompanyScopedData_RetainsAuditTrail()
    {
        await using var db = _fx.CreateDb();

        // Soft-deleted tenant carrying the full company-scoped object graph.
        var tenant = new Zayra.Api.Domain.Entities.Tenant { Name = "Purge Corp", Slug = $"__deleted_purge-{Guid.NewGuid():N}"[..38], IsActive = false };
        db.Tenants.Add(tenant);
        var company = new Company { TenantId = tenant.Id, LegalNameEn = "Purge LLC", RegistrationNumber = $"R-{Guid.NewGuid():N}", IsActive = true };
        db.Companies.Add(company);
        var employee = new Employee { TenantId = tenant.Id, CompanyId = company.Id, EmployeeCode = $"PG-{Guid.NewGuid():N}"[..12], FullName = "Purged Person", Status = "Active", JoiningDate = DateTime.UtcNow };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        db.LeaveRequests.Add(new LeaveRequest { TenantId = tenant.Id, CompanyId = company.Id, EmployeeId = employee.Id, StartDate = new DateOnly(2026, 5, 1), EndDate = new DateOnly(2026, 5, 2), Status = "Approved" });
        db.CompanyTaxPolicies.Add(new CompanyTaxPolicy { TenantId = tenant.Id, CompanyId = company.Id, CountryCode = "SA", EffectiveFrom = new DateOnly(2026, 1, 1), IncomeTaxRatePercent = 1m });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var result = await controller.PurgeTenant(tenant.Id, confirm: "PURGE", CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        // Company-scoped data must not be orphaned (the old fixed list left all of this).
        (await db.Companies.IgnoreQueryFilters().CountAsync(c => c.TenantId == tenant.Id)).Should().Be(0);
        (await db.Employees.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant.Id)).Should().Be(0);
        (await db.LeaveRequests.IgnoreQueryFilters().CountAsync(l => l.TenantId == tenant.Id)).Should().Be(0);
        (await db.CompanyTaxPolicies.IgnoreQueryFilters().CountAsync(p => p.TenantId == tenant.Id)).Should().Be(0);
        (await db.Tenants.CountAsync(t => t.Id == tenant.Id)).Should().Be(0);

        // Destructive cleanup is audited and the audit trail survives as the legal record.
        (await db.AdminAuditLogs.IgnoreQueryFilters()
                .Where(a => a.TenantId == tenant.Id && (a.Action == "TenantPurged" || a.Action == "TenantPurgeCompleted"))
                .CountAsync())
            .Should().BeGreaterThanOrEqualTo(2, "purge start and completion must both be audited");
    }
}
