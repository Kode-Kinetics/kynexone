using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Reports;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Reports;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The "Approved OT" report (overtime.approved) states the hours that were APPROVED, which is what payroll pays. It summed the
/// requested minutes, so every request HR trimmed at approval was overstated in the report Finance reconciles against payroll.
/// It also grouped by display name, so two employees who share a name were reported as one.
/// </summary>
public class OvertimeApprovedReportTests
{
    private static readonly DateOnly Day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-5);

    private static OvertimeRequest Request(Guid tenantId, int employeeId, string name, string status, int requested, int approved) => new()
    {
        TenantId = tenantId, EmployeeId = employeeId, EmployeeName = name, WorkDate = Day, Status = status,
        StartTimeUtc = Day.ToDateTime(new TimeOnly(17, 0), DateTimeKind.Utc), EndTimeUtc = Day.ToDateTime(new TimeOnly(17, 0), DateTimeKind.Utc).AddMinutes(requested),
        RequestedMinutes = requested, ApprovedMinutes = approved, Reason = "r",
    };

    [Fact]
    public async Task ApprovedOvertime_SumsApprovedMinutes_NotRequested_PerEmployee()
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase($"ot-report-{Guid.NewGuid():N}").Options);
        var tenantId = Guid.NewGuid();
        db.OvertimeRequests.AddRange(
            Request(tenantId, 1, "Sara Ali", "Approved", requested: 240, approved: 120),   // HR trimmed 4 h to 2 h
            Request(tenantId, 1, "Sara Ali", "Approved", requested: 90, approved: 90),
            Request(tenantId, 2, "Omar Saleh", "Approved", requested: 60, approved: 30),
            Request(tenantId, 3, "Omar Saleh", "Approved", requested: 60, approved: 60),     // a different Omar Saleh
            Request(tenantId, 1, "Sara Ali", "PendingHR", requested: 600, approved: 0),     // not approved: not in the report
            Request(tenantId, 2, "Omar Saleh", "Rejected", requested: 300, approved: 0));
        await db.SaveChangesAsync();

        var controller = new ReportsController(db, new NoScope());
        var data = await controller.ExecuteReportDataAsync(tenantId,
            new RunReportRequest("overtime.approved", new ReportFilters { DateFrom = DateTime.UtcNow.AddDays(-30), DateTo = DateTime.UtcNow }),
            new ReportDataScope(null, null, true), CancellationToken.None);

        var rows = JsonSerializer.SerializeToElement(data).EnumerateArray()
            .Select(r => (Hours: r.GetProperty("TotalHours").GetDouble(), Count: r.GetProperty("Count").GetInt32(), Employee: r.GetProperty("Employee").GetString()))
            .ToList();
        rows.Should().HaveCount(3, "employee 2 and employee 3 share a name but are two people");
        rows.Should().ContainEquivalentOf((3.5, 2, "Sara Ali"), "120 + 90 approved minutes, not 240 + 90 requested");
        rows.Where(r => r.Employee == "Omar Saleh").Select(r => r.Hours).Should().BeEquivalentTo(new[] { 0.5, 1.0 });
        rows.Sum(r => r.Hours).Should().Be(5.0);
    }

    private sealed class NoScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(System.Security.Claims.ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
