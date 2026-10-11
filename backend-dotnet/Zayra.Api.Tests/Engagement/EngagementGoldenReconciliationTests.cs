using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Controllers.Reports;
using Zayra.Api.Infrastructure.Reports;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Engagement;

/// <summary>
/// ENG-06/07/10 — the dashboard, the export and the scheduled delivery against an INDEPENDENTLY hand-computed table.
///
/// <para>How the expected numbers were computed (by hand from <see cref="Golden"/>, cross-checked once with a
/// throwaway script, then frozen here as literals — the test never recomputes them):</para>
/// <list type="bullet">
/// <item>Invited 33 (Ops 22, Fin 8, Legal 3), submitted 30 → response rate 30/33 = 90.909 → 90.9 %.</item>
/// <item>q1 answered 30: 1×3, 2×4, 3×5, 4×11, 5×7 → sum 105 → mean 3.50; favourable (4–5) 18/30 = 60.0 %.</item>
/// <item>q2 answered 30: 2×3, 3×8, 4×12, 5×7 → sum 113 → mean 3.77 (3.7667); favourable 19/30 = 63.3 %.</item>
/// <item>q3 answered 25 (5 skipped): 2×2, 3×7, 4×12, 5×4 → sum 93 → mean 3.72; favourable 16/25 = 64.0 %.</item>
/// <item>eNPS (q4, 30 answered): promoters (9–10) 13 = 43.3 %, passives (7–8) 8 = 26.7 %, detractors (0–6) 9 =
/// 30.0 %; eNPS = (13 − 9)/30 = 13.3.</item>
/// <item>Overall favourable = favourable answers on every 5-point item / answered = (18+19+16)/(30+30+25) = 53/85 = 62.4 %.</item>
/// <item>Recognition (q1) favourable 60.0, mean 3.50. Enablement (q2+q3, 55 answers) favourable 35/55 = 63.6, mean 206/55 = 3.75.</item>
/// <item>q5 answered 28: office 9, hybrid 13, remote 6. Comments 10.</item>
/// <item>Operations only (20 respondents): favourable 44/57 = 77.2; eNPS (11 − 3)/20 = 40.0; recognition 70.0;
/// enablement 30/37 = 81.1; q3 17 answered, mean 69/17 = 4.06.</item>
/// <item>Engineering team of Mona (10 respondents): favourable 22/28 = 78.6; eNPS (6 − 1)/10 = 50.0.</item>
/// </list>
/// </summary>
[Collection("Engagement")]
public sealed class EngagementGoldenReconciliationTests
{
    private readonly EngagementHostFixture _f;
    public EngagementGoldenReconciliationTests(EngagementHostFixture f) => _f = f;

    private Task<JsonElement> Results(string query = "", string? token = null) =>
        _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{_f.GoldenSurveyId}/results{query}", token ?? _f.TokenHr);

    private static JsonElement Item(JsonElement results, string key) =>
        results.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == key);

    private static JsonElement Driver(JsonElement results, string key) =>
        results.GetProperty("drivers").EnumerateArray().Single(i => i.GetProperty("key").GetString() == key);

    [Fact]
    public async Task Golden_WholeSurvey_MatchesTheHandComputedTable_Exactly()
    {
        var r = await Results();
        r.GetProperty("visible").GetBoolean().Should().BeTrue();
        r.GetProperty("minCohortSize").GetInt32().Should().Be(5);
        r.GetProperty("respondents").GetInt32().Should().Be(30);

        var p = r.GetProperty("participation");
        p.GetProperty("visible").GetBoolean().Should().BeTrue();
        p.GetProperty("invited").GetInt32().Should().Be(33);
        p.GetProperty("submitted").GetInt32().Should().Be(30);
        p.GetProperty("responseRatePct").GetDecimal().Should().Be(90.9m);

        r.GetProperty("favourablePct").GetDecimal().Should().Be(62.4m);
        var enps = r.GetProperty("enps");
        enps.GetProperty("score").GetDecimal().Should().Be(13.3m);
        enps.GetProperty("promotersPct").GetDecimal().Should().Be(43.3m);
        enps.GetProperty("passivesPct").GetDecimal().Should().Be(26.7m);
        enps.GetProperty("detractorsPct").GetDecimal().Should().Be(30.0m);
        enps.GetProperty("answered").GetInt32().Should().Be(30);

        var q1 = Item(r, "q1");
        q1.GetProperty("answered").GetInt32().Should().Be(30);
        q1.GetProperty("mean").GetDecimal().Should().Be(3.50m);
        q1.GetProperty("favourablePct").GetDecimal().Should().Be(60.0m);
        q1.GetProperty("distribution").EnumerateArray().Select(d => d.GetProperty("count").GetInt32()).Should().Equal(3, 4, 5, 11, 7);

        var q2 = Item(r, "q2");
        q2.GetProperty("mean").GetDecimal().Should().Be(3.77m);
        q2.GetProperty("favourablePct").GetDecimal().Should().Be(63.3m);
        q2.GetProperty("distribution").EnumerateArray().Select(d => d.GetProperty("count").GetInt32()).Should().Equal(0, 3, 8, 12, 7);

        var q3 = Item(r, "q3");
        q3.GetProperty("answered").GetInt32().Should().Be(25, "a skipped item leaves that item's denominator");
        q3.GetProperty("mean").GetDecimal().Should().Be(3.72m);
        q3.GetProperty("favourablePct").GetDecimal().Should().Be(64.0m);

        Item(r, "q4").GetProperty("enps").GetProperty("score").GetDecimal().Should().Be(13.3m);

        var q5 = Item(r, "q5");
        q5.GetProperty("answered").GetInt32().Should().Be(28);
        q5.GetProperty("distribution").EnumerateArray().Select(d => (d.GetProperty("key").GetString(), d.GetProperty("count").GetInt32()))
            .Should().Equal(("office", 9), ("hybrid", 13), ("remote", 6));

        Item(r, "q6").GetProperty("commentCount").GetInt32().Should().Be(10);

        var recognition = Driver(r, "recognition");
        recognition.GetProperty("favourablePct").GetDecimal().Should().Be(60.0m);
        recognition.GetProperty("mean").GetDecimal().Should().Be(3.50m);
        var enablement = Driver(r, "enablement");
        enablement.GetProperty("favourablePct").GetDecimal().Should().Be(63.6m);
        enablement.GetProperty("mean").GetDecimal().Should().Be(3.75m);
    }

    [Fact]
    public async Task Golden_ByDepartment_SmallDepartmentHidden_AndItsComplementSiblingHiddenToo()
    {
        var r = await Results("?breakdown=department");
        var cells = r.GetProperty("breakdown").GetProperty("cells").EnumerateArray()
            .ToDictionary(c => c.GetProperty("label").GetString()!, c => c);
        cells.Should().ContainKeys("Operations", "Finance", "Legal");

        cells["Legal"].GetProperty("visible").GetBoolean().Should().BeFalse();
        cells["Legal"].GetProperty("suppressionReason").GetString().Should().Be("suppressed");
        cells["Legal"].GetProperty("respondents").ValueKind.Should().Be(JsonValueKind.Null, "a hidden cell never reveals its count");

        // Total 30 − Operations 20 = 10 = Finance 7 + Legal 3: with Finance visible, Legal = 30 − 20 − 7.
        cells["Finance"].GetProperty("visible").GetBoolean().Should().BeFalse();
        cells["Finance"].GetProperty("suppressionReason").GetString().Should().Be("suppressed");
        cells["Finance"].GetProperty("favourablePct").ValueKind.Should().Be(JsonValueKind.Null);

        var ops = cells["Operations"];
        ops.GetProperty("visible").GetBoolean().Should().BeTrue();
        ops.GetProperty("respondents").GetInt32().Should().Be(20);
        ops.GetProperty("favourablePct").GetDecimal().Should().Be(77.2m);
        ops.GetProperty("enps").GetDecimal().Should().Be(40.0m);
        ops.GetProperty("driverFavourablePct").GetProperty("recognition").GetDecimal().Should().Be(70.0m);
        ops.GetProperty("driverFavourablePct").GetProperty("enablement").GetDecimal().Should().Be(81.1m);
    }

    [Fact]
    public async Task Golden_FilteredToOperations_IsVisible_AndMatches()
    {
        var r = await Results($"?department={_f.DeptOps}");
        r.GetProperty("visible").GetBoolean().Should().BeTrue();
        r.GetProperty("respondents").GetInt32().Should().Be(20);
        r.GetProperty("favourablePct").GetDecimal().Should().Be(77.2m);
        r.GetProperty("enps").GetProperty("score").GetDecimal().Should().Be(40.0m);
        Item(r, "q3").GetProperty("answered").GetInt32().Should().Be(17);
        Item(r, "q3").GetProperty("mean").GetDecimal().Should().Be(4.06m);
        // Comments: Operations wrote 7 of the 10; the other 3 would be isolated by subtraction → hidden.
        Item(r, "q6").GetProperty("visible").GetBoolean().Should().BeFalse();
        Item(r, "q6").GetProperty("suppressionReason").GetString().Should().Be("suppressed", "the commenters outside Operations are a group of 3");
    }

    [Fact]
    public async Task Golden_IntersectingFilterBelowK_IsHidden()
    {
        // Operations ∩ Analyst = 4 respondents.
        var r = await Results($"?department={_f.DeptOps}&designation={_f.DesigAnalyst}");
        r.GetProperty("visible").GetBoolean().Should().BeFalse();
        r.GetProperty("suppressionReason").GetString().Should().Be("suppressed");
        r.GetProperty("respondents").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("favourablePct").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("items").EnumerateArray().Should().OnlyContain(i => !i.GetProperty("visible").GetBoolean());
    }

    [Fact]
    public async Task Golden_FilterDifferenceAttack_IsBlocked()
    {
        // Engineers = 11 (10 in Operations, 1 in Finance). Shown beside Operations (20) and the whole survey, it would
        // let Engineer − (Operations ∩ Engineer), or Engineer + Operations − … isolate the one Finance engineer: the
        // engineer is an atom of one, so the cheapest view that separates them — the designation — is hidden.
        var engineers = await Results($"?designation={_f.DesigEngineer}");
        engineers.GetProperty("visible").GetBoolean().Should().BeFalse();
        engineers.GetProperty("suppressionReason").GetString().Should().Be("suppressed");
        (await Results($"?department={_f.DeptOps}")).GetProperty("visible").GetBoolean().Should().BeTrue("Operations stays visible");
        var slice = await Results($"?department={_f.DeptOps}&designation={_f.DesigEngineer}");
        slice.GetProperty("visible").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Golden_Comments_ArePermissionGated_SuppressedBelowK_AndCarryNoIdentity()
    {
        var all = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{_f.GoldenSurveyId}/comments?question=q6", _f.TokenHr);
        all.GetProperty("visible").GetBoolean().Should().BeTrue();
        var comments = all.GetProperty("comments").EnumerateArray().Select(c => c.GetString()!).ToList();
        comments.Should().BeEquivalentTo(Golden.WithComment.Select(Golden.CommentOf));
        comments.Should().NotContain("DRAFT-ONLY-TEXT").And.NotContain("UNSUBMITTED-DRAFT-TEXT");
        all.GetRawText().Should().NotContain("employee").And.NotContain("responseId");

        var ops = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{_f.GoldenSurveyId}/comments?question=q6&department={_f.DeptOps}", _f.TokenHr);
        ops.GetProperty("visible").GetBoolean().Should().BeFalse();
        ops.GetProperty("comments").GetArrayLength().Should().Be(0);

        // The Manager role has engagement.read but not engagement.comments.read.
        using var denied = await _f.SendAsync(HttpMethod.Get, $"/api/engagement/surveys/{_f.GoldenSurveyId}/comments?question=q6", _f.TokenMgrEng);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Golden_ManagerSeesOnlyTheirTeam_AndATinyTeamIsSuppressed()
    {
        var team = await Results(token: _f.TokenMgrEng);
        team.GetProperty("visible").GetBoolean().Should().BeTrue();
        team.GetProperty("respondents").GetInt32().Should().Be(10, "Mona manages the ten Operations engineers who responded");
        team.GetProperty("favourablePct").GetDecimal().Should().Be(78.6m);
        team.GetProperty("enps").GetProperty("score").GetDecimal().Should().Be(50.0m);
        team.GetProperty("participation").GetProperty("invited").GetInt32().Should().Be(11);
        team.GetProperty("participation").GetProperty("submitted").GetInt32().Should().Be(10);

        var legal = await Results(token: _f.TokenMgrLegal);
        legal.GetProperty("visible").GetBoolean().Should().BeFalse("Layla's team is three people");
        legal.GetProperty("participation").GetProperty("visible").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Golden_Export_EqualsTheSuppressedDashboard_AndExcludesCommentsUnlessAsked()
    {
        var dashboard = await Results("?breakdown=department");
        using var csv = await _f.SendAsync(HttpMethod.Post, $"/api/engagement/surveys/{_f.GoldenSurveyId}/export", _f.TokenHr,
            new { format = "csv", filters = new { breakdown = "department" } });
        csv.StatusCode.Should().Be(HttpStatusCode.OK);
        // The writer quotes every cell (RFC 4180); compare the values.
        var text = Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync()).Replace("\"", "");
        text.Should().Contain("Response rate %,90.9");
        text.Should().Contain("Favourable %,62.4");
        text.Should().Contain("eNPS,13.3");
        text.Should().Contain("department,Operations,20,77.2,40");
        text.Should().Contain("department,Finance,Suppressed,Suppressed,Suppressed");
        text.Should().Contain("department,Legal,Suppressed,Suppressed,Suppressed");
        text.Should().NotContain("Golden comment", "comments are excluded unless asked for and permitted");
        dashboard.GetProperty("favourablePct").GetDecimal().Should().Be(62.4m);

        using var withComments = await _f.SendAsync(HttpMethod.Post, $"/api/engagement/surveys/{_f.GoldenSurveyId}/export", _f.TokenHr,
            new { format = "csv", includeComments = true });
        Encoding.UTF8.GetString(await withComments.Content.ReadAsByteArrayAsync()).Should().Contain("Golden comment number 00");

        using var managerComments = await _f.SendAsync(HttpMethod.Post, $"/api/engagement/surveys/{_f.GoldenSurveyId}/export", _f.TokenMgrEng,
            new { format = "csv", includeComments = true });
        managerComments.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var xlsx = await _f.SendAsync(HttpMethod.Post, $"/api/engagement/surveys/{_f.GoldenSurveyId}/export", _f.TokenHr, new { format = "xlsx" });
        xlsx.StatusCode.Should().Be(HttpStatusCode.OK);
        xlsx.Content.Headers.ContentType!.MediaType.Should().Be(ReportWorkbookWriter.ContentType);

        await using var db = _f.CreateDb();
        (await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.TenantId == _f.TenantA && a.Action == "engagement.results.exported"
            && a.EntityId == _f.GoldenSurveyId.ToString())).Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Golden_ScheduledDelivery_IsSuppressedIdentically_AndRechecksTheRecipient()
    {
        await using (var db = _f.CreateDb())
        {
            db.ReportSchedules.Add(new ReportSchedule
            {
                TenantId = _f.TenantA, ReportKey = "engagement.results", ReportName = "Golden pulse weekly", Category = "Engagement",
                FiltersJson = JsonSerializer.Serialize(new ReportFilters { SurveyId = _f.GoldenSurveyId }),
                Frequency = "Weekly", DeliveryMethod = "Email", ExportFormat = "csv",
                Recipients = "hr-director@engagement.test,mgr-eng@engagement.test,stranger@example.test",
                IsActive = true, CreatedBy = _f.HrUserId, NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }
        var before = _f.Email.Sent.Count;
        var worker = new ReportScheduleWorker(_f.Host.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReportScheduleWorker>.Instance);
        await worker.ProcessOnceAsync(default);

        var sent = _f.Email.Sent.Skip(before).Where(s => s.Subject.Contains("Golden pulse weekly")).ToList();
        sent.Select(s => s.To).Should().Equal("hr-director@engagement.test");
        var attached = Encoding.UTF8.GetString(sent.Single().Attachments.Single().Data);

        using var export = await _f.SendAsync(HttpMethod.Post, $"/api/engagement/surveys/{_f.GoldenSurveyId}/export", _f.TokenHr, new { format = "csv" });
        var exported = Encoding.UTF8.GetString(await export.Content.ReadAsByteArrayAsync());
        static string WithoutTimestamp(string s) => string.Join('\n', s.Split('\n').Where(l => !l.Contains("Generated (UTC)", StringComparison.Ordinal)));
        WithoutTimestamp(attached).Should().Be(WithoutTimestamp(exported), "the scheduled file and the export are the same suppressed numbers");
        attached.Should().NotContain("Golden comment");

        await using var check = _f.CreateDb();
        var log = await check.ReportExecutionLogs.IgnoreQueryFilters().Where(l => l.TenantId == _f.TenantA && l.ReportKey == "engagement.results")
            .OrderByDescending(l => l.CreatedAtUtc).FirstAsync();
        log.ErrorMessage.Should().Contain("mgr-eng@engagement.test").And.Contain("stranger@example.test",
            "the team-scoped manager and a non-user are refused on every run");
    }
}
