using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Zayra.Api.Tests.Engagement;

/// <summary>
/// The third privacy review, through the real API: (P1) changing the tenant's k after close and re-reading must only
/// ever hide views of the family frozen at close; (P2) whether a scoped request is allowed and which breakdown cells are
/// listed must not depend on who responded.
/// </summary>
[Collection("Engagement")]
public sealed class EngagementReview3RegressionTests
{
    private readonly EngagementHostFixture _f;
    public EngagementReview3RegressionTests(EngagementHostFixture f) => _f = f;

    private async Task<(HttpStatusCode Status, JsonElement Body)> Get(string path, string token, Guid? company = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (company is not null) req.Headers.Add("X-Company-Id", company.Value.ToString());
        using var res = await _f.Client.SendAsync(req);
        return (res.StatusCode, JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    private async Task<JsonElement> Ok(string path, string token, Guid? company = null)
    {
        var (status, body) = await Get(path, token, company);
        status.Should().Be(HttpStatusCode.OK, body.GetRawText());
        return body;
    }

    /// <summary>Everything a group reader can see of the golden survey, as "view|metric" keys that are visible.</summary>
    private async Task<(HashSet<string> Visible, int K)> VisibleSetAsync()
    {
        var visible = new HashSet<string>(StringComparer.Ordinal);
        var k = 0;
        var requests = new List<string> { "", "?breakdown=department", "?breakdown=designation", "?breakdown=company" };
        foreach (var d in new[] { _f.DeptOps, _f.DeptFin, _f.DeptLegal }) requests.Add($"?department={d}");
        foreach (var d in new[] { _f.DesigEngineer, _f.DesigAnalyst, _f.DesigSupervisor }) requests.Add($"?designation={d}");
        requests.Add($"?department={_f.DeptOps}&designation={_f.DesigAnalyst}");
        foreach (var q in requests)
        {
            var r = await Ok($"/api/engagement/surveys/{_f.GoldenSurveyId}/results{q}", _f.TokenHr);
            k = r.GetProperty("minCohortSize").GetInt32();
            if (r.GetProperty("visible").GetBoolean()) visible.Add($"{q}|view");
            foreach (var i in r.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("visible").GetBoolean()))
                visible.Add($"{q}|item:{i.GetProperty("key").GetString()}");
            foreach (var d in r.GetProperty("drivers").EnumerateArray().Where(i => i.GetProperty("visible").GetBoolean()))
                visible.Add($"{q}|driver:{d.GetProperty("key").GetString()}");
            if (r.GetProperty("breakdown").ValueKind == JsonValueKind.Object)
                foreach (var c in r.GetProperty("breakdown").GetProperty("cells").EnumerateArray().Where(c => c.GetProperty("visible").GetBoolean()))
                    visible.Add($"{q}|cell:{c.GetProperty("value").GetString()}");
        }
        foreach (var q in new[] { "", $"&department={_f.DeptOps}", $"&department={_f.DeptFin}" })
        {
            var c = await Ok($"/api/engagement/surveys/{_f.GoldenSurveyId}/comments?question=q6{q}", _f.TokenHr);
            if (c.GetProperty("visible").GetBoolean()) visible.Add($"comments{q}");
        }
        return (visible, k);
    }

    private async Task SetTenantKAsync(int k, int months) =>
        await _f.JsonAsync(HttpMethod.Put, "/api/engagement/settings", _f.TokenHr, new { minCohortSize = k, freeTextRetentionMonths = months });

    [Fact]
    public async Task ChangingTheTenantsK_AfterClose_OnlyEverHidesViewsOfTheFrozenFamily()
    {
        var settings = await _f.JsonAsync(HttpMethod.Get, "/api/engagement/settings", _f.TokenHr);
        var originalK = settings.GetProperty("minCohortSize").GetInt32();
        var months = settings.GetProperty("freeTextRetentionMonths").GetInt32();
        await using var db = _f.CreateDb();
        try
        {
            // A survey closed before the column existed is frozen on its first results computation.
            await db.Database.ExecuteSqlRawAsync("UPDATE surveys SET frozen_min_cohort_size = NULL WHERE id = {0}", _f.GoldenSurveyId);
            var (frozenSet, frozenK) = await VisibleSetAsync();
            frozenK.Should().Be(5);
            (await db.EngagementSurveys.AsNoTracking().IgnoreQueryFilters().Where(s => s.Id == _f.GoldenSurveyId)
                .Select(s => s.FrozenMinCohortSize).SingleAsync()).Should().Be(5);
            frozenSet.Should().Contain($"?department={_f.DeptOps}|view", "the audit must not pass by hiding everything");

            foreach (var raised in new[] { 6, 7, 8, 5, 7, 3 })
            {
                await SetTenantKAsync(raised, months);
                var (visible, k) = await VisibleSetAsync();
                k.Should().Be(Math.Max(5, raised), "the frozen k is never lowered");
                visible.Should().BeSubsetOf(frozenSet, $"at tenant k {raised} only views of the family frozen at 5 may show");
                if (raised <= 5) visible.Should().BeEquivalentTo(frozenSet, "lowering k back never re-decides the family");
            }
        }
        finally
        {
            await SetTenantKAsync(originalK, months);
        }
    }

    [Fact]
    public async Task ARefusalOrAListedCell_DoesNotChange_WhenOnePersonResponds()
    {
        // A new A2 Operations engineer, with a login.
        int a2Person;
        await using (var db = _f.CreateDb())
        {
            a2Person = await EngagementHostFixture.AddEmployeeAsync(db, _f.TenantA, _f.CompanyA2, _f.DeptOps, _f.DesigEngineer, $"R3-{Guid.NewGuid():N}"[..12], "Jeddah Engineer R3", null);
            await EngagementHostFixture.AddUserAsync(db, _f.TenantA, $"r3-{a2Person}", "Employee", false, a2Person, _f.CompanyA2, "EssOnly");
        }
        var a2Token = await _f.MintAsync(_f.TenantA, $"r3-{a2Person}");

        async Task<Guid> SurveyAsync(string title, bool othersRespond)
        {
            var created = await _f.JsonAsync(HttpMethod.Post, "/api/engagement/surveys", _f.TokenHr, new
            {
                templateId = _f.TemplateId, titleEn = title, titleAr = "س", mode = "Anonymous", companyId = (Guid?)null,
                audience = new { departmentIds = new[] { _f.DeptOps, _f.DeptFin } }, opensAtUtc = DateTime.UtcNow.AddDays(-1),
                closesAtUtc = DateTime.UtcNow.AddDays(5), recurrence = "None", minCohortSize = 5, reminderOffsetsDays = Array.Empty<int>(),
                commentDimension = "department",
            }, HttpStatusCode.Created);
            var sid = created.GetProperty("id").GetGuid();
            await _f.JsonAsync(HttpMethod.Post, $"/api/engagement/surveys/{sid}/launch", _f.TokenHr, new { version = created.GetProperty("version").GetInt32() });
            var answers = new object[] { new { questionKey = "q1", value = 4 }, new { questionKey = "q2", value = 4 }, new { questionKey = "q4", value = 8 } };
            foreach (var i in Enumerable.Range(0, 14).Concat(Enumerable.Range(20, 6)))
            {
                var (t, _) = await _f.OpenAsync(i, sid);
                (await _f.SubmitAsync(i, sid, t, answers)).StatusCode.Should().Be(HttpStatusCode.OK);
            }
            if (othersRespond)
            {
                // The out-of-scope A2 engineer, and the ONLY Finance engineer (26).
                var s = await _f.JsonAsync(HttpMethod.Post, "/api/ess/surveys/open", a2Token, new { surveyId = sid });
                using (var r = await _f.SendAsync(HttpMethod.Post, "/api/ess/surveys/submit", a2Token, new { surveyId = sid, token = s.GetProperty("token").GetString(), answers }))
                    r.StatusCode.Should().Be(HttpStatusCode.OK);
                var (t26, _) = await _f.OpenAsync(26, sid);
                (await _f.SubmitAsync(26, sid, t26, answers)).StatusCode.Should().Be(HttpStatusCode.OK);
            }
            await _f.CloseAsync(sid);
            return sid;
        }

        var without = await SurveyAsync("Existence oracle — without", othersRespond: false);
        var with = await SurveyAsync("Existence oracle — with", othersRespond: true);

        // P2 (a): an A1-scoped reader's Operations + breakdown: refused whether or not the A2 engineer responded.
        var refusals = new List<(HttpStatusCode, string?)>();
        foreach (var sid in new[] { without, with })
        {
            var (status, body) = await Get($"/api/engagement/surveys/{sid}/results?department={_f.DeptOps}&breakdown=designation", _f.TokenHr, _f.CompanyA1);
            refusals.Add((status, body.TryGetProperty("code", out var code) ? code.GetString() : null));
        }
        refusals[0].Should().Be(refusals[1]);
        refusals[0].Should().Be((HttpStatusCode.BadRequest, "scope_counts_as_filter"), "an A2 Operations engineer is invited");

        // P2 (b): the Finance × designation cells list Engineer whether or not the only Finance engineer responded.
        var lists = new List<string[]>();
        foreach (var sid in new[] { without, with })
        {
            var r = await Ok($"/api/engagement/surveys/{sid}/results?department={_f.DeptFin}&breakdown=designation", _f.TokenHr);
            lists.Add(r.GetProperty("breakdown").GetProperty("cells").EnumerateArray().Select(c => c.GetProperty("value").GetString()!).ToArray());
            r.GetProperty("breakdown").GetProperty("cells").EnumerateArray().Where(c => !c.GetProperty("visible").GetBoolean())
                .Select(c => c.GetProperty("suppressionReason").GetString()).Should().OnlyContain(x => x == "suppressed");
        }
        lists[0].Should().Equal(lists[1]);
        lists[0].Should().Contain(_f.DesigEngineer.ToString());
    }
}
