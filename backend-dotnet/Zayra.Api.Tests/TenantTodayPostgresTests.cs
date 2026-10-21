using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// "Today" is the tenant's day, by TenantClock's rule, on the attendance dashboard (and the other surfaces this change
/// moved off the UTC date). The zone is chosen so its date differs from the UTC date at the moment the test runs:
/// UTC+14 is a day ahead from 10:00 UTC, UTC-11 a day behind until 11:00 UTC.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class TenantTodayPostgresTests
{
    private readonly PostgresFixture _fx;
    public TenantTodayPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task TheAttendanceTodaySummary_IsForTheTenantsDay_NotTheUtcDate()
    {
        var zone = DateTime.UtcNow.Hour >= 10 ? "Pacific/Kiritimati" : "Pacific/Pago_Pago";
        Guid tenantId;
        await using (var db = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tenantId, CountryCode = "KI", DefaultTimezone = zone });
            await db.SaveChangesAsync();
        }
        await using var check = _fx.CreateDb();
        var tenantToday = await TenantClock.TodayForAsync(check, tenantId, default);
        tenantToday.Should().NotBe(DateOnly.FromDateTime(DateTime.UtcNow), "premise: the zone's date differs from the UTC date now");

        await using var db2 = _fx.CreateDb();
        var ok = (await Controller(db2, tenantId).Today(default)).Should().BeOfType<OkObjectResult>().Subject;
        ok.Value!.GetType().GetProperty("date")!.GetValue(ok.Value).Should().Be(tenantToday);
    }

    [Fact]
    public async Task ASaudiTenantWithAStoredNewYorkZone_IsOnTheRiyadhDay()
    {
        Guid tenantId;
        await using (var db = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tenantId, CountryCode = "SAU", DefaultTimezone = "America/New_York" });
            await db.SaveChangesAsync();
        }
        await using var check = _fx.CreateDb();
        // 2026-09-15 22:00 UTC = 01:00 on the 16th in Riyadh = 18:00 on the 15th in New York.
        (await TenantClock.TodayForAsync(check, tenantId, default, new DateTime(2026, 9, 15, 22, 0, 0, DateTimeKind.Utc)))
            .Should().Be(new DateOnly(2026, 9, 16));
    }

    private static AttendanceController Controller(ZayraDbContext db, Guid tenantId)
    {
        var service = new AttendanceService(db, TestNotifications.For(db), new NoHttp());
        var controller = new AttendanceController(service, new DataScopeService(db), new HrmHierarchyService(db, new AuditService(db)), db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "attendance.read"),
                }, "Test")),
            },
        };
        controller.ControllerContext.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        return controller;
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
