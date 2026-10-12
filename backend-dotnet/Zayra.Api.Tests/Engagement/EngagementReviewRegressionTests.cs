using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Reports;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Reports;

namespace Zayra.Api.Tests.Engagement;

/// <summary>
/// The independent privacy review's attacks, kept as regressions that must now FAIL to re-identify anyone, plus the
/// rules the fix round introduced: a filtered view inherits its breakdown's decision, at most two dimensions combine,
/// Confidential/Anonymous results only after close, comments by one declared dimension, and every permitted view of
/// the golden survey enumerated through the real API.
/// </summary>
[Collection("Engagement")]
public sealed class EngagementReviewRegressionTests
{
    private readonly EngagementHostFixture _f;
    public EngagementReviewRegressionTests(EngagementHostFixture f) => _f = f;

    private Task<JsonElement> Results(Guid survey, string query = "", string? token = null) =>
        _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{survey}/results{query}", token ?? _f.TokenHr);

    private static int[]? Q1(JsonElement r)
    {
        var item = r.GetProperty("items").EnumerateArray().SingleOrDefault(i => i.GetProperty("key").GetString() == "q1");
        if (item.ValueKind == JsonValueKind.Undefined || !item.GetProperty("visible").GetBoolean()) return null;
        return item.GetProperty("distribution").EnumerateArray().Select(d => d.GetProperty("count").GetInt32()).ToArray();
    }

    // ── P0-1 ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewAttack1_SiblingSum_CannotRecoverLegal()
    {
        // The review's attack: Root − Operations − (Finance requested as a filter) = Legal (3 people, k = 5).
        var root = await Results(_f.GoldenSurveyId);
        var ops = await Results(_f.GoldenSurveyId, $"?department={_f.DeptOps}");
        var fin = await Results(_f.GoldenSurveyId, $"?department={_f.DeptFin}");
        Q1(root).Should().NotBeNull();
        Q1(ops).Should().NotBeNull();
        fin.GetProperty("visible").GetBoolean().Should().BeFalse("Finance is hidden in the department breakdown, so its filter form is hidden too");
        fin.GetProperty("suppressionReason").GetString().Should().Be("suppressed");
        Q1(fin).Should().BeNull();
    }

    [Fact]
    public async Task EverySecondarilyHiddenBreakdownCell_IsHiddenInItsFilterFormToo()
    {
        foreach (var dim in new[] { "department", "designation", "tenure", "company" })
        {
            var r = await Results(_f.GoldenSurveyId, $"?breakdown={dim}");
            foreach (var cell in r.GetProperty("breakdown").GetProperty("cells").EnumerateArray().Where(c => !c.GetProperty("visible").GetBoolean()))
            {
                var filtered = await Results(_f.GoldenSurveyId, $"?{dim}={cell.GetProperty("value").GetString()}");
                filtered.GetProperty("visible").GetBoolean().Should().BeFalse($"{dim}={cell.GetProperty("label").GetString()} is hidden in the breakdown");
            }
            foreach (var cell in r.GetProperty("breakdown").GetProperty("cells").EnumerateArray().Where(c => c.GetProperty("visible").GetBoolean()))
            {
                var filtered = await Results(_f.GoldenSurveyId, $"?{dim}={cell.GetProperty("value").GetString()}");
                filtered.GetProperty("respondents").GetInt32().Should().Be(cell.GetProperty("respondents").GetInt32(), "a filter and its breakdown cell agree");
            }
        }
    }

    [Fact]
    public async Task AtMostTwoDimensionsCombine_ABreakdownCountingAsOne()
    {
        using var three = await _f.SendAsync(HttpMethod.Get,
            $"/api/engagement/surveys/{_f.GoldenSurveyId}/results?department={_f.DeptOps}&designation={_f.DesigEngineer}&tenure=1to3y", _f.TokenHr);
        three.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonDocument.Parse(await three.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString().Should().Be("too_many_filters");
        using var twoPlusBreakdown = await _f.SendAsync(HttpMethod.Get,
            $"/api/engagement/surveys/{_f.GoldenSurveyId}/results?department={_f.DeptOps}&designation={_f.DesigEngineer}&breakdown=tenure", _f.TokenHr);
        twoPlusBreakdown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var export = await _f.SendAsync(HttpMethod.Post, $"/api/engagement/surveys/{_f.GoldenSurveyId}/export", _f.TokenHr,
            new { format = "csv", filters = new { department = _f.DeptOps.ToString(), designation = _f.DesigEngineer.ToString(), tenure = "1to3y" } });
        export.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Every view the API permits on the golden survey (whole survey, each single filter, each pair, and each breakdown
    /// cell) is requested, and the visible ones are checked against the golden membership: no two nested visible views
    /// differ by 1..k−1 people, and no visible parent minus its visible children leaves 1..k−1.
    /// </summary>
    [Fact]
    public async Task EnumeratingEveryPermittedView_IsolatesNoGroupSmallerThanK()
    {
        const int k = 5;
        var dims = new Dictionary<string, (Func<int, string> Of, string[] Values)>
        {
            ["department"] = (i => Golden.Groups[i].Dept switch { "Ops" => _f.DeptOps.ToString(), "Fin" => _f.DeptFin.ToString(), _ => _f.DeptLegal.ToString() },
                new[] { _f.DeptOps.ToString(), _f.DeptFin.ToString(), _f.DeptLegal.ToString() }),
            ["designation"] = (i => Golden.Groups[i].Desig switch { "Eng" => _f.DesigEngineer.ToString(), "Ana" => _f.DesigAnalyst.ToString(), _ => _f.DesigSupervisor.ToString() },
                new[] { _f.DesigEngineer.ToString(), _f.DesigAnalyst.ToString(), _f.DesigSupervisor.ToString() }),
            ["tenure"] = (_ => "1to3y", new[] { "1to3y" }),
        };
        var visible = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        async Task Probe(params (string Dim, string Value)[] filters)
        {
            var q = filters.Length == 0 ? "" : "?" + string.Join("&", filters.Select(f => $"{f.Dim}={f.Value}"));
            var r = await Results(_f.GoldenSurveyId, q);
            if (!r.GetProperty("visible").GetBoolean()) return;
            var members = Enumerable.Range(0, Golden.Groups.Length).Where(i => filters.All(f => dims[f.Dim].Of(i) == f.Value)).ToHashSet();
            r.GetProperty("respondents").GetInt32().Should().Be(members.Count, "the API's count matches the golden membership");
            visible[Key(filters)] = members;
        }
        await Probe();
        var names = dims.Keys.ToArray();
        foreach (var d in names) foreach (var v in dims[d].Values) await Probe((d, v));
        for (var a = 0; a < names.Length; a++)
            for (var b = a + 1; b < names.Length; b++)
                foreach (var va in dims[names[a]].Values)
                    foreach (var vb in dims[names[b]].Values)
                        await Probe((names[a], va), (names[b], vb));

        visible.Should().ContainKey(string.Empty);
        foreach (var (ka, a) in visible)
            foreach (var (kb, b) in visible)
                if (ka != kb && b.IsSubsetOf(a))
                    (a.Count - b.Count is 0 or >= k).Should().BeTrue($"'{ka}' minus '{kb}' isolates {a.Count - b.Count} people");
        foreach (var (kp, p) in visible)
        {
            foreach (var d in names)
            {
                if (kp.Contains(d + "=")) continue;
                var children = visible.Where(c => c.Key != kp && c.Key.Contains(d + "=") && c.Value.IsSubsetOf(p)
                                                  && (kp.Length == 0 || c.Key.Split('|').Contains(kp))
                                                  && c.Key.Split('|').Length == (kp.Length == 0 ? 1 : kp.Split('|').Length + 1)).ToList();
                var residue = p.Count - children.Sum(c => c.Value.Count);
                (residue is 0 or >= k).Should().BeTrue($"'{kp}' minus its visible {d} children isolates {residue} people");
            }
        }
    }

    private static string Key((string Dim, string Value)[] filters) =>
        string.Join("|", filters.OrderBy(f => f.Dim, StringComparer.Ordinal).Select(f => $"{f.Dim}={f.Value}"));

    // ── P0-2 ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewAttack2_LiveDifferencing_IsImpossibleWhileOpen()
    {
        var sid = await _f.CreateAndLaunchSurveyAsync(_f.TemplateId, "Attack conf", "Confidential", k: 5);
        for (var i = 0; i < 5; i++)
        {
            var (t, _) = await _f.OpenAsync(i, sid);
            (await _f.SubmitAsync(i, sid, t, Golden.Answers(i))).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        var before = await Results(sid);
        var (tok, _) = await _f.OpenAsync(27, sid);
        (await _f.SubmitAsync(27, sid, tok, Golden.Answers(27))).StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await Results(sid);
        foreach (var r in new[] { before, after })
        {
            r.GetProperty("resultsAvailable").GetBoolean().Should().BeFalse();
            r.GetProperty("visible").GetBoolean().Should().BeFalse();
            r.GetProperty("items").GetArrayLength().Should().Be(0);
            r.GetProperty("favourablePct").ValueKind.Should().Be(JsonValueKind.Null);
        }
        Q1(after).Should().BeNull("no distribution exists to difference while the survey is open");
        after.GetProperty("participation").GetProperty("submitted").GetInt32().Should().Be(6, "only the whole-survey response rate is live");

        using var comments = await _f.SendAsync(HttpMethod.Get, $"/api/engagement/surveys/{sid}/comments?question=q6", _f.TokenHr);
        comments.StatusCode.Should().Be(HttpStatusCode.Conflict);
        JsonDocument.Parse(await comments.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString().Should().Be("results_after_close");
        using var export = await _f.SendAsync(HttpMethod.Post, $"/api/engagement/surveys/{sid}/export", _f.TokenHr, new { format = "csv" });
        export.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var trend = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{sid}/trend", _f.TokenHr);
        trend.EnumerateArray().Should().OnlyContain(p => p.GetProperty("favourablePct").ValueKind == JsonValueKind.Null);

        await _f.CloseAsync(sid);
        var closed = await Results(sid);
        closed.GetProperty("resultsAvailable").GetBoolean().Should().BeTrue();
        closed.GetProperty("respondents").GetInt32().Should().Be(6, "once closed, one snapshot — nothing changes to difference against");
    }

    [Fact]
    public async Task SurveyList_ParticipationHonoursK()
    {
        var small = await _f.CreateAndLaunchSurveyAsync(_f.TemplateId, "Tiny team pulse", "Anonymous", [_f.DeptLegal]);
        var (t, _) = await _f.OpenAsync(27, small);
        (await _f.SubmitAsync(27, small, t, Golden.Answers(27))).StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await _f.JsonAsync(HttpMethod.Get, "/api/engagement/surveys", _f.TokenHr);
        var row = list.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == small);
        row.GetProperty("invitationCount").GetInt32().Should().Be(3);
        row.GetProperty("submitted").ValueKind.Should().Be(JsonValueKind.Null, "three invitees are fewer than k: no live count");
        var golden = list.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == _f.GoldenSurveyId);
        golden.GetProperty("submitted").GetInt32().Should().Be(30);
    }

    // ── P1-3 ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Comments_OnlyWholeSurveyOrTheDeclaredDimension_NeverIntersected()
    {
        var whole = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{_f.GoldenSurveyId}/comments?question=q6", _f.TokenHr);
        whole.GetProperty("visible").GetBoolean().Should().BeTrue();
        var byDept = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{_f.GoldenSurveyId}/comments?question=q6&department={_f.DeptOps}", _f.TokenHr);
        byDept.GetProperty("visible").GetBoolean().Should().BeFalse("Operations wrote 7 of 10: the other 3 would be isolated");
        foreach (var query in new[] { "&tenure=1to3y", $"&designation={_f.DesigEngineer}", $"&department={_f.DeptOps}&tenure=1to3y", "&breakdown=department" })
        {
            using var refused = await _f.SendAsync(HttpMethod.Get, $"/api/engagement/surveys/{_f.GoldenSurveyId}/comments?question=q6{query}", _f.TokenHr);
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, query);
            JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString().Should().Be("comment_filter_not_allowed");
        }
        using var exportRefused = await _f.SendAsync(HttpMethod.Post, $"/api/engagement/surveys/{_f.GoldenSurveyId}/export", _f.TokenHr,
            new { format = "csv", includeComments = true, filters = new { tenure = "1to3y" } });
        exportRefused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Low-9 ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ATeamScopedReport_AndTheDashboard_CountTheSamePopulation()
    {
        var dashboard = await Results(_f.GoldenSurveyId, token: _f.TokenMgrEng);
        using var scope = _f.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var dataScope = scope.ServiceProvider.GetRequiredService<IDataScopeService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new JwtSecurityTokenHandler().ReadJwtToken(_f.TokenMgrEng).Claims, "test"));
        var team = await dataScope.ResolveAsync(principal, _f.TenantA, default);
        team.IsUnrestricted.Should().BeFalse();
        var data = await new ReportsController(db, dataScope).ExecuteReportDataAsync(_f.TenantA,
            new RunReportRequest("engagement.results", new ReportFilters { SurveyId = _f.GoldenSurveyId }),
            new ReportDataScope(team.AllowedEmployeeIds, [_f.CompanyA1], false), default);
        var sections = JsonSerializer.SerializeToElement(data);
        var respondents = sections.GetProperty("summary").EnumerateArray()
            .Single(r => r.GetProperty("metric").GetString() == "Respondents in view").GetProperty("value").GetString();
        respondents.Should().Be(dashboard.GetProperty("respondents").GetInt32().ToString(), "the report and the dashboard cut the same team");
        dashboard.GetProperty("respondents").GetInt32().Should().Be(10);
    }
}
