using System.Net;
using FluentAssertions;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The AI assistant's history and payroll-validation reads honour the caller's company scope, through the real pipeline:
/// a one-company caller sees the query history of their own company's users only (a group caller sees all), and the
/// stored validation results of a payroll run are served only when the run itself is visible to the caller.
/// </summary>
public sealed class AiAssistantScopeHttpTests(LaunchSecurityFixture f) : IClassFixture<LaunchSecurityFixture>
{
    private sealed record Seed(Guid RunA, Guid RunB);

    private Task<Seed> SeedAsync() => f.OnceAsync("ai-assistant-scope", async () =>
    {
        await f.EnsureUserAsync("AdminB", "Admin", groupScope: false, f.CompanyB);
        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();
        await f.SeedAsync(db =>
        {
            AIHRQueryLog Log(string who, string text) => new()
            {
                TenantId = f.TenantId, UserId = f.UserId(who), UserRole = "Admin", Query = text, PromptSummary = text, ResponseStatus = "ok",
            };
            db.AIHRQueryLogs.AddRange(Log("AdminA", "QUERY-FROM-A"), Log("AdminB", "QUERY-FROM-B"), Log("AdminGroup", "QUERY-FROM-GROUP"));
            db.PayrollRuns.Add(new PayrollRun { Id = runA, TenantId = f.TenantId, CompanyId = f.CompanyA, Year = 2026, Month = 9 });
            db.PayrollRuns.Add(new PayrollRun { Id = runB, TenantId = f.TenantId, CompanyId = f.CompanyB, Year = 2026, Month = 9 });
            db.PayrollAIValidationResults.Add(new PayrollAIValidationResult { TenantId = f.TenantId, PayrollRunId = runA, ValidationType = "A_ONLY", Message = "a" });
            db.PayrollAIValidationResults.Add(new PayrollAIValidationResult { TenantId = f.TenantId, PayrollRunId = runB, ValidationType = "B_ONLY", Message = "b" });
            return Task.CompletedTask;
        });
        return new Seed(runA, runB);
    });

    private async Task<HashSet<string>> HistoryQueriesAsync(string persona)
    {
        var json = await f.Json(HttpMethod.Get, "/api/ai/query-history?pageSize=100", f.Token(persona));
        return json.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("query").GetString()!).ToHashSet();
    }

    [Fact]
    public async Task QueryHistory_OneCompanyCaller_SeesOnlyOwnCompanysRows()
    {
        await SeedAsync();
        var own = await HistoryQueriesAsync("AdminA");
        own.Should().Contain("QUERY-FROM-A").And.NotContain("QUERY-FROM-B").And.NotContain("QUERY-FROM-GROUP");
        var other = await HistoryQueriesAsync("AdminB");
        other.Should().Contain("QUERY-FROM-B").And.NotContain("QUERY-FROM-A").And.NotContain("QUERY-FROM-GROUP");
    }

    [Fact]
    public async Task QueryHistory_OneCompanyCaller_TotalCountsOnlyVisibleRows()
    {
        await SeedAsync();
        var json = await f.Json(HttpMethod.Get, "/api/ai/query-history?pageSize=100", f.Token("AdminA"));
        json.GetProperty("total").GetInt32().Should().Be(json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task QueryHistory_GroupCaller_SeesEveryCompanysRows()
    {
        await SeedAsync();
        var all = await HistoryQueriesAsync("AdminGroup");
        all.Should().Contain(["QUERY-FROM-A", "QUERY-FROM-B", "QUERY-FROM-GROUP"]);
    }

    [Fact]
    public async Task PayrollValidationRead_ForAnotherCompanysRun_IsNotFound_AndOwnRunStillAnswers()
    {
        var s = await SeedAsync();
        (await f.Status(HttpMethod.Get, $"/api/ai/payroll-validation/{s.RunB}", f.Token("AdminA"))).Should().Be(HttpStatusCode.NotFound);
        var own = await f.Json(HttpMethod.Get, $"/api/ai/payroll-validation/{s.RunA}", f.Token("AdminA"));
        own.EnumerateArray().Select(x => x.GetProperty("validationType").GetString()).Should().ContainSingle().Which.Should().Be("A_ONLY");
        (await f.Status(HttpMethod.Get, $"/api/ai/payroll-validation/{s.RunB}", f.Token("AdminGroup"))).Should().Be(HttpStatusCode.OK);
    }
}
