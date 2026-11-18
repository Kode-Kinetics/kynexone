using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Models;
using static Zayra.Api.Tests.OvertimeFixKit;

namespace Zayra.Api.Tests;

/// <summary>
/// One outcome per piece of overtime work: cash through payroll OR a compensatory day off, never both. The Overtime module's own
/// conversion already withdraws unpaid cash and refuses paid cash (OvertimeCompOffDoublePayTests). The leave module's comp-off door
/// (api/leave/compoff) had no link to overtime at all, so a day already on an overtime request could also be credited as a day
/// off, and a day already credited could also be filed as overtime. Both directions are now refused, in English and Arabic.
/// </summary>
public class OvertimeCashOrCompOffTests
{
    private static readonly DateOnly Day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);
    private static DateTime At(int hour) => Day.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Utc);

    private static Task<ActionResult<OvertimeRequest>> FileAsync(World w, int fromHour, int toHour) =>
        Controller(w, Guid.NewGuid(), new[] { "HR Manager" }, new[] { "overtime.write", "overtime.read", "overtime.approve" })
            .CreateRequest(new OvertimeRequestCreate(w.Subject.Id, null, null, Day, At(fromHour), At(toHour), "Manual", "Stayed late"), CancellationToken.None);

    private static JsonElement Body(IConvertToActionResult result) =>
        JsonSerializer.SerializeToElement(result.Convert().Should().BeAssignableTo<ObjectResult>().Subject.Value);

    private static CompOffController CompOff(World w, string role = "HR Manager")
    {
        var claims = new List<Claim>
        {
            new("tenant_id", w.TenantId.ToString()), new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, role), new("permission", "leave.write"), new("permission", "leave.approve"),
        };
        return new CompOffController(w.Db, new OrgScope())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) } },
        };
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }

    private static Task<IActionResult> CreditAsync(World w, string workType = "Overtime") =>
        CompOff(w).Create(new CreateCompOffRequest(w.Subject.Id, Day, workType, 2m, 1m, null), CancellationToken.None);

    [Theory]
    [InlineData("PendingManager", "Overtime")]
    [InlineData("Approved", "Overtime")]
    [InlineData("Approved", "HolidayWork")]
    public async Task ACompOffCredit_ForADayWithLiveOvertime_IsRefused409_InEnglishAndArabic(string otStatus, string workType)
    {
        var w = await SeedAsync();
        await AddRequestAsync(w, status: otStatus, minutes: 120, workDate: Day, approvedMinutes: otStatus == "Approved" ? 120 : 0);

        var result = await CreditAsync(w, workType);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        var body = JsonSerializer.SerializeToElement(conflict.Value);
        body.GetProperty("code").GetString().Should().Be("overtime_already_recorded_for_date");
        body.GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
        (await w.Db.CompOffCredits.CountAsync()).Should().Be(0, "the same work cannot be both cash and a day off");
    }

    [Fact]
    public async Task ACompOffCredit_ForADayWhoseOvertimeWasRejected_IsAllowed()
    {
        var w = await SeedAsync();
        await AddRequestAsync(w, status: "Rejected", minutes: 120, workDate: Day);

        (await CreditAsync(w)).Should().BeOfType<CreatedResult>();
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Approved")]
    public async Task OvertimeForADayAlreadyCreditedAsCompOff_IsRefused409(string creditStatus)
    {
        var w = await SeedAsync();
        w.Db.CompOffCredits.Add(new CompOffCredit
        {
            TenantId = w.TenantId, EmployeeId = w.Subject.Id, EmployeeName = w.Subject.FullName, WorkedDate = Day,
            WorkType = "RestDayWork", HoursWorked = 8m, DaysEarned = 1m, Status = creditStatus,
        });
        await w.Db.SaveChangesAsync();

        var manual = await FileAsync(w, 17, 19);

        manual.Result.Should().BeOfType<ConflictObjectResult>();
        var body = Body(manual);
        body.GetProperty("code").GetString().Should().Be("overtime_day_already_compensated");
        body.GetProperty("messageAr").GetString().Should().NotBeNullOrWhiteSpace();
        (await w.Db.OvertimeRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ACancelledCompOffCredit_DoesNotBlockOvertime()
    {
        var w = await SeedAsync();
        w.Db.CompOffCredits.Add(new CompOffCredit
        {
            TenantId = w.TenantId, EmployeeId = w.Subject.Id, EmployeeName = w.Subject.FullName, WorkedDate = Day,
            WorkType = "RestDayWork", HoursWorked = 8m, DaysEarned = 1m, Status = "Cancelled",
        });
        await w.Db.SaveChangesAsync();

        (await FileAsync(w, 17, 19)).Result.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task ApprovingAnAttendanceRestDayCredit_ForADayWithLiveOvertime_IsRefused409()
    {
        var w = await SeedAsync();
        await AddRequestAsync(w, status: "Approved", minutes: 120, workDate: Day, approvedMinutes: 120);
        var credit = new CompOffCredit
        {
            TenantId = w.TenantId, EmployeeId = w.Subject.Id, EmployeeName = w.Subject.FullName, WorkedDate = Day,
            WorkType = "RestDayWork", HoursWorked = 8m, DaysEarned = 1m, Status = "Pending",
        };
        w.Db.CompOffCredits.Add(credit);
        await w.Db.SaveChangesAsync();

        var result = await CompOff(w, "Manager").Approve(credit.Id, new CompOffApproveRequest("ok"), CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        (await w.Db.CompOffCredits.AsNoTracking().SingleAsync(x => x.Id == credit.Id)).Status.Should().Be("Pending");
    }
}
