using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using static Zayra.Api.Tests.AttendancePayrollPostgresKit;

namespace Zayra.Api.Tests;

/// <summary>
/// HR's Attendance → Exceptions: the open missing-punch, paid-day-changed and imported-day exceptions, each with its
/// day, resolved with a required reason. A resolution survives a recompute: the exception is not reopened unless the
/// punches (or the paid difference) change.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AttendanceExceptionsScreenPostgresTests
{
    private readonly PostgresFixture _fx;
    public AttendanceExceptionsScreenPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task HrSeesTheMissingPunchWithItsDay_ResolvesItWithAReason_AndARecomputeDoesNotReopenIt()
    {
        var k = await CreateAsync(_fx.CreateDb);
        await k.PunchAsync(Utc(Day, 9, 40), "In", processed: false);
        await RangeAsync(k);

        AttendanceExceptionDto item;
        await using (var db = k.NewContext())
        {
            var page = await Controller(db, k.TenantId, Unrestricted).Exceptions(null, null, null, null, null);
            item = page.Items.Should().ContainSingle().Subject;
            item.ExceptionType.Should().Be("MissingPunch");
            item.EmployeeName.Should().Be(k.Employee.FullName);
            item.Description.Should().Contain("30 late minutes").And.NotContain("[sig:", "people read the description, not the signature");
            item.Day.Should().NotBeNull();
            item.Day!.FirstInUtc.Should().Be(Utc(Day, 9, 40));
            item.Day.MissingPunch.Should().BeTrue();
        }

        await using (var db = k.NewContext())
            (await Controller(db, k.TenantId, Unrestricted).ResolveException(item.Id, new ResolveAttendanceExceptionRequest("ok"), default))
                .Should().BeOfType<BadRequestObjectResult>("a reason is required");
        await using (var db = k.NewContext())
        {
            var ok = (await Controller(db, k.TenantId, Unrestricted).ResolveException(item.Id,
                new ResolveAttendanceExceptionRequest("Employee left by the side door; manager confirmed 18:00."), default))
                .Should().BeOfType<OkObjectResult>().Subject;
            var resolved = ok.Value.Should().BeOfType<AttendanceExceptionDto>().Subject;
            resolved.IsResolved.Should().BeTrue();
            resolved.Resolution.Should().Contain("side door");
        }

        await RangeAsync(k);
        await RangeAsync(k);
        (await OpenCountAsync(k, "MissingPunch")).Should().Be(0, "a recompute of the same punches does not reopen what HR resolved");

        // A change to the punches is a new question: an Out stamped before the In arrives (still half a pair).
        await k.PunchAsync(Utc(Day, 9, 20), "Out", processed: false);
        await RangeAsync(k);
        (await OpenCountAsync(k, "MissingPunch")).Should().Be(1, "the day changed, so it is raised again");
    }

    [Fact]
    public async Task ACallerScopedToOtherEmployees_NeitherSeesNorResolvesTheException()
    {
        var k = await CreateAsync(_fx.CreateDb);
        await k.PunchAsync(Utc(Day, 9, 40), "In", processed: false);
        await RangeAsync(k);
        Guid id;
        await using (var db = k.NewContext())
            id = (await Controller(db, k.TenantId, Unrestricted).Exceptions(null, null, null, null, null)).Items.Single().Id;

        var someoneElse = new DataScope { Level = DataScopeLevel.Team, AllowedEmployeeIds = new HashSet<int> { k.Employee.Id + 100_000 } };
        await using (var db = k.NewContext())
            (await Controller(db, k.TenantId, someoneElse).Exceptions(null, null, null, null, k.Employee.Id)).Items.Should().BeEmpty();
        await using (var db = k.NewContext())
            (await Controller(db, k.TenantId, someoneElse).ResolveException(id,
                new ResolveAttendanceExceptionRequest("Trying to close another team's exception."), default))
                .Should().BeOfType<NotFoundResult>();
        (await OpenCountAsync(k, "MissingPunch")).Should().Be(1);
    }

    [Fact]
    public async Task APaidDayChangeResolvedOnTheScreen_IsNotRaisedAgainForTheSameDifference()
    {
        var k = await CreateAsync(_fx.CreateDb);
        var punchIn = await k.PunchAsync(Utc(Day, 9, 30), "In", processed: false);
        await k.PunchAsync(Utc(Day, 18, 0), "Out", processed: false);
        await RangeAsync(k);
        await k.MarkRunPaidAsync();
        await using (var db = k.NewContext())
        {
            (await db.AttendanceRawEvents.IgnoreQueryFilters().SingleAsync(r => r.Id == punchIn.Id)).PunchTimestampUtc = Utc(Day, 9, 50);
            await db.SaveChangesAsync();
        }
        await RangeAsync(k);
        Guid id;
        await using (var db = k.NewContext())
            id = (await Controller(db, k.TenantId, Unrestricted).Exceptions(null, AttendanceDerivedArtifacts.PaidAttendanceChanged, null, null, null)).Items.Single().Id;
        await using (var db = k.NewContext())
            (await Controller(db, k.TenantId, Unrestricted).ResolveException(id,
                new ResolveAttendanceExceptionRequest("Adjusted 20 minutes in the October run."), default)).Should().BeOfType<OkObjectResult>();

        await RangeAsync(k);
        (await OpenCountAsync(k, AttendanceDerivedArtifacts.PaidAttendanceChanged)).Should().Be(0);
    }

    private static readonly DataScope Unrestricted = new() { Level = DataScopeLevel.Organization };

    private static async Task<int> OpenCountAsync(AttendancePayrollPostgresKit k, string type)
    {
        await using var db = k.NewContext();
        return await db.AttendanceExceptions.IgnoreQueryFilters().CountAsync(x => x.TenantId == k.TenantId && x.ExceptionType == type && !x.IsResolved);
    }

    private static async Task RangeAsync(AttendancePayrollPostgresKit k)
    {
        await using var db = k.NewContext();
        await AttendanceService.ForRecompute(db).ProcessAsync(k.TenantId, new ProcessAttendanceRequest(Day, Day, k.Employee.Id),
            new RequestContext(null, null, Guid.NewGuid(), k.TenantId), default);
    }

    private static AttendanceController Controller(ZayraDbContext db, Guid tenantId, DataScope scope)
    {
        var service = new AttendanceService(db, TestNotifications.For(db), new NoHttp());
        var controller = new AttendanceController(service, new FixedScope(scope), new HrmHierarchyService(db, new AuditService(db)), db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Name, "HR Reviewer"), new Claim("permission", "attendance.read"), new Claim("permission", "attendance.write"),
                }, "Test")),
            },
        };
        controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        return controller;
    }

    private sealed class FixedScope(DataScope scope) : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) => Task.FromResult(scope);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
