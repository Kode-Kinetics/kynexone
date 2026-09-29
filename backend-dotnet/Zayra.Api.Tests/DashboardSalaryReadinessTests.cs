using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F06/I01 — the dashboard's payroll signal is live state, not a rules-engine insight. The
/// "MissingSalarySetup" insight stayed open after HR assigned every salary (the dashboard said all
/// 250 employees lacked one while Payroll showed 100% coverage), and nothing on the dashboard knew
/// that none of them had bank details. These KPIs use the payroll readiness definitions for the
/// current month, over the caller's own population.
/// </summary>
public class DashboardSalaryReadinessTests
{
    private static ZayraDbContext Db() => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static DashboardController Ctrl(ZayraDbContext db, Guid tid, IReadOnlyCollection<int>? allowed = null)
    {
        // F10: payroll readiness counts are payroll information, returned only with payroll.read.
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", tid.ToString()), new Claim("permission", "payroll.read")], "test"));
        return new DashboardController(
            db,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            new SalaryReadinessScope(allowed))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } } };
    }

    private static async Task<DashboardKpisDto> Kpis(ZayraDbContext db, Guid tid, IReadOnlyCollection<int>? allowed = null) =>
        Assert.IsType<DashboardKpisDto>(Assert.IsType<OkObjectResult>(await Ctrl(db, tid, allowed).Kpis(default)).Value);

    private static EmployeeSalaryStructure Salary(Guid tid, int employeeId, DateOnly effective, bool active = true) => new()
    {
        TenantId = tid, EmployeeId = employeeId, SalaryStructureId = Guid.NewGuid(),
        BasicSalary = 1000, Currency = "SAR", EffectiveDate = effective, IsActive = active,
    };

    private static async Task<Employee[]> SeedEmployees(ZayraDbContext db, Guid tid, params string[] codes)
    {
        db.Tenants.Add(new Tenant { Id = tid, Name = "Payroll KPI", Slug = $"payroll-kpi-{tid:N}" });
        var employees = codes.Select(c => new Employee { TenantId = tid, EmployeeCode = c, FullName = c, Status = "Active" }).ToArray();
        db.Employees.AddRange(employees);
        db.Employees.Add(new Employee { TenantId = tid, EmployeeCode = "LEFT", FullName = "Left", Status = "Terminated" });
        await db.SaveChangesAsync();
        return employees;
    }

    [Fact]
    public async Task MissingSalary_CountsActiveEmployeesWithoutASalaryEffectiveThisMonth()
    {
        await using var db = Db();
        var tid = Guid.NewGuid();
        var e = await SeedEmployees(db, tid, "A", "B", "C", "D");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthEnd = new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1);
        db.EmployeeSalaryStructures.AddRange(
            Salary(tid, e[0].Id, new DateOnly(2024, 1, 1)),
            Salary(tid, e[1].Id, monthEnd),                 // starts this month: payable this month
            Salary(tid, e[2].Id, monthEnd.AddDays(1)),      // starts next month: not payable yet
            Salary(tid, e[3].Id, new DateOnly(2024, 1, 1), active: false));
        await db.SaveChangesAsync();

        (await Kpis(db, tid)).MissingSalaryAssignments.Should().Be(2,
            "C's salary starts next month, D's assignment is inactive, and the terminated employee is not counted");
    }

    [Fact]
    public async Task MissingBankDetails_CountsActiveEmployeesWithNoIbanOnALiveProfile()
    {
        await using var db = Db();
        var tid = Guid.NewGuid();
        var e = await SeedEmployees(db, tid, "A", "B", "C", "D", "E");
        db.EmployeePayrollProfiles.AddRange(
            new EmployeePayrollProfile { TenantId = tid, EmployeeId = e[0].Id, Iban = "SA0380000000608010167519" },
            new EmployeePayrollProfile { TenantId = tid, EmployeeId = e[1].Id, Iban = "  " },
            new EmployeePayrollProfile { TenantId = tid, EmployeeId = e[2].Id, Iban = "SA0380000000608010167519", IsDeleted = true },
            // Another tenant's row naming E's id must not count for this tenant.
            new EmployeePayrollProfile { TenantId = Guid.NewGuid(), EmployeeId = e[4].Id, Iban = "SA0380000000608010167519" });
        await db.SaveChangesAsync();

        (await Kpis(db, tid)).MissingBankDetails.Should().Be(4, "only A has a live profile with an IBAN");
    }

    [Fact]
    public async Task PayrollKpis_ForAScopedCaller_CountOnlyTheirPopulation()
    {
        await using var db = Db();
        var tid = Guid.NewGuid();
        var e = await SeedEmployees(db, tid, "A", "B", "C");

        var kpis = await Kpis(db, tid, allowed: new[] { e[0].Id });

        kpis.MissingSalaryAssignments.Should().Be(1, "a manager must not see the tenant-wide payroll gap");
        kpis.MissingBankDetails.Should().Be(1);
    }
}

file sealed class SalaryReadinessScope(IReadOnlyCollection<int>? allowed) : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
        Task.FromResult(allowed is null
            ? new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null }
            : new DataScope { Level = DataScopeLevel.Department, AllowedEmployeeIds = allowed });
}
