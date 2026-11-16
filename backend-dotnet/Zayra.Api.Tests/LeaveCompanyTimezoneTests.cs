using FluentAssertions;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Leave;
using Zayra.Api.Models;
using Zayra.Api.Tests.SelfieAttendance;

namespace Zayra.Api.Tests;

/// <summary>
/// S3 — "today" in the leave and attendance code was the UTC date. A Riyadh tenant (UTC+3) is a calendar day ahead of
/// UTC for three hours every night, so notice, the insight window and a statutory figure were all judged against
/// yesterday. Every one of them now asks the tenant's own clock (<c>TenantClock</c>: Saudi tenants on Asia/Riyadh,
/// everyone else on their stored zone, UTC when unset), the same rule attendance dates its punches by.
/// </summary>
public sealed class LeaveCompanyTimezoneTests
{
    // The fixed clocks sit in 2025, far from the real date, so a service that still reads the system clock cannot pass by luck.
    // 10 Jun 2025 02:00 UTC is still 9 Jun, 22:00 in New York.
    private static readonly DateTime EarlyTuesdayUtc = new(2025, 6, 10, 2, 0, 0, DateTimeKind.Utc);
    // Thursday 12 Jun 2025, 22:00 UTC is already Friday 13 Jun, 01:00 in Riyadh.
    private static readonly DateTime LateThursdayUtc = new(2025, 6, 12, 22, 0, 0, DateTimeKind.Utc);

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    }

    private static async Task SetTenantZoneAsync(Zayra.Api.Data.ZayraDbContext db, Guid tenantId, string zone, string country)
    {
        db.TenantLocalizationSettings.Add(new TenantLocalizationSetting
        {
            TenantId = tenantId, DefaultTimezone = zone, CountryCode = country,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<(LeaveBatch1World World, LeaveService Service)> NoticeWorldAsync(string? zone)
    {
        var world = await LeaveBatch1World.CreateAsync();
        if (zone is not null) await SetTenantZoneAsync(world.Db, world.TenantId, zone, "US");
        world.Db.LeavePolicies.Add(new LeavePolicy
        {
            TenantId = world.TenantId, Name = "Annual", LeaveTypeId = world.LeaveType.Id, Status = "Active",
            AnnualEntitlementDays = 60, AppliesOnProbation = true, NoticeRequiredDays = 3,
        });
        world.Db.EmployeeLeaveBalances.Add(new EmployeeLeaveBalance
        {
            TenantId = world.TenantId, EmployeeId = world.Emp.Id, LeaveTypeId = world.LeaveType.Id, Year = 2025, Entitled = 30,
        });
        await world.Db.SaveChangesAsync();
        return (world, new LeaveService(world.Db, new ApprovalRouter(world.Db), clock: new FixedClock(EarlyTuesdayUtc)));
    }

    // Thursday 12 Jun 2025: two days after the UTC date (10 Jun), three after New York's (9 Jun).
    private static Task<LeaveRequest> SubmitThursdayAsync(LeaveBatch1World world, LeaveService service)
        => service.SubmitRequestAsync(world.TenantId, new LeaveRequest
        {
            TenantId = world.TenantId, EmployeeId = world.Emp.Id, LeaveTypeId = world.LeaveType.Id,
            StartDate = new DateOnly(2025, 6, 12), EndDate = new DateOnly(2025, 6, 12), DayType = "Full",
        }, world.EmpUserId);

    [Fact]
    public async Task Notice_IsCountedFromTheTenantsToday_NotTheUtcDate()
    {
        var (world, service) = await NoticeWorldAsync("America/New_York");

        var submitted = await SubmitThursdayAsync(world, service);

        submitted.Status.Should().StartWith("Pending", "in New York it is still 9 June, so the Thursday is three days away");
    }

    [Fact]
    public async Task Notice_ForATenantWithNoZoneStored_StillUsesUtc()
    {
        var (world, service) = await NoticeWorldAsync(null);

        var submit = () => SubmitThursdayAsync(world, service);

        (await submit.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*3 day(s) advance notice*", "no stored zone means UTC, where the Thursday is two days away");
    }

    private static async Task<int> InsightsFor(string? zone)
    {
        var world = await LeaveBatch1World.CreateAsync();
        if (zone is not null) await SetTenantZoneAsync(world.Db, world.TenantId, zone, "SA");
        world.LeaveType.Category = "Sick";
        world.LeaveType.NameEn = "Sick Leave";
        // Three Mondays before today, and a Friday (13 Jun) that only the Riyadh calendar has reached.
        foreach (var day in new[] { new DateOnly(2025, 5, 12), new DateOnly(2025, 5, 19), new DateOnly(2025, 5, 26), new DateOnly(2025, 6, 13) })
            await world.SeedAsync("Approved", day, day);

        await new LeaveService(world.Db, new ApprovalRouter(world.Db), clock: new FixedClock(LateThursdayUtc))
            .GenerateInsightsAsync(world.TenantId);

        return await world.Db.LeaveAIInsights.CountAsync(i => i.TenantId == world.TenantId && i.InsightType == "AbsencePattern");
    }

    [Fact]
    public async Task AbsencePatternInsight_IncludesALeaveThatStartsOnTheTenantsToday()
        => (await InsightsFor("Asia/Riyadh")).Should().Be(1, "the fourth Monday/Friday leave starts on the Riyadh today, so it is inside the window");

    [Fact]
    public async Task AbsencePatternInsight_ForATenantWithNoZoneStored_StopsAtTheUtcDate()
        => (await InsightsFor(null)).Should().Be(0, "in UTC it is still Thursday, so the Friday leave has not started");

    [Fact]
    public async Task StatutoryFigure_IsTheOneInForceOnTheTenantsToday()
    {
        await using var db = new Zayra.Api.Data.ZayraDbContext(new DbContextOptionsBuilder<Zayra.Api.Data.ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var tenantId = Guid.NewGuid();
        var company = new Company { TenantId = tenantId, LegalNameEn = "KSA Co", CountryCode = "SA" };
        db.Companies.Add(company);
        var employee = new Employee { TenantId = tenantId, EmployeeCode = "S1", FullName = "S", Status = "Active", CompanyId = company.Id };
        db.Employees.Add(employee);
        var maternity = new LeaveType { TenantId = tenantId, Code = "MATERNITY", NameEn = "Maternity Leave", Category = "Maternity", IsPaid = true, IsActive = true };
        db.LeaveTypes.Add(maternity);
        await db.SaveChangesAsync();
        await SetTenantZoneAsync(db, tenantId, "America/New_York", "US");

        // 19 Feb 2025, 02:00 UTC is still 18 Feb in New York: the 12-week figure (84) is not in force on the tenant's today.
        var utcAfterTheAmendmentStarted = new DateTime(2025, 2, 19, 2, 0, 0, DateTimeKind.Utc);
        var figures = await new LeaveService(db, new ApprovalRouter(db), clock: new FixedClock(utcAfterTheAmendmentStarted))
            .GetKsaStatutoryEntitlementsAsync(tenantId, new[] { (employee.Id, maternity.Id) });

        figures[(employee.Id, maternity.Id)].Should().Be(70m);
    }

    [Fact]
    public async Task MockUnsupportedReport_DefaultsToTheTenantsToday()
    {
        var w = await SelfieWorld.CreateAsync();
        // A zone on the other side of the date line from UTC right now, so the two readings of "today" differ.
        var zoneId = DateTime.UtcNow.Hour >= 10 ? "Pacific/Kiritimati" : "Pacific/Pago_Pago";
        await SetTenantZoneAsync(w.Db, w.TenantId, zoneId, "US");
        var hr = w.Attendance(await w.RoleAsync("HR Manager", w.Caller, w.CallerUserId));

        var report = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await hr.GeofenceMockUnsupportedReport(null, null, default)).Value);

        var tenantToday = TenantTimeZone.LocalDate(TenantTimeZone.FromId(zoneId), DateTime.UtcNow);
        DateOnly.Parse(report.GetProperty("to").GetString()!).Should().Be(tenantToday);
        DateOnly.Parse(report.GetProperty("from").GetString()!).Should().Be(tenantToday.AddDays(-30));
    }
}
