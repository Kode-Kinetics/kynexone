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
/// <para>Each (b) round is pinned to ONE work date: the punch stamps its own UtcNow inside the call, so a round that
/// straddled the tenant's midnight (or a device stamp 30 s in the past, taken in the first 30 s after it) used to put
/// the two sides on different dates and fail <c>Assert.Single</c> for a reason that had nothing to do with the race.
/// A round now starts only with <see cref="MidnightMargin"/> to spare, and the device stamp never reaches back into
/// the previous day.</para>
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
            await StayClearOfMidnightAsync();
            var day = Today();
            var outcomes = await RaceAsync(
                () => PunchAsync(tenantId, employeeId, "In"),
                () => ProcessAsync(tenantId, employeeId, day));

            Assert.All(outcomes, o => Assert.Equal("ok", o));
            await AssertOneDailyRecordWithTheInPunchAsync(tenantId, employeeId, day);
        }
    }

    [Fact]
    public async Task B_APunchRacingTheProcessingJobsEmployeeRange_BothSucceed_AndLeaveOneDailyRecord()
    {
        var tenantId = await SeedTenantAsync(withPolicy: true);
        for (var round = 0; round < Rounds; round++)
        {
            var employeeId = await AddEmployeeAsync(tenantId);
            await StayClearOfMidnightAsync();
            var day = Today();
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
                        await service.ProcessEmployeeRangeAsync(tenantId, employee, policies.ToList(), day, day, Context(tenantId), default);
                        await db.SaveChangesAsync();
                        await tx.CommitAsync();
                    });
                    return "ok";
                });

            Assert.All(outcomes, o => Assert.Equal("ok", o));
            await AssertOneDailyRecordWithTheInPunchAsync(tenantId, employeeId, day);
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

            await StayClearOfMidnightAsync();
            var day = Today();
            var deviceAt = DeviceStampOnTheSameDay(DateTime.UtcNow);
            var outcomes = await RaceAsync(
                () => PunchAsync(tenantId, employeeId, "In"),
                async () =>
                {
                    await using var db = _fx.CreateDb();
                    var result = await Service(db).IngestByDeviceKeyAsync(key, new DeviceIngestRequest(
                        [new DeviceIngestPunch(code, deviceAt, "Out", null, null, null, null, null, null)], AutoProcess: true), "127.0.0.1", default);
                    return result is { Accepted: 1 } ? "ok" : "not accepted";
                });

            Assert.All(outcomes, o => Assert.Equal("ok", o));
            await using var verify = _fx.CreateDb();
            var raws = await verify.AttendanceRawEvents.IgnoreQueryFilters().Where(r => r.TenantId == tenantId && r.EmployeeId == employeeId).ToListAsync();
            var punch = Assert.Single(raws, r => r.DeviceId == null);
            var device = Assert.Single(raws, r => r.DeviceId != null);
            Assert.Equal(day, WorkDateOf(punch.PunchTimestampUtc));
            Assert.Equal(day, WorkDateOf(device.PunchTimestampUtc));
            // The invariant: one daily record for the (employee, work date), and it reflects BOTH events — whichever of
            // the punch and the ingest recomputed the day last, it saw the other's raw event under the same lock.
            var daily = Assert.Single(await verify.AttendanceDailyRecords.IgnoreQueryFilters().Where(d => d.TenantId == tenantId && d.EmployeeId == employeeId).ToListAsync());
            Assert.Equal(day, daily.WorkDate);
            Assert.Equal(punch.PunchTimestampUtc, daily.FirstInUtc);
            Assert.Equal(device.PunchTimestampUtc, daily.LastOutUtc);
            // The device's Out is stamped 30 s BEFORE the punch's In: it closes nothing, so the day is a missing punch
            // (attended, raised for review) — not a zero-length complete day recorded as Absent with an absence deduction.
            Assert.True(daily.MissingPunch);
            Assert.Equal("Present", daily.Status);
        }
    }

    /// <summary>
    /// Two SEPARATE device batches for the same employee-day, on real PostgreSQL (InMemory does not enforce the unique
    /// (tenant_id, employee_id, work_date) index on attendance_records). The webhook is anonymous, so its company scope is
    /// empty: the second batch's recompute used to miss the legacy row the first created, insert another, and fail with
    /// 23505 after its raw events were saved.
    /// </summary>
    [Fact]
    public async Task C_TwoDeviceBatchesForTheSameEmployeeDay_FromTheAnonymousWebhook_BothSucceed_AndLeaveOneLegacyRow()
    {
        var tenantId = await SeedTenantAsync(withPolicy: true);
        const string key = "knx_test_two_batches_device_key";
        Guid companyId;
        string code;
        int employeeId;
        await using (var db = _fx.CreateDb())
        {
            var company = new Company { TenantId = tenantId, LegalNameEn = "Gate Co", TradeName = "Gate", CountryCode = "SA", IsActive = true };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            companyId = company.Id;
            var employee = new Employee
            {
                TenantId = tenantId, CompanyId = companyId, EmployeeCode = $"PTWO-{Guid.NewGuid():N}"[..20], FullName = "Two Batches",
                Status = EmployeeStatuses.Active, JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            db.Employees.Add(employee);
            db.AttendanceDevices.Add(new AttendanceDevice
            {
                TenantId = tenantId, DeviceName = "Gate", SerialNumber = $"SN-{Guid.NewGuid():N}", IsActive = true,
                ApiKeyReference = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))),
            });
            await db.SaveChangesAsync();
            code = employee.EmployeeCode;
            employeeId = employee.Id;
        }

        var day = new DateOnly(2026, 8, 18);
        var clockIn = new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc);
        var clockOut = new DateTime(2026, 8, 18, 17, 0, 0, DateTimeKind.Utc);
        foreach (var (at, direction) in new[] { (clockIn, "In"), (clockOut, "Out") })
        {
            await using var db = _fx.CreateDbWithAccessor(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
            var result = await Service(db).IngestByDeviceKeyAsync(key, new DeviceIngestRequest(
                [new DeviceIngestPunch(code, at, direction, null, null, null, null, null, null)], AutoProcess: true), "127.0.0.1", default);
            Assert.NotNull(result);
            Assert.Equal(1, result!.Accepted);
            Assert.Equal(1, result.Processed);
        }

        await using var verify = _fx.CreateDb();
        var legacy = Assert.Single(await verify.AttendanceRecords.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.EmployeeId == employeeId).ToListAsync());
        Assert.Equal(day, legacy.WorkDate);
        Assert.Equal(companyId, legacy.CompanyId);
        var daily = Assert.Single(await verify.AttendanceDailyRecords.IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId && d.EmployeeId == employeeId).ToListAsync());
        Assert.Equal(clockIn, daily.FirstInUtc);
        Assert.Equal(clockOut, daily.LastOutUtc);
        Assert.False(daily.MissingPunch);
    }

    [Fact]
    public void DeviceStampOnTheSameDay_JustAfterMidnight_StaysOnThatDay()
    {
        // The stamp the device test used to send, UtcNow - 30 s, fell on the previous work date for the first 30 s of
        // every day: the punch and the ingest then wrote two different days' records and the test failed.
        var justAfterMidnight = TenantTimeZone.LocalDayStartUtc(TenantTimeZone.FromId(null), Today()).AddSeconds(10);
        Assert.NotEqual(WorkDateOf(justAfterMidnight), WorkDateOf(justAfterMidnight.AddSeconds(-30)));
        Assert.Equal(WorkDateOf(justAfterMidnight), WorkDateOf(DeviceStampOnTheSameDay(justAfterMidnight)));
        Assert.True(DeviceStampOnTheSameDay(justAfterMidnight) <= justAfterMidnight);
        var midday = justAfterMidnight.AddHours(12);
        Assert.Equal(midday.AddSeconds(-30), DeviceStampOnTheSameDay(midday));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The punch's work date: today in the tenant's (default) time zone.</summary>
    private static DateOnly Today() => WorkDateOf(DateTime.UtcNow);

    /// <summary>The work date of an instant in the tenant's (default) time zone; these tenants have no shifts.</summary>
    private static DateOnly WorkDateOf(DateTime utc) => TenantTimeZone.LocalDate(TenantTimeZone.FromId(null), utc);

    /// <summary>How long a (b) round may take, from choosing its day to the punch stamping UtcNow, without leaving it.</summary>
    private static readonly TimeSpan MidnightMargin = TimeSpan.FromSeconds(30);

    /// <summary>Waits out the tenant's midnight when it is less than <see cref="MidnightMargin"/> away, so the
    /// round's day, the punch's own UtcNow and the device stamp all fall on one work date.</summary>
    private static async Task StayClearOfMidnightAsync()
    {
        var untilMidnight = TenantTimeZone.LocalDayStartUtc(TenantTimeZone.FromId(null), Today().AddDays(1)) - DateTime.UtcNow;
        if (untilMidnight < MidnightMargin) await Task.Delay(untilMidnight + TimeSpan.FromSeconds(1));
    }

    /// <summary>A device stamp 30 s before <paramref name="nowUtc"/>, clamped to the start of <paramref name="nowUtc"/>'s
    /// work date so it can never land on the previous day.</summary>
    private static DateTime DeviceStampOnTheSameDay(DateTime nowUtc)
    {
        var dayStart = TenantTimeZone.LocalDayStartUtc(TenantTimeZone.FromId(null), WorkDateOf(nowUtc));
        var stamp = nowUtc.AddSeconds(-30);
        return stamp < dayStart ? dayStart : stamp;
    }

    private async Task AssertOneDailyRecordWithTheInPunchAsync(Guid tenantId, int employeeId, DateOnly day)
    {
        await using var verify = _fx.CreateDb();
        var punch = await verify.AttendanceRawEvents.IgnoreQueryFilters().SingleAsync(r => r.TenantId == tenantId && r.EmployeeId == employeeId);
        Assert.Equal(day, WorkDateOf(punch.PunchTimestampUtc));
        var daily = await verify.AttendanceDailyRecords.IgnoreQueryFilters().Where(d => d.TenantId == tenantId && d.EmployeeId == employeeId).ToListAsync();
        // One daily record for the (employee, work date)...
        var record = Assert.Single(daily);
        Assert.Equal(day, record.WorkDate);
        // ...and no lost update: whichever ran second, the punch is reflected in it.
        Assert.Equal(punch.PunchTimestampUtc, record.FirstInUtc);
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
