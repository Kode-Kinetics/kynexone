using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Notifications;
using static Zayra.Api.Tests.AttendancePayrollPostgresKit;

namespace Zayra.Api.Tests;

/// <summary>
/// I7 under concurrency, on a real PostgreSQL. The web/CSV punches carry no device, and NULLs are distinct in the punch unique index, so
/// the index cannot arbitrate: the per-tenant advisory lock taken by the bulk writes does. Two (or more) identical mass entries or
/// imports sent at the same moment must leave each punch exactly once, with the duplicates counted rather than raised.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AttendanceBulkConcurrencyPostgresTests
{
    private readonly PostgresFixture _fx;
    public AttendanceBulkConcurrencyPostgresTests(PostgresFixture fx) => _fx = fx;

    private static readonly TimeProvider LaterThanTheDay = new FixedClock(new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));

    private AttendanceService Service(Zayra.Api.Data.ZayraDbContext db) => new(db, new NullNotifications(), new NullHttpClients(), LaterThanTheDay);

    [Fact]
    public async Task FourIdenticalMassEntries_AtOnce_CreateEachPunchOnce_AndCountTheRestAsDuplicates()
    {
        var k = await CreateAsync(_fx.CreateDb);
        var request = new BulkAttendanceEntryRequest(new[] { k.Employee.Id }, Day, Day.AddDays(2), "08:00", "17:00", "Device down on site");

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await using var db = _fx.CreateDb();
            return await Service(db).BulkEntryAsync(k.TenantId, request, AttendanceBulkAuthority.Unrestricted,
                new RequestContext("127.0.0.1", "test", null, k.TenantId), default);
        })));

        results.Sum(r => r.Created).Should().Be(6, "3 days x (in + out), once, however many requests raced");
        results.Sum(r => r.Duplicates).Should().Be(4 * 6 - 6);
        results.Sum(r => r.Failed).Should().Be(0);
        await using var check = _fx.CreateDb();
        (await check.AttendanceRawEvents.IgnoreQueryFilters().CountAsync(e => e.TenantId == k.TenantId)).Should().Be(6);
    }

    [Fact]
    public async Task FourIdenticalImports_AtOnce_StoreEachRowOnce()
    {
        var k = await CreateAsync(_fx.CreateDb);
        var csv = $"employeeCode,punchTimestamp,punchDirection\n{k.Employee.EmployeeCode},{Day:yyyy-MM-dd} 08:00,In\n{k.Employee.EmployeeCode},{Day:yyyy-MM-dd} 17:00,Out\n";

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await using var db = _fx.CreateDb();
            return await Service(db).ImportCsvAsync(k.TenantId, new ImportAttendanceRequest("same.csv", csv), AttendanceBulkAuthority.Unrestricted,
                new RequestContext("127.0.0.1", "test", null, k.TenantId), default);
        })));

        results.Sum(r => r.ImportedRows).Should().Be(2);
        results.Sum(r => r.DuplicateRows).Should().Be(4 * 2 - 2);
        results.Sum(r => r.FailedRows).Should().Be(0);
        await using var check = _fx.CreateDb();
        (await check.AttendanceRawEvents.IgnoreQueryFilters().CountAsync(e => e.TenantId == k.TenantId)).Should().Be(2);
    }

    [Fact]
    public async Task ABulkImportAndASinglePush_OfTheSameInstant_LeaveExactlyOnePunch()
    {
        var k = await CreateAsync(_fx.CreateDb);
        for (var round = 0; round < 6; round++)
        {
            var at = new DateTime(2026, 8, 10 + round, 5, 0, 0, DateTimeKind.Utc);
            var csv = $"employeeCode,punchTimestamp,punchDirection\n{k.Employee.EmployeeCode},{at:yyyy-MM-ddTHH:mm:ssZ},In\n";

            var bulk = Task.Run(async () =>
            {
                await using var db = _fx.CreateDb();
                await Service(db).ImportCsvAsync(k.TenantId, new ImportAttendanceRequest("race.csv", csv), AttendanceBulkAuthority.Unrestricted,
                    new RequestContext("127.0.0.1", "test", null, k.TenantId), default);
            });
            var single = Task.Run(async () =>
            {
                await using var db = _fx.CreateDb();
                try
                {
                    await Service(db).PushEventAsync(k.TenantId, new AttendanceRawEventRequest(k.Employee.Id, null, null, "API push", at, "In", null, null, null,
                        null, null, null, null, "API", null), new RequestContext("127.0.0.1", "test", null, k.TenantId), default);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("Duplicate")) { /* it lost the race: fine */ }
            });
            await Task.WhenAll(bulk, single);

            await using var check = _fx.CreateDb();
            (await check.AttendanceRawEvents.IgnoreQueryFilters().CountAsync(e => e.TenantId == k.TenantId && e.PunchTimestampUtc == at && e.PunchDirection == "In"))
                .Should().Be(1, $"round {round}: the shared tenant lock makes the single push and the bulk write take turns");
        }
    }

    [Fact]
    public async Task WhileAnotherBulkWriteHoldsTheTenantLock_TheNextBulkWriteAnswersBusy_AndASinglePunchWaitsForIt()
    {
        var k = await CreateAsync(_fx.CreateDb);
        await using var holder = _fx.CreateDb();
        await using var held = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({AttendanceService.BulkLockKey(k.TenantId)}, 0))");

        await using var bulkDb = _fx.CreateDb();
        var started = DateTime.UtcNow;
        var busy = async () => await Service(bulkDb).BulkEntryAsync(k.TenantId, new BulkAttendanceEntryRequest(new[] { k.Employee.Id }, Day, Day, "08:00", "17:00", "Device down"),
            AttendanceBulkAuthority.Unrestricted, new RequestContext("127.0.0.1", "test", null, k.TenantId), default);
        await busy.Should().ThrowAsync<AttendanceBusyException>();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(30), "the wait is bounded (about 5 seconds)");

        await using var pushDb = _fx.CreateDb();
        var push = Task.Run(() => Service(pushDb).PushEventAsync(k.TenantId, new AttendanceRawEventRequest(k.Employee.Id, null, null, "API push",
            new DateTime(2026, 8, 20, 5, 0, 0, DateTimeKind.Utc), "In", null, null, null, null, null, null, null, "API", null),
            new RequestContext("127.0.0.1", "test", null, k.TenantId), default));
        await Task.Delay(1500);
        push.IsCompleted.Should().BeFalse("a single punch takes the tenant bulk lock SHARED, so it waits for an exclusive holder");
        await held.CommitAsync();
        await push;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
