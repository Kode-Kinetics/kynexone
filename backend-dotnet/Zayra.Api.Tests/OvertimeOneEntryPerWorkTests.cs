using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Models;
using static Zayra.Api.Tests.OvertimeFixKit;

namespace Zayra.Api.Tests;

/// <summary>
/// The same worked hours are recorded once and have one outcome. Every door that records overtime (the manual request,
/// detect-from-attendance, the leave module's manual comp-off credit) asks one shared rule (OvertimeEntryGuard).
///
/// <para>Before: detect-from-attendance only skipped a day that already had an attendance-sourced request, so hours keyed
/// by hand and then detected from the punches became two requests that could both be approved and paid; the manual door
/// treated a Cancelled request as live; and the comp-off door checked nothing, so a day already paid as overtime could
/// also be credited as a day off.</para>
/// </summary>
public class OvertimeOneEntryPerWorkTests
{
    private static readonly string[] Hr = { "HR Manager" };
    private static readonly string[] HrWrite = { "overtime.write", "overtime.read", "overtime.approve" };
    private static readonly DateOnly Day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);

    private static DateTime At(int hour, int minute = 0) => Day.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Utc);

    private static OvertimeController HrController(World w) => Controller(w, Guid.NewGuid(), Hr, HrWrite);

    private static Task<ActionResult<OvertimeRequest>> FileAsync(World w, int fromHour, int toHour) =>
        HrController(w).CreateRequest(new OvertimeRequestCreate(w.Subject.Id, null, null, Day, At(fromHour), At(toHour), "Manual", "Stayed late"), CancellationToken.None);

    private static async Task AddAttendanceDayAsync(World w, int overtimeMinutes, int lastOutHour)
    {
        w.Db.AttendanceDailyRecords.Add(new AttendanceDailyRecord
        {
            TenantId = w.TenantId, EmployeeId = w.Subject.Id, EmployeeName = w.Subject.FullName, WorkDate = Day,
            FirstInUtc = At(8), LastOutUtc = At(lastOutHour), TotalWorkedMinutes = 480 + overtimeMinutes,
            OvertimeMinutes = overtimeMinutes, Status = "Present",
        });
        await w.Db.SaveChangesAsync();
    }

    private static async Task<DetectOvertimeResult> DetectAsync(World w)
    {
        var result = await HrController(w).DetectFromAttendance(new DetectOvertimeRequest(Day, Day, null), CancellationToken.None);
        return (result.Result.Should().BeOfType<OkObjectResult>().Subject.Value as DetectOvertimeResult)!;
    }

    private static JsonElement Body(IConvertToActionResult result)
    {
        var obj = result.Convert().Should().BeAssignableTo<ObjectResult>().Subject;
        return JsonSerializer.SerializeToElement(obj.Value);
    }

    // ── manual first, then detect ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ManualThenDetect_SameHours_DetectRaisesNoSecondRequest()
    {
        var w = await SeedAsync();
        (await FileAsync(w, 17, 19)).Result.Should().BeOfType<CreatedResult>();
        await AddAttendanceDayAsync(w, overtimeMinutes: 120, lastOutHour: 19);

        var detected = await DetectAsync(w);

        detected.Created.Should().BeEmpty("the hours are already on a live manual request; a second request would pay them twice");
        (await w.Db.OvertimeRequests.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ManualThenDetect_ManualWindowElsewhereInTheDay_DetectStillRaisesNothing()
    {
        // The attendance figure is the day's whole measured overtime; the window it is stamped with is only its tail.
        var w = await SeedAsync();
        (await FileAsync(w, 5, 7)).Result.Should().BeOfType<CreatedResult>();
        await AddAttendanceDayAsync(w, overtimeMinutes: 120, lastOutHour: 19);

        (await DetectAsync(w)).Created.Should().BeEmpty();
        (await w.Db.OvertimeRequests.CountAsync()).Should().Be(1);
    }

    // ── detect first, then manual ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DetectThenManual_OverlappingHours_AreRefused409_InEnglishAndArabic()
    {
        var w = await SeedAsync();
        await AddAttendanceDayAsync(w, overtimeMinutes: 120, lastOutHour: 19);
        (await DetectAsync(w)).Created.Should().ContainSingle();

        var manual = await FileAsync(w, 17, 19);

        manual.Result.Should().BeOfType<ConflictObjectResult>();
        var body = Body(manual);
        body.GetProperty("code").GetString().Should().Be("overtime_overlaps_existing");
        body.GetProperty("message").GetString().Should().Be("This overtime request overlaps an existing request.");
        body.GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
        (await w.Db.OvertimeRequests.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DetectThenManual_OtherWindowOnTheSameDay_IsRefused_BecauseAttendanceCoversTheWholeDay()
    {
        var w = await SeedAsync();
        await AddAttendanceDayAsync(w, overtimeMinutes: 120, lastOutHour: 19);
        (await DetectAsync(w)).Created.Should().ContainSingle();

        (await FileAsync(w, 5, 7)).Result.Should().BeOfType<ConflictObjectResult>();
        (await w.Db.OvertimeRequests.CountAsync()).Should().Be(1);
    }

    // ── closed entries never block ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Rejected")]
    [InlineData("Cancelled")]
    public async Task AClosedManualRequest_BlocksNeitherDetectNorANewManualRequest(string closed)
    {
        var w = await SeedAsync();
        var old = await AddRequestAsync(w, status: closed, minutes: 120, workDate: Day);
        old.StartTimeUtc.Should().Be(At(17), "premise: the closed request covers the same window");

        (await FileAsync(w, 17, 19)).Result.Should().BeOfType<CreatedResult>("a {0} request is not payable work", closed);

        var w2 = await SeedAsync();
        await AddRequestAsync(w2, status: closed, minutes: 120, workDate: Day);
        await AddAttendanceDayAsync(w2, overtimeMinutes: 120, lastOutHour: 19);
        (await DetectAsync(w2)).Created.Should().ContainSingle();
    }

    [Fact]
    public async Task ARejectedAttendanceRequest_DoesNotBlockAManualOne_AndIsNotReRaisedByDetect()
    {
        var w = await SeedAsync();
        await AddAttendanceDayAsync(w, overtimeMinutes: 120, lastOutHour: 19);
        var detected = (await DetectAsync(w)).Created.Single();
        var row = await w.Db.OvertimeRequests.SingleAsync(x => x.Id == detected.Id);
        row.Status = "Rejected";
        await w.Db.SaveChangesAsync();

        (await DetectAsync(w)).Created.Should().BeEmpty("HR rejected the detected day; pressing Detect again must not raise it again");
        (await FileAsync(w, 17, 19)).Result.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task TwoManualRequests_InDifferentWindowsOfOneDay_AreStillAllowed()
    {
        var w = await SeedAsync();
        (await FileAsync(w, 5, 7)).Result.Should().BeOfType<CreatedResult>();
        (await FileAsync(w, 17, 19)).Result.Should().BeOfType<CreatedResult>();
    }
}
