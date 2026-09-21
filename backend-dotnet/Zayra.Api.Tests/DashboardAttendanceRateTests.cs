using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// THE ATTENDANCE RATE = days attended ÷ days the employee was ROSTERED TO WORK.
///
/// <para>It used to be <c>Count(Status == "Present") ÷ Count(*)</c>, which is wrong at both ends. A
/// rest day, an approved leave day and a public holiday went into the denominator, so the calendar
/// counted against the employee as though they had failed to turn up on days nobody asked them to.
/// And a <c>"Late"</c> day — on which they DID turn up, and for which they are already docked through
/// the short-hours deduction — was excluded from the numerator, charging them a second time.</para>
///
/// <para>The only existing test of the rate seeds nothing but <c>"Present"</c> and <c>"Absent"</c>, so
/// the defect was invisible to it. These tests seed the vocabulary the processor actually writes.</para>
/// </summary>
public class DashboardAttendanceRateTests
{
    private static ZayraDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static DashboardController Controller(ZayraDbContext db, Guid tenantId)
        => new(db, new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
               new RateTestScopeService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("tenant_id", tenantId.ToString())], "Test")),
                },
            },
        };

    /// <summary>Adds <paramref name="count"/> rows with that status, one per synthetic employee id.</summary>
    private static int Seed(ZayraDbContext db, Guid tenantId, DateOnly day, string status, int count, int nextId)
    {
        for (var i = 0; i < count; i++)
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                Id = nextId++, TenantId = tenantId, EmployeeId = nextId, WorkDate = day, Status = status,
            });
        return nextId;
    }

    /// <summary>
    /// EVOSTEL'S LIVE SHAPE, to the tenth of a percent.
    ///
    /// <para>226 attendance rows: 158 Present, 24 Late, 27 Absent, 9 on leave, 8 rest days.</para>
    /// <list type="bullet">
    ///   <item><b>Was:</b> 158 ÷ 226 = 69.911…% → the <b>69.9%</b> on Evostel's dashboard.</item>
    ///   <item><b>Is:</b> (158 + 24) ÷ (226 − 8 − 9) = 182 ÷ 209 = 87.081…% → <b>87.1%</b>.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task RestDaysAndApprovedLeaveAreNotAbsence_AndALateDayIsAttendance()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Evostel LLC", Slug = $"evostel-{tenantId:N}" });

        var id = 1;
        id = Seed(db, tenantId, today, AttendanceStatuses.Present, 158, id);
        id = Seed(db, tenantId, today, AttendanceStatuses.Late, 24, id);
        id = Seed(db, tenantId, today, AttendanceStatuses.Absent, 27, id);
        id = Seed(db, tenantId, today, AttendanceStatuses.LeaveLegacy, 9, id);
        Seed(db, tenantId, today, AttendanceStatuses.RestDay, 8, id);
        await db.SaveChangesAsync();

        var result = await Controller(db, tenantId).Trends(1, CancellationToken.None);
        var trends = (List<DashboardTrendDto>)((OkObjectResult)result).Value!;

        trends.Should().ContainSingle().Which.AttendanceRate.Should().Be(87.1m,
            "182 attended of 209 rostered days; the old rate divided 158 Present by all 226 rows and " +
            "reported 69.9% by counting 8 rest days and 9 leave days as failures to attend");
    }

    /// <summary>
    /// Both live spellings of a leave day must leave the denominator. The processor writes
    /// <c>"On leave"</c>; older seeded rows in the same production database carry <c>"Leave"</c>.
    /// </summary>
    [Theory]
    [InlineData(AttendanceStatuses.OnLeave)]
    [InlineData(AttendanceStatuses.LeaveLegacy)]
    [InlineData(AttendanceStatuses.OnLeaveTitle)]
    [InlineData(AttendanceStatuses.RestDay)]
    [InlineData(AttendanceStatuses.PublicHoliday)]
    public async Task ANonWorkingDayNeverDepressesTheRate(string nonWorkingStatus)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = $"t-{tenantId:N}" });

        var id = Seed(db, tenantId, today, AttendanceStatuses.Present, 4, 1);
        Seed(db, tenantId, today, nonWorkingStatus, 6, id);
        await db.SaveChangesAsync();

        var result = await Controller(db, tenantId).Trends(1, CancellationToken.None);
        var trends = (List<DashboardTrendDto>)((OkObjectResult)result).Value!;

        trends.Single().AttendanceRate.Should().Be(100m,
            $"4 of 4 rostered days were attended; the 6 '{nonWorkingStatus}' rows were never owed");
    }

    /// <summary>
    /// A month containing nothing but non-working days has no rate. Reporting 0% would assert that
    /// nobody turned up on days nobody was rostered for.
    /// </summary>
    [Fact]
    public async Task AMonthOfOnlyRestDaysReportsZeroRatherThanDividingByZero()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = $"t-{tenantId:N}" });
        Seed(db, tenantId, today, AttendanceStatuses.RestDay, 5, 1);
        await db.SaveChangesAsync();

        var result = await Controller(db, tenantId).Trends(1, CancellationToken.None);
        var trends = (List<DashboardTrendDto>)((OkObjectResult)result).Value!;

        trends.Single().AttendanceRate.Should().Be(0m);
    }

    /// <summary>
    /// The on-leave tile counted only <c>"Leave"</c> and <c>"On Leave"</c>, while the processor writes
    /// <c>"On leave"</c>. The tile was therefore structurally ZERO on every processed tenant, and the
    /// one test covering it seeded the title-case spelling by hand and passed.
    /// </summary>
    [Fact]
    public async Task OnLeaveTile_CountsTheSpellingTheProcessorActuallyWrites()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = $"t-{tenantId:N}" });
        db.Employees.Add(new Employee { Id = 1, TenantId = tenantId, EmployeeCode = "E1", FullName = "A", Status = "Active" });
        db.AttendanceRecords.Add(new AttendanceRecord
        {
            Id = 1, TenantId = tenantId, EmployeeId = 1, WorkDate = today, Status = AttendanceStatuses.OnLeave,
        });
        await db.SaveChangesAsync();

        var result = await Controller(db, tenantId).Summary(CancellationToken.None);
        var summary = (DashboardSummaryDto)((OkObjectResult)result).Value!;

        summary.OnLeave.Should().Be(1, "the processor writes \"On leave\" with a lower-case L");
    }
}

file sealed class RateTestScopeService : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization, AllowedEmployeeIds = null });
}
