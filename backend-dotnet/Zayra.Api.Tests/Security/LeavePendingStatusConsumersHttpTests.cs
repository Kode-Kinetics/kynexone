using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Leave Batch 1, item 3. LeaveService saves a request as PendingManagerApproval / PendingHRApproval (and an approved
/// leave awaiting a cancellation decision as CancellationRequested); "Submitted" and "Pending" are never saved. Several
/// consumers compared to "Submitted"/"Pending" only and so read 0 for every real request, and the calendar views
/// disagreed about which statuses to show. One request is seeded in EVERY status and each consumer is asked what it
/// counts or lists.
/// </summary>
public sealed class LeavePendingStatusConsumersHttpTests(LeaveBatch1Fixture f) : IClassFixture<LeaveBatch1Fixture>, IAsyncLifetime
{
    private static readonly string[] EveryStatus =
        ["Draft", "Submitted", "PendingManagerApproval", "PendingHRApproval", "Approved", "CancellationRequested", "Rejected", "Cancelled", "Withdrawn"];

    // A request still waiting for its first decision.
    private static readonly string[] AwaitingFirstDecision = ["Submitted", "PendingManagerApproval", "PendingHRApproval"];

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // One leave per status, seeded ONCE for the class: xUnit calls InitializeAsync before every test on the shared fixture.
    public Task InitializeAsync() => f.OnceAsync(nameof(LeavePendingStatusConsumersHttpTests), async () =>
    {
        // Spans today in every timezone, so the calendar window, "today" and the counters all see each row.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var status in EveryStatus)
            await f.SeedLeaveAsync(status, today.AddDays(-1), today.AddDays(1));
    });

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<JsonElement> GetJsonAsync(string path, string who)
    {
        using var response = await f.Send(HttpMethod.Get, path, f.Token(who));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string Window()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return $"fromDate={today.AddDays(-3):yyyy-MM-dd}&toDate={today.AddDays(3):yyyy-MM-dd}";
    }

    [Fact]
    public async Task The_dashboard_pending_leave_counter_counts_requests_awaiting_a_first_decision()
    {
        var kpis = await GetJsonAsync("/api/dashboard/kpis", "admin");
        kpis.GetProperty("pendingLeaveRequests").GetInt32().Should().Be(AwaitingFirstDecision.Length);
    }

    // /api/analytics/kpis is covered by LeavePendingStatusConsumerTests (controller over the same rules): over HTTP on this
    // SQLite host the endpoint's loan-balance SumAsync(decimal) cannot be translated, so an admin call is a 500 here only.

    [Fact]
    public async Task The_pending_leave_report_lists_requests_awaiting_a_first_decision()
    {
        using var response = await f.Send(HttpMethod.Post, "/api/reports/run", f.Token("admin"), new { reportKey = "leave.pending" });
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var statuses = JsonDocument.Parse(text).RootElement.GetProperty("data").EnumerateArray()
            .Select(x => x.GetProperty("status").GetString()).ToList();
        statuses.Should().BeEquivalentTo(AwaitingFirstDecision);
    }

    [Fact]
    public async Task The_mobile_dashboard_pending_count_includes_requests_waiting_for_hr()
    {
        var dashboard = await GetJsonAsync($"/api/mobile/dashboard/{f.EmployeeId}", "employee");
        dashboard.GetProperty("pendingLeave").GetInt32().Should().Be(AwaitingFirstDecision.Length);
    }

    [Fact]
    public async Task The_calendar_list_shows_taken_leave_including_one_awaiting_cancellation_and_every_pending_request()
    {
        var rows = await GetJsonAsync($"/api/leave/calendar?{Window()}", "admin");
        rows.EnumerateArray().Select(x => x.GetProperty("status").GetString()).Should().BeEquivalentTo(
            ["Approved", "CancellationRequested", "Submitted", "PendingManagerApproval", "PendingHRApproval"]);
    }

    [Fact]
    public async Task The_calendar_team_view_agrees_with_the_calendar_list()
    {
        var groups = await GetJsonAsync($"/api/leave/calendar/team?{Window()}", "admin");
        groups.EnumerateArray().SelectMany(g => g.GetProperty("employees").EnumerateArray())
            .Select(x => x.GetProperty("status").GetString()).Should().BeEquivalentTo(
                ["Approved", "CancellationRequested", "Submitted", "PendingManagerApproval", "PendingHRApproval"]);
    }

    [Fact]
    public async Task The_on_leave_today_view_keeps_an_employee_on_leave_while_a_cancellation_is_pending()
    {
        var today = await GetJsonAsync("/api/leave/calendar/today", "admin");
        today.EnumerateArray().Select(x => x.GetProperty("status").GetString())
            .Should().BeEquivalentTo(["Approved", "CancellationRequested"]);
    }
}
