using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Timesheets;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Timesheets;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The attendance-variance report honours the caller's employee scope like the timesheet register does: a line manager sees their team's
/// rows, never another team's; an organisation-wide caller still sees every row.
/// </summary>
public class TimesheetVarianceScopeTests
{
    private static readonly DateOnly Monday = new(2026, 11, 2);

    private sealed class FixedScope(DataScope scope) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(scope);
    }

    private static async Task<(ZayraDbContext Db, Guid Tenant)> SeedAsync()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenant = Guid.NewGuid();
        foreach (var employeeId in new[] { 1, 2, 3 })
            db.TimesheetDayReconciliations.Add(new TimesheetDayReconciliation
            {
                TenantId = tenant, TimesheetId = Guid.NewGuid(), EmployeeId = employeeId, EmployeeName = $"Employee {employeeId}", WorkDate = Monday,
                LoggedMinutes = 480, AttendanceMinutes = 480, VarianceMinutes = 0, AttendanceStatus = "Present",
            });
        await db.SaveChangesAsync();
        return (db, tenant);
    }

    private static TimesheetsController Controller(ZayraDbContext db, Guid tenant, DataScope scope) =>
        new(new TimesheetService(db, null!), null!, new FixedScope(scope))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim("permission", "attendance.read"),
                    }, "test")),
                },
            },
        };

    private static List<int> EmployeesIn(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = JsonSerializer.SerializeToElement(ok.Value);
        return body.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("EmployeeId").GetInt32()).OrderBy(x => x).ToList();
    }

    [Fact]
    public async Task ALineManager_SeesOnlyTheirTeamsRows()
    {
        var (db, tenant) = await SeedAsync();
        await using var _ = db;
        var team = new DataScope { Level = DataScopeLevel.Team, CallerEmployeeId = 1, AllowedEmployeeIds = new[] { 1, 2 } };

        var result = await Controller(db, tenant, team).AttendanceVariance(Monday, Monday.AddDays(6), null, false, default);

        EmployeesIn(result).Should().Equal(1, 2);
    }

    [Fact]
    public async Task ALineManager_AskingForSomeoneOutsideTheirTeam_GetsNoRowsOfThatPerson()
    {
        var (db, tenant) = await SeedAsync();
        await using var _ = db;
        var team = new DataScope { Level = DataScopeLevel.Team, CallerEmployeeId = 1, AllowedEmployeeIds = new[] { 1, 2 } };

        var result = await Controller(db, tenant, team).AttendanceVariance(Monday, Monday.AddDays(6), 3, false, default);

        EmployeesIn(result).Should().NotContain(3);
    }

    [Fact]
    public async Task AnOrganisationWideCaller_StillSeesEveryRow_AndCanFilterToOneEmployee()
    {
        var (db, tenant) = await SeedAsync();
        await using var _ = db;
        var org = new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null };

        EmployeesIn(await Controller(db, tenant, org).AttendanceVariance(Monday, Monday.AddDays(6), null, false, default)).Should().Equal(1, 2, 3);
        EmployeesIn(await Controller(db, tenant, org).AttendanceVariance(Monday, Monday.AddDays(6), 3, false, default)).Should().Equal(3);
    }

    [Fact]
    public void TheServiceRequiresTheEmployeeScopeArgument_SoACallerThatForgetsItCannotReadEveryone()
    {
        var parameter = typeof(Zayra.Api.Application.Timesheets.ITimesheetService).GetMethod("GetAttendanceVarianceAsync")!
            .GetParameters().Single(p => p.Name == "allowedEmployeeIds");

        parameter.IsOptional.Should().BeFalse("an omitted scope must be a compile error, not an unrestricted read");
    }
}
