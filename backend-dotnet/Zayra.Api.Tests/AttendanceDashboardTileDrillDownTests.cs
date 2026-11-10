using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The Live Dashboard KPI tiles are filters: the number on a tile and the list shown after clicking it must be the same
/// set of records. Both now come from <c>AttendanceTilePredicates</c> (one definition) over the same date and the same
/// active-employee restriction, so they cannot drift again.
/// </summary>
public class AttendanceDashboardTileDrillDownTests
{
    private static readonly Guid CompanyA = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 8, 18);

    private sealed class Seed
    {
        public Guid TenantId = Guid.NewGuid();
        public ZayraDbContext Db = null!;
    }

    private static Seed NewSeed() => new() { Db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options) };

    private static Employee Emp(Seed s, string status = EmployeeStatuses.Active, bool deleted = false)
    {
        var e = new Employee
        {
            TenantId = s.TenantId, CompanyId = CompanyA, EmployeeCode = $"TD-{Guid.NewGuid():N}"[..16], FullName = $"Tile Person {Guid.NewGuid():N}"[..20],
            Status = status, IsDeleted = deleted, JoiningDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        s.Db.Employees.Add(e);
        s.Db.SaveChanges();
        return e;
    }

    private static AttendanceDailyRecord Rec(Seed s, Employee e, string status, int late = 0, bool missing = false, DateOnly? day = null)
    {
        var r = new AttendanceDailyRecord
        {
            TenantId = s.TenantId, EmployeeId = e.Id, EmployeeName = e.FullName, WorkDate = day ?? Day, Status = status,
            LateMinutes = late, MissingPunch = missing,
        };
        s.Db.AttendanceDailyRecords.Add(r);
        return r;
    }

    private static AttendanceService Service(ZayraDbContext db) => new(db, new NullNotifications(), new NullHttpClients());

    private static Task<Zayra.Api.Application.Common.PagedResult<AttendanceDailyDto>> Drill(Seed s, string tile, int page = 1, int pageSize = 50, IReadOnlyCollection<int>? scope = null) =>
        Service(s.Db).GetDailyAsync(s.TenantId, Day, Day, null, null, page, pageSize, default, scope, tile);

    private static async Task<AttendanceDashboardDto> Counts(Seed s) => await Service(s.Db).DashboardAsync(s.TenantId, Day, default);

    private static async Task<Seed> MixedDay()
    {
        var s = NewSeed();
        Rec(s, Emp(s), "Present");
        Rec(s, Emp(s), "Present", late: 12);            // on time by status, late minutes recorded
        Rec(s, Emp(s), "Late", late: 25);
        Rec(s, Emp(s), "Half day");
        Rec(s, Emp(s), "Absent");
        Rec(s, Emp(s), "Absent");
        Rec(s, Emp(s), "Present", missing: true);
        Rec(s, Emp(s), "On leave");
        // An inactive employee's records must not appear in the tile or the list.
        Rec(s, Emp(s, EmployeeStatuses.Terminated), "Present", late: 5, missing: true);
        Rec(s, Emp(s, EmployeeStatuses.Terminated), "Absent");
        Rec(s, Emp(s, deleted: true), "Present");
        await s.Db.SaveChangesAsync();
        return s;
    }

    [Fact]
    public async Task PresentTile_DrillDown_IncludesLateAndHalfDay_AndMatchesTheTileCount()
    {
        var s = await MixedDay();
        var page = await Drill(s, "present");
        page.Items.Select(x => x.Status).Should().BeEquivalentTo(["Present", "Present", "Late", "Half day", "Present"]);
        page.Total.Should().Be((await Counts(s)).Present);
        page.Total.Should().Be(5);
    }

    [Fact]
    public async Task LateTile_DrillDown_IsEveryRecordWithLateMinutes_IncludingAPresentStatusOne()
    {
        var s = await MixedDay();
        var page = await Drill(s, "late");
        page.Items.Should().OnlyContain(x => x.LateMinutes > 0);
        page.Items.Select(x => x.Status).Should().BeEquivalentTo(["Present", "Late"]);
        page.Total.Should().Be((await Counts(s)).Late).And.Be(2);
    }

    [Fact]
    public async Task AbsentTile_DrillDown_MatchesTheTileCount()
    {
        var s = await MixedDay();
        var page = await Drill(s, "absent");
        page.Items.Should().OnlyContain(x => x.Status == "Absent");
        page.Total.Should().Be((await Counts(s)).Absent).And.Be(2);
    }

    [Fact]
    public async Task MissingPunchTile_DrillDown_FindsRecordsBeyondTheFirstPageOfFifty()
    {
        var s = NewSeed();
        // 60 ordinary days sort before (alphabetical employee name) the 3 missing-punch ones, so they are NOT on page 1 of the unfiltered list.
        for (var i = 0; i < 60; i++) { var e = Emp(s); e.FullName = $"A{i:000} Regular"; Rec(s, e, "Present"); }
        for (var i = 0; i < 3; i++) { var e = Emp(s); e.FullName = $"Z{i:000} Missed"; Rec(s, e, "Present", missing: true); }
        await s.Db.SaveChangesAsync();

        var unfiltered = await Service(s.Db).GetDailyAsync(s.TenantId, Day, Day, null, null, 1, 50, default);
        unfiltered.Items.Should().NotContain(x => x.MissingPunch, "premise: the missing punches are past the first page");

        var page = await Drill(s, "missingPunch", 1, 50);
        page.Total.Should().Be(3).And.Be((await Counts(s)).MissingPunch);
        page.Items.Should().HaveCount(3).And.OnlyContain(x => x.MissingPunch);
    }

    [Fact]
    public async Task EveryTilesDrillDownTotal_EqualsTheDashboardCount()
    {
        var s = await MixedDay();
        var c = await Counts(s);
        (await Drill(s, "present")).Total.Should().Be(c.Present);
        (await Drill(s, "absent")).Total.Should().Be(c.Absent);
        (await Drill(s, "late")).Total.Should().Be(c.Late);
        (await Drill(s, "missingPunch")).Total.Should().Be(c.MissingPunch);
    }

    [Fact]
    public async Task InactiveAndDeletedEmployees_AreNeverInATileDrillDown()
    {
        var s = await MixedDay();
        foreach (var tile in new[] { "present", "absent", "late", "missingPunch" })
        {
            var page = await Drill(s, tile);
            var ids = page.Items.Select(x => x.EmployeeId).ToList();
            var active = await s.Db.Employees.Where(e => e.Status == EmployeeStatuses.Active && !e.IsDeleted).Select(e => e.Id).ToListAsync();
            ids.Should().OnlyContain(id => active.Contains(id), $"the {tile} tile counts active employees only");
        }
    }

    [Fact]
    public async Task ASoftDeletedRecord_IsNeitherCountedNorListed()
    {
        var s = NewSeed();
        var e = Emp(s);
        Rec(s, e, "Absent").IsDeleted = true;
        Rec(s, Emp(s), "Absent");
        await s.Db.SaveChangesAsync();
        (await Counts(s)).Absent.Should().Be(1);
        (await Drill(s, "absent")).Total.Should().Be(1);
    }

    [Fact]
    public async Task TileName_IsCaseInsensitive()
    {
        var s = await MixedDay();
        (await Drill(s, "MISSINGPUNCH")).Total.Should().Be((await Drill(s, "missingPunch")).Total);
        (await Drill(s, "Late")).Total.Should().Be(2);
    }

    [Fact]
    public async Task AnUnknownTile_IsRefused_NotIgnored()
    {
        var s = await MixedDay();
        var act = () => Drill(s, "everyone");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task TileDrillDown_StaysInsideTheCallersScope()
    {
        var s = NewSeed();
        var mine = Emp(s); var theirs = Emp(s);
        Rec(s, mine, "Absent"); Rec(s, theirs, "Absent");
        await s.Db.SaveChangesAsync();
        var page = await Drill(s, "absent", scope: [mine.Id]);
        page.Items.Select(x => x.EmployeeId).Should().Equal(mine.Id);
    }

    [Fact]
    public async Task TheStatusParameter_KeepsItsExactMatchBehaviour()
    {
        var s = await MixedDay();
        // Exact match, no active-employee restriction, "Present" does not include Late/Half day.
        var present = await Service(s.Db).GetDailyAsync(s.TenantId, Day, Day, null, "Present", 1, 50, default);
        present.Items.Should().OnlyContain(x => x.Status == "Present");
        present.Total.Should().Be(5, "3 active + 1 terminated + 1 deleted-employee record with status Present, exactly as before");
        var late = await Service(s.Db).GetDailyAsync(s.TenantId, Day, Day, null, "Late", 1, 50, default);
        late.Total.Should().Be(1);
    }

    private sealed class NullNotifications : INotificationService
    {
        public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullHttpClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
