using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using static Zayra.Api.Tests.AttendancePayrollPostgresKit;

namespace Zayra.Api.Tests;

/// <summary>
/// Locking a payroll run locks its employees' attendance for the month; voiding it releases them. Before, nothing
/// wrote the attendance lock, so a locked month's In / Out / status could be rewritten by a range run, a correction or
/// a late punch.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AttendancePayrollLockPostgresTests
{
    private readonly PostgresFixture _fx;
    public AttendancePayrollLockPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task ALockedRunsDays_CannotBeRewritten_ByARangeRunACorrectionOrAPunch_UntilTheRunIsVoided()
    {
        var k = await CreateAsync(_fx.CreateDb);
        var punchIn = await k.PunchAsync(Utc(Day, 9, 30), "In", processed: false);
        await k.PunchAsync(Utc(Day, 18, 0), "Out", processed: false);
        await RangeAsync(k);
        await LockRunAsync(k);

        await using (var db = k.NewContext())
        {
            (await db.AttendanceDailyRecords.IgnoreQueryFilters().SingleAsync(x => x.TenantId == k.TenantId && x.WorkDate == Day))
                .IsPayrollLocked.Should().BeTrue("locking the run locks its employees' days");
            (await db.AttendanceLockPeriods.IgnoreQueryFilters().SingleAsync(x => x.TenantId == k.TenantId))
                .LockType.Should().Be(AttendancePayrollLock.RunLockType(k.Run.Id));
        }

        // A range run after the In punch moved: the locked day is not rewritten.
        await using (var db = k.NewContext())
        {
            (await db.AttendanceRawEvents.IgnoreQueryFilters().SingleAsync(r => r.Id == punchIn.Id)).PunchTimestampUtc = Utc(Day, 9, 50);
            await db.SaveChangesAsync();
        }
        await RangeAsync(k);
        (await DayAsync(k)).FirstInUtc.Should().Be(Utc(Day, 9, 30), "a range run skips a payroll-locked day");

        // A late punch for the day is recorded as a raw event but does not touch the day.
        await using (var db = k.NewContext())
            await new AttendanceService(db, new NoNotifications(), new NoHttp(), new FixedClock(Utc(Day, 19, 0)))
                .PunchAsync(k.TenantId, new WebPunchRequest(k.Employee.Id, "Out", null, null, null), "Web",
                    new RequestContext(null, null, Guid.NewGuid(), k.TenantId), default);
        (await DayAsync(k)).LastOutUtc.Should().Be(Utc(Day, 18, 0), "a punch does not rewrite a payroll-locked day");

        // A correction for the day is refused.
        await using (var db = k.NewContext())
        {
            var service = AttendanceService.ForRecompute(db);
            var reg = await service.CreateRegularizationAsync(k.TenantId, new RegularizationRequestDto(k.Employee.Id, Day, "Missed punch",
                Utc(Day, 9, 0), null, "late by mistake"), new RequestContext(null, null, Guid.NewGuid(), k.TenantId), default);
            var refuse = () => service.ApproveRegularizationAsync(k.TenantId, reg.Id, new RegularizationDecisionRequest("ok"),
                new RequestContext(null, null, Guid.NewGuid(), k.TenantId), default);
            await refuse.Should().ThrowAsync<InvalidOperationException>().WithMessage("*payroll locked*");
        }

        // Voiding the run releases the lock: the day follows its punches again.
        await using (var db = k.NewContext())
            (await new PayrollVoidService(db).VoidAsync(k.Run.Id, k.TenantId, null, "test", "wrong month", default)).IsVoided.Should().BeTrue();
        await using (var db = k.NewContext())
        {
            (await db.AttendanceLockPeriods.IgnoreQueryFilters().AnyAsync(x => x.TenantId == k.TenantId)).Should().BeFalse();
            (await DayAsync(k)).IsPayrollLocked.Should().BeFalse();
        }
        await RangeAsync(k);
        (await DayAsync(k)).FirstInUtc.Should().Be(Utc(Day, 9, 50));
    }

    [Fact]
    public async Task ADayWithNoRecordYet_InALockedRunsMonth_IsNotCreated_ByAPunchARangeRunOrAnImport()
    {
        var k = await CreateAsync(_fx.CreateDb);
        await k.PunchAsync(Utc(Day, 9, 0), "In", processed: false);
        await k.PunchAsync(Utc(Day, 18, 0), "Out", processed: false);
        await RangeAsync(k);
        await LockRunAsync(k);
        var lateDay = Day.AddDays(1); // covered by the locked run, and no record exists for it

        // A late first punch for the locked month: kept as a raw event, but no day is opened or processed.
        await using (var db = k.NewContext())
            await new AttendanceService(db, new NoNotifications(), new NoHttp(), new FixedClock(Utc(lateDay, 9, 5)))
                .PunchAsync(k.TenantId, new WebPunchRequest(k.Employee.Id, "In", null, null, null), "Web",
                    new RequestContext(null, null, Guid.NewGuid(), k.TenantId), default);
        // A range run over the locked month does not open it either.
        await using (var db = k.NewContext())
            await AttendanceService.ForRecompute(db).ProcessAsync(k.TenantId, new ProcessAttendanceRequest(lateDay, lateDay.AddDays(1), k.Employee.Id),
                new RequestContext(null, null, Guid.NewGuid(), k.TenantId), default);

        // An import row for the locked day is refused, not written.
        await using (var db = k.NewContext())
        {
            var controller = new Zayra.Api.Controllers.MigrationImportController(db, new Zayra.Api.Infrastructure.Auth.Pbkdf2PasswordHasher(),
                new Zayra.Api.Infrastructure.Audit.AuditService(db))
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                    {
                        User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
                        {
                            new System.Security.Claims.Claim("tenant_id", k.TenantId.ToString()),
                            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin"),
                        }, "test")),
                    },
                },
            };
            var result = await controller.Commit(new Zayra.Api.Models.MigrationPackageRequest($"locked-import-{Guid.NewGuid():N}", new Dictionary<string, string>
            {
                ["attendanceDaily"] = "EmployeeCode,WorkDate,FirstInUtc,LastOutUtc,TotalWorkedMinutes,BreakMinutes,LateMinutes,"
                    + "EarlyExitMinutes,OvertimeMinutes,UndertimeMinutes,MissingPunch,Status,WorkMode\n"
                    + $"{k.Employee.EmployeeCode},{lateDay:yyyy-MM-dd},{lateDay:yyyy-MM-dd}T09:00:00Z,{lateDay:yyyy-MM-dd}T18:00:00Z,480,60,0,0,0,0,false,Present,Work from site\n",
            }, false), default);
            System.Text.Json.JsonSerializer.Serialize((result.Result as ObjectResult)?.Value).Should().Contain("payroll-locked");
        }

        await using var check = k.NewContext();
        (await check.AttendanceRawEvents.IgnoreQueryFilters().CountAsync(r => r.TenantId == k.TenantId && r.PunchTimestampUtc == Utc(lateDay, 9, 5)))
            .Should().Be(1, "the punch itself is recorded");
        (await check.AttendanceDailyRecords.IgnoreQueryFilters().AnyAsync(x => x.TenantId == k.TenantId && x.WorkDate >= lateDay))
            .Should().BeFalse("a payroll-locked month is not written to, even on a day it has no record for");
        (await check.AttendancePayrollImpacts.IgnoreQueryFilters().AnyAsync(x => x.TenantId == k.TenantId && x.WorkDate >= lateDay)).Should().BeFalse();
        (await AttendancePayrollLock.IsLockedAsync(check, k.TenantId, k.Employee.Id, lateDay, default)).Should().BeTrue();
        (await AttendancePayrollLock.IsLockedAsync(check, k.TenantId, k.Employee.Id, lateDay.AddMonths(1), default))
            .Should().BeFalse("the lock covers the run's month only");
    }

    private async Task LockRunAsync(AttendancePayrollPostgresKit k)
    {
        await using (var db = k.NewContext())
        {
            await GlDriverSeeder.SeedTenantDefaultsAsync(db, k.TenantId, CancellationToken.None);
            await db.SaveChangesAsync();
        }
        await using (var db = k.NewContext())
            (await k.Payroll(db).Process(k.Run.Id, default)).Should().BeOfType<OkObjectResult>();
        await using (var db = k.NewContext())
            await db.PayrollRuns.IgnoreQueryFilters().Where(r => r.Id == k.Run.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Approved"));
        await using (var db = k.NewContext())
        {
            var result = await k.Payroll(db).Lock(k.Run.Id, default);
            result.Should().BeOfType<OkObjectResult>(System.Text.Json.JsonSerializer.Serialize((result as ObjectResult)?.Value));
        }
    }

    private static async Task<Zayra.Api.Models.AttendanceDailyRecord> DayAsync(AttendancePayrollPostgresKit k)
    {
        await using var db = k.NewContext();
        return await db.AttendanceDailyRecords.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.TenantId == k.TenantId && x.WorkDate == Day);
    }

    private static async Task RangeAsync(AttendancePayrollPostgresKit k)
    {
        await using var db = k.NewContext();
        await AttendanceService.ForRecompute(db).ProcessAsync(k.TenantId, new ProcessAttendanceRequest(Day, Day, k.Employee.Id),
            new RequestContext(null, null, Guid.NewGuid(), k.TenantId), default);
    }

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    }

    private sealed class NoNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
