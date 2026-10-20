using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The processing job runs with no request principal, so its company scope is empty, and LeaveRequest is
/// company-scoped. A filtered leave read there saw nothing, so an approved leave day was recomputed as Absent with an
/// absence deduction. The day processor reads leave tenant-wide (AttendanceService.ApprovedLeaveReadJustification);
/// this proves it on the real queue, real runner and real handler, on PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AttendanceProcessingJobLeaveScopePostgresTests
{
    private static readonly DateOnly LeaveDay = new(2026, 8, 3); // a Monday: a working day in every work week
    private readonly PostgresFixture _fx;
    public AttendanceProcessingJobLeaveScopePostgresTests(PostgresFixture fx) => _fx = fx;

    [Theory]
    [InlineData(false)] // the worker: no HTTP context at all
    [InlineData(true)]  // an anonymous request context: an empty, strict company scope
    public async Task TheProcessingJob_KeepsAnApprovedLeaveDayOnLeave_WithNoAbsenceDeduction(bool anonymousContext)
    {
        Guid tenantId;
        int employeeId;
        await using (var db = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            var company = new Company { TenantId = tenantId, LegalNameEn = "Leave Co", CountryCode = "SA" };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            var employee = new Employee
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"JL-{Guid.NewGuid():N}"[..16], FullName = "On Leave",
                Status = "Active", JoiningDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            db.Employees.Add(employee);
            await db.SaveChangesAsync();
            employeeId = employee.Id;
            db.LeaveRequests.Add(new LeaveRequest
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeId = employee.Id, EmployeeName = employee.FullName,
                LeaveTypeId = Guid.NewGuid(), LeaveTypeName = "Annual", StartDate = LeaveDay, EndDate = LeaveDay, TotalDays = 1m,
                Status = "Approved",
            });
            await db.SaveChangesAsync();
        }

        await using var sp = BuildWorker(anonymousContext);
        Guid jobId;
        await using (var db = _fx.CreateDb())
        {
            var accepted = (await Controller(db, tenantId).ProcessInBackground(new ProcessAttendanceRequest(LeaveDay, LeaveDay, null),
                new BackgroundJobStore(db, sp.GetRequiredService<BackgroundJobTypeRegistry>()), null, default)).Result
                .Should().BeOfType<AcceptedResult>().Subject;
            jobId = accepted.Value.Should().BeOfType<BackgroundJobEnqueueResponse>().Subject.JobId;
        }
        await RunUntilTerminalAsync(sp, jobId);

        await using var verify = _fx.CreateDb();
        var job = await verify.BackgroundJobs.SingleAsync(j => j.Id == jobId);
        job.Status.Should().Be(BackgroundJobStatuses.Succeeded, job.LastError);
        (await verify.AttendanceDailyRecords.IgnoreQueryFilters().SingleAsync(d => d.TenantId == tenantId && d.EmployeeId == employeeId))
            .Status.Should().Be("On leave");
        (await verify.AttendancePayrollImpacts.IgnoreQueryFilters().AnyAsync(i => i.TenantId == tenantId && i.EmployeeId == employeeId))
            .Should().BeFalse("an approved leave day is never deducted as an absence");
    }

    private async Task RunUntilTerminalAsync(ServiceProvider sp, Guid jobId)
    {
        var runner = sp.GetRequiredService<BackgroundJobRunner>();
        for (var i = 0; i < 25; i++)
        {
            await using (var db = _fx.CreateDb())
                if (BackgroundJobStatuses.IsTerminal(await db.BackgroundJobs.Where(j => j.Id == jobId).Select(j => j.Status).SingleAsync())) return;
            if (!await runner.RunNextAsync("w", default, default, [AttendanceProcessingJobHandler.JobType]))
                await Task.Delay(100);
        }
        throw new TimeoutException($"Job {jobId} did not finish.");
    }

    private ServiceProvider BuildWorker(bool anonymousContext)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => anonymousContext ? _fx.CreateDbWithAccessor(new Accessor(new DefaultHttpContext())) : _fx.CreateDbWithAccessor(new Accessor(null)));
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

    private static AttendanceController Controller(ZayraDbContext db, Guid tenantId)
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
                    new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim("sub", userId.ToString()), new Claim(ClaimTypes.Role, "Admin"),
                    new Claim("permission", "employees.write"), new Claim("permission", "attendance.read"), new Claim("permission", "attendance.write"),
                }, "Test")),
            },
        };
        controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        return controller;
    }

    private sealed class Accessor(HttpContext? ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = ctx;
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
