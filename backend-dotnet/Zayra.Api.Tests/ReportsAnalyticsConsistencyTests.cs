using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Reports;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The analytics tab of the Reports page: its windows are bounded, its vocabulary matches the
/// attendance module, and each figure is authorized against the data behind it.
/// </summary>
public sealed class ReportsAnalyticsConsistencyTests
{
    /// <summary>What an HR director's token carries for this tab.</summary>
    private static readonly string[] FullReader =
    [
        "reports.read", "employees.read", "attendance.read", "leave.read", "overtime.read", "payroll.read",
    ];

    [Fact]
    public async Task Kpis_CountOnlyThisMonth_AndTreatLateAndHalfDayAsAttended()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var month = new DateOnly(today.Year, today.Month, 1);
        var next = month.AddMonths(1);
        var currentJoin = Emp(tid, "NOW", "Active", Utc(month));
        var futureJoin = Emp(tid, "FUT", "Active", Utc(next));             // joins next month: not "new this month"
        var currentExit = Emp(tid, "EXIT1", "Resigned", DateTime.UtcNow.AddYears(-1), today);
        var futureExit = Emp(tid, "EXIT2", "Resigned", DateTime.UtcNow.AddYears(-1), next);   // leaves next month
        db.Employees.AddRange(currentJoin, futureJoin, currentExit, futureExit);
        await db.SaveChangesAsync();
        db.AttendanceDailyRecords.AddRange(
            Day(tid, currentJoin.Id, today, AttendanceStatuses.Present),
            Day(tid, futureJoin.Id, today, AttendanceStatuses.Late, lateMinutes: 5),
            Day(tid, currentExit.Id, today, AttendanceStatuses.HalfDay));
        await db.SaveChangesAsync();

        var json = Ok(await Analytics(db, tid, FullReader).GetKPIs(default));

        Assert.Equal(1, json.GetProperty("headcount").GetProperty("newThisMonth").GetInt32());
        Assert.Equal(1, json.GetProperty("headcount").GetProperty("exitsThisMonth").GetInt32());
        Assert.Equal(3, json.GetProperty("attendance").GetProperty("presentToday").GetInt32());
        Assert.Equal(1, json.GetProperty("attendance").GetProperty("lateToday").GetInt32());
    }

    [Fact]
    public async Task AttendanceTrend_DaysMeansExactlyThatManyCalendarDaysEndingToday()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        for (var i = -1; i <= 30; i++)   // tomorrow, today, and 30 days back
            db.AttendanceDailyRecords.Add(Day(tid, 1000 + i, today.AddDays(-i), AttendanceStatuses.Present));
        await db.SaveChangesAsync();

        var json = Ok(await Analytics(db, tid, FullReader).AttendanceTrend(30, default));

        Assert.Equal(30, json.GetArrayLength());
        Assert.Equal(today.AddDays(-29).ToString("yyyy-MM-dd"), json[0].GetProperty("date").GetString());
        Assert.Equal(today.ToString("yyyy-MM-dd"), json[29].GetProperty("date").GetString());
    }

    [Theory]
    [InlineData(nameof(AnalyticsController.PayrollTrend))]
    [InlineData(nameof(AnalyticsController.DepartmentComparison))]
    public void PayrollAnalytics_AreGatedOnPermissions_NotOnAStaleRoleList(string methodName)
    {
        // The list named a "Finance" role that no tenant has, and left out Payroll Manager, Payroll
        // Officer, HR Director and Auditor — every role that actually holds payroll.read.
        var attrs = typeof(AnalyticsController).GetMethod(methodName)!
            .GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>();

        Assert.DoesNotContain(attrs, a => !string.IsNullOrWhiteSpace(a.Roles));
    }

    [Fact]
    public async Task PayrollTrend_WithoutPayrollRead_IsRefusedWithAReason()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        db.PayrollRuns.Add(new PayrollRun { TenantId = tid, Year = 2026, Month = 8, Status = "Locked", TotalNetSalary = 250000 });
        await db.SaveChangesAsync();
        var compliance = Analytics(db, tid, "reports.read", "employees.read", "compliance.read");

        ReportDataAuthorizationTests.AssertForbiddenWithReason(await compliance.PayrollTrend(6, default), "payroll.read");
        ReportDataAuthorizationTests.AssertForbiddenWithReason(await compliance.DepartmentComparison(default), "payroll.read");
    }

    [Fact]
    public async Task Kpis_WithholdPayrollFigures_FromACallerWithoutPayrollRead()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        db.PayrollRuns.Add(new PayrollRun { TenantId = tid, Year = 2026, Month = 8, Status = "Locked", TotalNetSalary = 250000 });
        await db.SaveChangesAsync();

        var withheld = JsonSerializer.Serialize(Ok(await Analytics(db, tid, "reports.read", "employees.read", "compliance.read").GetKPIs(default)));
        Assert.DoesNotContain("250000", withheld);
        Assert.Contains("payroll.read", withheld);   // says what was held back, rather than showing nothing

        var shown = JsonSerializer.Serialize(Ok(await Analytics(db, tid, FullReader).GetKPIs(default)));
        Assert.Contains("250000", shown);
    }

    [Fact]
    public async Task LeaveTrend_WithoutLeaveRead_IsRefusedWithAReason()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);

        ReportDataAuthorizationTests.AssertForbiddenWithReason(
            await Analytics(db, tid, "reports.read", "employees.read", "payroll.read").LeaveTrend(6, default), "leave.read");
    }

    [Fact]
    public async Task Analytics_ForATeamScopedCaller_AreRefused_NotServedOrganisationWide()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        var manager = Analytics(db, tid, "reports.read", "employees.read", "manager.read", "attendance.read");

        ReportDataAuthorizationTests.AssertForbiddenWithReason(await manager.GetKPIs(default), "organisation");
        ReportDataAuthorizationTests.AssertForbiddenWithReason(await manager.AttendanceTrend(30, default), "organisation");
    }

    [Fact]
    public async Task Kpis_ForACompanyScopedCaller_CountOnlyTheirCompanysAttendance()
    {
        await using var db = Db();
        var tid = await SeedTenantAsync(db);
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var alpha = Emp(tid, "A-1", "Active", DateTime.UtcNow.AddYears(-1));
        alpha.CompanyId = companyA;
        var beta = Emp(tid, "B-1", "Active", DateTime.UtcNow.AddYears(-1));
        beta.CompanyId = companyB;
        db.Employees.AddRange(alpha, beta);
        await db.SaveChangesAsync();
        // Attendance rows carry no company of their own; only the employee says whose they are.
        db.AttendanceDailyRecords.AddRange(
            Day(tid, alpha.Id, today, AttendanceStatuses.Present),
            Day(tid, beta.Id, today, AttendanceStatuses.Late, lateMinutes: 12));
        await db.SaveChangesAsync();

        var json = Ok(await Analytics(db, tid, companyA, FullReader).GetKPIs(default));

        Assert.Equal(1, json.GetProperty("attendance").GetProperty("presentToday").GetInt32());
        Assert.Equal(0, json.GetProperty("attendance").GetProperty("lateToday").GetInt32());
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static JsonElement Ok(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

    private static ZayraDbContext Db() => new(
        new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedTenantAsync(ZayraDbContext db)
    {
        var tid = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tid, Name = "Analytics", Slug = $"analytics-{tid:N}" });
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tid, DefaultTimezone = "UTC" });
        await db.SaveChangesAsync();
        return tid;
    }

    private static DateTime Utc(DateOnly date) => DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    private static Employee Emp(Guid tid, string code, string status, DateTime joined, DateOnly? contractEnd = null) => new()
    {
        TenantId = tid, EmployeeCode = code, FullName = code, Status = status, JoiningDate = joined, ContractEndDate = contractEnd,
    };

    private static AttendanceDailyRecord Day(Guid tid, int employeeId, DateOnly date, string status, int lateMinutes = 0) => new()
    {
        TenantId = tid, EmployeeId = employeeId, WorkDate = date, Status = status, LateMinutes = lateMinutes,
    };

    private static AnalyticsController Analytics(ZayraDbContext db, Guid tid, params string[] permissions) =>
        Analytics(db, tid, null, permissions);

    private static AnalyticsController Analytics(ZayraDbContext db, Guid tid, Guid? companyId, params string[] permissions)
    {
        var claims = new List<Claim> { new("tenant_id", tid.ToString()) };
        if (companyId is Guid company)
            claims.Add(new Claim(EntityScopeContext.V2ClaimType,
                JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { company } })));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        return new AnalyticsController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
    }
}
