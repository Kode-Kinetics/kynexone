using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Shifts;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A roster change re-dates punches. Rostering a night shift onto a day whose 22:00–02:00 punches were split into an
/// In-only evening and an Out-only morning queues the attendance job for D-1..D+1 (once, however often the change is
/// saved), and the job puts both punches on the shift's day.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class RosterChangeAttendanceRecomputePostgresTests
{
    private static readonly DateOnly D = new(2026, 9, 15);
    private readonly PostgresFixture _fx;
    public RosterChangeAttendanceRecomputePostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task AssigningANightShift_QueuesOneRecomputeOfTheDaysAround_AndTheJobMovesThePunchesOntoTheShiftsDay()
    {
        Guid tenantId;
        int employeeId;
        Guid nightId;
        await using (var db = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tenantId, CountryCode = "SAU", DefaultTimezone = "Asia/Riyadh" });
            var employee = new Employee
            {
                TenantId = tenantId, EmployeeCode = $"RC-{Guid.NewGuid():N}"[..16], FullName = "Night Worker", Status = "Active",
                JoiningDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            var night = new ShiftDefinition { TenantId = tenantId, Code = "NIGHT", Name = "Night", StartTime = new TimeOnly(22, 0), EndTime = new TimeOnly(6, 0), BreakMinutes = 60, IsActive = true };
            db.AddRange(employee, night);
            await db.SaveChangesAsync();
            employeeId = employee.Id;
            nightId = night.Id;
            db.AttendanceRawEvents.AddRange(
                new AttendanceRawEvent { TenantId = tenantId, EmployeeId = employeeId, PunchDirection = "In", PunchTimestampUtc = new DateTime(2026, 9, 15, 19, 0, 0, DateTimeKind.Utc) },
                new AttendanceRawEvent { TenantId = tenantId, EmployeeId = employeeId, PunchDirection = "Out", PunchTimestampUtc = new DateTime(2026, 9, 15, 23, 0, 0, DateTimeKind.Utc) });
            await db.SaveChangesAsync();
        }
        await using (var db = _fx.CreateDb())
            await AttendanceService.ForRecompute(db).ProcessAsync(tenantId, new ProcessAttendanceRequest(D, D.AddDays(1), employeeId),
                new RequestContext(null, null, Guid.NewGuid(), tenantId), default);
        await using (var db = _fx.CreateDb())
            (await db.AttendanceDailyRecords.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenantId && x.WorkDate == D))
                .LastOutUtc.Should().BeNull("premise: with no shift the 02:00 clock-out belongs to the next day");

        await using var sp = Services();
        await using (var db = _fx.CreateDb())
        {
            var first = await Controller(db, tenantId, sp).Assign(new AssignShiftRequest(employeeId, nightId, D, null), default);
            Prop<int>(first.Should().BeOfType<OkObjectResult>().Subject.Value!, "attendanceRecomputeQueued").Should().Be(1);
        }
        await using (var db = _fx.CreateDb())
        {
            var again = await Controller(db, tenantId, sp).Assign(new AssignShiftRequest(employeeId, nightId, D, "same shift, saved again"), default);
            Prop<int>(again.Should().BeOfType<OkObjectResult>().Subject.Value!, "attendanceRecomputeQueued").Should().Be(0, "the same days share one live job");
        }
        await using (var db = _fx.CreateDb())
        {
            var job = await db.BackgroundJobs.SingleAsync(j => j.TenantId == tenantId && j.JobType == AttendanceProcessingJobHandler.JobType);
            job.IdempotencyKey.Should().Contain($"{D.AddDays(-1):yyyy-MM-dd}:{D.AddDays(1):yyyy-MM-dd}:{employeeId}");
        }

        var runner = sp.GetRequiredService<BackgroundJobRunner>();
        for (var i = 0; i < 10 && await runner.RunNextAsync("w", default, default, [AttendanceProcessingJobHandler.JobType]); i++) { }

        await using var check = _fx.CreateDb();
        var day = await check.AttendanceDailyRecords.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenantId && x.WorkDate == D);
        day.FirstInUtc.Should().Be(new DateTime(2026, 9, 15, 19, 0, 0, DateTimeKind.Utc));
        day.LastOutUtc.Should().Be(new DateTime(2026, 9, 15, 23, 0, 0, DateTimeKind.Utc), "the night's clock-out is now the shift's");
        day.MissingPunch.Should().BeFalse();
        (await check.AttendanceDailyRecords.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenantId && x.WorkDate == D.AddDays(1)))
            .LastOutUtc.Should().BeNull("the next day no longer holds the night's clock-out");
    }

    private ServiceProvider Services()
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

    private static ShiftsController Controller(ZayraDbContext db, Guid tenantId, IServiceProvider sp) =>
        new(db, new Unrestricted(), new NoPlanner())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    }, "Test")),
                    RequestServices = new StoreOnly(new BackgroundJobStore(db, sp.GetRequiredService<BackgroundJobTypeRegistry>())),
                },
            },
        };

    private static T Prop<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;

    private sealed class StoreOnly(BackgroundJobStore store) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(BackgroundJobStore) ? store : null;
    }

    private sealed class Unrestricted : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
            => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }

    private sealed class NoPlanner : IRosterPlannerService
    {
        public Task<RosterPlanResult> PlanAsync(RosterPlanInput input, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
