using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Leave;
using Zayra.Api.Controllers;
using Zayra.Api.Controllers.Leave;
using Zayra.Api.Controllers.Reports;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Common;

namespace Zayra.Api.Tests;

/// <summary>
/// Leave Batch 1, item 3 (in memory). LeaveService saves a request as PendingManagerApproval / PendingHRApproval, and
/// an approved leave awaiting a cancellation decision as CancellationRequested; "Submitted" and "Pending" are never
/// saved. Counters and reports that compared to "Submitted"/"Pending" only read 0 for every real request, and the
/// calendar views disagreed about which statuses to show. One request is seeded in EVERY status and each consumer is
/// asked what it counts or lists.
/// </summary>
public sealed class LeavePendingStatusConsumerTests
{
    private static readonly string[] EveryStatus =
        ["Draft", "Submitted", "PendingManagerApproval", "PendingHRApproval", "Approved", "CancellationRequested", "Rejected", "Cancelled", "Withdrawn"];

    private static readonly string[] AwaitingFirstDecision = ["Submitted", "PendingManagerApproval", "PendingHRApproval"];
    private static readonly string[] ShownOnCalendar = ["Approved", "CancellationRequested", "Submitted", "PendingManagerApproval", "PendingHRApproval"];

    private static async Task<LeaveBatch1World> SeededAsync()
    {
        var w = await LeaveBatch1World.CreateAsync();
        await w.SeedEveryStatusAroundTodayAsync(EveryStatus);
        return w;
    }

    private static object? Prop(object? target, string name) =>
        target!.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)!.GetValue(target);

    private static object? Ok(IActionResult result) => result.Should().BeOfType<OkObjectResult>().Subject.Value;

    private static IEnumerable<string?> Statuses(object? rows) =>
        ((System.Collections.IEnumerable)rows!).Cast<object>().Select(r => (string?)Prop(r, "Status"));

    // ── The shared definition itself ────────────────────────────────────────────────────────────

    [Fact]
    public void The_awaiting_decision_statuses_are_the_ones_the_service_can_decide()
    {
        LeaveAwaitingDecisionStatuses.NewRequests.Should().BeEquivalentTo(AwaitingFirstDecision);
        LeaveAwaitingDecisionStatuses.All.Should().BeEquivalentTo([.. AwaitingFirstDecision, "CancellationRequested"]);
        LeaveAwaitingDecisionStatuses.ShownOnCalendar.Should().BeEquivalentTo(ShownOnCalendar);
        foreach (var status in EveryStatus)
            LeaveAwaitingDecisionStatuses.IsNewRequest(status).Should().Be(AwaitingFirstDecision.Contains(status), status);
    }

    // ── Counters and reports ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_dashboard_pending_leave_counter_counts_requests_awaiting_a_first_decision()
    {
        var w = await SeededAsync();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var controller = new DashboardController(w.Db, cache, new LeaveBatch1World.FixedScope(LeaveBatch1World.OrgScope()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = w.User(w.HrUserId, "HR Manager", "payroll.read") } },
        };

        var kpis = Ok(await controller.Kpis(CancellationToken.None));

        Prop(kpis, "PendingLeaveRequests").Should().Be(AwaitingFirstDecision.Length);
    }

    [Fact]
    public async Task The_analytics_pending_leave_figure_counts_requests_awaiting_a_first_decision()
    {
        var w = await SeededAsync();
        var controller = new AnalyticsController(w.Db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = w.User(w.HrUserId, "Admin", "reports.read", "employees.read", "leave.read", "attendance.read", "payroll.read", "overtime.read"),
                },
            },
        };

        var kpis = Ok(await controller.GetKPIs(CancellationToken.None));

        Prop(Prop(kpis, "leave"), "pendingLeave").Should().Be(AwaitingFirstDecision.Length);
    }

    [Fact]
    public async Task The_pending_leave_report_lists_requests_awaiting_a_first_decision()
    {
        var w = await SeededAsync();
        var controller = new ReportsController(w.Db, new DataScopeService(w.Db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = w.User(w.HrUserId, "Admin", "reports.read", "leave.read", "employees.read"),
                },
            },
        };

        var body = Ok(await controller.RunReport(new RunReportRequest("leave.pending", null), CancellationToken.None));

        Statuses(Prop(body, "data")).Should().BeEquivalentTo(AwaitingFirstDecision);
    }

    [Fact]
    public async Task The_mobile_dashboard_pending_count_includes_requests_waiting_for_hr()
    {
        var w = await SeededAsync();
        var controller = new MobileController(w.Db, new AttendanceVerificationService(w.Db), TimeProvider.System)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = w.User(w.EmpUserId, "Employee", "ess.read"),
                },
            },
        };
        controller.ControllerContext.HttpContext.User.AddIdentity(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("employee_id", w.Emp.Id.ToString())]));

        var dashboard = Ok(await controller.MobileDashboard(w.Emp.Id, CancellationToken.None));

        Prop(dashboard, "pendingLeave").Should().Be(AwaitingFirstDecision.Length);
    }

    // ── Calendar views ──────────────────────────────────────────────────────────────────────────

    private static LeaveCalendarController Calendar(LeaveBatch1World w) => new(w.Db, new LeaveBatch1World.FixedScope(LeaveBatch1World.OrgScope()))
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = w.User(w.HrUserId, "HR Manager", "leave.read") } },
    };

    [Fact]
    public async Task The_calendar_list_shows_taken_leave_including_one_awaiting_cancellation_and_every_pending_request()
    {
        var w = await SeededAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var rows = Ok(await Calendar(w).List(today.AddDays(-3), today.AddDays(3), null, null, ct: CancellationToken.None));

        Statuses(rows).Should().BeEquivalentTo(ShownOnCalendar);
    }

    [Fact]
    public async Task The_calendar_team_view_agrees_with_the_calendar_list()
    {
        var w = await SeededAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var groups = Ok(await Calendar(w).Team(today.AddDays(-3), today.AddDays(3), null, null, CancellationToken.None));

        ((System.Collections.IEnumerable)groups!).Cast<object>()
            .SelectMany(g => ((System.Collections.IEnumerable)Prop(g, "Employees")!).Cast<object>())
            .Select(e => (string?)Prop(e, "Status")).Should().BeEquivalentTo(ShownOnCalendar);
    }

    [Fact]
    public async Task The_on_leave_today_view_keeps_an_employee_on_leave_while_a_cancellation_is_pending()
    {
        var w = await SeededAsync();

        var rows = Ok(await Calendar(w).Today(CancellationToken.None));

        Statuses(rows).Should().BeEquivalentTo(["Approved", "CancellationRequested"]);
    }
}
