using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>A legacy lower-case former-employee status ("retired") still means former: the current list and the archive match the status without regard to case.</summary>
public sealed class EmployeeFormerStatusCaseHttpTests(EmployeeDirectoryHttpFixture f) : IClassFixture<EmployeeDirectoryHttpFixture>
{
    private static string NewTag() => "R" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private async Task<(HttpStatusCode Status, JsonElement Body)> Call(HttpMethod method, string url, string token, object? body = null)
    {
        using var r = await f.Send(method, url, token, body);
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, text.Length > 0 && text[0] is '{' or '[' ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    // ═════════════ 6. A legacy lower-case former-employee status still means former ═════════════

    [Theory]
    [InlineData("retired")]
    [InlineData("DECEASED")]
    [InlineData("terminated")]
    public async Task LegacyLowerCaseExitStatus_IsInTheArchive_AndNotInTheCurrentList(string stored)
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 1, status: stored);
        var token = f.Token("HR Officer");

        var (_, current) = await Call(HttpMethod.Get, $"/api/employees?search={tag}", token);
        var (_, archive) = await Call(HttpMethod.Get, $"/api/employees/ex-employees?search={tag}", token);

        current.GetProperty("total").GetInt32().Should().Be(0, $"'{stored}' is a former employee whatever its spelling");
        archive.GetProperty("total").GetInt32().Should().Be(1);
    }

    // ═════════════ 7. The People status filter ignores case, so a legacy "active" row still matches "Active" ═════════════

    [Theory]
    [InlineData("HR Officer", false)]   // group-level caller: EmployeeManagementService.SearchAsync
    [InlineData("HR Officer", true)]    // company-scoped caller: EmployeesController.List
    public async Task StatusFilter_MatchesLegacyMixedCaseRows_InBothListPaths(string role, bool scoped)
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 1, suffix: "a", status: "Active");
        await f.SeedAsync(tag, 1, suffix: "b", status: "active");
        await f.SeedAsync(tag, 1, suffix: "c", status: "ACTIVE");
        await f.SeedAsync(tag, 1, suffix: "d", status: "Suspended");
        var token = scoped ? f.ScopedToken(role) : f.Token(role);

        foreach (var typed in new[] { "Active", "active", "ACTIVE" })
        {
            var (_, body) = await Call(HttpMethod.Get, $"/api/employees?search={tag}&status={typed}", token);
            body.GetProperty("total").GetInt32().Should().Be(3, $"the status filter '{typed}' means Active whatever the stored spelling");
        }
        var (_, other) = await Call(HttpMethod.Get, $"/api/employees?search={tag}&status=Suspended", token);
        other.GetProperty("total").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task StatusFilter_ArchiveAndBulkSelectAll_UseTheSameRule()
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 1, suffix: "a", status: "Active");
        await f.SeedAsync(tag, 1, suffix: "b", status: "active");
        await f.SeedAsync(tag, 1, suffix: "c", status: "Terminated");
        await f.SeedAsync(tag, 1, suffix: "d", status: "terminated");
        var token = f.Token("HR Manager");

        var (_, archive) = await Call(HttpMethod.Get, $"/api/employees/ex-employees?search={tag}&status=Terminated", token);
        archive.GetProperty("total").GetInt32().Should().Be(2);

        var (_, list) = await Call(HttpMethod.Get, $"/api/employees?search={tag}&status=Active&pageSize=1", token);
        using var export = await f.Send(HttpMethod.Post, "/api/employees/bulk", token,
            new { action = "export", selectionMode = "allMatching", filter = new { search = tag, status = "Active" } });
        var rows = (await export.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.Contains(tag));
        list.GetProperty("total").GetInt32().Should().Be(2);
        rows.Should().Be(2, "select-all-matching resolves exactly the set the list shows");
    }

    [Fact]
    public async Task StatusCounts_CollapseSpellingsOfTheSameStatus()
    {
        var tag = NewTag();
        await f.SeedAsync(tag, 2, suffix: "a", status: "Active");
        await f.SeedAsync(tag, 1, suffix: "b", status: "active");
        var token = f.Token("HR Manager");

        var (code, summary) = await Call(HttpMethod.Get, "/api/employees/reports/status-summary", token);
        code.Should().Be(HttpStatusCode.OK);
        var actives = summary.GetProperty("statuses").EnumerateArray()
            .Where(s => string.Equals(s.GetProperty("name").GetString(), "Active", StringComparison.OrdinalIgnoreCase)).ToList();
        actives.Should().HaveCount(1, "'Active' and 'active' are one status, counted once");
        actives[0].GetProperty("name").GetString().Should().Be("Active");
        actives[0].GetProperty("count").GetInt32().Should().BeGreaterThanOrEqualTo(3);

        var (_, reports) = await Call(HttpMethod.Get, "/api/employees/reports/summary", token);
        reports.GetProperty("activeEmployees").GetInt32().Should().Be(actives[0].GetProperty("count").GetInt32(),
            "the headline Active count and the status breakdown use the same rule");
    }
}
