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
}
