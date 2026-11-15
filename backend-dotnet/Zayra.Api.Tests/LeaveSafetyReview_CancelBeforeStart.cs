using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Review follow-up: CancelBeforeStart. See <see cref="LeaveSafetyReviewTests"/>.</summary>
public sealed partial class LeaveSafetyReviewTests
{
    // ── 5. Cancelling approved leave: the employee only before it starts ─────────────────────────

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);

    private static async Task<(LeaveBatch1World W, LeaveRequest Leave)> ApprovedAsync(int startOffsetDays)
    {
        var w = await LeaveBatch1World.CreateAsync();
        var start = Today().AddDays(startOffsetDays);
        return (w, await w.SeedAsync("Approved", start, start.AddDays(1)));
    }

    private static string? CodeOf(IActionResult result)
    {
        var body = result.Should().BeOfType<BadRequestObjectResult>().Subject.Value!;
        return JsonSerializer.SerializeToElement(body).GetProperty("code").GetString();
    }

    [Fact]
    public async Task An_employee_can_ask_to_cancel_approved_leave_that_has_not_started()
    {
        var (w, leave) = await ApprovedAsync(startOffsetDays: 1);
        var c = w.Leave(w.OwnScope(w.Emp), w.User(w.EmpUserId, "Employee", "ess.write"));

        (await c.Cancel(leave.Id, new CancelLeaveRequest("plans changed"), CancellationToken.None)).Should().BeOfType<AcceptedResult>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task An_employee_cannot_cancel_approved_leave_that_starts_today_or_has_started(int startOffsetDays)
    {
        var (w, leave) = await ApprovedAsync(startOffsetDays);
        var c = w.Leave(w.OwnScope(w.Emp), w.User(w.EmpUserId, "Employee", "ess.write"));

        CodeOf(await c.Cancel(leave.Id, new CancelLeaveRequest("plans changed"), CancellationToken.None)).Should().Be("leave_cancel_after_start");

        (await w.Db.LeaveRequests.AsNoTracking().SingleAsync(r => r.Id == leave.Id)).Status.Should().Be("Approved");
        (await w.Db.LeaveCancellationRequests.AnyAsync(x => x.LeaveRequestId == leave.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Hr_can_still_ask_to_cancel_approved_leave_that_has_started()
    {
        var (w, leave) = await ApprovedAsync(startOffsetDays: -1);
        var c = w.Leave(LeaveBatch1World.OrgScope(), w.User(Guid.NewGuid(), "HR Manager", "leave.write", "leave.cancel"));

        (await c.Cancel(leave.Id, new CancelLeaveRequest("recalled"), CancellationToken.None)).Should().BeOfType<AcceptedResult>();
    }
}
