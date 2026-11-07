using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Models;
using static Zayra.Api.Tests.BulkKit;

namespace Zayra.Api.Tests;

/// <summary>
/// B3 — mass entry: the same In (+ Out) for a SELECTION of employees over a date range, through the same judging and idempotent save
/// as the CSV import. An explicit selection is a deliberate act, unlike a file: one out-of-scope id refuses the WHOLE request; the
/// caller's own id, a locked day or a future day is refused per item; the reason is mandatory and is kept with every punch and the audit.
/// </summary>
public sealed class AttendanceBulkEntryTests
{
    private static BulkAttendanceEntryRequest Entry(int[] ids, DateOnly from, DateOnly to, string? inTime = "08:00", string? outTime = "17:00",
        string? reason = "Biometric device down on site", string? method = null, string? location = null) =>
        new(ids, from, to, inTime, outTime, reason, method, location);

    // ── creates N × days × 2 ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatesAnInAndAnOutPerEmployeePerDay_InTenantTime_WithSourceReasonAndMethod()
    {
        var k = await CreateAsync();
        var request = Entry(new[] { k.Report.Id, k.Stranger.Id }, Wednesday, Wednesday.AddDays(2), location: "Site B");

        var result = await k.Service().BulkEntryAsync(k.TenantId, request, k.Authority(), k.Context(), default);

        (result.Requested, result.Created, result.Duplicates, result.Failed).Should().Be((12, 12, 0, 0));
        result.Errors.Should().BeEmpty();
        var events = k.Events();
        events.Should().HaveCount(2 * 3 * 2, "employees x days x (in + out)");
        events.Count(e => e.PunchDirection == "In").Should().Be(6);
        var first = events.First(e => e.EmployeeId == k.Report.Id && e.PunchDirection == "In");
        first.PunchTimestampUtc.Should().Be(new DateTime(2026, 6, 10, 5, 0, 0, DateTimeKind.Utc), "08:00 Riyadh");
        events.First(e => e.EmployeeId == k.Report.Id && e.PunchDirection == "Out").PunchTimestampUtc
            .Should().Be(new DateTime(2026, 6, 10, 14, 0, 0, DateTimeKind.Utc), "17:00 Riyadh");
        events.Should().OnlyContain(e => e.Source == "Manual (bulk)" && e.VerificationMethod == "Manual" && e.LocationName == "Site B" && e.CreatedBy == k.UserId);
        events.Should().OnlyContain(e => e.RawPayloadJson.Contains("Biometric device down on site"));
        events.Select(e => e.SyncBatchReference).Distinct().Should().ContainSingle().Which.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task WithNoOutTime_OnlyTheInPunchIsCreated()
    {
        var k = await CreateAsync();

        var result = await k.Service().BulkEntryAsync(k.TenantId, Entry(new[] { k.Report.Id }, Wednesday, Wednesday, outTime: null), k.Authority(), k.Context(), default);

        (result.Requested, result.Created).Should().Be((1, 1));
        k.Events().Should().ContainSingle().Which.PunchDirection.Should().Be("In");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task WithNoInTime_OnlyTheOutPunchIsCreated_OnThatDay(string? inTime)
    {
        // The everyday single-person fix: the employee forgot to punch out.
        var k = await CreateAsync();

        var result = await k.Service().BulkEntryAsync(k.TenantId, Entry(new[] { k.Report.Id }, Wednesday, Wednesday, inTime: inTime, outTime: "17:00"), k.Authority(), k.Context(), default);

        (result.Requested, result.Created, result.Failed).Should().Be((1, 1, 0));
        var only = k.Events().Should().ContainSingle().Which;
        only.PunchDirection.Should().Be("Out");
        only.PunchTimestampUtc.Should().Be(new DateTime(2026, 6, 10, 14, 0, 0, DateTimeKind.Utc), "17:00 Riyadh on the same day: with no in time there is no overnight to infer");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(" ", " ")]
    public async Task NeitherAnInNorAnOutTime_IsA400_AndNothingIsWritten(string? inTime, string? outTime)
    {
        var k = await CreateAsync();

        var result = await k.Controller().BulkEntry(Entry(new[] { k.Report.Id }, Wednesday, Wednesday, inTime, outTime), default);

        JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(result.Result).Value).Should().Contain("in time or an out time");
        (await k.Db.AttendanceRawEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AnOutTimeBeforeTheInTime_IsTheNextDay_AnOvernightShift()
    {
        var k = await CreateAsync();

        await k.Service().BulkEntryAsync(k.TenantId, Entry(new[] { k.Report.Id }, Wednesday, Wednesday, inTime: "22:00", outTime: "06:00"), k.Authority(), k.Context(), default);

        var report = k.Events().Where(e => e.EmployeeId == k.Report.Id).ToList();
        report.Single(e => e.PunchDirection == "In").PunchTimestampUtc.Should().Be(new DateTime(2026, 6, 10, 19, 0, 0, DateTimeKind.Utc));
        report.Single(e => e.PunchDirection == "Out").PunchTimestampUtc.Should().Be(new DateTime(2026, 6, 11, 3, 0, 0, DateTimeKind.Utc), "06:00 the next morning, Riyadh");
    }

    [Fact]
    public async Task AnOutTimeEqualToTheInTime_IsA400_NotAGuess()
    {
        var k = await CreateAsync();

        var result = await k.Controller().BulkEntry(Entry(new[] { k.Report.Id }, Wednesday, Wednesday, inTime: "08:00", outTime: "08:00"), default);

        JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(result.Result).Value).Should().Contain("same");
        (await k.Db.AttendanceRawEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TheSameIdListWithRepeats_IsCountedOnce()
    {
        var k = await CreateAsync();

        var result = await k.Service().BulkEntryAsync(k.TenantId, Entry(new[] { k.Report.Id, k.Report.Id, k.Report.Id }, Wednesday, Wednesday), k.Authority(), k.Context(), default);

        (result.Requested, result.Created).Should().Be((2, 2));
    }

    // ── I7 idempotent ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASecondSend_CreatesNothing_AndReportsTheDuplicates()
    {
        var k = await CreateAsync();
        var request = Entry(new[] { k.Report.Id, k.Stranger.Id }, Wednesday, Wednesday.AddDays(1));

        var first = await k.Service().BulkEntryAsync(k.TenantId, request, k.Authority(), k.Context(), default);
        var second = await k.Service().BulkEntryAsync(k.TenantId, request, k.Authority(), k.Context(), default);

        first.Created.Should().Be(8);
        (second.Requested, second.Created, second.Duplicates, second.Failed).Should().Be((8, 0, 8, 0));
        second.Errors.Should().BeEmpty("a duplicate is counted, not an error");
        (await k.Db.AttendanceRawEvents.CountAsync()).Should().Be(8);
    }

    [Fact]
    public async Task APartiallyEnteredRange_IsCompletedWithoutDuplicatingWhatWasThere()
    {
        var k = await CreateAsync();
        await k.Service().BulkEntryAsync(k.TenantId, Entry(new[] { k.Report.Id }, Wednesday, Wednesday), k.Authority(), k.Context(), default);

        var result = await k.Service().BulkEntryAsync(k.TenantId, Entry(new[] { k.Report.Id }, Wednesday, Wednesday.AddDays(1)), k.Authority(), k.Context(), default);

        (result.Requested, result.Created, result.Duplicates).Should().Be((4, 2, 2));
    }

    // ── I1 / I2 / I3 ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheCallersOwnId_IsRefusedPerItem_NothingIsWrittenForIt_AndTheRestProceeds()
    {
        var k = await CreateAsync();

        var result = await k.Service().BulkEntryAsync(k.TenantId, Entry(new[] { k.Caller.Id, k.Report.Id }, Wednesday, Wednesday.AddDays(1)), k.Authority(), k.Context(), default);

        (result.Requested, result.Created, result.Failed).Should().Be((8, 4, 4));
        result.Errors.Should().HaveCount(2).And.OnlyContain(e => e.EmployeeCode == "E-CALLER" && e.Message.Contains("own"));
        result.Errors.Select(e => e.Date).Should().Equal(Wednesday, Wednesday.AddDays(1));
        k.Events().Should().NotContain(e => e.EmployeeId == k.Caller.Id);
    }

    [Fact]
    public async Task AnInactiveOrUnknownId_IsRefusedPerItem()
    {
        var k = await CreateAsync();

        var result = await k.Service().BulkEntryAsync(k.TenantId, Entry(new[] { k.Inactive.Id, 99999, k.Report.Id }, Wednesday, Wednesday), k.Authority(), k.Context(), default);

        (result.Requested, result.Created, result.Failed).Should().Be((6, 2, 4));
        result.Errors.Select(e => e.EmployeeCode).Should().BeEquivalentTo("#99999", "#" + k.Inactive.Id);
        k.Events().Should().OnlyContain(e => e.EmployeeId == k.Report.Id);
    }

    [Fact]
    public async Task TheController_OneOutOfScopeId_RefusesTheWholeRequest_AndNothingIsWritten()
    {
        var k = await CreateAsync();
        var c = k.Controller(allowed: new[] { k.Report.Id });

        var result = await c.BulkEntry(Entry(new[] { k.Report.Id, k.Stranger.Id }, Wednesday, Wednesday), default);

        result.Result.Should().BeOfType<ForbidResult>();
        (await k.Db.AttendanceRawEvents.CountAsync()).Should().Be(0);
        (await k.Db.AttendanceAuditLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TheController_ASelectionInScope_Works_AndTheCallersOwnIdIsRefusedPerItem()
    {
        var k = await CreateAsync();
        var c = k.Controller(allowed: new[] { k.Report.Id, k.Caller.Id });

        var result = await c.BulkEntry(Entry(new[] { k.Report.Id, k.Caller.Id }, Wednesday, Wednesday), default);

        var dto = result.Value.Should().BeOfType<BulkAttendanceEntryResult>().Subject;
        (dto.Created, dto.Failed).Should().Be((2, 2));
        k.Events().Should().OnlyContain(e => e.EmployeeId == k.Report.Id);
    }

    // ── I4 reserved labels ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Selfie")]
    [InlineData("Geofence")]
    [InlineData("Selfie+Geofence")]
    public async Task AReservedVerificationMethod_IsRefused_AndNothingIsWritten(string method)
    {
        var k = await CreateAsync();
        var c = k.Controller();

        var result = await c.BulkEntry(Entry(new[] { k.Report.Id }, Wednesday, Wednesday, method: method), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        (await k.Db.AttendanceRawEvents.CountAsync()).Should().Be(0);
    }

    // ── I5 payroll lock / I6 future ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ALockedPeriodDay_IsRefusedPerItem_AndTheOtherDaysAreEntered()
    {
        var k = await CreateAsync();
        k.Db.AttendanceLockPeriods.Add(new AttendanceLockPeriod { TenantId = k.TenantId, PeriodStart = Wednesday, PeriodEnd = Wednesday, Status = "Locked", LockType = "Payroll" });
        await k.Db.SaveChangesAsync();

        var result = await k.Service().BulkEntryAsync(k.TenantId, Entry(new[] { k.Report.Id }, Wednesday, Wednesday.AddDays(1)), k.Authority(), k.Context(), default);

        (result.Requested, result.Created, result.Failed).Should().Be((4, 2, 2));
        var error = result.Errors.Should().ContainSingle().Subject;
        (error.EmployeeCode, error.Date).Should().Be(("E-REPORT", Wednesday));
        error.Message.Should().Contain("locked");
        k.Events().Should().HaveCount(2).And.OnlyContain(e => e.PunchTimestampUtc >= new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task AFutureDay_IsRefusedPerItem_BothPunchesOrNeither()
    {
        var k = await CreateAsync();
        // Now is Saturday 2026-06-20 12:00 Riyadh. 08:00-17:00 today: the In is past, the Out is in the future -> the whole day is refused.
        var today = new DateOnly(2026, 6, 20);

        var result = await k.Service().BulkEntryAsync(k.TenantId, Entry(new[] { k.Report.Id }, today.AddDays(-1), today.AddDays(1)), k.Authority(), k.Context(), default);

        (result.Requested, result.Created, result.Failed).Should().Be((6, 2, 4));
        result.Errors.Select(e => e.Date).Should().Equal(today, today.AddDays(1));
        result.Errors.Should().OnlyContain(e => e.Message.Contains("future"));
        k.Events().Should().HaveCount(2).And.OnlyContain(e => e.PunchTimestampUtc < new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc));
    }

    // ── 400s ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AMissingReason_IsA400_AndNothingIsWritten(string? reason)
    {
        var k = await CreateAsync();
        var c = k.Controller();

        var result = await c.BulkEntry(Entry(new[] { k.Report.Id }, Wednesday, Wednesday, reason: reason), default);

        JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(result.Result).Value).Should().Contain("reason");
        (await k.Db.AttendanceRawEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AReasonOver500Characters_IsA400()
    {
        var k = await CreateAsync();

        var result = await k.Controller().BulkEntry(Entry(new[] { k.Report.Id }, Wednesday, Wednesday, reason: new string('x', 501)), default);
        var edge = await k.Controller().BulkEntry(Entry(new[] { k.Report.Id }, Wednesday, Wednesday, reason: new string('x', 500)), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        edge.Value.Should().BeOfType<BulkAttendanceEntryResult>();
    }

    [Fact]
    public async Task OverTheCaps_IsA400WithAPlainExplanation()
    {
        var k = await CreateAsync();
        var c = k.Controller();
        var many = Enumerable.Range(1, 501).ToArray();

        var tooManyEmployees = await c.BulkEntry(Entry(many, Wednesday, Wednesday), default);
        var tooManyDays = await c.BulkEntry(Entry(new[] { k.Report.Id }, Wednesday, Wednesday.AddDays(31)), default);
        var maxDays = await c.BulkEntry(Entry(new[] { k.Report.Id }, Wednesday, Wednesday.AddDays(30)), default);

        JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(tooManyEmployees.Result).Value).Should().Contain("500");
        JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(tooManyDays.Result).Value).Should().Contain("31");
        maxDays.Value.Should().BeOfType<BulkAttendanceEntryResult>("31 days is the cap itself and is accepted");
    }

    [Theory]
    [InlineData("25:00", "17:00")]
    [InlineData("8am", "17:00")]
    [InlineData("08:00", "five")]
    [InlineData(null, "five")]
    public async Task AnUnreadableTime_IsA400(string? inTime, string? outTime)
    {
        var k = await CreateAsync();

        var result = await k.Controller().BulkEntry(Entry(new[] { k.Report.Id }, Wednesday, Wednesday, inTime, outTime), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        (await k.Db.AttendanceRawEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task NoEmployees_OrABackwardsRange_IsA400()
    {
        var k = await CreateAsync();
        var c = k.Controller();

        var none = await c.BulkEntry(Entry(Array.Empty<int>(), Wednesday, Wednesday), default);
        var backwards = await c.BulkEntry(Entry(new[] { k.Report.Id }, Wednesday.AddDays(1), Wednesday), default);

        Assert.IsType<BadRequestObjectResult>(none.Result);
        Assert.IsType<BadRequestObjectResult>(backwards.Result);
    }

    // ── review hardening ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheTotalNumberOfPunchesIsCapped_At5000_WithTheMathsExplained()
    {
        var k = await CreateAsync();
        var c = k.Controller();
        var hundred = Enumerable.Range(1, 100).ToArray();

        var over = await c.BulkEntry(Entry(hundred, Wednesday, Wednesday.AddDays(25)), default);   // 100 x 26 x 2 = 5,200
        var atCap = await c.BulkEntry(Entry(hundred, Wednesday, Wednesday.AddDays(24)), default);  // 100 x 25 x 2 = 5,000

        var message = JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(over.Result).Value);
        message.Should().Contain("5,000").And.Contain("5,200").And.Contain("100 employees").And.Contain("26 days");
        atCap.Value.Should().BeOfType<BulkAttendanceEntryResult>().Which.Requested.Should().Be(5000);
    }

    [Fact]
    public async Task MissingOrDefaultDates_AreA400()
    {
        var k = await CreateAsync();
        var c = k.Controller();

        var noDates = await c.BulkEntry(new BulkAttendanceEntryRequest(new[] { k.Report.Id }, default, default, "08:00", "17:00", "Device down"), default);
        var noTo = await c.BulkEntry(new BulkAttendanceEntryRequest(new[] { k.Report.Id }, Wednesday, default, "08:00", "17:00", "Device down"), default);

        Assert.IsType<BadRequestObjectResult>(noDates.Result);
        Assert.IsType<BadRequestObjectResult>(noTo.Result);
        (await k.Db.AttendanceRawEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ARangeOlderThan400DaysOrBefore2000_IsA400()
    {
        var k = await CreateAsync();
        var c = k.Controller();

        var old = await c.BulkEntry(Entry(new[] { k.Report.Id }, new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 2)), default);       // 535 days back
        var ancient = await c.BulkEntry(Entry(new[] { k.Report.Id }, new DateOnly(1999, 12, 30), new DateOnly(1999, 12, 31)), default);
        var edge = await c.BulkEntry(Entry(new[] { k.Report.Id }, new DateOnly(2025, 6, 1), new DateOnly(2025, 6, 1)), default);        // 384 days back

        JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(old.Result).Value).Should().Contain("400 days");
        Assert.IsType<BadRequestObjectResult>(ancient.Result);
        edge.Value.Should().BeOfType<BulkAttendanceEntryResult>();
    }

    // ── I8 audit ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OneAuditRowPerRequest_WithTheReasonCountsAndCaller()
    {
        var k = await CreateAsync();

        await k.Service().BulkEntryAsync(k.TenantId, Entry(new[] { k.Report.Id, k.Stranger.Id }, Wednesday, Wednesday.AddDays(1)), k.Authority(), k.Context(), default);

        var audit = await k.Db.AttendanceAuditLogs.SingleAsync(a => a.Action == "attendance.bulk_entry.completed");
        audit.UserId.Should().Be(k.UserId);
        audit.MetadataJson.Should().Contain("Biometric device down on site").And.Contain("\"created\":8").And.Contain("\"employees\":2")
            .And.Contain($"\"employeeIds\":[{k.Report.Id},{k.Stranger.Id}]");
        audit.EntityId.Should().Be(k.Events().Select(e => e.SyncBatchReference).Distinct().Single());
        (await k.Db.AttendanceAuditLogs.CountAsync()).Should().Be(1, "one audit row per batch, not per punch");
    }
}
