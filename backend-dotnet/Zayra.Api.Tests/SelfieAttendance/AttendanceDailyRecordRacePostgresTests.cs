using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.SelfieAttendance;

/// <summary>
/// Pre-existing (also on main): two first-of-day punches of one employee racing on the daily record. Each read "no
/// daily record yet", each inserted one, and the loser hit the unique index (tenant_id, employee_id, work_date): an
/// unhandled 23505, answered 500, after its raw event had committed. Truly parallel callers on real PostgreSQL, through
/// <c>punch/mobile</c> (AttendanceService.PunchAsync) and the legacy <c>/api/mobile/attendance/punch</c>. Written only
/// against types the #212 head (e835077f) has, so it compiles there and fails there.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AttendanceDailyRecordRacePostgresTests
{
    private const int Rounds = 3;
    private readonly PostgresFixture _fx;
    public AttendanceDailyRecordRacePostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task TwoParallelFirstOfDayPunches_BothSucceed_AndLeaveExactlyOneDailyRecord()
    {
        var tenantId = await SeedTenantAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var employeeId = await AddEmployeeAsync(tenantId);
            var outcomes = await RaceAsync(["In", "Out"], async direction =>
            {
                await using var db = _fx.CreateDb();
                var controller = new AttendanceController(new AttendanceService(db, new NullNotifications(), new NullHttpClients()),
                    new DataScopeService(db), new HrmHierarchyService(db, new NullAudit()), db, new AttendanceVerificationService(db))
                { ControllerContext = Context(tenantId, employeeId) };
                var result = await controller.MobilePunch(new WebPunchRequest(0, direction, null, null, null), default);
                return result.Result is OkObjectResult ? "ok" : $"refused {SelfieWorld.MessageOf(result.Result)}";
            });

            Assert.All(outcomes, o => Assert.Equal("ok", o));
            await using var verify = _fx.CreateDb();
            Assert.Equal(2, await verify.AttendanceRawEvents.IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId && r.EmployeeId == employeeId));
            var daily = await verify.AttendanceDailyRecords.IgnoreQueryFilters().Where(d => d.TenantId == tenantId && d.EmployeeId == employeeId).ToListAsync();
            Assert.Single(daily);
            Assert.NotNull(daily[0].FirstInUtc); // the record reflects both punches, whichever wrote first
            Assert.Equal(1, await verify.AttendanceRecords.IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId && r.EmployeeId == employeeId));
        }
    }

    [Fact]
    public async Task TwoParallelLegacyMobilePunches_BothSucceed_AndLeaveExactlyOneDailyRecord()
    {
        var tenantId = await SeedTenantAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var employeeId = await AddEmployeeAsync(tenantId);
            var outcomes = await RaceAsync(["In", "In"], async direction =>
            {
                await using var db = _fx.CreateDb();
                var controller = new MobileController(db, new AttendanceVerificationService(db)) { ControllerContext = Context(tenantId, employeeId) };
                var result = await controller.Punch(new MobilePunchRequest(0, direction, null), default);
                return result is OkObjectResult ? "ok" : $"refused {SelfieWorld.MessageOf(result)}";
            });

            Assert.All(outcomes, o => Assert.Equal("ok", o));
            await using var verify = _fx.CreateDb();
            Assert.Single(await verify.AttendanceDailyRecords.IgnoreQueryFilters().Where(d => d.TenantId == tenantId && d.EmployeeId == employeeId).ToListAsync());
        }
    }

    /// <summary>Runs one call per direction, all released at once; an exception (the old 23505) becomes its outcome.</summary>
    private static async Task<string[]> RaceAsync(string[] directions, Func<string, Task<string>> punch)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = directions.Select(d => Task.Run(async () =>
        {
            await start.Task;
            try { return await punch(d); }
            catch (Exception ex) { return $"threw {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}"; }
        })).ToList();
        start.SetResult();
        return await Task.WhenAll(calls);
    }

    private async Task<Guid> SeedTenantAsync()
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        // An active policy, so the race is about the daily record alone (not the tenant's first default policy).
        db.AttendancePolicies.Add(new AttendancePolicy { TenantId = tenantId, Code = "STD", Name = "Standard", IsActive = true });
        await db.SaveChangesAsync();
        return tenantId;
    }

    private async Task<int> AddEmployeeAsync(Guid tenantId)
    {
        await using var db = _fx.CreateDb();
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"RACE-{Guid.NewGuid():N}"[..20], FullName = "Race Person",
            Status = EmployeeStatuses.Active, JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee.Id;
    }

    internal static ControllerContext Context(Guid tenantId, int employeeId)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant_id", tenantId.ToString()),
                new Claim("sub", Guid.NewGuid().ToString()),
                new Claim("employee_id", employeeId.ToString()),
                new Claim("access_mode", AccessModes.Mobile),
            ], "Test")),
        };
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        return new ControllerContext { HttpContext = context };
    }
}
