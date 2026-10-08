using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
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
/// The rest of the daily-record race class, on real PostgreSQL with truly parallel callers:
/// (a) the tenant's first DEFAULT attendance policy, created by two employees' first punches (or two processing runs)
///     at once, used to 23505 on the unique (tenant_id, code) index;
/// (b) a punch racing the day-processing run (ProcessAsync, and the job's ProcessEmployeeRangeAsync) or device ingest on
///     the same employee-day used to 23505 on the daily record's unique (tenant_id, employee_id, work_date).
/// Written only against types that exist before the fix (08bf4175), so it compiles there and fails there.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AttendanceProcessingRacePostgresTests
{
    private const int Rounds = 4;
    private readonly PostgresFixture _fx;
    public AttendanceProcessingRacePostgresTests(PostgresFixture fx) => _fx = fx;

    // ── (a) the tenant's first default policy ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_FirstPunchesOfFourEmployees_InATenantWithNoPolicy_AllSucceed_AndCreateOneDefaultPolicy()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var tenantId = await SeedTenantAsync(withPolicy: false);
            var employees = new List<int>();
            for (var i = 0; i < 4; i++) employees.Add(await AddEmployeeAsync(tenantId));

            var outcomes = await RaceAsync(employees.Select(e => (Func<Task<string>>)(() => PunchAsync(tenantId, e, "In"))).ToArray());

            Assert.All(outcomes, o => Assert.Equal("ok", o));
            await using var verify = _fx.CreateDb();
            Assert.Equal(1, await verify.AttendancePolicies.IgnoreQueryFilters().CountAsync(p => p.TenantId == tenantId));
            Assert.Equal(4, await verify.AttendanceDailyRecords.IgnoreQueryFilters().CountAsync(d => d.TenantId == tenantId));
        }
    }

    [Fact]
    public async Task A_TwoProcessingRuns_InATenantWithNoPolicy_BothSucceed_AndCreateOneDefaultPolicy()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var tenantId = await SeedTenantAsync(withPolicy: false);
            var first = await AddEmployeeAsync(tenantId);
            var second = await AddEmployeeAsync(tenantId);
            var day = Today();

            var outcomes = await RaceAsync(
                () => ProcessAsync(tenantId, first, day),
                () => ProcessAsync(tenantId, second, day));

            Assert.All(outcomes, o => Assert.Equal("ok", o));
            await using var verify = _fx.CreateDb();
            Assert.Equal(1, await verify.AttendancePolicies.IgnoreQueryFilters().CountAsync(p => p.TenantId == tenantId));
        }
    }

    // ── (b) a punch racing processing or ingest on the same employee-day ─────────────────────────────────

    [Fact]
    public async Task B_APunchRacingTheProcessingRun_OnTheSameEmployeeDay_BothSucceed_AndLeaveOneDailyRecord()
    {
        var tenantId = await SeedTenantAsync(withPolicy: true);
        for (var round = 0; round < Rounds; round++)
        {
            var employeeId = await AddEmployeeAsync(tenantId);
            var outcomes = await RaceAsync(
                () => PunchAsync(tenantId, employeeId, "In"),
                () => ProcessAsync(tenantId, employeeId, Today()));

            Assert.All(outcomes, o => Assert.Equal("ok", o));
            await AssertOneDailyRecordWithTheInPunchAsync(tenantId, employeeId);
        }
    }

    [Fact]
    public async Task B_APunchRacingTheProcessingJobsEmployeeRange_BothSucceed_AndLeaveOneDailyRecord()
    {
        var tenantId = await SeedTenantAsync(withPolicy: true);
        for (var round = 0; round < Rounds; round++)
        {
            var employeeId = await AddEmployeeAsync(tenantId);
            var outcomes = await RaceAsync(
                () => PunchAsync(tenantId, employeeId, "In"),
                async () =>
                {
                    await using var db = _fx.CreateDb();
                    var service = Service(db);
                    var employee = await db.Employees.IgnoreQueryFilters().SingleAsync(e => e.Id == employeeId);
                    var policies = await service.EnsureActivePoliciesAsync(tenantId, default);
                    // As AttendanceProcessingJobHandler's RunItemAsync does: one transaction per employee item, saved at its end.
                    await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                    {
                        await using var tx = await db.Database.BeginTransactionAsync();
                        await service.ProcessEmployeeRangeAsync(tenantId, employee, policies.ToList(), Today(), Today(), Context(tenantId), default);
                        await db.SaveChangesAsync();
                        await tx.CommitAsync();
                    });
                    return "ok";
                });

            Assert.All(outcomes, o => Assert.Equal("ok", o));
            await AssertOneDailyRecordWithTheInPunchAsync(tenantId, employeeId);
        }
    }

    [Fact]
    public async Task B_APunchRacingDeviceIngest_OnTheSameEmployeeDay_BothSucceed_AndLeaveOneDailyRecord()
    {
        var tenantId = await SeedTenantAsync(withPolicy: true);
        const string key = "knx_test_race_device_key";
        await using (var db = _fx.CreateDb())
        {
            db.AttendanceDevices.Add(new AttendanceDevice
            {
                TenantId = tenantId, DeviceName = "Gate", SerialNumber = $"SN-{Guid.NewGuid():N}", IsActive = true,
                ApiKeyReference = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))),
            });
            await db.SaveChangesAsync();
        }
        for (var round = 0; round < Rounds; round++)
        {
            var employeeId = await AddEmployeeAsync(tenantId);
            string code;
            await using (var db = _fx.CreateDb())
                code = await db.Employees.IgnoreQueryFilters().Where(e => e.Id == employeeId).Select(e => e.EmployeeCode).SingleAsync();

            var outcomes = await RaceAsync(
                () => PunchAsync(tenantId, employeeId, "In"),
                async () =>
                {
                    await using var db = _fx.CreateDb();
                    var result = await Service(db).IngestByDeviceKeyAsync(key, new DeviceIngestRequest(
                        [new DeviceIngestPunch(code, DateTime.UtcNow.AddSeconds(-30), "Out", null, null, null, null, null, null)], AutoProcess: true), "127.0.0.1", default);
                    return result is { Accepted: 1 } ? "ok" : "not accepted";
                });

            Assert.All(outcomes, o => Assert.Equal("ok", o));
            await using var verify = _fx.CreateDb();
            Assert.Single(await verify.AttendanceDailyRecords.IgnoreQueryFilters().Where(d => d.TenantId == tenantId && d.EmployeeId == employeeId).ToListAsync());
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The punch's work date: today in the tenant's (default) time zone.</summary>
    private static DateOnly Today() => TenantTimeZone.LocalDate(TenantTimeZone.FromId(null), DateTime.UtcNow);

    private async Task AssertOneDailyRecordWithTheInPunchAsync(Guid tenantId, int employeeId)
    {
        await using var verify = _fx.CreateDb();
        var daily = await verify.AttendanceDailyRecords.IgnoreQueryFilters().Where(d => d.TenantId == tenantId && d.EmployeeId == employeeId).ToListAsync();
        Assert.Single(daily);
        // No lost update: whichever ran second, the punch is reflected in the one record.
        Assert.NotNull(daily[0].FirstInUtc);
    }

    private static async Task<string[]> RaceAsync(params Func<Task<string>>[] calls)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = calls.Select(call => Task.Run(async () =>
        {
            await start.Task;
            try { return await call(); }
            catch (Exception ex) { return $"threw {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}"; }
        })).ToList();
        start.SetResult();
        return await Task.WhenAll(running);
    }

    private async Task<string> PunchAsync(Guid tenantId, int employeeId, string direction)
    {
        await using var db = _fx.CreateDb();
        var controller = new AttendanceController(Service(db), new DataScopeService(db), new HrmHierarchyService(db, new NullAudit()), db,
            new AttendanceVerificationService(db))
        { ControllerContext = ControllerFor(tenantId, employeeId) };
        var result = await controller.MobilePunch(new WebPunchRequest(0, direction, null, null, null), default);
        return result.Result is OkObjectResult ? "ok" : $"refused {SelfieWorld.MessageOf(result.Result)}";
    }

    private async Task<string> ProcessAsync(Guid tenantId, int employeeId, DateOnly day)
    {
        await using var db = _fx.CreateDb();
        await Service(db).ProcessAsync(tenantId, new ProcessAttendanceRequest(day, day, employeeId), Context(tenantId), default);
        return "ok";
    }

    private static AttendanceService Service(ZayraDbContext db) => new(db, new NullNotifications(), new NullHttpClients());

    private static RequestContext Context(Guid tenantId) => new("127.0.0.1", "test", null, tenantId);

    private async Task<Guid> SeedTenantAsync(bool withPolicy)
    {
        await using var db = _fx.CreateDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        if (withPolicy)
        {
            db.AttendancePolicies.Add(new AttendancePolicy { TenantId = tenantId, Code = "STD", Name = "Standard", IsActive = true });
            await db.SaveChangesAsync();
        }
        return tenantId;
    }

    private async Task<int> AddEmployeeAsync(Guid tenantId)
    {
        await using var db = _fx.CreateDb();
        var employee = new Employee
        {
            TenantId = tenantId, EmployeeCode = $"PRACE-{Guid.NewGuid():N}"[..20], FullName = "Race Person",
            Status = EmployeeStatuses.Active, JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee.Id;
    }

    private static ControllerContext ControllerFor(Guid tenantId, int employeeId)
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
