using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Learning;
using Zayra.Api.Models;
using static Zayra.Api.Tests.Learning.LearningTestWorld;

namespace Zayra.Api.Tests.Learning;

/// <summary>
/// The cmi5 route (REGISTER §3.1, LRN-05/13): one-time fetch, token expiry, statement idempotency, binding of a token
/// to its session (actor, registration, activity, tenant), every moveOn rule, abandoned sessions, and — the production
/// trap — an anonymous AU call under STRICT entity scope still finds and completes its enrollment.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public sealed class LearningCmi5Tests(PostgresFixture fx)
{
    private const string Origin = "https://au.example.test:5411";

    private sealed record Launch(Guid EnrollmentId, Guid SessionId, string FetchToken, Guid Registration, string ActivityId, string ActorName, string HomePage);

    private static async Task<(LearningTestWorld World, Guid PathId)> WorldAsync(PostgresFixture fx, string moveOn, string code = "AU",
        CourseInput? course = null, int[]? employees = null)
    {
        var w = await CreateAsync(fx);
        var (pathId, _) = await w.PublishedPathAsync(code, [course ?? Cmi5(code, moveOn, validityMonths: 12)]);
        await w.AssignAsync(pathId, employees ?? [w.Ops1]);
        return (w, pathId);
    }

    private static async Task<Launch> LaunchAsync(LearningTestWorld w, Guid pathId, int employee = 0)
    {
        employee = employee == 0 ? w.Ops1 : employee;
        var enrollment = await w.EnrollmentAsync(employee, pathId, 1);
        var (scope, me) = w.Employee(employee);
        await using (scope)
        {
            var dto = await Get<LearningProgressService>(scope).LaunchAsync(me, employee, enrollment.Id, "https://ignored.test", null, default);
            dto.Kind.Should().Be(LearningContentKinds.Cmi5);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(dto.LaunchUrl!).Query);
            query["endpoint"].ToString().Should().Be("https://api.kynexone.test/api/learning/xapi/");
            var fetch = query["fetch"].ToString();
            fetch.Should().StartWith("https://api.kynexone.test/api/learning/cmi5/fetch/");
            var actor = JsonNode.Parse(query["actor"].ToString())!;
            return new Launch(enrollment.Id, dto.SessionId!.Value, fetch[(fetch.LastIndexOf('/') + 1)..], Guid.Parse(query["registration"]!),
                query["activityId"]!, actor["account"]!["name"]!.GetValue<string>(), actor["account"]!["homePage"]!.GetValue<string>());
        }
    }

    private static async Task<string> FetchAsync(LearningTestWorld w, Launch launch)
    {
        await using var scope = w.As(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()), strict: true);
        return await Get<LearningCmi5Service>(scope).FetchAsync(launch.FetchToken, default);
    }

    private static JsonElement Statement(Launch launch, string verb, Guid? id = null, bool? success = null, bool? completion = null,
        decimal? scaled = null, decimal? progress = null, string? actorName = null, Guid? registration = null, string? activity = null)
    {
        var result = new JsonObject();
        if (success is not null) result["success"] = success;
        if (completion is not null) result["completion"] = completion;
        if (scaled is not null) result["score"] = new JsonObject { ["scaled"] = scaled };
        if (progress is not null) result["extensions"] = new JsonObject { [Cmi5Verbs.ProgressExtension] = progress };
        var statement = new JsonObject
        {
            ["id"] = (id ?? Guid.NewGuid()).ToString(),
            ["actor"] = new JsonObject { ["objectType"] = "Agent", ["account"] = new JsonObject { ["homePage"] = launch.HomePage, ["name"] = actorName ?? launch.ActorName } },
            ["verb"] = new JsonObject { ["id"] = verb },
            ["object"] = new JsonObject { ["id"] = activity ?? launch.ActivityId, ["objectType"] = "Activity" },
            ["context"] = new JsonObject
            {
                ["registration"] = (registration ?? launch.Registration).ToString(),
                ["extensions"] = new JsonObject { [Cmi5Verbs.SessionIdExtension] = launch.SessionId.ToString() },
            },
            ["result"] = result,
        };
        return JsonDocument.Parse(statement.ToJsonString()).RootElement;
    }

    /// <summary>Exactly what the controller does for an AU call: anonymous principal, strict scope (production), Basic token, origin.</summary>
    private static async Task<IReadOnlyList<Guid>> PostAsync(LearningTestWorld w, string authToken, JsonElement body, string? origin = Origin)
    {
        await using var scope = w.As(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()), strict: true);
        var cmi5 = Get<LearningCmi5Service>(scope);
        var session = await cmi5.AuthenticateAsync("Basic " + authToken, origin, default);
        return await cmi5.StoreStatementsAsync(session, body, null, default);
    }

    [Fact]
    public async Task Fetch_IsOneTime_AndExpires()
    {
        var (w, pathId) = await WorldAsync(fx, Cmi5MoveOnValues.Completed);
        await using var _ = w;
        var first = await LaunchAsync(w, pathId);
        var token = await FetchAsync(w, first);
        Encoding.UTF8.GetString(Convert.FromBase64String(token)).Should().StartWith(first.SessionId.ToString("N") + ":");
        var reuse = () => FetchAsync(w, first);
        var error = (await reuse.Should().ThrowAsync<Cmi5Exception>()).Which;
        error.FetchErrorCode.Should().Be("1");
        error.Code.Should().Be("fetch_already_used");

        // A relaunch abandons the open session and issues a new fetch URL, which expires after 10 minutes.
        var second = await LaunchAsync(w, pathId);
        second.Registration.Should().Be(first.Registration, "cmi5 registration is per enrollment, constant across sessions");
        second.SessionId.Should().NotBe(first.SessionId);
        w.Clock.Advance(TimeSpan.FromMinutes(11));
        var late = () => FetchAsync(w, second);
        (await late.Should().ThrowAsync<Cmi5Exception>()).Which.FetchErrorCode.Should().Be("2");

        await using var db = w.Db();
        var sessions = await db.LearningLaunchSessions.AsNoTracking().Where(s => s.EnrollmentId == first.EnrollmentId).OrderBy(s => s.LaunchedAtUtc).ToListAsync();
        sessions[0].Status.Should().Be(LearningSessionStatuses.Abandoned);
        sessions.Should().OnlyContain(s => s.FetchTokenHash.Length == 64 && s.FetchTokenHash != first.FetchToken && s.FetchTokenHash != second.FetchToken);
        (await db.LearningXapiStatements.AsNoTracking().CountAsync(s => s.SessionId == first.SessionId && s.Verb == Cmi5Verbs.Abandoned)).Should().Be(1);
    }

    [Fact]
    public async Task ExpiredAuthToken_IsRefused_AndTheEnrollmentStaysInProgress()
    {
        var (w, pathId) = await WorldAsync(fx, Cmi5MoveOnValues.Completed);
        await using var _ = w;
        var launch = await LaunchAsync(w, pathId);
        var token = await FetchAsync(w, launch);
        await PostAsync(w, token, Statement(launch, Cmi5Verbs.Initialized));
        w.Clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));
        var late = () => PostAsync(w, token, Statement(launch, Cmi5Verbs.Completed, completion: true));
        (await late.Should().ThrowAsync<Cmi5Exception>()).Which.Code.Should().Be("token_expired");

        // Provider failure: the AU never terminated. The sweep abandons the session; nothing is completed.
        await using (var sweep = w.System())
            (await Get<LearningSweepService>(sweep).RunTenantAsync(w.TenantA, default)).SessionsAbandoned.Should().Be(1);
        await using var db = w.Db();
        (await db.LearningLaunchSessions.AsNoTracking().SingleAsync(s => s.Id == launch.SessionId)).Status.Should().Be(LearningSessionStatuses.Abandoned);
        (await db.LearningEnrollments.AsNoTracking().SingleAsync(e => e.Id == launch.EnrollmentId)).Status.Should().Be(LearningEnrollmentStatuses.InProgress);
    }

    [Fact]
    public async Task DuplicateStatementReplay_IsANoOp_AndCompletesOnce_UnderStrictAnonymousScope()
    {
        var (w, pathId) = await WorldAsync(fx, Cmi5MoveOnValues.Completed);
        await using var _ = w;
        var launch = await LaunchAsync(w, pathId);
        var token = await FetchAsync(w, launch);
        await PostAsync(w, token, Statement(launch, Cmi5Verbs.Initialized));
        await PostAsync(w, token, Statement(launch, Cmi5Verbs.Progressed, progress: 40));
        var completedId = Guid.NewGuid();
        var completed = Statement(launch, Cmi5Verbs.Completed, completedId, completion: true);
        (await PostAsync(w, token, completed)).Should().Equal(completedId);
        (await PostAsync(w, token, completed)).Should().Equal(completedId);
        // Concurrent replays as well.
        await Task.WhenAll(PostAsync(w, token, completed), PostAsync(w, token, completed));

        await using var db = w.Db();
        (await db.LearningXapiStatements.AsNoTracking().CountAsync(s => s.StatementId == completedId)).Should().Be(1);
        var enrollment = await db.LearningEnrollments.AsNoTracking().SingleAsync(e => e.Id == launch.EnrollmentId);
        enrollment.Status.Should().Be(LearningEnrollmentStatuses.Completed);
        enrollment.CompletionSource.Should().Be(LearningCompletionSources.Cmi5);
        enrollment.CompletionEvidenceRef.Should().Be(completedId.ToString());
        (await db.LearningCredentials.AsNoTracking().CountAsync(c => c.EnrollmentId == launch.EnrollmentId)).Should().Be(1);
        (await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "learning.enrollment.completed" && a.EntityId == launch.EnrollmentId.ToString()))
            .Should().Be(1);
        (await db.LearningXapiStatements.AsNoTracking().CountAsync(s => s.SessionId == launch.SessionId && s.Verb == Cmi5Verbs.Satisfied && s.IssuedByLms))
            .Should().Be(1);
    }

    [Fact]
    public async Task TokensAreBoundToTheirSession_ActorRegistrationActivityOriginAndTenant()
    {
        var (w, pathId) = await WorldAsync(fx, Cmi5MoveOnValues.Completed);
        await using var _ = w;
        var launch = await LaunchAsync(w, pathId);
        var token = await FetchAsync(w, launch);

        async Task<string> Refused(JsonElement body, string? origin = Origin)
        {
            var call = () => PostAsync(w, token, body, origin);
            return (await call.Should().ThrowAsync<Cmi5Exception>()).Which.Code;
        }
        (await Refused(Statement(launch, Cmi5Verbs.Completed, completion: true, actorName: Guid.NewGuid().ToString()))).Should().Be("actor_mismatch");
        (await Refused(Statement(launch, Cmi5Verbs.Completed, completion: true, registration: Guid.NewGuid()))).Should().Be("registration_mismatch");
        (await Refused(Statement(launch, Cmi5Verbs.Completed, completion: true, activity: "https://evil.test/other"))).Should().Be("activity_mismatch");
        (await Refused(Statement(launch, Cmi5Verbs.Satisfied))).Should().Be("verb_reserved");
        (await Refused(Statement(launch, Cmi5Verbs.Passed, success: false))).Should().Be("invalid_statement");
        (await Refused(Statement(launch, Cmi5Verbs.Completed, completion: true), "https://evil.test")).Should().Be("origin_not_allowed");

        // Tenant B's session cannot be driven with tenant A's secret (or vice versa).
        var (pathB, _) = await PathInTenantBAsync(w);
        var launchB = await LaunchTenantBAsync(w, pathB);
        var secretA = Encoding.UTF8.GetString(Convert.FromBase64String(token)).Split(':')[1];
        var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{launchB.SessionId:N}:{secretA}"));
        var cross = () => PostAsync(w, forged, Statement(launchB, Cmi5Verbs.Completed, completion: true));
        (await cross.Should().ThrowAsync<Cmi5Exception>()).Which.Code.Should().Be("invalid_token");

        await using var db = w.Db();
        (await db.LearningEnrollments.AsNoTracking().SingleAsync(e => e.Id == launch.EnrollmentId)).Status.Should().NotBe(LearningEnrollmentStatuses.Completed);
        (await db.LearningEnrollments.AsNoTracking().SingleAsync(e => e.Id == launchB.EnrollmentId)).Status.Should().NotBe(LearningEnrollmentStatuses.Completed);
    }

    [Theory]
    [InlineData(Cmi5MoveOnValues.Completed, new[] { "completed" }, true)]
    [InlineData(Cmi5MoveOnValues.Completed, new[] { "passed" }, false)]
    [InlineData(Cmi5MoveOnValues.Passed, new[] { "completed" }, false)]
    [InlineData(Cmi5MoveOnValues.Passed, new[] { "failed", "passed" }, true)]
    [InlineData(Cmi5MoveOnValues.CompletedAndPassed, new[] { "completed" }, false)]
    [InlineData(Cmi5MoveOnValues.CompletedAndPassed, new[] { "passed", "completed" }, true)]
    [InlineData(Cmi5MoveOnValues.CompletedOrPassed, new[] { "passed" }, true)]
    [InlineData(Cmi5MoveOnValues.CompletedOrPassed, new[] { "failed" }, false)]
    public async Task MoveOn_DecidesCompletion(string moveOn, string[] verbs, bool completes)
    {
        var (w, pathId) = await WorldAsync(fx, moveOn, "MV");
        await using var _ = w;
        var launch = await LaunchAsync(w, pathId);
        var token = await FetchAsync(w, launch);
        await PostAsync(w, token, Statement(launch, Cmi5Verbs.Initialized));
        foreach (var verb in verbs)
        {
            var body = verb switch
            {
                "completed" => Statement(launch, Cmi5Verbs.Completed, completion: true),
                "passed" => Statement(launch, Cmi5Verbs.Passed, success: true, scaled: 0.9m),
                _ => Statement(launch, Cmi5Verbs.Failed, success: false, scaled: 0.2m),
            };
            await PostAsync(w, token, body);
        }
        await using var db = w.Db();
        var enrollment = await db.LearningEnrollments.AsNoTracking().SingleAsync(e => e.Id == launch.EnrollmentId);
        (enrollment.Status == LearningEnrollmentStatuses.Completed).Should().Be(completes);
        if (completes && verbs.Contains("passed")) enrollment.ScorePercent.Should().Be(90m);
    }

    [Fact]
    public async Task MoveOnNotApplicable_IsSatisfiedAtLaunch()
    {
        var (w, pathId) = await WorldAsync(fx, Cmi5MoveOnValues.NotApplicable, "NA");
        await using var _ = w;
        var launch = await LaunchAsync(w, pathId);
        await using var db = w.Db();
        var enrollment = await db.LearningEnrollments.AsNoTracking().SingleAsync(e => e.Id == launch.EnrollmentId);
        enrollment.Status.Should().Be(LearningEnrollmentStatuses.Completed);
        var satisfied = await db.LearningXapiStatements.AsNoTracking().SingleAsync(s => s.SessionId == launch.SessionId && s.Verb == Cmi5Verbs.Satisfied);
        enrollment.CompletionEvidenceRef.Should().Be(satisfied.StatementId.ToString());
    }

    [Fact]
    public async Task LaunchData_ServesTheSessionContext_AndTerminatedSessionsRefuseFurtherStatements()
    {
        var (w, pathId) = await WorldAsync(fx, Cmi5MoveOnValues.Completed, "LD");
        await using var _ = w;
        var launch = await LaunchAsync(w, pathId);
        var token = await FetchAsync(w, launch);
        await using (var scope = w.As(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()), strict: true))
        {
            var cmi5 = Get<LearningCmi5Service>(scope);
            var session = await cmi5.AuthenticateAsync("Basic " + token, Origin, default);
            var agent = JsonSerializer.Serialize(new { account = new { homePage = launch.HomePage, name = launch.ActorName } });
            var data = await cmi5.LaunchDataAsync(session, "LMS.LaunchData", launch.ActivityId, agent, launch.Registration.ToString(), default);
            data["launchMode"]!.GetValue<string>().Should().Be("Normal");
            data["moveOn"]!.GetValue<string>().Should().Be(Cmi5MoveOnValues.Completed);
            data["contextTemplate"]!["extensions"]![Cmi5Verbs.SessionIdExtension]!.GetValue<string>().Should().Be(launch.SessionId.ToString());
            var other = () => cmi5.LaunchDataAsync(session, "bookmark", launch.ActivityId, agent, launch.Registration.ToString(), default);
            (await other.Should().ThrowAsync<Cmi5Exception>()).Which.Status.Should().Be(404);
        }
        await PostAsync(w, token, Statement(launch, Cmi5Verbs.Initialized));
        await PostAsync(w, token, Statement(launch, Cmi5Verbs.Terminated));
        var after = () => PostAsync(w, token, Statement(launch, Cmi5Verbs.Completed, completion: true));
        (await after.Should().ThrowAsync<Cmi5Exception>()).Which.Code.Should().Be("session_closed");
    }

    // ── Concurrency, evidence semantics, HR verification, limits ───────────────────────────────────────────────────

    /// <summary>Posts like an AU: a 503 "busy" is retried after its Retry-After, anything else surfaces.</summary>
    private static async Task PostRetryingAsync(LearningTestWorld w, string token, JsonElement body)
    {
        for (var i = 0; ; i++)
        {
            try { await PostAsync(w, token, body); return; }
            catch (Cmi5Exception e) when (e.Status == 503 && i < 10) { await Task.Delay(20); }
        }
    }

    [Fact]
    public async Task ConcurrentStatements_FourteenParallelWithDuplicates_AllStored_CompletionExactlyOnce()
    {
        var (w, pathId) = await WorldAsync(fx, Cmi5MoveOnValues.CompletedAndPassed, "CC");
        await using var _ = w;
        var launch = await LaunchAsync(w, pathId);
        var token = await FetchAsync(w, launch);
        await PostAsync(w, token, Statement(launch, Cmi5Verbs.Initialized));

        var distinct = Enumerable.Range(1, 8).Select(i => Statement(launch, Cmi5Verbs.Progressed, progress: i * 10)).ToList();
        var completed = Statement(launch, Cmi5Verbs.Completed, completion: true);
        var passed = Statement(launch, Cmi5Verbs.Passed, success: true, scaled: 0.9m);
        distinct.Add(completed);
        distinct.Add(passed);
        // 10 distinct + 4 duplicates (the completed/passed pair and two progress statements), all at once.
        var all = distinct.Concat([completed, passed, distinct[0], distinct[1]]).ToList();
        await Task.WhenAll(all.Select(body => Task.Run(() => PostRetryingAsync(w, token, body))));

        await using var db = w.Db();
        (await db.LearningXapiStatements.AsNoTracking().CountAsync(s => s.SessionId == launch.SessionId && !s.IssuedByLms)).Should().Be(11);
        var ids = distinct.Select(d => Guid.Parse(d.GetProperty("id").GetString()!)).ToList();
        (await db.LearningXapiStatements.AsNoTracking().CountAsync(s => ids.Contains(s.StatementId))).Should().Be(10);
        var enrollment = await db.LearningEnrollments.AsNoTracking().SingleAsync(e => e.Id == launch.EnrollmentId);
        enrollment.Status.Should().Be(LearningEnrollmentStatuses.Completed);
        enrollment.ScorePercent.Should().Be(90m);
        enrollment.ProgressPercent.Should().Be(100m);
        (await db.LearningCredentials.AsNoTracking().CountAsync(c => c.EnrollmentId == launch.EnrollmentId)).Should().Be(1);
        (await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "learning.enrollment.completed" && a.EntityId == launch.EnrollmentId.ToString()))
            .Should().Be(1);
        (await db.LearningXapiStatements.AsNoTracking().CountAsync(s => s.SessionId == launch.SessionId && s.Verb == Cmi5Verbs.Satisfied)).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentFetch_OneWins_TheOtherIsTold_AlreadyUsed()
    {
        var (w, pathId) = await WorldAsync(fx, Cmi5MoveOnValues.Completed, "CF");
        await using var _ = w;
        var launch = await LaunchAsync(w, pathId);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            try { return await FetchAsync(w, launch); }
            catch (Cmi5Exception e) { return "error:" + e.Code; }
        })));
        results.Count(r => !r.StartsWith("error:")).Should().Be(1);
        results.Where(r => r.StartsWith("error:")).Should().OnlyContain(r => r == "error:fetch_already_used");
    }

    [Fact]
    public async Task ContentReportedCertificate_WaitsForHr_SelfVerificationRefused_RejectReopens()
    {
        // No explicit choice: a cmi5 unit that grants a certificate requires verification by default.
        var course = Cmi5("VER", Cmi5MoveOnValues.Completed, validityMonths: 12) with { RequiresHrVerification = null };
        var (w, pathId) = await WorldAsync(fx, Cmi5MoveOnValues.Completed, "VER", course);
        await using var _w = w;
        await w.AssignAsync(pathId, [w.Ops2]);
        await using (var hr = w.As(w.HrA()))
            (await Get<LearningCatalogService>(hr).GetCourseAsync(w.TenantA, (await w.EnrollmentAsync(w.Ops1, pathId, 1)).CourseId, false, default))
                .RequiresHrVerification.Should().BeTrue();

        foreach (var employee in new[] { w.Ops1, w.Ops2 })
        {
            var launch = await LaunchAsync(w, pathId, employee);
            var token = await FetchAsync(w, launch);
            await PostAsync(w, token, Statement(launch, Cmi5Verbs.Initialized));
            await PostAsync(w, token, Statement(launch, Cmi5Verbs.Completed, completion: true));
        }
        var ops1 = await w.EnrollmentAsync(w.Ops1, pathId, 1);
        var ops2 = await w.EnrollmentAsync(w.Ops2, pathId, 1);
        ops1.Status.Should().Be(LearningEnrollmentStatuses.PendingVerification);
        ops1.CompletionSource.Should().Be(LearningCompletionSources.Cmi5);
        await using (var db = w.Db())
        {
            (await db.LearningCredentials.AsNoTracking().CountAsync(c => c.EnrollmentId == ops1.Id)).Should().Be(0);
            (await db.LearningAssignments.AsNoTracking().SingleAsync(a => a.Id == ops1.AssignmentId)).Status.Should().NotBe(LearningAssignmentStatuses.Completed);
        }
        var (me, actorMe) = w.Employee(w.Ops1);
        await using (me)
            (await Get<LearningProgressService>(me).MyLearningAsync(w.TenantA, w.Ops1, default)).Assignments.Single().Items.Single().NextAction
                .Should().Be(LearningNextActions.PendingVerification);

        // HR who is the learner cannot verify their own record.
        await using (var selfHr = w.As(Principal(w.TenantA, w.Ops1User, w.Ops1, HrPermissions)))
        {
            var verifyOwn = async () => await Get<LearningProgressService>(selfHr).VerifyAsync(await ActorAsync(selfHr, w.TenantA), ops1.Id, default);
            (await verifyOwn.Should().ThrowAsync<LearningException>()).Which.Code.Should().Be("self_decision_forbidden");
        }

        await using (var hr = w.As(w.HrA()))
        {
            var actor = await ActorAsync(hr, w.TenantA);
            var progress = Get<LearningProgressService>(hr);
            (await Get<LearningReportService>(hr).PendingVerificationAsync(actor, default)).Select(r => r.EmployeeId)
                .Should().BeEquivalentTo([w.Ops1, w.Ops2]);
            var verified = await progress.VerifyAsync(actor, ops1.Id, default);
            verified.Status.Should().Be(LearningAssignmentStatuses.Completed);
            var rejected = await progress.RejectVerificationAsync(actor, ops2.Id, "No record of attendance in the training log", default);
            rejected.Items.Single().Status.Should().Be(LearningEnrollmentStatuses.InProgress);
            var again = () => progress.VerifyAsync(actor, ops2.Id, default);
            (await again.Should().ThrowAsync<LearningException>()).Which.Code.Should().Be("not_pending_verification");
        }

        await using (var db = w.Db())
        {
            var done = await db.LearningEnrollments.AsNoTracking().SingleAsync(e => e.Id == ops1.Id);
            done.Status.Should().Be(LearningEnrollmentStatuses.Completed);
            done.VerifiedByUserId.Should().Be(w.HrUserA);
            var credential = await db.LearningCredentials.AsNoTracking().SingleAsync(c => c.EnrollmentId == ops1.Id);
            credential.CompletionSource.Should().Be(LearningCompletionSources.Cmi5);
            (await db.LearningCredentials.AsNoTracking().CountAsync(c => c.EnrollmentId == ops2.Id)).Should().Be(0);
            (await db.LearningXapiStatements.AsNoTracking().CountAsync(s => s.EnrollmentId == ops2.Id && s.Verb == Cmi5Verbs.Completed)).Should().Be(1,
                "a rejected completion keeps its statements as evidence");
            var actions = await db.AuditLogs.AsNoTracking().Where(a => a.TenantId == w.TenantA).Select(a => a.Action).ToListAsync();
            actions.Should().Contain(["learning.enrollment.verified", "learning.enrollment.verification_rejected", "learning.credential.issued"]);
            // The unit's report is audited as reported; "completed" is written once, when HR verifies.
            (await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "learning.enrollment.completion_reported" && a.EntityId == ops1.Id.ToString())).Should().Be(1);
            (await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "learning.enrollment.completed" && a.EntityId == ops1.Id.ToString())).Should().Be(1);
            (await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "learning.enrollment.completed" && a.EntityId == ops2.Id.ToString())).Should().Be(0);
        }
    }

    [Fact]
    public async Task Publish_RefusesSatisfiedOnLaunchWithACertificate_AUnitWithoutMastery_AndAZeroPassMark()
    {
        await using var w = await CreateAsync(fx);
        await using var hr = w.As(w.HrA());
        var actor = await ActorAsync(hr, w.TenantA);
        var catalog = Get<LearningCatalogService>(hr);

        async Task<IEnumerable<string>> PublishProblems(CourseInput input)
        {
            var created = await catalog.CreateCourseAsync(actor, input, default);
            var publish = () => catalog.PublishCourseAsync(actor, created.Id, null, default);
            var error = (await publish.Should().ThrowAsync<LearningException>()).Which;
            error.Code.Should().Be("course_incomplete");
            return (IEnumerable<string>)error.Details!;
        }
        (await PublishProblems(Cmi5("NA1", Cmi5MoveOnValues.NotApplicable) with { IssuesCertificate = true }))
            .Should().Contain("move_on_not_applicable_with_certificate");
        (await PublishProblems(Cmi5("NA2", Cmi5MoveOnValues.NotApplicable) with { IssuesCertificate = false, ValidityMonths = 12 }))
            .Should().Contain("move_on_not_applicable_with_certificate");
        (await PublishProblems(Cmi5("PM", Cmi5MoveOnValues.Passed) with { PassMarkPercent = null })).Should().Contain("mastery_score_missing");

        var zero = () => catalog.CreateCourseAsync(actor, Lesson("ZERO", LearningCompletionRules.Assessment, passMark: 0m), default);
        (await zero.Should().ThrowAsync<LearningException>()).Which.Status.Should().Be(400);
        // Without a certificate or validity, "satisfied at launch" may still be published (an awareness item).
        var plain = await catalog.CreateCourseAsync(actor, Cmi5("NA3", Cmi5MoveOnValues.NotApplicable), default);
        (await catalog.PublishCourseAsync(actor, plain.Id, null, default)).Status.Should().Be(LearningCourseStatuses.Published);
    }

    [Fact]
    public async Task PassedBelowMastery_FailedAtOrAboveIt_AndCallsWithoutTheAuOrigin_AreRefused()
    {
        var (w, pathId) = await WorldAsync(fx, Cmi5MoveOnValues.Passed, "MS");
        await using var _ = w;
        var launch = await LaunchAsync(w, pathId);
        var token = await FetchAsync(w, launch);
        async Task<string> Refused(JsonElement body, string? origin = Origin)
        {
            var call = () => PostAsync(w, token, body, origin);
            return (await call.Should().ThrowAsync<Cmi5Exception>()).Which.Code;
        }
        (await Refused(Statement(launch, Cmi5Verbs.Passed, success: true, scaled: 0.5m))).Should().Be("invalid_statement");
        (await Refused(Statement(launch, Cmi5Verbs.Passed, success: true))).Should().Be("invalid_statement");
        (await Refused(Statement(launch, Cmi5Verbs.Failed, success: false, scaled: 0.85m))).Should().Be("invalid_statement");
        (await Refused(Statement(launch, Cmi5Verbs.Passed, success: true, scaled: 0.9m), origin: null)).Should().Be("origin_not_allowed");

        // A fetch from another origin is refused with cmi5 error code 3 (and does not burn the token).
        var second = await LaunchAsync(w, pathId);
        await using (var scope = w.As(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()), strict: true))
        {
            var foreign = () => Get<LearningCmi5Service>(scope).FetchAsync(second.FetchToken, default, "https://evil.test");
            (await foreign.Should().ThrowAsync<Cmi5Exception>()).Which.FetchErrorCode.Should().Be("3");
        }
        (await FetchAsync(w, second)).Should().NotBeNullOrEmpty();

        await using var db = w.Db();
        (await db.LearningEnrollments.AsNoTracking().SingleAsync(e => e.Id == launch.EnrollmentId)).Status.Should().NotBe(LearningEnrollmentStatuses.Completed);
    }

    [Fact]
    public async Task Limits_OversizedStatement413_SessionQuota429()
    {
        var (w, pathId) = await WorldAsync(fx, Cmi5MoveOnValues.Completed, "LIM");
        await using var _ = w;
        var launch = await LaunchAsync(w, pathId);
        var token = await FetchAsync(w, launch);

        var big = JsonNode.Parse(Statement(launch, Cmi5Verbs.Progressed, progress: 5).GetRawText())!;
        big["result"]!["response"] = new string('x', 70_000);
        var oversized = () => PostAsync(w, token, JsonDocument.Parse(big.ToJsonString()).RootElement);
        (await oversized.Should().ThrowAsync<Cmi5Exception>()).Which.Status.Should().Be(413);

        // The test world allows 60 statements per session.
        JsonElement Batch(int n) => JsonDocument.Parse("[" + string.Join(",", Enumerable.Range(0, n)
            .Select(_ => Statement(launch, Cmi5Verbs.Progressed, progress: 5).GetRawText())) + "]").RootElement;
        await PostAsync(w, token, Batch(50));
        await PostAsync(w, token, Batch(10));
        var over = () => PostAsync(w, token, Batch(1));
        var quota = (await over.Should().ThrowAsync<Cmi5Exception>()).Which;
        quota.Status.Should().Be(429);
        quota.Code.Should().Be("statement_quota_exceeded");
        await using var db = w.Db();
        (await db.LearningXapiStatements.AsNoTracking().CountAsync(s => s.SessionId == launch.SessionId && !s.IssuedByLms)).Should().Be(60);
    }

    [Fact]
    public async Task AnAssignmentOnlyWaitingForHr_IsNotOverdue_ButListedAsWaitingForVerification()
    {
        var course = Cmi5("WAIT", Cmi5MoveOnValues.Completed, validityMonths: 12, verification: true);
        var w = await CreateAsync(fx);
        await using var _ = w;
        var (pathId, _) = await w.PublishedPathAsync("WAIT", [course]);
        await w.AssignAsync(pathId, [w.Ops1], w.Clock.Today.AddDays(2));
        var launch = await LaunchAsync(w, pathId);
        var token = await FetchAsync(w, launch);
        await PostAsync(w, token, Statement(launch, Cmi5Verbs.Initialized));
        await PostAsync(w, token, Statement(launch, Cmi5Verbs.Completed, completion: true));
        w.Clock.Advance(TimeSpan.FromDays(5));

        await using (var hr = w.As(w.HrA()))
        {
            var actor = await ActorAsync(hr, w.TenantA);
            var reports = Get<LearningReportService>(hr);
            var kpis = (await reports.SummaryAsync(actor, default)).Kpis;
            kpis.Single(k => k.Key == "overdue").Value.Should().Be(0);
            kpis.Single(k => k.Key == "pending").Value.Should().Be(1);
            (await reports.StatusMatrixAsync(actor, pathId, null, null, true, default)).Should().BeEmpty();
            var waiting = (await reports.PendingVerificationAsync(actor, default)).Single();
            waiting.AwaitingVerification.Should().BeTrue();
            waiting.Overdue.Should().BeFalse();
            (await reports.CompletionAsync(actor, "path", default)).Single(r => r.GroupId == pathId).Overdue.Should().Be(0);
        }
        await using (var sweep = w.System())
            (await Get<LearningSweepService>(sweep).RunTenantAsync(w.TenantA, default)).OverdueNotices.Should().Be(0);
        var (scope, _) = w.Employee(w.Ops1);
        await using (scope)
        {
            var mine = (await Get<LearningProgressService>(scope).MyLearningAsync(w.TenantA, w.Ops1, default)).Assignments.Single();
            mine.IsOverdue.Should().BeFalse();
            mine.AwaitingVerification.Should().BeTrue();
        }
        await using var db = w.Db();
        (await db.NotificationDeliveries.AsNoTracking().CountAsync(d => d.TenantId == w.TenantA && d.EventCode == LearningEventCodes.Overdue)).Should().Be(0);
    }

    // ── A rejected or revoked completion stays undone: a new registration, evaluated on its own statements ─────────

    [Fact]
    public async Task ARejectedCompletion_DoesNotComeBackOnRelaunch_UntilTheUnitReportsAgain()
    {
        var course = Cmi5("RJ", Cmi5MoveOnValues.Completed, validityMonths: 12, verification: true);
        var (w, pathId) = await WorldAsync(fx, Cmi5MoveOnValues.Completed, "RJ", course);
        await using var _ = w;
        var launch = await LaunchAsync(w, pathId);
        var token = await FetchAsync(w, launch);
        await PostAsync(w, token, Statement(launch, Cmi5Verbs.Initialized));
        await PostAsync(w, token, Statement(launch, Cmi5Verbs.Completed, completion: true));
        (await w.EnrollmentAsync(w.Ops1, pathId, 1)).Status.Should().Be(LearningEnrollmentStatuses.PendingVerification);
        await using (var hr = w.As(w.HrA()))
            await Get<LearningProgressService>(hr).RejectVerificationAsync(await ActorAsync(hr, w.TenantA), launch.EnrollmentId, "No attendance record", default);

        // The old session is closed: its token cannot report again.
        var stale = () => PostAsync(w, token, Statement(launch, Cmi5Verbs.Completed, completion: true));
        (await stale.Should().ThrowAsync<Cmi5Exception>()).Which.Code.Should().Be("session_closed");

        var relaunch = await LaunchAsync(w, pathId);
        relaunch.Registration.Should().NotBe(launch.Registration, "a rejected completion starts a new cmi5 registration");
        var token2 = await FetchAsync(w, relaunch);
        await PostAsync(w, token2, Statement(relaunch, Cmi5Verbs.Initialized));
        var afterInit = await w.EnrollmentAsync(w.Ops1, pathId, 1);
        afterInit.Status.Should().Be(LearningEnrollmentStatuses.InProgress);
        afterInit.CompletionEvidenceRef.Should().BeNull();

        var again = Guid.NewGuid();
        await PostAsync(w, token2, Statement(relaunch, Cmi5Verbs.Completed, again, completion: true));
        var reported = await w.EnrollmentAsync(w.Ops1, pathId, 1);
        reported.Status.Should().Be(LearningEnrollmentStatuses.PendingVerification);
        reported.CompletionEvidenceRef.Should().Be(again.ToString());
        await using var db = w.Db();
        (await db.LearningXapiStatements.AsNoTracking().CountAsync(s => s.Registration == launch.Registration && s.Verb == Cmi5Verbs.Completed))
            .Should().Be(1, "the first registration's statements stay as history");
    }

    [Fact]
    public async Task ARevokedCmi5Certificate_DoesNotComeBackOnRelaunch_UntilTheUnitReportsAgain()
    {
        var (w, pathId) = await WorldAsync(fx, Cmi5MoveOnValues.Completed, "RV5");
        await using var _ = w;
        var launch = await LaunchAsync(w, pathId);
        var token = await FetchAsync(w, launch);
        await PostAsync(w, token, Statement(launch, Cmi5Verbs.Initialized));
        await PostAsync(w, token, Statement(launch, Cmi5Verbs.Completed, completion: true));
        await PostAsync(w, token, Statement(launch, Cmi5Verbs.Terminated));
        Guid revokedId;
        await using (var db = w.Db()) revokedId = (await db.LearningCredentials.AsNoTracking().SingleAsync(c => c.EnrollmentId == launch.EnrollmentId)).Id;
        await using (var hr = w.As(w.HrA()))
            await Get<LearningProgressService>(hr).RevokeAsync(await ActorAsync(hr, w.TenantA), revokedId,
                new RevokeCredentialInput("Incident", w.Clock.Today.AddDays(14)), default);

        var relaunch = await LaunchAsync(w, pathId);
        relaunch.Registration.Should().NotBe(launch.Registration);
        var token2 = await FetchAsync(w, relaunch);
        await PostAsync(w, token2, Statement(relaunch, Cmi5Verbs.Initialized));
        (await w.EnrollmentAsync(w.Ops1, pathId, 1)).Status.Should().Be(LearningEnrollmentStatuses.InProgress);
        await using (var db = w.Db())
            (await db.LearningCredentials.AsNoTracking().Where(c => c.EnrollmentId == launch.EnrollmentId).Select(c => c.Status).ToListAsync())
                .Should().Equal(LearningCredentialStatuses.Revoked);

        await PostAsync(w, token2, Statement(relaunch, Cmi5Verbs.Completed, completion: true));
        (await w.EnrollmentAsync(w.Ops1, pathId, 1)).Status.Should().Be(LearningEnrollmentStatuses.Completed);
        await using var check = w.Db();
        var credentials = await check.LearningCredentials.AsNoTracking().Where(c => c.EnrollmentId == launch.EnrollmentId).ToListAsync();
        credentials.Should().HaveCount(2);
        credentials.Single(c => c.Id != revokedId).Status.Should().Be(LearningCredentialStatuses.Active);
    }

    // ── Tenant B ────────────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<(Guid PathId, Guid CourseId)> PathInTenantBAsync(LearningTestWorld w)
    {
        await using var scope = w.As(w.HrB());
        var actor = await ActorAsync(scope, w.TenantB);
        var catalog = Get<LearningCatalogService>(scope);
        var course = await catalog.CreateCourseAsync(actor, Cmi5("AUB", Cmi5MoveOnValues.Completed), default);
        await catalog.PublishCourseAsync(actor, course.Id, null, default);
        var path = await catalog.CreatePathAsync(actor, new PathInput("PB", "Path B", null, null, null, null, null, 30, [new PathItemInput(course.Id, true, null)], null), default);
        await catalog.ActivatePathAsync(actor, path.Id, default);
        await Get<LearningAssignmentService>(scope).AssignAsync(actor, new AssignInput(path.Id, "Employee", [w.B1], null, null, null, true), default);
        return (path.Id, course.Id);
    }

    private static async Task<Launch> LaunchTenantBAsync(LearningTestWorld w, Guid pathId)
    {
        Guid enrollmentId;
        await using (var db = w.Db())
        {
            var assignment = await db.LearningAssignments.AsNoTracking().SingleAsync(a => a.PathId == pathId);
            enrollmentId = (await db.LearningEnrollments.AsNoTracking().SingleAsync(e => e.AssignmentId == assignment.Id)).Id;
        }
        await using var scope = w.As(w.EmployeeB());
        var actor = new LearningActor(w.TenantB, Guid.NewGuid(), null, "test", new Zayra.Api.Application.Common.DataScope
        {
            Level = Zayra.Api.Application.Common.DataScopeLevel.Own, CallerEmployeeId = w.B1, AllowedEmployeeIds = [w.B1],
        });
        var dto = await Get<LearningProgressService>(scope).LaunchAsync(actor, w.B1, enrollmentId, "https://x.test", null, default);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(dto.LaunchUrl!).Query);
        var fetch = query["fetch"].ToString();
        var actorJson = JsonNode.Parse(query["actor"].ToString())!;
        return new Launch(enrollmentId, dto.SessionId!.Value, fetch[(fetch.LastIndexOf('/') + 1)..], Guid.Parse(query["registration"]!),
            query["activityId"]!, actorJson["account"]!["name"]!.GetValue<string>(), actorJson["account"]!["homePage"]!.GetValue<string>());
    }
}
