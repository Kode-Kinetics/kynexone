using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// F3 — "Process Attendance" moved onto the durable queue, end to end on real PostgreSQL: enqueue through
/// the real controller endpoint, execute on the real runner with the real handler and the real
/// AttendanceService, and compare the outcome with the synchronous endpoint run over identical data.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AttendanceProcessingJobPostgresTests
{
    private static readonly DateOnly From = new(2026, 8, 2);  // Sun..Tue — no weekend in any work-week
    private static readonly DateOnly To = new(2026, 8, 4);
    private readonly PostgresFixture _fx;
    public AttendanceProcessingJobPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task AsyncEndpoint_EnqueuesDurableJob_WorkerProducesSameResultAsSynchronousEndpoint()
    {
        var (asyncTenant, _, _) = await SeedAsync();
        var (syncTenant, _, _) = await SeedAsync();
        await using var sp = BuildInstance();

        // Async path: 202 + job id, then the worker runs it.
        Guid jobId;
        await using (var db = _fx.CreateDb())
        {
            var accepted = Assert.IsType<AcceptedResult>(
                (await CreateController(db, asyncTenant).ProcessInBackground(
                    new ProcessAttendanceRequest(From, To, null),
                    new BackgroundJobStore(db, sp.GetRequiredService<BackgroundJobTypeRegistry>()), null, default)).Result);
            var body = Assert.IsType<BackgroundJobEnqueueResponse>(accepted.Value);
            Assert.False(body.Deduplicated);
            Assert.Equal(BackgroundJobStatuses.Queued, body.Status);
            jobId = body.JobId;
        }
        await RunUntilTerminalAsync(sp, jobId);

        // Sync path over identical data in a second tenant.
        await using (var db = _fx.CreateDb())
        {
            var ok = Assert.IsType<OkObjectResult>(
                (await CreateController(db, syncTenant).Process(new ProcessAttendanceRequest(From, To, null), default)).Result);
            Assert.Equal(9, ok.Value);
        }

        await using var verify = _fx.CreateDb();
        var job = await verify.BackgroundJobs.SingleAsync(j => j.Id == jobId);
        Assert.True(job.Status == BackgroundJobStatuses.Succeeded, $"{job.Status}: {job.LastError}");
        Assert.Equal(3, job.ProgressTotal);
        Assert.Equal(3, job.ProgressCompleted);
        Assert.Contains("\"employeeDays\": 9", job.ResultJson!.Replace("\"employeeDays\":9", "\"employeeDays\": 9"));
        // One checkpoint per employee + the final audit step.
        Assert.Equal(4, await verify.BackgroundJobItems.CountAsync(i => i.JobId == jobId));
        Assert.Equal(1, await verify.AttendanceAuditLogs.CountAsync(a => a.TenantId == asyncTenant && a.Action == "attendance.processed"));

        var asyncRows = await Snapshot(verify, asyncTenant);
        var syncRows = await Snapshot(verify, syncTenant);
        Assert.Equal(9, asyncRows.Count);
        Assert.Equal(syncRows, asyncRows);
        Assert.Contains(asyncRows, r => r.Status == "Late");    // the late employee
        Assert.Contains(asyncRows, r => r.Status == "Absent");  // the employee with no punches

        // Legacy attendance_records carry the employee's company on the worker path too (the worker has
        // no principal, so nothing but the explicit stamping in AttendanceService could supply it).
        Assert.False(await verify.AttendanceRecords.IgnoreQueryFilters()
            .AnyAsync(r => r.TenantId == asyncTenant && r.CompanyId == null));
    }

    [Fact]
    public async Task DoubleClickedProcess_ReturnsTheSameJob()
    {
        var (tenant, _, _) = await SeedAsync();
        await using var sp = BuildInstance();
        await using var db = _fx.CreateDb();
        var store = new BackgroundJobStore(db, sp.GetRequiredService<BackgroundJobTypeRegistry>());
        var controller = CreateController(db, tenant);

        var first = Assert.IsType<BackgroundJobEnqueueResponse>(Assert.IsType<AcceptedResult>(
            (await controller.ProcessInBackground(new ProcessAttendanceRequest(From, To, null), store, null, default)).Result).Value);
        var second = Assert.IsType<BackgroundJobEnqueueResponse>(Assert.IsType<AcceptedResult>(
            (await controller.ProcessInBackground(new ProcessAttendanceRequest(From, To, null), store, null, default)).Result).Value);

        Assert.Equal(first.JobId, second.JobId);
        Assert.True(second.Deduplicated);
        Assert.Equal("true", controller.Response.Headers["X-Job-Deduplicated"].ToString());
        Assert.Equal(1, await db.BackgroundJobs.CountAsync(j => j.TenantId == tenant));
        // Don't leave a live attendance job for other tests' workers to pick up.
        Assert.Equal(BackgroundJobCancelOutcome.Cancelled, await store.RequestCancelAsync(tenant, first.JobId, default));
    }

    [Fact]
    public async Task LockedPeriod_IsRejectedAtEnqueue_WithTheSame400AsTheSyncEndpoint()
    {
        var (tenant, _, _) = await SeedAsync();
        await using (var seed = _fx.CreateDb())
        {
            seed.AttendanceLockPeriods.Add(new AttendanceLockPeriod { TenantId = tenant, PeriodStart = From, PeriodEnd = To, Status = "Locked" });
            await seed.SaveChangesAsync();
        }
        await using var sp = BuildInstance();
        await using var db = _fx.CreateDb();
        var result = await CreateController(db, tenant).ProcessInBackground(
            new ProcessAttendanceRequest(From, To, null),
            new BackgroundJobStore(db, sp.GetRequiredService<BackgroundJobTypeRegistry>()), null, default);
        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.False(await db.BackgroundJobs.AnyAsync(j => j.TenantId == tenant));
    }

    [Fact]
    public async Task CompanyScopedJob_ProcessesOnlyTheCallersCompanies()
    {
        var (tenant, companyA, _) = await SeedAsync();
        await using var sp = BuildInstance();
        Guid jobId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var payload = new AttendanceProcessingJobPayload(From, To, null, GroupScope: false, CompanyIds: [companyA],
                RequestedByUserId: null, IpAddress: null, UserAgent: null);
            jobId = (await scope.ServiceProvider.GetRequiredService<BackgroundJobStore>().EnqueueAsync(tenant,
                AttendanceProcessingJobHandler.JobType, AttendanceProcessingJobHandler.DefaultIdempotencyKey(payload), payload, null, default)).Job.Id;
        }
        await RunUntilTerminalAsync(sp, jobId);

        await using var verify = _fx.CreateDb();
        Assert.Equal(2, (await verify.BackgroundJobs.SingleAsync(j => j.Id == jobId)).ProgressTotal);
        var processedEmployees = await verify.AttendanceDailyRecords.Where(r => r.TenantId == tenant)
            .Select(r => r.EmployeeId).Distinct().ToListAsync();
        var companyAEmployees = await verify.Employees.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenant && e.CompanyId == companyA).Select(e => e.Id).ToListAsync();
        Assert.Equal(companyAEmployees.OrderBy(x => x), processedEmployees.OrderBy(x => x));
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>
    /// The attendance job type is shared by every test in the collection, so the queue may hold other
    /// tests' jobs; keep draining until OUR job reaches a terminal state.
    /// </summary>
    private async Task RunUntilTerminalAsync(ServiceProvider sp, Guid jobId)
    {
        var runner = sp.GetRequiredService<BackgroundJobRunner>();
        for (var i = 0; i < 25; i++)
        {
            await using (var db = _fx.CreateDb())
            {
                var status = await db.BackgroundJobs.Where(j => j.Id == jobId).Select(j => j.Status).SingleAsync();
                if (BackgroundJobStatuses.IsTerminal(status)) return;
            }
            if (!await runner.RunNextAsync("w", default, default, [AttendanceProcessingJobHandler.JobType]))
                await Task.Delay(100);
        }
        throw new TimeoutException($"Job {jobId} did not finish.");
    }

    private record Row(string Code, DateOnly Date, string Status, int Worked, int Late, bool Missing);

    private static async Task<List<Row>> Snapshot(ZayraDbContext db, Guid tenant)
    {
        var rows = await db.AttendanceDailyRecords.Where(r => r.TenantId == tenant)
            .Join(db.Employees.IgnoreQueryFilters(), r => r.EmployeeId, e => e.Id,
                (r, e) => new { e.EmployeeCode, r.WorkDate, r.Status, r.TotalWorkedMinutes, r.LateMinutes, r.MissingPunch })
            .ToListAsync();
        return rows.Select(r => new Row(r.EmployeeCode, r.WorkDate, r.Status, r.TotalWorkedMinutes, r.LateMinutes, r.MissingPunch))
            .OrderBy(r => r.Code).ThenBy(r => r.Date).ToList();
    }

    private async Task<(Guid Tenant, Guid CompanyA, Guid CompanyB)> SeedAsync()
    {
        await using var db = _fx.CreateDb();
        var tenant = await PostgresFixture.SeedMinimalTenant(db);
        var a = new Company { TenantId = tenant, LegalNameEn = "Entity A", CountryCode = "SA" };
        var b = new Company { TenantId = tenant, LegalNameEn = "Entity B", CountryCode = "SA" };
        db.AddRange(a, b);
        await db.SaveChangesAsync();
        Employee Emp(string code, Guid company) => new()
        {
            TenantId = tenant, CompanyId = company, EmployeeCode = code, FullName = code,
            Status = "Active", JoiningDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var onTime = Emp("ON-TIME", a.Id);
        var late = Emp("LATE", a.Id);
        var absent = Emp("ABSENT", b.Id);
        db.Employees.AddRange(onTime, late, absent);
        await db.SaveChangesAsync();
        for (var d = From; d <= To; d = d.AddDays(1))
        {
            db.AttendanceRawEvents.AddRange(
                Punch(tenant, onTime.Id, d, 9, 0, "In"), Punch(tenant, onTime.Id, d, 17, 30, "Out"),
                Punch(tenant, late.Id, d, 9, 45, "In"), Punch(tenant, late.Id, d, 17, 30, "Out"));
        }
        await db.SaveChangesAsync();
        return (tenant, a.Id, b.Id);
    }

    private static AttendanceRawEvent Punch(Guid tenant, int employeeId, DateOnly d, int h, int m, string dir) => new()
    {
        TenantId = tenant, EmployeeId = employeeId, Source = "test", PunchDirection = dir,
        PunchTimestampUtc = DateTime.SpecifyKind(d.ToDateTime(new TimeOnly(h, m)), DateTimeKind.Utc),
    };

    private ServiceProvider BuildInstance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _fx.CreateDb());
        services.AddScoped<INotificationService>(p => TestNotifications.For(p.GetRequiredService<ZayraDbContext>()));
        services.AddSingleton<IHttpClientFactory, NoHttp>();
        services.AddScoped<IAttendanceService, AttendanceService>();
        services.AddSingleton(new BackgroundJobOptions { LeaseDuration = TimeSpan.FromMinutes(1), HeartbeatInterval = TimeSpan.FromSeconds(20) });
        services.AddSingleton(AttendanceProcessingJobHandler.Descriptor);
        services.AddScoped<AttendanceProcessingJobHandler>();
        services.AddSingleton<BackgroundJobTypeRegistry>();
        services.AddScoped<BackgroundJobStore>();
        services.AddSingleton<BackgroundJobRunner>();
        return services.BuildServiceProvider();
    }

    private static AttendanceController CreateController(ZayraDbContext db, Guid tenantId)
    {
        var service = new AttendanceService(db, TestNotifications.For(db), new NoHttp());
        var controller = new AttendanceController(service, new DataScopeService(db), new HrmHierarchyService(db, new AuditService(db)), db);
        var userId = Guid.NewGuid();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim("sub", userId.ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                    new Claim("permission", "employees.write"),
                    new Claim("permission", "attendance.read"),
                    new Claim("permission", "attendance.write"),
                }, "Test")),
            },
        };
        controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        controller.ControllerContext.HttpContext.Request.Headers.UserAgent = "test";
        return controller;
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
