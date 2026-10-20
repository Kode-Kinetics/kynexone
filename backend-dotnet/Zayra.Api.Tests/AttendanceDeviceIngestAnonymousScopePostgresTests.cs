using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The device webhook (<c>POST /api/attendance/ingest</c>, <c>[AllowAnonymous]</c>, authenticated by the device key)
/// through the REAL Program.cs pipeline over a REAL PostgreSQL built by every EF migration.
///
/// <para>An anonymous request has an EMPTY company scope in every environment (RequestEntityScopeResolver's
/// unauthenticated branch), and ZayraDbContext applies the company filter to company-scoped entities. So two reads on
/// the device recompute saw nothing:</para>
/// <list type="bullet">
///   <item>the legacy <c>attendance_records</c> lookup missed the day's existing row, inserted a second one, and the
///     unique (tenant_id, employee_id, work_date) index failed the second batch of a day with 23505 → HTTP 500, after
///     its raw events were saved — so a device retry skipped them as duplicates and the day stayed stale;</item>
///   <item>the approved-leave lookup saw no leave, so an approved leave day the batch recomputed became "Absent" with
///     an absence deduction.</item>
/// </list>
/// The host runs in Development on purpose: the defect is not a Production-only strict-mode effect.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AttendanceDeviceIngestAnonymousScopePostgresTests : IClassFixture<DeviceIngestHostFixture>
{
    private readonly DeviceIngestHostFixture _fx;
    public AttendanceDeviceIngestAnonymousScopePostgresTests(DeviceIngestHostFixture fx) => _fx = fx;

    /// <summary>A Tuesday outside Ramadan: an ordinary working day for a Saudi employer.</summary>
    private static readonly DateOnly WorkDay = new(2026, 9, 15);

    [Fact]
    public async Task TwoDeviceBatches_ForTheSameEmployeeDay_BothSucceed_AndLeaveOneLegacyRowAndOneDailyRecordWithInAndOut()
    {
        var (tenantId, employeeId, code, key) = await _fx.SeedTenantAsync("TWO");
        var inAt = LocalToUtc(WorkDay, 9, 0);
        var outAt = LocalToUtc(WorkDay, 17, 0);

        var first = await _fx.IngestAsync(key, new DeviceIngestPunch(code, inAt, "In", null, null, null, null, null, null));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // The second batch recomputes a day whose legacy attendance_records row the first batch created.
        var second = await _fx.IngestAsync(key, new DeviceIngestPunch(code, outAt, "Out", null, null, null, null, null, null));
        Assert.True(second.StatusCode == HttpStatusCode.OK,
            $"the second device batch of the day must succeed, got {(int)second.StatusCode}: {await second.Content.ReadAsStringAsync()}");

        await using var db = _fx.CreateDb();
        var legacy = await db.AttendanceRecords.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.EmployeeId == employeeId).ToListAsync();
        Assert.Single(legacy);
        Assert.Equal(WorkDay, legacy[0].WorkDate);
        Assert.NotNull(legacy[0].TimeIn);
        Assert.NotNull(legacy[0].TimeOut);

        var daily = await db.AttendanceDailyRecords.IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId && d.EmployeeId == employeeId).ToListAsync();
        Assert.Single(daily);
        Assert.Equal(inAt, daily[0].FirstInUtc);
        Assert.Equal(outAt, daily[0].LastOutUtc);
    }

    /// <summary>
    /// One device batch: employee A punched Monday, employee B punched Tuesday, and A is on approved leave Tuesday.
    /// Ingest used to recompute every matched employee for every date in the batch, so A's Tuesday was recomputed with
    /// no punches — the one case where the day's status consults the approved leave — and, on the anonymous webhook,
    /// read no leave and deducted an absence. Ingest now processes each employee only on the work dates of their own
    /// punches (#239), so A's leave day is not touched at all, and it never carries an absence deduction.
    /// </summary>
    [Fact]
    public async Task DeviceIngest_RecomputingAnApprovedLeaveDayWithNoPunches_KeepsItOnLeave_WithNoAbsenceDeduction()
    {
        var monday = WorkDay.AddDays(-1);
        var (tenantId, onLeaveId, onLeaveCode, key) = await _fx.SeedTenantAsync("LEAVE", approvedLeaveOn: WorkDay);
        var (_, colleagueCode) = await _fx.AddEmployeeAsync(tenantId, "LEAVE-B");

        var response = await _fx.IngestAsync(key,
            new DeviceIngestPunch(onLeaveCode, LocalToUtc(monday, 9, 0), "In", null, null, null, null, null, null),
            new DeviceIngestPunch(onLeaveCode, LocalToUtc(monday, 17, 0), "Out", null, null, null, null, null, null),
            new DeviceIngestPunch(colleagueCode, LocalToUtc(WorkDay, 9, 0), "In", null, null, null, null, null, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = _fx.CreateDb();
        var leaveDay = await db.AttendanceDailyRecords.IgnoreQueryFilters()
            .SingleOrDefaultAsync(d => d.TenantId == tenantId && d.EmployeeId == onLeaveId && d.WorkDate == WorkDay);
        Assert.True(leaveDay is null, $"a colleague's punch must not recompute A's leave day (found '{leaveDay?.Status}')");
        Assert.False(await db.AttendancePayrollImpacts.IgnoreQueryFilters().AnyAsync(i =>
                i.TenantId == tenantId && i.EmployeeId == onLeaveId && i.WorkDate == WorkDay && i.ImpactType == "Absence deduction"),
            "an approved leave day is never deducted as an absence");
    }

    /// <summary>Local wall-clock time on <paramref name="date"/> in the tenant's (default) time zone, as UTC.</summary>
    private static DateTime LocalToUtc(DateOnly date, int hour, int minute) =>
        TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(hour, minute)), DateTimeKind.Unspecified),
            TenantTimeZone.FromId(null));
}

/// <summary>Its own PostgreSQL 16, migrated by every EF migration, behind the real Program.cs pipeline.</summary>
public sealed class DeviceIngestHostFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private DeviceIngestHost _host = null!;
    private HttpClient _client = null!;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _connectionString = _pg.GetConnectionString();
        await using (var migrate = CreateDb())
            await migrate.Database.MigrateAsync();
        _host = new DeviceIngestHost(_connectionString);
        _client = _host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _host?.Dispose();
        return _pg.DisposeAsync().AsTask();
    }

    public ZayraDbContext CreateDb() => new(new DbContextOptionsBuilder<ZayraDbContext>()
        .UseNpgsql(_connectionString, PostgresFixture.ProductionProviderOptions)
        .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
        .AddInterceptors(Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance)
        .Options);

    /// <summary>POSTs one batch to the anonymous device webhook — no JWT, only the device key.</summary>
    public Task<HttpResponseMessage> IngestAsync(string deviceKey, params DeviceIngestPunch[] punches)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/attendance/ingest")
        {
            Content = JsonContent.Create(new DeviceIngestRequest(punches, AutoProcess: true)),
        };
        request.Headers.Add("X-Device-Key", deviceKey);
        return _client.SendAsync(request);
    }

    /// <summary>A tenant with one Saudi company, one employee of that company, and an active device keyed by the returned key.</summary>
    public async Task<(Guid TenantId, int EmployeeId, string Code, string DeviceKey)> SeedTenantAsync(string label, DateOnly? approvedLeaveOn = null)
    {
        var tenantId = Guid.NewGuid();
        var key = $"knx_device_ingest_{Guid.NewGuid():N}";
        await using (var db = CreateDb())
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = $"Device ingest {label}", Slug = $"dev-ingest-{tenantId:N}" });
            db.Companies.Add(new Company { Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = $"Device Co {label}", CountryCode = "SA", IsActive = true });
            db.AttendancePolicies.Add(new AttendancePolicy { TenantId = tenantId, Code = "STD", Name = "Standard", IsActive = true });
            db.AttendanceDevices.Add(new AttendanceDevice
            {
                TenantId = tenantId, DeviceName = "Gate", SerialNumber = $"SN-{Guid.NewGuid():N}", IsActive = true,
                ApiKeyReference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))),
            });
            await db.SaveChangesAsync();
        }
        var (employeeId, code) = await AddEmployeeAsync(tenantId, label, approvedLeaveOn);
        return (tenantId, employeeId, code, key);
    }

    /// <summary>An active employee of the tenant's company, optionally with an approved one-day leave.</summary>
    public async Task<(int EmployeeId, string Code)> AddEmployeeAsync(Guid tenantId, string label, DateOnly? approvedLeaveOn = null)
    {
        await using var db = CreateDb();
        var companyId = await db.Companies.IgnoreQueryFilters().Where(c => c.TenantId == tenantId).Select(c => c.Id).SingleAsync();
        var employee = new Employee
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeCode = $"DEV-{label}-{Guid.NewGuid():N}"[..20],
            FullName = $"Device Person {label}", Status = EmployeeStatuses.Active,
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        if (approvedLeaveOn is DateOnly day)
        {
            db.LeaveRequests.Add(new LeaveRequest
            {
                TenantId = tenantId, CompanyId = companyId, EmployeeId = employee.Id, EmployeeName = employee.FullName,
                LeaveTypeName = "Annual leave", Status = "Approved", StartDate = day, EndDate = day, TotalDays = 1,
            });
            await db.SaveChangesAsync();
        }
        return (employee.Id, employee.EmployeeCode);
    }
}

/// <summary>The real Program.cs host over the fixture's database, with the application's background workers removed.</summary>
internal sealed class DeviceIngestHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    public DeviceIngestHost(string connectionString) => _connectionString = connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Database:RunMigrationsOnStartup", "false");
        builder.ConfigureTestServices(services =>
        {
            var apiAssembly = typeof(ZayraDbContext).Assembly;
            foreach (var worker in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Assembly == apiAssembly).ToList())
                services.Remove(worker);
        });
    }
}
