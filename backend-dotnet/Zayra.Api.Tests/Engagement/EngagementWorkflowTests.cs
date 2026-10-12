using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zayra.Api.Infrastructure.Engagement;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Infrastructure.Retention;
using Zayra.Api.Infrastructure.Retention.Rules;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Engagement;

/// <summary>
/// ENG-03/04/05/08/09 and the authorization negatives, through the real pipeline on a real database.
/// </summary>
[Collection("Engagement")]
public sealed class EngagementWorkflowTests
{
    private readonly EngagementHostFixture _f;
    public EngagementWorkflowTests(EngagementHostFixture f) => _f = f;

    // ── ENG-05: anonymity at rest ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Anonymous_ResponseRows_HaveNoIdentityColumn_AndTheDatabaseHasNoJoinPathToAPerson()
    {
        await using var db = _f.CreateDb();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();

        async Task<List<string>> Column(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            var list = new List<string>();
            while (await reader.ReadAsync()) list.Add(reader.GetString(0));
            return list;
        }

        (await Column("SELECT column_name FROM information_schema.columns WHERE table_name = 'survey_responses' ORDER BY column_name"))
            .Should().BeEquivalentTo(new[] { "id", "tenant_id", "survey_id", "company_id", "department_id", "designation_id", "grade_band",
                "tenure_band", "cohort_manager_employee_id" }, "a response carries no date or time at all — the cycle is the only 'when'");
        (await Column("SELECT column_name FROM information_schema.columns WHERE table_name = 'survey_answers' ORDER BY column_name"))
            .Should().BeEquivalentTo("id", "tenant_id", "company_id", "survey_id", "response_id", "question_key", "numeric_value",
                "choice_keys", "text_value", "text_purged");

        // Every foreign key out of the anonymous side, and every one INTO it.
        var fks = await Column("""
            SELECT tc.table_name || '->' || ccu.table_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_name = tc.constraint_name
            WHERE tc.constraint_type = 'FOREIGN KEY'
              AND (tc.table_name IN ('survey_responses','survey_answers') OR ccu.table_name IN ('survey_responses','survey_answers'))
            """);
        fks.Should().BeEquivalentTo("survey_responses->surveys", "survey_answers->surveys", "survey_answers->survey_responses");

        // Anonymous ledger: the submitted flag only — no date, no "opened". No row points at a response.
        var ledger = await db.EngagementSurveyInvitations.IgnoreQueryFilters()
            .Where(i => i.SurveyId == _f.GoldenSurveyId && i.Status == EngagementInvitationStatuses.Submitted).ToListAsync();
        ledger.Should().HaveCount(30);
        ledger.Should().OnlyContain(i => i.IdentifiedResponseId == null && i.DraftCipher == null && i.SessionTokenHash == null
                                         && i.SubmittedOn == null && i.FirstOpenedOn == null);
        (await db.EngagementSurveyInvitations.IgnoreQueryFilters().AnyAsync(i => i.Mode == "Anonymous" && (i.Status == "Opened" || i.FirstOpenedOn != null)))
            .Should().BeFalse("an anonymous ledger never records who opened a survey");
        var opened = async () =>
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE survey_invitations SET first_opened_on = current_date WHERE survey_id = @s AND mode = 'Anonymous'", conn);
            cmd.Parameters.AddWithValue("s", _f.GoldenSurveyId);
            await cmd.ExecuteNonQueryAsync();
        };
        (await opened.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ck_survey_invitations__anonymous_ledger_minimal");

        // The CHECK refuses an identified link on a non-identified invitation.
        var act = async () =>
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE survey_invitations SET identified_response_id = gen_random_uuid() WHERE survey_id = @s AND status = 'Submitted'", conn);
            cmd.Parameters.AddWithValue("s", _f.GoldenSurveyId);
            await cmd.ExecuteNonQueryAsync();
        };
        (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ck_survey_invitations__identified_link_only_when_identified");

        // Responses are append-only (trigger from the migration).
        var update = async () =>
        {
            await using var cmd = new NpgsqlCommand("UPDATE survey_responses SET grade_band = 'x' WHERE survey_id = @s", conn);
            cmd.Parameters.AddWithValue("s", _f.GoldenSurveyId);
            await cmd.ExecuteNonQueryAsync();
        };
        (await update.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("survey_response_append_only");
    }

    [Fact]
    public async Task Audit_HoldsSurveyLevelAdminActionsOnly_NeverAResponse()
    {
        await using var db = _f.CreateDb();
        var responseIds = await db.EngagementSurveyResponses.IgnoreQueryFilters().Where(r => r.SurveyId == _f.GoldenSurveyId)
            .Select(r => r.Id.ToString()).ToListAsync();
        responseIds.Should().HaveCount(30);
        var audit = await db.AuditLogs.IgnoreQueryFilters().Where(a => a.TenantId == _f.TenantA).ToListAsync();
        audit.Should().NotContain(a => a.EntityName.Contains("Response") || a.EntityName.Contains("Answer") || a.EntityName.Contains("Invitation"));
        audit.Should().NotContain(a => a.EntityId != null && responseIds.Contains(a.EntityId));
        audit.Where(a => a.Metadata != null).Should().NotContain(a => responseIds.Any(id => a.Metadata!.Contains(id)));
        audit.Where(a => a.Action.StartsWith("engagement.", StringComparison.Ordinal))
            .Select(a => a.Action).Distinct()
            .Should().OnlyContain(action => action.StartsWith("engagement.template.") || action.StartsWith("engagement.survey.")
                || action.StartsWith("engagement.results.") || action.StartsWith("engagement.comments.")
                || action.StartsWith("engagement.action.") || action.StartsWith("engagement.settings.") || action.StartsWith("engagement.identified."));
        // Nothing the respondents did is in the notification outbox either.
        (await db.NotificationDeliveries.IgnoreQueryFilters().AnyAsync(d => d.TenantId == _f.TenantA && responseIds.Contains(d.EntityId!)))
            .Should().BeFalse();
    }

    // ── ENG-04: one response per invitee, drafts, lock ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DoubleSubmit_IsRefused_WithOneResponseOnly()
    {
        _f.SecondSubmitStatus.Should().Be(HttpStatusCode.Conflict);
        _f.SecondSubmitError.Should().Be("survey_already_submitted");
        await using var db = _f.CreateDb();
        (await db.EngagementSurveyResponses.IgnoreQueryFilters().CountAsync(r => r.SurveyId == _f.LiveSurveyId)).Should().Be(2);

        // Re-opening after submit is refused too.
        using var reopen = await _f.SendAsync(HttpMethod.Post, "/api/ess/surveys/open", _f.EmployeeTokens[1], new { surveyId = _f.LiveSurveyId });
        reopen.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ConcurrentSubmits_FromTwoTabs_ProduceOneResponse()
    {
        var template = await _f.CreatePublishedTemplateAsync("Concurrency template");
        var survey = await _f.CreateAndLaunchSurveyAsync(template, "Concurrency pulse", "Confidential", [_f.DeptLegal]);
        var (token, _) = await _f.OpenAsync(29, survey);
        var answers = Golden.Answers(29);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _f.SubmitAsync(29, survey, token, answers)));
        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(3);
        await using var db = _f.CreateDb();
        (await db.EngagementSurveyResponses.IgnoreQueryFilters().CountAsync(r => r.SurveyId == survey)).Should().Be(1);
    }

    [Fact]
    public async Task Draft_ComesBackToItsOwner_IsEncryptedAtRest_AndIsNeverReported()
    {
        _f.ReopenedDraft.EnumerateArray().Should().Contain(a => a.GetProperty("questionKey").GetString() == "q6"
            && a.GetProperty("text").GetString() == "DRAFT-ONLY-TEXT");

        await using var db = _f.CreateDb();
        var invitation = await db.EngagementSurveyInvitations.IgnoreQueryFilters()
            .SingleAsync(i => i.SurveyId == _f.LiveSurveyId && i.EmployeeId == _f.Invitees[31]);
        invitation.Status.Should().Be(EngagementInvitationStatuses.Invited, "an anonymous ledger has no Opened state");
        invitation.FirstOpenedOn.Should().BeNull();
        invitation.DraftCipher.Should().NotBeNullOrEmpty();
        invitation.DraftCipher.Should().NotContain("UNSUBMITTED-DRAFT-TEXT", "drafts are protected with DataProtection at rest");
        (await db.EngagementSurveyAnswers.IgnoreQueryFilters().AnyAsync(a => a.TextValue == "UNSUBMITTED-DRAFT-TEXT" || a.TextValue == "DRAFT-ONLY-TEXT"))
            .Should().BeFalse();
        var live = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{_f.LiveSurveyId}/results", _f.TokenHr);
        live.GetRawText().Should().NotContain("DRAFT");
        live.GetProperty("resultsAvailable").GetBoolean().Should().BeFalse("anonymous results exist only once the survey closes");
        live.GetProperty("participation").GetProperty("submitted").GetInt32().Should().Be(2, "a draft is not a response");
    }

    [Fact]
    public async Task Anonymous_HasNoPersonView_AndNoIdentifiedView()
    {
        using var nonRespondents = await _f.SendAsync(HttpMethod.Get, $"/api/engagement/surveys/{_f.GoldenSurveyId}/non-respondents", _f.TokenHr);
        nonRespondents.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var identified = await _f.SendAsync(HttpMethod.Get, $"/api/engagement/surveys/{_f.GoldenSurveyId}/identified-responses", _f.TokenHr);
        identified.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Confidential_ShowsWhoHasNotResponded_Identified_LinksAnswersForThePermittedOnly()
    {
        var template = await _f.CreatePublishedTemplateAsync("Modes template");
        var confidential = await _f.CreateAndLaunchSurveyAsync(template, "Confidential pulse", "Confidential", [_f.DeptLegal]);
        var (token, session) = await _f.OpenAsync(27, confidential);
        session.GetProperty("privacyNoticeEn").GetString().Should().Contain("HR can see whether you have responded");
        (await _f.SubmitAsync(27, confidential, token, Golden.Answers(27))).StatusCode.Should().Be(HttpStatusCode.OK);
        var pending = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{confidential}/non-respondents", _f.TokenHr);
        pending.EnumerateArray().Select(e => e.GetProperty("employeeId").GetInt32()).Should().BeEquivalentTo(new[] { _f.Invitees[28], _f.Invitees[29] });

        var identified = await _f.CreateAndLaunchSurveyAsync(template, "Identified pulse", "Identified", [_f.DeptLegal]);
        var (t2, s2) = await _f.OpenAsync(28, identified);
        s2.GetProperty("privacyNoticeEn").GetString().Should().Contain("saved with your name");
        (await _f.SubmitAsync(28, identified, t2, Golden.Answers(28))).StatusCode.Should().Be(HttpStatusCode.OK);
        var people = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{identified}/identified-responses", _f.TokenHr);
        people.EnumerateArray().Should().ContainSingle(p => p.GetProperty("employeeId").GetInt32() == _f.Invitees[28]);
        // HR Manager does not hold engagement.identified.read.
        using var denied = await _f.SendAsync(HttpMethod.Get, $"/api/engagement/surveys/{identified}/identified-responses", _f.TokenMgrEng);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Jobs: reminders, recurrence ──────────────────────────────────────────────────────────────────────────────

    private EngagementSurveyWorker Worker() =>
        new(_f.Host.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<EngagementSurveyWorker>.Instance);

    [Fact]
    public async Task Reminders_GoOnce_AndAnAnonymousSurveyRemindsEveryone()
    {
        await Worker().RunOnceAsync(default);
        await Worker().RunOnceAsync(default);
        await using var db = _f.CreateDb();
        var reminded = await db.NotificationDeliveries.IgnoreQueryFilters()
            .Where(d => d.TenantId == _f.TenantA && d.EventCode == EngagementEventCodes.Reminder && d.EntityId == $"{_f.LiveSurveyId}:r1"
                        && d.Channel == "InApp")
            .Select(d => d.EmployeeId).ToListAsync();
        reminded.Should().BeEquivalentTo(_f.Invitees.Select(i => (int?)i),
            "an anonymous survey reminds every invitee, respondents included, so the notification ledger never singles out who has not answered");
        var invites = await db.NotificationDeliveries.IgnoreQueryFilters()
            .CountAsync(d => d.TenantId == _f.TenantA && d.EventCode == EngagementEventCodes.Invite && d.EntityId == _f.LiveSurveyId.ToString() && d.Channel == "InApp");
        invites.Should().Be(33, "one invite per invitee, even after the worker re-ran the invite step");
    }

    [Fact]
    public async Task Recurrence_CreatesTheNextCycleExactlyOnce_AndTrendSpansCycles()
    {
        var template = await _f.CreatePublishedTemplateAsync("Recurring template");
        var first = await _f.CreateAndLaunchSurveyAsync(template, "Quarterly pulse", "Anonymous", [_f.DeptFin], recurrence: "Quarterly");
        var detail = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{first}", _f.TokenHr);
        await _f.JsonAsync(HttpMethod.Post, $"/api/engagement/surveys/{first}/close", _f.TokenHr,
            new { version = detail.GetProperty("survey").GetProperty("version").GetInt32() });
        await Worker().RunOnceAsync(default);
        await Worker().RunOnceAsync(default);

        await using var db = _f.CreateDb();
        var series = await db.EngagementSurveys.IgnoreQueryFilters().Where(s => s.SeriesId == first).OrderBy(s => s.CycleNumber).ToListAsync();
        series.Select(s => s.CycleNumber).Should().Equal(1, 2);
        series[1].Status.Should().Be(EngagementSurveyStatuses.Scheduled);
        series[1].OpensAtUtc.Should().BeCloseTo(series[0].OpensAtUtc.AddMonths(3), TimeSpan.FromSeconds(1));
        using (var scope = _f.Host.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<EngagementSurveyService>();
            (await service.CreateNextCycleAsync(series[0], default)).Should().BeNull("the next cycle already exists");
        }
        var trend = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{first}/trend", _f.TokenHr);
        trend.EnumerateArray().Should().ContainSingle(p => p.GetProperty("cycleNumber").GetInt32() == 1);
        (await db.AuditLogs.IgnoreQueryFilters().CountAsync(a => a.Action == "engagement.survey.closed" && a.EntityId == first.ToString())).Should().Be(1);
    }

    [Fact]
    public async Task Worker_SkipsAPoisonedSurvey_AndStillOpensTheOthers()
    {
        var template = await _f.CreatePublishedTemplateAsync("Poison template");
        async Task<Guid> Scheduled(string title)
        {
            var created = await _f.JsonAsync(HttpMethod.Post, "/api/engagement/surveys", _f.TokenHr, new
            {
                templateId = template, titleEn = title, titleAr = "استبيان", mode = "Anonymous", companyId = _f.CompanyA1,
                audience = new { departmentIds = new[] { _f.DeptLegal } }, opensAtUtc = DateTime.UtcNow.AddDays(2),
                closesAtUtc = DateTime.UtcNow.AddDays(9), recurrence = "None", reminderOffsetsDays = Array.Empty<int>(),
            }, HttpStatusCode.Created);
            var id = created.GetProperty("id").GetGuid();
            var launched = await _f.JsonAsync(HttpMethod.Post, $"/api/engagement/surveys/{id}/launch", _f.TokenHr, new { version = created.GetProperty("version").GetInt32() });
            launched.GetProperty("status").GetString().Should().Be("Scheduled");
            return id;
        }
        var poisoned = await Scheduled("Poisoned");
        var healthy = await Scheduled("Healthy");
        await using (var db = _f.CreateDb())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE surveys SET opens_at_utc = now() - interval '1 hour' WHERE id IN ({poisoned}, {healthy})");
            // A JSON string where an audience object belongs: the audience cannot be read.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE surveys SET audience_json = '\"broken\"'::jsonb WHERE id = {poisoned}");
        }
        await Worker().RunOnceAsync(default);
        await using var check = _f.CreateDb();
        (await check.EngagementSurveys.IgnoreQueryFilters().SingleAsync(s => s.Id == poisoned)).Status.Should().Be("Scheduled");
        var opened = await check.EngagementSurveys.IgnoreQueryFilters().SingleAsync(s => s.Id == healthy);
        opened.Status.Should().Be("Open", "one survey that cannot be processed must not stop the others");
        opened.InvitesSentAtUtc.Should().NotBeNull("the sweep that opened it also sent its invites");
        await check.Database.ExecuteSqlInterpolatedAsync($"UPDATE surveys SET status = 'Cancelled' WHERE id = {poisoned}");
    }

    [Fact]
    public async Task RaisingTheTenantMinimum_ProtectsSurveysAlreadyRunning()
    {
        try
        {
            await _f.JsonAsync(HttpMethod.Put, "/api/engagement/settings", _f.TokenHr, new { minCohortSize = 31, freeTextRetentionMonths = 24 });
            var r = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{_f.GoldenSurveyId}/results", _f.TokenHr);
            r.GetProperty("minCohortSize").GetInt32().Should().Be(31);
            r.GetProperty("visible").GetBoolean().Should().BeFalse("30 respondents are fewer than the tenant's new minimum of 31");
        }
        finally
        {
            await _f.JsonAsync(HttpMethod.Put, "/api/engagement/settings", _f.TokenHr, new { minCohortSize = 5, freeTextRetentionMonths = 24 });
        }
    }

    [Fact]
    public async Task ATemplateOptionKey_IsBoundedSoEveryChoiceFitsItsColumn()
    {
        using var response = await _f.SendAsync(HttpMethod.Post, "/api/engagement/templates", _f.TokenHr, new
        {
            nameEn = "Long keys", nameAr = "مفاتيح طويلة",
            questions = new object[]
            {
                new { key = "q1", type = "multi", textEn = "Pick", textAr = "اختر", required = true,
                    options = new[] { new { key = "an-option-key-longer-than-16", labelEn = "A", labelAr = "أ" }, new { key = "b", labelEn = "B", labelAr = "ب" } } },
            },
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("1–16");
    }

    // ── ENG-08: actions ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Action_Lifecycle_OwnerProgressesItToDone_AndOverdueReminderGoesOnce()
    {
        var created = await _f.JsonAsync(HttpMethod.Post, "/api/engagement/actions", _f.TokenHr, new
        {
            surveyId = _f.GoldenSurveyId, sourceKind = "Driver", sourceRef = "recognition", title = "Monthly recognition round",
            description = "Recognition favourable is 60 %.", ownerEmployeeId = _f.MgrEngId, dueOn = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"),
        }, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("overdue").GetBoolean().Should().BeTrue();

        var mine = await _f.JsonAsync(HttpMethod.Get, "/api/ess/surveys/actions", _f.TokenMgrEng);
        var action = mine.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == id);
        var started = await _f.JsonAsync(HttpMethod.Post, "/api/ess/surveys/actions/progress", _f.TokenMgrEng,
            new { actionId = id, version = action.GetProperty("version").GetInt32(), status = "InProgress", note = "Booked the first round." });
        // A stale version is refused.
        using var stale = await _f.SendAsync(HttpMethod.Post, "/api/ess/surveys/actions/progress", _f.TokenMgrEng,
            new { actionId = id, version = action.GetProperty("version").GetInt32(), status = "Done", note = "late" });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await Worker().RunOnceAsync(default);
        await Worker().RunOnceAsync(default);

        var done = await _f.JsonAsync(HttpMethod.Post, "/api/ess/surveys/actions/progress", _f.TokenMgrEng,
            new { actionId = id, version = started.GetProperty("version").GetInt32(), status = "Done", note = "Held and recorded." });
        done.GetProperty("status").GetString().Should().Be("Done");
        done.GetProperty("progress").EnumerateArray().Select(p => p.GetProperty("status").GetString()).Should().Equal("Open", "InProgress", "Done");

        // Survives a reload.
        var listed = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/actions?surveyId={_f.GoldenSurveyId}", _f.TokenHr);
        listed.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == id).GetProperty("status").GetString().Should().Be("Done");

        await using var db = _f.CreateDb();
        (await db.NotificationDeliveries.IgnoreQueryFilters().CountAsync(d => d.EventCode == EngagementEventCodes.ActionDue
            && d.EntityId!.StartsWith(id.ToString()) && d.Channel == "InApp")).Should().Be(1);
    }

    // ── Authorization negatives ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Negatives_ManagerOtherTeam_HrOtherCompany_TenantB_EmployeeReadingOthers_FlagOff()
    {
        // A manager cannot give an action to, or touch an action of, someone outside their team.
        using var foreignOwner = await _f.SendAsync(HttpMethod.Post, "/api/engagement/actions", _f.TokenMgrEng, new
        {
            sourceKind = "Overall", title = "Not my team", ownerEmployeeId = _f.MgrLegalId, dueOn = DateTime.UtcNow.AddDays(5).ToString("yyyy-MM-dd"),
        });
        foreignOwner.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var legalAction = await _f.JsonAsync(HttpMethod.Post, "/api/engagement/actions", _f.TokenHr, new
        {
            sourceKind = "Cohort", sourceRef = "Legal", title = "Legal workload review", ownerEmployeeId = _f.MgrLegalId,
            dueOn = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd"),
        }, HttpStatusCode.Created);
        using var touch = await _f.SendAsync(HttpMethod.Post, $"/api/engagement/actions/{legalAction.GetProperty("id").GetGuid()}/progress",
            _f.TokenMgrEng, new { version = legalAction.GetProperty("version").GetInt32(), status = "Cancelled", note = "x" });
        touch.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var otherOwner = await _f.SendAsync(HttpMethod.Post, "/api/ess/surveys/actions/progress", _f.TokenMgrEng,
            new { actionId = legalAction.GetProperty("id").GetGuid(), version = legalAction.GetProperty("version").GetInt32(), status = "Done", note = "x" });
        otherOwner.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // HR scoped to the other company cannot see a company-1 survey at all.
        using var hrC2 = await _f.SendAsync(HttpMethod.Get, $"/api/engagement/surveys/{_f.GoldenSurveyId}/results", _f.TokenHrManagerC2);
        hrC2.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var c2List = await _f.JsonAsync(HttpMethod.Get, "/api/engagement/surveys", _f.TokenHrManagerC2);
        c2List.EnumerateArray().Should().NotContain(s => s.GetProperty("id").GetGuid() == _f.GoldenSurveyId);
        using var c2Launch = await _f.SendAsync(HttpMethod.Post, $"/api/engagement/surveys/{_f.GoldenSurveyId}/close", _f.TokenHrManagerC2, new { version = 0 });
        c2Launch.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);

        // Another tenant sees nothing of tenant A.
        using var tenantB = await _f.SendAsync(HttpMethod.Get, $"/api/engagement/surveys/{_f.GoldenSurveyId}/results", _f.TokenHrB);
        tenantB.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var tenantBComments = await _f.SendAsync(HttpMethod.Get, $"/api/engagement/surveys/{_f.GoldenSurveyId}/comments?question=q6", _f.TokenHrB);
        tenantBComments.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // An employee cannot read results, comments, or other people's invitations.
        using var employeeResults = await _f.SendAsync(HttpMethod.Get, $"/api/engagement/surveys/{_f.GoldenSurveyId}/results", _f.EmployeeTokens[3]);
        employeeResults.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var employeeExport = await _f.SendAsync(HttpMethod.Post, $"/api/engagement/surveys/{_f.GoldenSurveyId}/export", _f.EmployeeTokens[3], new { format = "csv" });
        employeeExport.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var mine = await _f.JsonAsync(HttpMethod.Get, "/api/ess/surveys", _f.EmployeeTokens[3]);
        mine.EnumerateArray().Should().OnlyContain(s => s.GetProperty("surveyId").GetGuid() != Guid.Empty);
        mine.GetRawText().Should().NotContain("employeeId");

        // Not invited (outsider survey for Legal only): employee 3 is in Operations.
        var template = await _f.CreatePublishedTemplateAsync("Negatives template");
        var legalOnly = await _f.CreateAndLaunchSurveyAsync(template, "Legal only", "Anonymous", [_f.DeptLegal]);
        using var notInvited = await _f.SendAsync(HttpMethod.Post, "/api/ess/surveys/open", _f.EmployeeTokens[3], new { surveyId = legalOnly });
        notInvited.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var forged = await _f.SendAsync(HttpMethod.Post, "/api/ess/surveys/submit", _f.EmployeeTokens[3],
            new { surveyId = legalOnly, token = "forged", answers = Golden.Answers(3) });
        forged.StatusCode.Should().Be(HttpStatusCode.NotFound);
        // Identified / Confidential: a token from one session does not survive a new open (single-use, replaced).
        // (Anonymous sessions are stateless by design — nothing is stored when an anonymous survey is opened.)
        var legalConfidential = await _f.CreateAndLaunchSurveyAsync(template, "Legal only, confidential", "Confidential", [_f.DeptLegal]);
        var (oldToken, _) = await _f.OpenAsync(27, legalConfidential);
        await _f.OpenAsync(27, legalConfidential);
        using var replay = await _f.SubmitAsync(27, legalConfidential, oldToken, Golden.Answers(27));
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict);
        JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString().Should().Be("survey_session_expired");

        // A tenant without the opt-in flag is refused at the gate.
        using var flagOff = await _f.SendAsync(HttpMethod.Get, "/api/engagement/surveys", _f.TokenHrC);
        flagOff.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        JsonDocument.Parse(await flagOff.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString().Should().Be("feature_not_enabled");
    }

    [Fact]
    public async Task PublishedTemplate_IsImmutable_InTheApiAndInTheDatabase()
    {
        var created = await _f.JsonAsync(HttpMethod.Post, "/api/engagement/templates", _f.TokenHr, EngagementHostFixture.TemplateBody("Immutable template"), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        var published = await _f.JsonAsync(HttpMethod.Post, $"/api/engagement/templates/{id}/publish", _f.TokenHr, new { version = created.GetProperty("version").GetInt32() });
        using var edit = await _f.SendAsync(HttpMethod.Put, $"/api/engagement/templates/{id}", _f.TokenHr,
            new { nameEn = "Changed", nameAr = "تغيير", drivers = Array.Empty<object>(), questions = Array.Empty<object>(), version = published.GetProperty("version").GetInt32() });
        edit.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await using var db = _f.CreateDb();
        var act = async () => await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE survey_templates SET name_en = 'tampered' WHERE id = {id}");
        (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("survey_template_published_immutable");

        var v2 = await _f.JsonAsync(HttpMethod.Post, $"/api/engagement/templates/{id}/versions", _f.TokenHr, null, HttpStatusCode.Created);
        v2.GetProperty("versionNumber").GetInt32().Should().Be(2);
        v2.GetProperty("status").GetString().Should().Be("Draft");
    }

    [Fact]
    public async Task Settings_FloorIsEnforced_AndAThresholdChangeIsAudited()
    {
        using var low = await _f.SendAsync(HttpMethod.Put, "/api/engagement/settings", _f.TokenHr, new { minCohortSize = 2, freeTextRetentionMonths = 24 });
        low.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await _f.JsonAsync(HttpMethod.Put, "/api/engagement/settings", _f.TokenHr, new { minCohortSize = 5, freeTextRetentionMonths = 24 });
        await using var db = _f.CreateDb();
        (await db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.TenantId == _f.TenantA && a.Action == "engagement.settings.updated")).Should().BeTrue();
        var act = async () => await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE surveys SET min_cohort_size = 2 WHERE id = {_f.GoldenSurveyId}");
        (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ck_surveys__min_cohort_floor");
    }

    // ── ENG-09: retention ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Retention_PurgesCommentText_KeepsEveryNumber_AndAuditsAtSurveyLevel()
    {
        var template = await _f.CreatePublishedTemplateAsync("Retention template");
        var survey = await _f.CreateAndLaunchSurveyAsync(template, "Old pulse", "Anonymous", [_f.DeptOps]);
        for (var i = 0; i < 20; i++)
        {
            var (token, _) = await _f.OpenAsync(i, survey);
            (await _f.SubmitAsync(i, survey, token, Golden.Answers(i))).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        await _f.CloseAsync(survey);
        var before = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{survey}/results", _f.TokenHr);
        await using (var db = _f.CreateDb())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE surveys SET closed_at_utc = now() - interval '25 months' WHERE id = {survey}");

        await SweepAsync(_f.TenantA);

        await using var check = _f.CreateDb();
        var answers = await check.EngagementSurveyAnswers.IgnoreQueryFilters().Where(a => a.SurveyId == survey).ToListAsync();
        answers.Where(a => a.QuestionKey == "q6").Should().OnlyContain(a => a.TextValue == null && a.TextPurged);
        answers.Count(a => a.QuestionKey == "q6").Should().Be(7);
        answers.Where(a => a.QuestionKey != "q6").Should().OnlyContain(a => !a.TextPurged);
        var audits = await check.RetentionPurgeAudits.IgnoreQueryFilters().Where(a => a.TenantId == _f.TenantA && a.RuleKey == RetentionRuleKeys.EngagementFreeTextExpired).ToListAsync();
        audits.Should().ContainSingle(a => a.EntityId == survey.ToString());

        var after = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{survey}/results", _f.TokenHr);
        after.GetProperty("favourablePct").GetDecimal().Should().Be(before.GetProperty("favourablePct").GetDecimal());
        after.GetProperty("enps").GetProperty("score").GetDecimal().Should().Be(before.GetProperty("enps").GetProperty("score").GetDecimal());
        after.GetProperty("respondents").GetInt32().Should().Be(20);
        var comments = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{survey}/comments?question=q6", _f.TokenHr);
        comments.GetProperty("comments").GetArrayLength().Should().Be(0);

        // Deleting the closed survey removes its responses and is audited once, at survey level.
        var reloaded = await _f.JsonAsync(HttpMethod.Get, $"/api/engagement/surveys/{survey}", _f.TokenHr);
        using var delete = await _f.SendAsync(HttpMethod.Delete, $"/api/engagement/surveys/{survey}", _f.TokenHr);
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await check.EngagementSurveyResponses.IgnoreQueryFilters().CountAsync(r => r.SurveyId == survey)).Should().Be(0);
        (await check.EngagementSurveyAnswers.IgnoreQueryFilters().CountAsync(r => r.SurveyId == survey)).Should().Be(0);
        var deleted = await check.AuditLogs.IgnoreQueryFilters().SingleAsync(a => a.Action == "engagement.survey.deleted" && a.EntityId == survey.ToString());
        deleted.Metadata.Should().Contain("\"responsesDeleted\":20");
        reloaded.GetProperty("survey").GetProperty("status").GetString().Should().Be("Closed");
    }

    private async Task SweepAsync(Guid tenantId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _f.CreateDb());
        services.AddSingleton(new DataRetentionOptions { ScheduleEnabled = true, ApplyDeletions = true });
        services.AddSingleton(new BackgroundJobOptions { LeaseDuration = TimeSpan.FromMinutes(2), HeartbeatInterval = TimeSpan.FromSeconds(30) });
        services.AddScoped<IRetentionRule, EngagementFreeTextRetentionRule>();
        services.AddSingleton(DataRetentionSweepJobHandler.Descriptor);
        services.AddScoped<DataRetentionSweepJobHandler>();
        services.AddSingleton<BackgroundJobTypeRegistry>();
        services.AddScoped<BackgroundJobStore>();
        services.AddSingleton<BackgroundJobRunner>();
        await using var sp = services.BuildServiceProvider();
        var now = DateTime.UtcNow;
        Guid jobId;
        await using (var db = _f.CreateDb())
        {
            var store = new BackgroundJobStore(db, sp.GetRequiredService<BackgroundJobTypeRegistry>());
            jobId = (await store.EnqueueAsync(tenantId, DataRetentionSweepJobHandler.JobType,
                DataRetentionSweepJobHandler.DefaultIdempotencyKey(now) + ":eng", new DataRetentionSweepPayload(now), null, default)).Job.Id;
        }
        var runner = sp.GetRequiredService<BackgroundJobRunner>();
        for (var i = 0; i < 40; i++)
        {
            await using (var db = _f.CreateDb())
            {
                var job = await db.BackgroundJobs.IgnoreQueryFilters().Where(j => j.Id == jobId).Select(j => new { j.Status, j.LastError }).SingleAsync();
                if (BackgroundJobStatuses.IsTerminal(job.Status))
                {
                    job.Status.Should().Be(BackgroundJobStatuses.Succeeded, job.LastError ?? "no error");
                    return;
                }
            }
            if (!await runner.RunNextAsync("w", default, default, [DataRetentionSweepJobHandler.JobType])) await Task.Delay(50);
        }
        throw new TimeoutException("retention sweep did not finish");
    }
}
