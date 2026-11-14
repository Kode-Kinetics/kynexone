using System.Security.Claims;
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
/// The home tile said "Overtime hours: 99 h month to date" while the Overtime dashboard said 5 h. The tile summed every attendance row's raw
/// OvertimeHours (any status, detected, never approved). It is now the APPROVED overtime of the month: the minutes on the payroll impacts of requests
/// whose work date falls this month, the same source the Overtime dashboard's approved hours read. Raw detected hours and pending requests never count.
/// </summary>
public class DashboardApprovedOvertimeTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static ZayraDbContext MakeDb()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Tenants.Add(new Tenant { Id = Tenant, Name = "Overtime", Slug = $"overtime-{Tenant:N}" });
        db.SaveChanges();
        return db;
    }

    private static DashboardController Controller(ZayraDbContext db)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", Tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim("permission", "payroll.read"),
        }, "test"));
        return new DashboardController(db, new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new OrganizationScope())
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } } };
    }

    private static async Task<DashboardFullDto> FullAsync(ZayraDbContext db) =>
        Assert.IsType<DashboardFullDto>(Assert.IsType<OkObjectResult>(await Controller(db).Full(3)).Value);

    private static DateOnly ThisMonthDay()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return new DateOnly(today.Year, today.Month, 1);
    }

    private static void Seed(ZayraDbContext db, DateOnly day)
    {
        var emp = new Employee { TenantId = Tenant, EmployeeCode = "E1", FullName = "Person", Status = "Active", JoiningDate = DateTime.UtcNow.AddYears(-1) };
        db.Employees.Add(emp);
        db.SaveChanges();
        // Raw attendance overtime: 99 hours detected across statuses, none of it approved.
        db.AttendanceRecords.Add(new AttendanceRecord { TenantId = Tenant, EmployeeId = emp.Id, WorkDate = day, Status = "Present", OvertimeHours = 90m });
        db.AttendanceRecords.Add(new AttendanceRecord { TenantId = Tenant, EmployeeId = emp.Id, WorkDate = day, Status = "Absent", OvertimeHours = 9m });
        // Approved overtime: 4 h + 1 h, paid through impacts. A pending request has no impact and counts for nothing.
        var approvedA = new OvertimeRequest { TenantId = Tenant, EmployeeId = emp.Id, WorkDate = day, Status = "Approved", ApprovedMinutes = 240 };
        var approvedB = new OvertimeRequest { TenantId = Tenant, EmployeeId = emp.Id, WorkDate = day, Status = "Approved", ApprovedMinutes = 60 };
        var pending = new OvertimeRequest { TenantId = Tenant, EmployeeId = emp.Id, WorkDate = day, Status = "PendingManager", RequestedMinutes = 600 };
        db.OvertimeRequests.AddRange(approvedA, approvedB, pending);
        db.OvertimePayrollImpacts.AddRange(
            new OvertimePayrollImpact { TenantId = Tenant, OvertimeRequestId = approvedA.Id, EmployeeId = emp.Id, Minutes = 240 },
            new OvertimePayrollImpact { TenantId = Tenant, OvertimeRequestId = approvedB.Id, EmployeeId = emp.Id, Minutes = 60 });
        db.SaveChanges();
    }

    [Fact]
    public async Task MonthToDateOvertime_IsTheApprovedHours_NotTheRawAttendanceHours()
    {
        using var db = MakeDb();
        Seed(db, ThisMonthDay());

        var full = await FullAsync(db);

        Assert.Equal(5m, full.Summary.OvertimeHours);
    }

    [Fact]
    public async Task TheMonthlyOvertimeBars_UseTheSameApprovedHours()
    {
        using var db = MakeDb();
        Seed(db, ThisMonthDay());

        var current = (await FullAsync(db)).Trends.Last();

        Assert.Equal(5m, current.OvertimeHours);
    }

    private sealed class OrganizationScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
    }
}
