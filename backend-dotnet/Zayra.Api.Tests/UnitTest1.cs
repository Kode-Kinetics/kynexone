using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

public class DashboardControllerTests
{
    [Fact]
    public async Task Summary_ReturnsLiveAttendanceMetrics()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var earlier = today.AddDays(-2);
        SeedEmployees(db, tenantId);
        db.AttendanceRecords.AddRange(
            new AttendanceRecord { TenantId = tenantId, EmployeeId = 1, WorkDate = today, Status = "Present", OvertimeHours = 2m },
            new AttendanceRecord { TenantId = tenantId, EmployeeId = 2, WorkDate = today, Status = "Absent", OvertimeHours = 0m },
            new AttendanceRecord { TenantId = tenantId, EmployeeId = 3, WorkDate = today, Status = "On Leave", OvertimeHours = 0m },
            new AttendanceRecord { TenantId = tenantId, EmployeeId = 4, WorkDate = earlier, Status = "Present", OvertimeHours = 4.5m });
        await db.SaveChangesAsync();

        var result = await CreateController(db, tenantId).Summary(CancellationToken.None);

        // Overtime is summed month-to-date; on the 1st/2nd of a month the "earlier" record
        // falls in the previous month, so mirror the controller's window instead of
        // hard-coding 6.5 (this test used to fail on those two days each month).
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var expectedOvertime = 2m + (earlier >= monthStart ? 4.5m : 0m);

        var summary = AssertOk<DashboardSummaryDto>(result);
        Assert.Equal(4, summary.TotalEmployees);
        Assert.Equal(3, summary.ActiveEmployees);
        Assert.Equal(1, summary.PresentToday);
        Assert.Equal(1, summary.Absent);
        Assert.Equal(1, summary.OnLeave);
        Assert.Equal(expectedOvertime, summary.OvertimeHours);
        Assert.Equal(2, summary.ChurnRisk);
    }

    [Fact]
    public async Task Trends_ReturnsMonthlyAttendanceAndOvertimeSeries()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var currentMonth = new DateOnly(today.Year, today.Month, 1);
        var previousMonth = currentMonth.AddMonths(-1);
        SeedEmployees(db, tenantId);
        db.AttendanceRecords.AddRange(
            new AttendanceRecord { TenantId = tenantId, EmployeeId = 1, WorkDate = previousMonth.AddDays(1), Status = "Present", OvertimeHours = 1m },
            new AttendanceRecord { TenantId = tenantId, EmployeeId = 2, WorkDate = previousMonth.AddDays(2), Status = "Absent", OvertimeHours = 0m },
            new AttendanceRecord { TenantId = tenantId, EmployeeId = 1, WorkDate = today, Status = "Present", OvertimeHours = 3m },
            new AttendanceRecord { TenantId = tenantId, EmployeeId = 2, WorkDate = today, Status = "Present", OvertimeHours = 2m });
        await db.SaveChangesAsync();

        var result = await CreateController(db, tenantId).Trends(2, CancellationToken.None);

        var trends = AssertOk<List<DashboardTrendDto>>(result);
        Assert.Equal(2, trends.Count);
        Assert.Equal(50m, trends[0].AttendanceRate);
        Assert.Equal(1m, trends[0].OvertimeHours);
        Assert.Equal(100m, trends[1].AttendanceRate);
        Assert.Equal(5m, trends[1].OvertimeHours);
    }

    private static ZayraDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ZayraDbContext(options);
    }

    private static void SeedEmployees(ZayraDbContext db, Guid tenantId)
    {
        db.Employees.AddRange(
            new Employee { Id = 1, TenantId = tenantId, EmployeeCode = "E001", FullName = "Aisha Khan", Status = "Active" },
            new Employee { Id = 2, TenantId = tenantId, EmployeeCode = "E002", FullName = "Omar Ali", Status = "Active" },
            new Employee { Id = 3, TenantId = tenantId, EmployeeCode = "E003", FullName = "Maya Shah", Status = "Inactive" },
            new Employee { Id = 4, TenantId = tenantId, EmployeeCode = "E004", FullName = "Noor Ahmed", Status = "Active" });
    }

    private static DashboardController CreateController(ZayraDbContext db, Guid tenantId)
    {
        var controller = new DashboardController(db, new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new UnrestrictedDataScopeService());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString())
                }, "Test"))
            }
        };
        return controller;
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<T>(ok.Value);
    }
}

// Test stub: always returns an unrestricted (org-wide) scope.
file sealed class UnrestrictedDataScopeService : IDataScopeService
{
    public Task<DataScope> ResolveAsync(System.Security.Claims.ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}
