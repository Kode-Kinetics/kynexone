using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Reports;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Every lateness total reconciles with what payroll deducts: a half-pair day's late minutes are held, not deducted, so
/// the payroll summary, the monthly and late-arrivals reports and the attendance trend leave them out — through one
/// shared rule (<see cref="AttendanceDerivedArtifacts.LatenessCountsExpression"/>). A live "late today" figure still
/// counts a late clock-in that has no clock-out yet. On real Postgres, so the grouped queries are proved to translate.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AttendanceLatenessTotalsPostgresTests
{
    private readonly PostgresFixture _fx;
    public AttendanceLatenessTotalsPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task HeldLateMinutes_AreLeftOutOfThePayrollSummary_AndTheAttendanceReports()
    {
        var (tid, employeeId) = await SeedAsync(
            (new DateOnly(2026, 8, 17), 20, complete: true),   // a complete late day: deducted
            (new DateOnly(2026, 8, 18), 30, complete: false)); // an In-only late day: held

        await using (var db = _fx.CreateDb())
        {
            var summary = await new AttendanceService(db, new Notifications(), new HttpClients())
                .PayrollSummaryAsync(tid, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), default);
            summary.Should().ContainSingle().Which.LateMinutes.Should().Be(20, "the payroll summary agrees with the deductions");
        }

        var monthly = await RunReportAsync(tid, "attendance.monthly");
        monthly.GetArrayLength().Should().Be(1);
        monthly[0].GetProperty("TotalLateMinutes").GetInt32().Should().Be(20);
        monthly[0].GetProperty("LateDays").GetInt32().Should().Be(1);
        monthly[0].GetProperty("WorkDays").GetInt32().Should().Be(2, "only lateness is filtered, not the days");

        var lateArrivals = await RunReportAsync(tid, "attendance.late-arrivals");
        lateArrivals.EnumerateArray().Select(r => r.GetProperty("LateMinutes").GetInt32()).Should().Equal([20]);
    }

    [Fact]
    public async Task TheAttendanceTrend_LeavesHeldLatenessOutOfPastDays_ButTodayCountsTheLateClockIn()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var (tid, _) = await SeedAsync(
            (today.AddDays(-2), 20, complete: true),
            (today.AddDays(-1), 30, complete: false),
            (today, 15, complete: false));

        await using var db = _fx.CreateDb();
        var trend = Ok(await Analytics(db, tid).AttendanceTrend(3, default));
        trend.EnumerateArray().Select(d => d.GetProperty("late").GetInt32()).Should().Equal([1, 0, 1],
            "yesterday's held lateness is not counted; today's late clock-in, still without a clock-out, is");
        var kpis = Ok(await Analytics(db, tid).GetKPIs(default));
        kpis.GetProperty("attendance").GetProperty("lateToday").GetInt32().Should().Be(1, "the live tile and today's trend point agree");
    }

    private async Task<(Guid TenantId, int EmployeeId)> SeedAsync(params (DateOnly Day, int Late, bool complete)[] days)
    {
        await using var db = _fx.CreateDb();
        var tid = await PostgresFixture.SeedMinimalTenant(db);
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tid, DefaultTimezone = "UTC", CountryCode = "AE" });
        var employee = new Employee
        {
            TenantId = tid, EmployeeCode = $"LT-{Guid.NewGuid():N}"[..16], FullName = "Late Person", Status = "Active",
            JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        foreach (var (day, late, complete) in days)
            db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
            {
                TenantId = tid, EmployeeId = employee.Id, EmployeeName = employee.FullName, WorkDate = day,
                FirstInUtc = day.ToDateTime(new TimeOnly(9, 10 + late), DateTimeKind.Utc),
                LastOutUtc = complete ? day.ToDateTime(new TimeOnly(18, 0), DateTimeKind.Utc) : null,
                MissingPunch = !complete, LateMinutes = late,
                Status = complete ? AttendanceStatuses.Late : AttendanceStatuses.Present,
            });
        await db.SaveChangesAsync();
        return (tid, employee.Id);
    }

    private async Task<JsonElement> RunReportAsync(Guid tid, string key)
    {
        await using var db = _fx.CreateDb();
        var claims = new List<Claim> { new("tenant_id", tid.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new(ClaimTypes.Role, "Admin") };
        claims.AddRange(new[] { "reports.read", "attendance.read", "employees.read" }.Select(p => new Claim("permission", p)));
        var ctrl = new ReportsController(db, new DataScopeService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
        };
        var result = await ctrl.RunReport(new RunReportRequest(key, new ReportFilters
        {
            DateFrom = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), DateTo = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc),
        }), default);
        var body = JsonSerializer.SerializeToElement(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        return body.ValueKind == JsonValueKind.Array ? body : body.GetProperty("data");
    }

    private static AnalyticsController Analytics(ZayraDbContext db, Guid tid)
    {
        var claims = new List<Claim> { new("tenant_id", tid.ToString()) };
        claims.AddRange(new[] { "reports.read", "employees.read", "attendance.read", "leave.read", "overtime.read", "payroll.read" }
            .Select(p => new Claim("permission", p)));
        return new AnalyticsController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
        };
    }

    private static JsonElement Ok(IActionResult result) =>
        JsonSerializer.SerializeToElement(result.Should().BeOfType<OkObjectResult>().Subject.Value);

    private sealed class Notifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class HttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
