using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Zayra.Api.Tests.Engagement;

/// <summary>
/// The second privacy review's attacks, through the real API, as regressions that must stay closed:
/// inclusion–exclusion inside one reader's views (root − X − Y + X∩Y), scope switching (group HR vs the same HR with a
/// company selected, numbers and comment lists), and an anonymous ledger that marks who opened the survey.
/// </summary>
[Collection("Engagement")]
public sealed class EngagementReview2RegressionTests
{
    private readonly EngagementHostFixture _f;
    public EngagementReview2RegressionTests(EngagementHostFixture f) => _f = f;

    private static int[] Q1(JsonElement r) => r.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == "q1")
        .GetProperty("distribution").EnumerateArray().Select(d => d.GetProperty("count").GetInt32()).ToArray();

    private static object[] Answers(int q1, string text) =>
        [new { questionKey = "q1", value = q1 }, new { questionKey = "q2", value = 3 }, new { questionKey = "q4", value = 7 }, new { questionKey = "q6", text }];

    private static bool Visible(JsonElement r) => r.GetProperty("visible").GetBoolean();

    private async Task<JsonElement> Get(string path, string token, Guid? company = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (company is not null) req.Headers.Add("X-Company-Id", company.Value.ToString());
        using var res = await _f.Client.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task InclusionExclusion_CannotRecoverTheOneFinanceEngineer()
    {
        var sid = await _f.CreateAndLaunchSurveyAsync(_f.TemplateId, "IE regression", "Anonymous", [_f.DeptOps, _f.DeptFin]);
        // Ops engineers, analysts, supervisors; Finance analysts and the ONE Finance engineer (26), who answers q1 = 1.
        var who = Enumerable.Range(0, 10).Concat([30]).Concat(Enumerable.Range(10, 4)).Concat([31]).Concat(Enumerable.Range(20, 6)).Concat([32]).Append(26).ToList();
        foreach (var i in who)
        {
            var (t, _) = await _f.OpenAsync(i, sid);
            (await _f.SubmitAsync(i, sid, t, Answers(i == 26 ? 1 : 5, $"c{i}"))).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        await _f.CloseAsync(sid);
        var root = await Get($"/api/engagement/surveys/{sid}/results", _f.TokenHr);
        var ops = await Get($"/api/engagement/surveys/{sid}/results?department={_f.DeptOps}", _f.TokenHr);
        var ana = await Get($"/api/engagement/surveys/{sid}/results?designation={_f.DesigAnalyst}", _f.TokenHr);
        var opsAna = await Get($"/api/engagement/surveys/{sid}/results?department={_f.DeptOps}&designation={_f.DesigAnalyst}", _f.TokenHr);
        var eng = await Get($"/api/engagement/surveys/{sid}/results?designation={_f.DesigEngineer}", _f.TokenHr);
        var fin = await Get($"/api/engagement/surveys/{sid}/results?department={_f.DeptFin}", _f.TokenHr);
        var direct = await Get($"/api/engagement/surveys/{sid}/results?department={_f.DeptFin}&designation={_f.DesigEngineer}", _f.TokenHr);

        Visible(root).Should().BeTrue("the whole survey has 24 respondents");
        Visible(direct).Should().BeFalse();
        new[] { root, ops, ana, opsAna }.All(Visible).Should().BeFalse("root − Ops − Analyst + Ops∩Analyst = the Finance engineer");
        new[] { root, fin, eng }.Count(Visible).Should().BeLessThan(3, "Finance + Engineer − root (nobody is outside both) = the Finance engineer");
        new[] { root, ops, eng, opsAna }.Count(Visible).Should().BeLessThan(4);
    }

    [Fact]
    public async Task ScopeSwitching_GroupVsSelectedCompany_CannotIsolateTheOtherCompanysPerson()
    {
        var tokens = new Dictionary<int, string>();
        await using (var db = _f.CreateDb())
        {
            await EngagementHostFixture.AddUserAsync(db, _f.TenantA, "r2-out", "Employee", false, _f.OutsiderEmployeeId, _f.CompanyA2, "EssOnly");
            for (var i = 0; i < 9; i++)
            {
                var id = await EngagementHostFixture.AddEmployeeAsync(db, _f.TenantA, _f.CompanyA2, _f.DeptFin, _f.DesigAnalyst, $"R2-{i}", $"Jeddah Analyst {i}", null);
                await EngagementHostFixture.AddUserAsync(db, _f.TenantA, $"r2-{i}", "Employee", false, id, _f.CompanyA2, "EssOnly");
            }
        }
        tokens[-1] = await _f.MintAsync(_f.TenantA, "r2-out");
        for (var i = 0; i < 9; i++) tokens[i] = await _f.MintAsync(_f.TenantA, $"r2-{i}");

        // A group survey (every company): A1 Ops 14 + Fin 6, A2 Ops 1 (the outsider, q1 = 1) + Fin 9.
        var created = await _f.JsonAsync(HttpMethod.Post, "/api/engagement/surveys", _f.TokenHr, new
        {
            templateId = _f.TemplateId, titleEn = "Scope regression", titleAr = "انحدار", mode = "Anonymous", companyId = (Guid?)null,
            audience = new { departmentIds = new[] { _f.DeptOps, _f.DeptFin } }, opensAtUtc = DateTime.UtcNow.AddDays(-1), closesAtUtc = DateTime.UtcNow.AddDays(5),
            recurrence = "None", minCohortSize = 5, reminderOffsetsDays = Array.Empty<int>(), commentDimension = "department",
        }, HttpStatusCode.Created);
        var sid = created.GetProperty("id").GetGuid();
        await _f.JsonAsync(HttpMethod.Post, $"/api/engagement/surveys/{sid}/launch", _f.TokenHr, new { version = created.GetProperty("version").GetInt32() });
        foreach (var i in Enumerable.Range(0, 14).Concat(Enumerable.Range(20, 6)))
        {
            var (t, _) = await _f.OpenAsync(i, sid);
            (await _f.SubmitAsync(i, sid, t, Answers(5, $"a1-{i}"))).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        foreach (var (k, tok) in tokens)
        {
            var s = await _f.JsonAsync(HttpMethod.Post, "/api/ess/surveys/open", tok, new { surveyId = sid });
            using var r = await _f.SendAsync(HttpMethod.Post, "/api/ess/surveys/submit", tok, new
            {
                surveyId = sid, token = s.GetProperty("token").GetString(),
                answers = Answers(k == -1 ? 1 : 5, k == -1 ? "SECRET-OUTSIDER-COMMENT" : $"a2-{k}"),
            });
            r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        }
        await _f.CloseAsync(sid);

        var group = await Get($"/api/engagement/surveys/{sid}/results?department={_f.DeptOps}", _f.TokenHr);
        var groupPair = await Get($"/api/engagement/surveys/{sid}/results?department={_f.DeptOps}&company={_f.CompanyA1}", _f.TokenHr);
        var selected = await Get($"/api/engagement/surveys/{sid}/results?department={_f.DeptOps}", _f.TokenHr, _f.CompanyA1);
        foreach (var other in new[] { groupPair, selected })
            if (Visible(group) && Visible(other))
                Q1(group).Zip(Q1(other), (a, b) => a - b).Sum().Should().NotBeInRange(1, 4, "Ops (group) − Ops (A1) must not isolate the A2 Ops person");

        var groupComments = await Get($"/api/engagement/surveys/{sid}/comments?question=q6&department={_f.DeptOps}", _f.TokenHr);
        var selectedComments = await Get($"/api/engagement/surveys/{sid}/comments?question=q6&department={_f.DeptOps}", _f.TokenHr, _f.CompanyA1);
        var a1Comments = await Get($"/api/engagement/surveys/{sid}/comments?question=q6", _f.TokenHr, _f.CompanyA1);
        var all = await Get($"/api/engagement/surveys/{sid}/comments?question=q6", _f.TokenHr);
        IEnumerable<string> List(JsonElement c) => c.GetProperty("comments").EnumerateArray().Select(x => x.GetString()!);
        // Any two visible lists differ by 0 or ≥ k comments (and never by the outsider's comment alone).
        var lists = new[] { groupComments, selectedComments, a1Comments, all }.Where(Visible).Select(List).Select(l => l.ToHashSet()).ToList();
        foreach (var a in lists)
            foreach (var b in lists)
            {
                var difference = a.Except(b).Count();
                (difference == 0 || difference >= 5).Should().BeTrue($"two visible comment lists differ by {difference}");
                var intersection = a.Intersect(b).Count();
                (intersection == 0 || intersection >= 5).Should().BeTrue($"two visible comment lists share {intersection}");
            }
    }

    [Fact]
    public async Task TheAnonymousLedger_DoesNotMarkWhoOpened()
    {
        await using var db = _f.CreateDb();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        async Task<long> Scalar(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        // The live anonymous survey: 31 opened it and saved a draft, 30 and 32 never opened it.
        var live = _f.LiveSurveyId;
        (await Scalar($"SELECT count(*) FROM survey_invitations WHERE survey_id = '{live}' AND mode = 'Anonymous' AND status = 'Invited'")).Should().BeGreaterThan(0);
        (await Scalar("SELECT count(*) FROM survey_invitations WHERE mode = 'Anonymous' AND session_token_hash IS NOT NULL")).Should().Be(0, "an anonymous session is not stored");
        (await Scalar("SELECT count(*) FROM survey_invitations WHERE mode = 'Anonymous' AND (first_opened_on IS NOT NULL OR draft_saved_on IS NOT NULL OR submitted_on IS NOT NULL)")).Should().Be(0);
        (await Scalar("SELECT count(*) FROM survey_invitations WHERE mode = 'Anonymous' AND version <> CASE WHEN status = 'Submitted' THEN 1 ELSE 0 END")).Should().Be(0,
            "an anonymous row changes version once, at submit");
        // Opener 31 (with a draft) and non-opener 30 differ ONLY in the encrypted draft column, until submit or close.
        (await Scalar($"""
            SELECT count(DISTINCT (status, version, reminders_sent, first_opened_on, draft_saved_on, session_token_hash))
            FROM survey_invitations WHERE survey_id = '{live}' AND employee_id IN ({_f.Invitees[30]}, {_f.Invitees[31]}, {_f.Invitees[32]})
            """)).Should().Be(1);

        foreach (var sql in new[]
        {
            "UPDATE survey_invitations SET status = 'Opened' WHERE mode = 'Anonymous' AND status = 'Invited'",
            "UPDATE survey_invitations SET submitted_on = current_date WHERE mode = 'Anonymous' AND status = 'Submitted'",
            "UPDATE survey_invitations SET version = version + 1 WHERE mode = 'Anonymous' AND status = 'Invited'",
            "UPDATE survey_invitations SET session_token_hash = 'x' WHERE mode = 'Anonymous' AND status = 'Invited'",
            "UPDATE survey_invitations SET draft_saved_on = current_date WHERE mode = 'Anonymous' AND status = 'Invited'",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var act = async () => await cmd.ExecuteNonQueryAsync();
            (await act.Should().ThrowAsync<PostgresException>(sql)).Which.ConstraintName.Should().Be("ck_survey_invitations__anonymous_ledger_minimal");
        }
    }

    [Fact]
    public async Task AClosedSurvey_CannotBeRelaunchedReopenedCancelledOrEdited()
    {
        var detail = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{_f.GoldenSurveyId}", _f.TokenHr);
        var version = detail.GetProperty("survey").GetProperty("version").GetInt32();
        foreach (var path in new[] { "launch", "close", "cancel" })
        {
            using var r = await _f.SendAsync(HttpMethod.Post, $"/api/engagement/surveys/{_f.GoldenSurveyId}/{path}", _f.TokenHr, new { version });
            r.StatusCode.Should().Be(HttpStatusCode.Conflict, path);
        }
        using var put = await _f.SendAsync(HttpMethod.Put, $"/api/engagement/surveys/{_f.GoldenSurveyId}", _f.TokenHr, new
        {
            templateId = _f.TemplateId, titleEn = "x", titleAr = "x", mode = "Identified", companyId = _f.CompanyA1,
            opensAtUtc = DateTime.UtcNow, closesAtUtc = DateTime.UtcNow.AddDays(30), version,
        });
        put.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AHiddenItem_HidesTheDriverAndTheOverallScoreThatContainIt()
    {
        // q3 (enablement, optional) is answered by 3 of 12: hidden. Overall favourable and the enablement driver both
        // contain it, so (overall − q1 − q2) or (enablement − q2) would give those 3 people's q3: both hidden too.
        var sid = await _f.CreateAndLaunchSurveyAsync(_f.TemplateId, "Contained items", "Anonymous", [_f.DeptOps]);
        for (var i = 0; i < 12; i++)
        {
            var answers = new List<object> { new { questionKey = "q1", value = 4 }, new { questionKey = "q2", value = 4 }, new { questionKey = "q4", value = 8 } };
            if (i < 3) answers.Add(new { questionKey = "q3", value = 1 });
            var (t, _) = await _f.OpenAsync(i, sid);
            (await _f.SubmitAsync(i, sid, t, answers.ToArray())).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        await _f.CloseAsync(sid);
        var r = await Get($"/api/engagement/surveys/{sid}/results", _f.TokenHr);
        Visible(r).Should().BeTrue();
        var q3 = r.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == "q3");
        q3.GetProperty("visible").GetBoolean().Should().BeFalse();
        r.GetProperty("favourablePct").ValueKind.Should().Be(JsonValueKind.Null, "the overall score contains the hidden q3");
        var drivers = r.GetProperty("drivers").EnumerateArray().ToDictionary(d => d.GetProperty("key").GetString()!, d => d.GetProperty("visible").GetBoolean());
        drivers["enablement"].Should().BeFalse("enablement contains q3");
        drivers["recognition"].Should().BeTrue("recognition (q1) contains no hidden item");
    }

    [Fact]
    public async Task OpenSurveyParticipation_IsTheCallersOwnScope_WithK()
    {
        // The live survey (company A1, open): 33 invited, 2 submitted, both Operations engineers (Mona's team of 11).
        var group = await Get($"/api/engagement/surveys/{_f.LiveSurveyId}/results", _f.TokenHr);
        group.GetProperty("resultsAvailable").GetBoolean().Should().BeFalse();
        group.GetProperty("participation").GetProperty("invited").GetInt32().Should().Be(33);
        var team = await Get($"/api/engagement/surveys/{_f.LiveSurveyId}/results", _f.TokenMgrEng);
        team.GetProperty("participation").GetProperty("visible").GetBoolean().Should().BeTrue();
        team.GetProperty("participation").GetProperty("invited").GetInt32().Should().Be(11, "a manager sees their own team's response rate");
        team.GetProperty("participation").GetProperty("submitted").GetInt32().Should().Be(2);
        var legal = await Get($"/api/engagement/surveys/{_f.LiveSurveyId}/results", _f.TokenMgrLegal);
        legal.GetProperty("participation").GetProperty("visible").GetBoolean().Should().BeFalse("Layla's team is three people");
        using var otherCompany = await _f.SendAsync(HttpMethod.Get, $"/api/engagement/surveys/{_f.LiveSurveyId}/results", _f.TokenHrManagerC2);
        otherCompany.StatusCode.Should().Be(HttpStatusCode.NotFound, "an A1 survey does not exist for an A2-only reader");
    }

    [Fact]
    public async Task AScopedReader_GetsOneFilterOrOneBreakdown_AndIsToldWhy()
    {
        var plain = await Get($"/api/engagement/surveys/{_f.GoldenSurveyId}/results?breakdown=tenure", _f.TokenMgrEng);
        plain.GetProperty("scopeDimension").GetString().Should().Be("team");
        using var refused = await _f.SendAsync(HttpMethod.Get,
            $"/api/engagement/surveys/{_f.GoldenSurveyId}/results?department={_f.DeptOps}&breakdown=tenure", _f.TokenMgrEng);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("code").GetString().Should().Be("scope_counts_as_filter");
        body.GetProperty("message").GetString().Should().Contain("team");
        (await Get($"/api/engagement/surveys/{_f.GoldenSurveyId}/results", _f.TokenHr)).GetProperty("scopeDimension").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
