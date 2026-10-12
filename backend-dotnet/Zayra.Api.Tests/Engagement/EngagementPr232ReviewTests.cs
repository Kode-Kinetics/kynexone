using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Engagement;

/// <summary>PR #232 review threads: purged comments keep their counts, a company-scoped HR user can create a template,
/// and the non-respondent list stays inside the caller's data scope.</summary>
[Collection("Engagement")]
public sealed class EngagementPr232ReviewTests
{
    private readonly EngagementHostFixture _f;
    public EngagementPr232ReviewTests(EngagementHostFixture f) => _f = f;

    private static JsonElement Item(JsonElement r, string key) =>
        r.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == key);

    [Fact]
    public async Task APurgedComment_StillCountsAsAnswered_OnlyItsTextIsGone()
    {
        var sid = await _f.CreateAndLaunchSurveyAsync(_f.TemplateId, "Retention keeps counts", "Anonymous", [_f.DeptOps]);
        for (var i = 0; i < 12; i++)
        {
            var (t, _) = await _f.OpenAsync(i, sid);
            (await _f.SubmitAsync(i, sid, t, [new { questionKey = "q1", value = 4 }, new { questionKey = "q2", value = 4 }, new { questionKey = "q4", value = 8 },
                new { questionKey = "q6", text = $"retained comment {i}" }])).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        await _f.CloseAsync(sid);
        var before = Item(await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{sid}/results", _f.TokenHr), "q6");
        before.GetProperty("visible").GetBoolean().Should().BeTrue();
        before.GetProperty("commentCount").GetInt32().Should().Be(12);

        // Exactly what EngagementFreeTextRetentionRule does to an expired survey's comments.
        await using (var db = _f.CreateDb())
            await db.EngagementSurveyAnswers.IgnoreQueryFilters().Where(a => a.SurveyId == sid && a.TextValue != null)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.TextValue, (string?)null).SetProperty(a => a.TextPurged, true));

        var after = Item(await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{sid}/results", _f.TokenHr), "q6");
        after.GetProperty("visible").GetBoolean().Should().BeTrue("retention removes the words, not the fact that 12 people answered");
        after.GetProperty("answered").GetInt32().Should().Be(12);
        after.GetProperty("commentCount").GetInt32().Should().Be(12);
        var comments = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{sid}/comments?question=q6", _f.TokenHr);
        comments.GetProperty("comments").GetArrayLength().Should().Be(0, "the purged text itself is never returned");
    }

    [Fact]
    public async Task ACompanyScopedHrUser_CreatesATemplate_ForTheirCompany()
    {
        var body = JsonSerializer.SerializeToElement(EngagementHostFixture.TemplateBody("Company template")).Deserialize<Dictionary<string, object?>>()!;
        body["companyId"] = null;
        using var res = await _f.SendAsync(HttpMethod.Post, "/api/engagement/templates", _f.TokenHrManagerC2, body);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var created = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        created.GetProperty("companyId").GetGuid().Should().Be(_f.CompanyA2, "an unspecified company defaults to the caller's only company");

        // A group-level reader still creates a tenant-wide one.
        var group = await _f.JsonAsync(HttpMethod.Post, "/api/engagement/templates", _f.TokenHr, body, HttpStatusCode.Created);
        group.GetProperty("companyId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task TheNonRespondentList_StaysInsideTheCallersDataScope()
    {
        // Mona (a line manager: team data scope) given engagement.manage by a per-user grant.
        Guid overrideId;
        await using (var db = _f.CreateDb())
        {
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.TenantId == _f.TenantA && u.Email == "mgr-eng@engagement.test");
            var grant = new UserPermissionOverride { TenantId = _f.TenantA, UserId = user.Id, PermissionKey = "engagement.manage", Effect = "Allow", Reason = "PR 232 test" };
            db.Set<UserPermissionOverride>().Add(grant);
            await db.SaveChangesAsync();
            overrideId = grant.Id;
        }
        try
        {
            var token = await _f.MintAsync(_f.TenantA, "mgr-eng");
            var sid = await _f.CreateAndLaunchSurveyAsync(_f.TemplateId, "Confidential, nobody answered", "Confidential");
            var all = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{sid}/non-respondents", _f.TokenHr);
            all.GetArrayLength().Should().Be(33, "HR (organisation scope) sees every invitee");

            var team = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{sid}/non-respondents", token);
            var mine = Enumerable.Range(0, 10).Append(30).Select(i => _f.Invitees[i]).ToHashSet();
            team.EnumerateArray().Select(r => r.GetProperty("employeeId").GetInt32()).Should().BeEquivalentTo(mine,
                "Mona sees only her own reporting line — the ten Operations engineers and the one who never responds");
        }
        finally
        {
            await using var db = _f.CreateDb();
            await db.Set<UserPermissionOverride>().IgnoreQueryFilters().Where(o => o.Id == overrideId).ExecuteDeleteAsync();
        }
    }
}
