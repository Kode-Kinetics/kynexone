using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Setup;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// A Saudi tenant is on Riyadh time whatever zone is stored — the rule TenantClock (the app's "today") has always applied,
/// which attendance now shares. Stored non-Riyadh zones exist in production: the entity default is America/New_York and
/// the setup assistant used to write "UTC" for a blank zone. On real Postgres, through the punch path and range processing.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class SaudiTimeZoneAttendancePostgresTests
{
    private readonly PostgresFixture _fx;
    public SaudiTimeZoneAttendancePostgresTests(PostgresFixture fx) => _fx = fx;

    // 2026-09-15 22:00 UTC = 2026-09-16 01:00 in Riyadh = 2026-09-15 18:00 in New York.
    private static readonly DateTime OneAmRiyadh = new(2026, 9, 15, 22, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("SAU", "UTC", 16)]
    [InlineData("SAU", "America/New_York", 16)]
    [InlineData("SA", "", 16)]
    [InlineData("ARE", "Asia/Dubai", 16)]   // a non-Saudi tenant's stored zone is honoured (02:00 in Dubai)
    [InlineData("ARE", "America/New_York", 15)] // ...even an odd one: only Saudi tenants are pinned to Riyadh
    public async Task APunchAt1amRiyadh_IsDatedByTheTenantClockRule_OnThePunchPathAndInARangeRun(string country, string storedZone, int expectedDay)
    {
        Guid tenantId;
        int employeeId;
        await using (var db = _fx.CreateDb())
        {
            tenantId = await PostgresFixture.SeedMinimalTenant(db);
            db.TenantLocalizationSettings.Add(new TenantLocalizationSetting { TenantId = tenantId, CountryCode = country, DefaultTimezone = storedZone });
            var employee = new Employee
            {
                TenantId = tenantId, EmployeeCode = $"TZ-{Guid.NewGuid():N}"[..16], FullName = "Zone Person", Status = "Active",
                JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            db.Employees.Add(employee);
            await db.SaveChangesAsync();
            employeeId = employee.Id;
        }

        await using (var db = _fx.CreateDb())
            await new AttendanceService(db, new Notifications(), new HttpClients(), new FixedClock(OneAmRiyadh)).PunchAsync(tenantId,
                new WebPunchRequest(employeeId, "In", null, null, null), "Web", new RequestContext(null, null, Guid.NewGuid(), tenantId), default);

        var expected = new DateOnly(2026, 9, expectedDay);
        await using (var db = _fx.CreateDb())
            (await db.AttendanceDailyRecords.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).Select(x => x.WorkDate).ToListAsync())
                .Should().Equal([expected], "the punch path dates by the tenant clock's zone");

        // A range run over both dates keeps the punch on the same day and opens nothing on the other.
        await using (var db = _fx.CreateDb())
            await AttendanceService.ForRecompute(db).ProcessAsync(tenantId, new ProcessAttendanceRequest(new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 16), employeeId),
                new RequestContext(null, null, Guid.NewGuid(), tenantId), default);
        await using var check = _fx.CreateDb();
        (await check.AttendanceDailyRecords.IgnoreQueryFilters().Where(x => x.TenantId == tenantId && x.FirstInUtc == OneAmRiyadh)
                .Select(x => x.WorkDate).ToListAsync())
            .Should().Equal([expected], "range processing uses the same zone");
    }

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    }

    private sealed class Notifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class HttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

/// <summary>
/// New tenants are born on their country's zone, never the entity's America/New_York default or a blanket UTC. No stored
/// data is changed: these are the paths that create the localization row.
/// </summary>
public sealed class NewTenantLocalizationZoneTests
{
    [Theory]
    [InlineData("SA", "Asia/Riyadh")]
    [InlineData("AE", "Asia/Dubai")]
    [InlineData("GB", "Europe/London")]
    public async Task ASetupApplyThatCreatesTheLocalizationRow_StartsItOnTheCountrysZone(string country, string expected)
    {
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var draft = SetupDraft.Empty() with { WorkingWeek = new DraftWorkingWeek("Sun-Thu", "Sunday") };

        (await SetupController(db, tenantId).Apply(new ApplySetupRequest(draft, country, "SAR"), default)).Should().BeOfType<OkObjectResult>();

        (await db.TenantLocalizationSettings.SingleAsync(x => x.TenantId == tenantId)).DefaultTimezone.Should().Be(expected,
            "a draft that sets only the working week used to leave the tenant on the America/New_York entity default");
    }

    private sealed class NoPreview : ISetupAssistantService
    {
        public Task<SetupPreviewResult> GenerateAsync(SetupRequester requester, CompanyProfile profile, CancellationToken ct)
            => Task.FromResult(new SetupPreviewResult(SetupDraft.Empty(), [], "test"));
    }

    private static SetupAssistantController SetupController(ZayraDbContext db, Guid tenantId)
    {
        var claims = new[]
        {
            new System.Security.Claims.Claim("tenant_id", tenantId.ToString()),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new System.Security.Claims.Claim("permission", "organization.setup.apply"),
        };
        return new SetupAssistantController(db, new NoPreview(), new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "Test")),
                },
            },
        };
    }
}
