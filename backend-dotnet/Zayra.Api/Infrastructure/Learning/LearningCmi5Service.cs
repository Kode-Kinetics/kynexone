using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Learning;

/// <summary>Operator settings (appsettings section "Learning"). Defaults are the values LRN_DESIGN.md documents.</summary>
public sealed class LearningOptions
{
    public const string Section = "Learning";
    /// <summary>How long the one-time fetch URL in a cmi5 launch stays usable.</summary>
    public TimeSpan FetchTokenTtl { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>How long the auth token returned by fetch stays valid.</summary>
    public TimeSpan AuthTokenTtl { get; set; } = TimeSpan.FromHours(2);
    /// <summary>Absolute base URL the AU calls back (xAPI endpoint, fetch URL). Empty = the request's own scheme and host.</summary>
    public string? PublicApiBaseUrl { get; set; }
    /// <summary>The actor account homePage (an IRI that names this LMS). Empty = PublicApiBaseUrl.</summary>
    public string? ActorHomePage { get; set; }
    /// <summary>Days before a due date that LEARNING_DUE is sent.</summary>
    public int DueReminderDays { get; set; } = 7;
    /// <summary>How often the sweep runs.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);
    /// <summary>Rows the sweep reads per page; it pages through every due row each run.</summary>
    public int SweepPageSize { get; set; } = 500;
    /// <summary>Largest single xAPI statement accepted, in characters of JSON.</summary>
    public int MaxStatementChars { get; set; } = 64 * 1024;
    /// <summary>Most statements one launch session may store (a unit that floods is refused with 429).</summary>
    public int MaxStatementsPerSession { get; set; } = 2000;
    /// <summary>Requests per minute per client address on the anonymous cmi5 endpoints.</summary>
    public int Cmi5RequestsPerMinute { get; set; } = 600;
}

/// <summary>cmi5 errors carry the spec's shape for fetch ({"error-code","error-text"}) and a code for the rest.</summary>
public sealed class Cmi5Exception(int status, string code, string message, string? fetchErrorCode = null, int? retryAfterSeconds = null) : Exception(message)
{
    public int Status { get; } = status;
    /// <summary>For 503/429: seconds before a retry.</summary>
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
    public string Code { get; } = code;
    /// <summary>Fetch only: cmi5 error-code 1 (already used), 2 (expired/invalid) or 3 (general).</summary>
    public string? FetchErrorCode { get; } = fetchErrorCode;
}

public static class Cmi5Verbs
{
    public const string Launched = "http://adlnet.gov/expapi/verbs/launched";
    public const string Initialized = "http://adlnet.gov/expapi/verbs/initialized";
    public const string Completed = "http://adlnet.gov/expapi/verbs/completed";
    public const string Passed = "http://adlnet.gov/expapi/verbs/passed";
    public const string Failed = "http://adlnet.gov/expapi/verbs/failed";
    public const string Terminated = "http://adlnet.gov/expapi/verbs/terminated";
    public const string Progressed = "http://adlnet.gov/expapi/verbs/progressed";
    public const string Abandoned = "https://w3id.org/xapi/adl/verbs/abandoned";
    public const string Satisfied = "https://w3id.org/xapi/adl/verbs/satisfied";
    public const string SessionIdExtension = "https://w3id.org/xapi/cmi5/context/extensions/sessionid";
    public const string ProgressExtension = "https://w3id.org/xapi/cmi5/result/extensions/progress";
    public const string LaunchDataStateId = "LMS.LaunchData";
    public const string XapiVersion = "1.0.3";
}

/// <summary>
/// KynexOne as the cmi5 LMS and minimal LRS (REGISTER §3.1, LRN-05/13).
///
/// <para><b>Credentials.</b> A launch stores only hashes: the fetch token (in the launch URL) is one-time and expires in
/// <see cref="LearningOptions.FetchTokenTtl"/>; fetch returns the auth token once, as an HTTP Basic credential
/// <c>base64(sessionId:secret)</c> valid for <see cref="LearningOptions.AuthTokenTtl"/>. A token is bound to its session,
/// so it can never act on another session, tenant, registration, actor or activity.</para>
///
/// <para><b>Evidence.</b> Launch is recorded when the session is issued; progress from <c>progressed</c> statements;
/// completion only when the stored statements of the registration satisfy the version's moveOn — evaluated over every
/// stored statement, so a duplicate (same statement id) is a no-op and can never complete anything twice.</para>
/// </summary>
public sealed class LearningCmi5Service(ZayraDbContext db, IAuditService audit, TimeProvider clock, IOptions<LearningOptions> options,
    LearningProgressService progress, ITenantModuleService modules)
{
    private readonly LearningOptions _options = options.Value;

    // ── Launch (called from LearningProgressService.LaunchAsync with the tracked enrollment) ────────────────────────

    internal async Task<(string LaunchUrl, Guid SessionId)> CreateSessionAsync(RequestContext request, LearningEnrollment enrollment,
        LearningCourse course, string publicApiBase, string? returnUrl, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var apiBase = (string.IsNullOrWhiteSpace(_options.PublicApiBaseUrl) ? publicApiBase : _options.PublicApiBaseUrl).TrimEnd('/');
        var homePage = (string.IsNullOrWhiteSpace(_options.ActorHomePage) ? apiBase : _options.ActorHomePage).TrimEnd('/');
        var employee = await db.Employees.AsNoTracking().Where(e => e.Id == enrollment.EmployeeId)
            .Select(e => new { e.PublicId, e.FullName }).FirstAsync(ct);
        var assignment = await db.LearningAssignments.FirstAsync(a => a.Id == enrollment.AssignmentId, ct);

        // A relaunch replaces the open session: the old one is abandoned (provider failure / closed window), never reused.
        var open = await db.LearningLaunchSessions
            .Where(s => s.EnrollmentId == enrollment.Id && LearningSessionStatuses.Open.Contains(s.Status)).ToListAsync(ct);
        foreach (var stale in open) Abandon(stale, now);

        // cmi5 registration is per enrollment (until HR rejects or revokes, which starts a new one): every session shares it.
        enrollment.Cmi5Registration ??= Guid.NewGuid();
        var registration = enrollment.Cmi5Registration.Value;
        var fetchToken = LearningTokens.NewSecret();
        var session = new LearningLaunchSession
        {
            TenantId = enrollment.TenantId, EnrollmentId = enrollment.Id, EmployeeId = enrollment.EmployeeId,
            Registration = registration, ActivityId = course.Cmi5ActivityId!, ActorAccountName = employee.PublicId.ToString(), ActorHomePage = homePage,
            AuOrigin = course.Cmi5AuOrigin!, MoveOn = course.Cmi5MoveOn ?? Cmi5MoveOnValues.Completed, ReturnUrl = returnUrl,
            Status = LearningSessionStatuses.Launched, FetchTokenHash = LearningTokens.Sha256Hex(fetchToken),
            FetchExpiresAtUtc = now + _options.FetchTokenTtl, LaunchedAtUtc = now,
        };
        db.LearningLaunchSessions.Add(session);
        db.LearningXapiStatements.Add(LmsStatement(session, Cmi5Verbs.Launched, now, extra: s => s["context"]!["extensions"]!.AsObject()
            .Add("https://w3id.org/xapi/cmi5/context/extensions/launchmode", "Normal")));

        // moveOn NotApplicable: the AU is satisfied on launch (cmi5 §10). The LMS records "satisfied" as the evidence.
        LearningCredential? credential = null;
        var completedNow = false;
        if (session.MoveOn == Cmi5MoveOnValues.NotApplicable && !LearningEnrollmentStatuses.IsDone(enrollment.Status))
        {
            completedNow = true;
            var satisfied = LmsStatement(session, Cmi5Verbs.Satisfied, now);
            db.LearningXapiStatements.Add(satisfied);
            credential = await progress.CompleteAsync(enrollment, assignment, course, LearningCompletionSources.Cmi5, satisfied.StatementId.ToString(), null, now, ct);
        }
        enrollment.Version++;
        assignment.Version++;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("learning.cmi5.launched", nameof(LearningLaunchSession), session.Id.ToString(), request,
            JsonSerializer.Serialize(new { session.EnrollmentId, session.Registration, abandoned = open.Select(o => o.Id) }), ct);
        if (completedNow) await progress.AuditCompletionAsync(request, enrollment, assignment, credential, ct);

        var actor = JsonSerializer.Serialize(new { objectType = "Agent", account = new { homePage, name = session.ActorAccountName } });
        var query = string.Join('&',
            $"endpoint={Uri.EscapeDataString(apiBase + "/api/learning/xapi/")}",
            $"fetch={Uri.EscapeDataString(apiBase + "/api/learning/cmi5/fetch/" + fetchToken)}",
            $"actor={Uri.EscapeDataString(actor)}",
            $"registration={Uri.EscapeDataString(registration.ToString())}",
            $"activityId={Uri.EscapeDataString(session.ActivityId)}");
        var url = course.Cmi5AuUrl! + (course.Cmi5AuUrl!.Contains('?') ? "&" : "?") + query;
        return (url, session.Id);
    }

    // ── Fetch ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>cmi5 §8.2: POST to the fetch URL returns {"auth-token"} exactly once. Reuse is error 1; expired/unknown is 2.</summary>
    public async Task<string> FetchAsync(string? token, CancellationToken ct, string? origin = null)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) throw new Cmi5Exception(400, "fetch_invalid", "The fetch URL is not valid.", "2");
        var hash = LearningTokens.Sha256Hex(token);
        var session = await db.LearningLaunchSessions.FirstOrDefaultAsync(s => s.FetchTokenHash == hash, ct)
            ?? throw new Cmi5Exception(400, "fetch_invalid", "The fetch URL is not valid.", "2");
        var now = clock.GetUtcNow().UtcDateTime;
        if (session.FetchUsedAtUtc is not null) throw new Cmi5Exception(400, "fetch_already_used", "The fetch URL has already been used.", "1");
        if (session.FetchExpiresAtUtc <= now || !LearningSessionStatuses.Open.Contains(session.Status))
            throw new Cmi5Exception(400, "fetch_expired", "The fetch URL has expired. Launch the course again.", "2");
        if (!string.IsNullOrEmpty(origin) && !string.Equals(origin.TrimEnd('/'), session.AuOrigin, StringComparison.OrdinalIgnoreCase))
            throw new Cmi5Exception(403, "origin_not_allowed", "This content is not allowed to fetch from this address.", "3");
        await RequireEnabledAsync(session.TenantId, ct);

        var secret = LearningTokens.NewSecret();
        session.FetchUsedAtUtc = now;
        session.AuthTokenHash = LearningTokens.Sha256Hex(secret);
        session.AuthExpiresAtUtc = now + _options.AuthTokenTtl;
        session.Status = LearningSessionStatuses.Fetched;
        session.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Two fetches raced; the other one got the token. One-time means this one gets the reuse error.
            throw new Cmi5Exception(400, "fetch_already_used", "The fetch URL has already been used.", "1");
        }
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{session.Id:N}:{secret}"));
    }

    // ── Authentication of AU calls ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the session from <c>Authorization: Basic base64(sessionId:secret)</c>. Unknown, mismatched (e.g. tenant A's
    /// secret against tenant B's session id), expired, closed, or opt-in-disabled all refuse. A browser call from any
    /// origin but the AU's is refused too.
    /// </summary>
    /// <param name="requireOrigin">
    /// Every session is launched into the learner's browser (OwnWindow), so a genuine AU call always carries an Origin.
    /// Refusing calls without the AU's Origin stops a stray script or tool from posting with a copied token. This is
    /// defence in depth, NOT a security boundary: the learner holds the token and can send any header. That is why
    /// cmi5 completion is labelled content-reported and, for certificates, waits for HR verification.
    /// </param>
    public async Task<LearningLaunchSession> AuthenticateAsync(string? authorization, string? origin, CancellationToken ct, bool requireOrigin = true)
    {
        if (string.IsNullOrWhiteSpace(authorization) || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            throw new Cmi5Exception(401, "auth_required", "Send the auth token from the fetch URL as HTTP Basic credentials.");
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim())); }
        catch (FormatException) { throw new Cmi5Exception(401, "invalid_token", "The auth token is not valid."); }
        var separator = decoded.IndexOf(':');
        if (separator <= 0 || !Guid.TryParse(decoded[..separator], out var sessionId))
            throw new Cmi5Exception(401, "invalid_token", "The auth token is not valid.");
        var session = await db.LearningLaunchSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null || !LearningTokens.HashEquals(session.AuthTokenHash, LearningTokens.Sha256Hex(decoded[(separator + 1)..])))
            throw new Cmi5Exception(401, "invalid_token", "The auth token is not valid.");
        if (session.AuthExpiresAtUtc is not { } expires || expires <= clock.GetUtcNow().UtcDateTime)
            throw new Cmi5Exception(401, "token_expired", "The auth token has expired. Launch the course again.");
        if (!LearningSessionStatuses.Open.Contains(session.Status))
            throw new Cmi5Exception(403, "session_closed", "This launch session has ended. Launch the course again.");
        if ((requireOrigin && string.IsNullOrEmpty(origin))
            || (!string.IsNullOrEmpty(origin) && !string.Equals(origin.TrimEnd('/'), session.AuOrigin, StringComparison.OrdinalIgnoreCase)))
            throw new Cmi5Exception(403, "origin_not_allowed", "This content is not allowed to report from this address.");
        await RequireEnabledAsync(session.TenantId, ct);
        return session;
    }

    // ── Statements ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores statements for the authenticated session, then applies them. Contention-free by design:
    /// <list type="number">
    /// <item>each statement row is inserted on its own; the unique (tenant, statement id) index makes a duplicate (a
    /// replay, or a concurrent request with the same id) a no-op — the "ON CONFLICT DO NOTHING" of this module;</item>
    /// <item>the session/enrollment state is then RECOMPUTED from every stored statement of the registration, under the
    /// rows' version tokens, retried on a conflict. Recomputing from stored rows means no statement's effect can be lost
    /// to a concurrent request, and the enrollment's version token means completion is applied exactly once.</item>
    /// </list>
    /// A conflict that outlasts the retries is a 503 with Retry-After, never a 500.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> StoreStatementsAsync(LearningLaunchSession session, JsonElement body, Guid? putStatementId, CancellationToken ct)
    {
        var raw = body.ValueKind switch
        {
            JsonValueKind.Array => body.EnumerateArray().ToList(),
            JsonValueKind.Object => [body],
            _ => throw new Cmi5Exception(400, "invalid_statement", "Send a statement object or an array of statements."),
        };
        if (raw.Count is 0 or > 50) throw new Cmi5Exception(400, "invalid_statement", "Send between 1 and 50 statements.");
        if (putStatementId is not null && raw.Count != 1) throw new Cmi5Exception(400, "invalid_statement", "PUT stores exactly one statement.");
        if (raw.Any(r => r.GetRawText().Length > _options.MaxStatementChars))
            throw new Cmi5Exception(413, "statement_too_large", "A statement is larger than this LRS accepts.");

        List<ParsedStatement> parsed;
        try { parsed = raw.Select(r => Parse(session, r, putStatementId)).ToList(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
        {
            // A field of the wrong JSON type (a number where an IRI belongs, …).
            throw new Cmi5Exception(400, "invalid_statement", "The statement is not valid xAPI.");
        }
        if (parsed.Select(p => p.Id).Distinct().Count() != parsed.Count)
            throw new Cmi5Exception(400, "invalid_statement", "Two statements in the batch have the same id.");

        // Mastery: with a pass mark (masteryScore), "passed" must carry a score at or above it and "failed" one below it.
        var enrollmentRow = await LearningDb.TenantRows(db.LearningEnrollments, session.TenantId).AsNoTracking()
            .Where(e => e.Id == session.EnrollmentId).Select(e => new { e.CourseId }).FirstAsync(ct);
        var mastery = await db.LearningCourses.AsNoTracking().Where(c => c.Id == enrollmentRow.CourseId)
            .Select(c => c.PassMarkPercent).FirstAsync(ct) is decimal mark && LearningCatalogService.UsesPassed(session.MoveOn) ? mark / 100m : (decimal?)null;
        if (mastery is decimal m)
        {
            foreach (var statement in parsed)
            {
                if (statement.Verb == Cmi5Verbs.Passed && (statement.ScoreScaled is not decimal sp || sp < m))
                    throw new Cmi5Exception(400, "invalid_statement", "A passed statement needs a score at or above the mastery score.");
                if (statement.Verb == Cmi5Verbs.Failed && statement.ScoreScaled is decimal sf && sf >= m)
                    throw new Cmi5Exception(400, "invalid_statement", "A failed statement cannot carry a score at or above the mastery score.");
            }
        }

        // 1. Insert, idempotently.
        var now = clock.GetUtcNow().UtcDateTime;
        var ids = parsed.Select(p => p.Id).ToList();
        var known = await db.LearningXapiStatements.AsNoTracking()
            .Where(x => x.TenantId == session.TenantId && ids.Contains(x.StatementId)).Select(x => new { x.StatementId, x.SessionId }).ToListAsync(ct);
        if (known.Any(k => k.SessionId != session.Id))
            throw new Cmi5Exception(409, "statement_conflict", "A statement with this id was already stored by another session.");
        var fresh = parsed.Where(p => known.All(k => k.StatementId != p.Id)).ToList();
        if (fresh.Count > 0)
        {
            var stored = await db.LearningXapiStatements.AsNoTracking().CountAsync(x => x.SessionId == session.Id && !x.IssuedByLms, ct);
            if (stored + fresh.Count > _options.MaxStatementsPerSession)
                throw new Cmi5Exception(429, "statement_quota_exceeded", "This launch has sent more statements than allowed. Launch the course again.", null, 60);
        }
        // Stored times keep the batch's own order (one tick apart), so "latest" means what the unit sent last.
        foreach (var (statement, index) in fresh.Select((p, i) => (p, i)))
        {
            db.LearningXapiStatements.Add(new LearningXapiStatement
            {
                TenantId = session.TenantId, SessionId = session.Id, EnrollmentId = session.EnrollmentId,
                StatementId = statement.Id, Registration = session.Registration, Verb = statement.Verb, ResultSuccess = statement.Success,
                ResultCompletion = statement.Completion, ScoreScaled = statement.ScoreScaled, ProgressPercent = statement.Progress,
                StatementJson = Stamp(statement.Json, now), StoredAtUtc = now.AddTicks(index),
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (LearningDb.IsUniqueViolation(ex))
            {
                // A concurrent request stored the same id first: fine if it is this session's, a conflict otherwise.
                LearningDb.DetachAdded(db);
                var owner = await db.LearningXapiStatements.AsNoTracking()
                    .Where(x => x.TenantId == session.TenantId && x.StatementId == statement.Id).Select(x => x.SessionId).FirstAsync(ct);
                if (owner != session.Id) throw new Cmi5Exception(409, "statement_conflict", "A statement with this id was already stored by another session.");
            }
        }

        // 2. Apply, from everything stored.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                await ApplyAsync(session.Id, ct);
                return ids;
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException || (ex is DbUpdateException u && LearningDb.IsUniqueViolation(u)))
            {
                db.ChangeTracker.Clear();
                await Task.Delay(Random.Shared.Next(10, 40 * (attempt + 1)), ct);
            }
        }
        throw new Cmi5Exception(503, "busy", "The learning system is busy. Send the statements again.", null, 1);
    }

    /// <summary>Recomputes session status, progress, score and moveOn completion from the stored statements.</summary>
    private async Task ApplyAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.LearningLaunchSessions.FirstAsync(s => s.Id == sessionId, ct);
        var enrollment = await LearningDb.TenantRows(db.LearningEnrollments, session.TenantId).FirstAsync(e => e.Id == session.EnrollmentId, ct);
        var statements = await db.LearningXapiStatements.AsNoTracking()
            .Where(x => x.TenantId == session.TenantId && x.Registration == session.Registration && !x.IssuedByLms)
            .Select(x => new { x.StatementId, x.SessionId, x.Verb, x.ScoreScaled, x.ProgressPercent, x.StoredAtUtc, x.Id })
            .ToListAsync(ct);
        var mine = statements.Where(x => x.SessionId == session.Id).ToList();
        var now = clock.GetUtcNow().UtcDateTime;

        var sessionChanged = false;
        if (mine.Any(x => x.Verb == Cmi5Verbs.Terminated) && session.Status != LearningSessionStatuses.Terminated)
        {
            session.Status = LearningSessionStatuses.Terminated;
            session.EndedAtUtc ??= now;
            sessionChanged = true;
        }
        else if (mine.Any(x => x.Verb == Cmi5Verbs.Initialized) && session.Status is LearningSessionStatuses.Launched or LearningSessionStatuses.Fetched)
        {
            session.Status = LearningSessionStatuses.Initialized;
            session.InitializedAtUtc ??= now;
            sessionChanged = true;
        }
        if (session.LastStatementAtUtc is null || now - session.LastStatementAtUtc > TimeSpan.FromSeconds(5))
        {
            session.LastStatementAtUtc = now;
            sessionChanged = true;
        }
        if (sessionChanged) session.Version++;

        var enrollmentChanged = false;
        LearningCredential? credential = null;
        LearningAssignment? assignment = null;
        // Only the enrollment's current registration can move it (an older one was closed by a reject or a revoke).
        var current = enrollment.Cmi5Registration is not Guid live || live == session.Registration;
        if (current && !LearningEnrollmentStatuses.IsDone(enrollment.Status))
        {
            // Progress evidence: the furthest the unit has reported. Never completion.
            var furthest = statements.Where(x => x.Verb == Cmi5Verbs.Progressed && x.ProgressPercent is not null).Select(x => x.ProgressPercent).Max();
            if (furthest is decimal p && (enrollment.ProgressPercent ?? 0) < p) { enrollment.ProgressPercent = p; enrollmentChanged = true; }
            // The latest judged score.
            // Same stored instant (concurrent requests): a pass outranks a fail, so a pass is never lost to a race.
            var judged = statements.Where(x => x.Verb is Cmi5Verbs.Passed or Cmi5Verbs.Failed && x.ScoreScaled is not null)
                .OrderBy(x => x.StoredAtUtc).ThenBy(x => x.Verb == Cmi5Verbs.Passed ? 1 : 0).ThenBy(x => x.Id).LastOrDefault();
            if (judged is not null)
            {
                var score = Math.Clamp(Math.Round(judged.ScoreScaled!.Value * 100m, 2, MidpointRounding.AwayFromZero), 0m, 100m);
                if (enrollment.ScorePercent != score) { enrollment.ScorePercent = score; enrollmentChanged = true; }
            }
            var verbs = statements.Select(x => x.Verb).ToHashSet(StringComparer.Ordinal);
            if (MoveOnSatisfied(session.MoveOn, verbs))
            {
                assignment = await LearningDb.TenantRows(db.LearningAssignments, session.TenantId).FirstAsync(a => a.Id == enrollment.AssignmentId, ct);
                if (LearningAssignmentStatuses.Active.Contains(assignment.Status) && enrollment.Status != LearningEnrollmentStatuses.Cancelled)
                {
                    var course = await db.LearningCourses.AsNoTracking().FirstAsync(c => c.Id == enrollment.CourseId, ct);
                    var satisfied = LmsStatement(session, Cmi5Verbs.Satisfied, now);
                    db.LearningXapiStatements.Add(satisfied);
                    // The evidence is the AU statement that tipped moveOn over (the latest completed/passed).
                    var trigger = statements.Where(x => x.Verb is Cmi5Verbs.Completed or Cmi5Verbs.Passed)
                        .OrderBy(x => x.StoredAtUtc).ThenBy(x => x.Id).LastOrDefault()?.StatementId ?? satisfied.StatementId;
                    credential = await progress.CompleteAsync(enrollment, assignment, course, LearningCompletionSources.Cmi5, trigger.ToString(),
                        enrollment.ScorePercent, now, ct);
                    enrollment.ProgressPercent = 100m;
                    enrollmentChanged = true;
                }
                else assignment = null;
            }
            if (enrollmentChanged)
            {
                enrollment.LastActivityAtUtc = now;
                enrollment.Version++;
            }
        }
        if (!sessionChanged && !enrollmentChanged) return;
        await db.SaveChangesAsync(ct);

        if (assignment is not null && LearningEnrollmentStatuses.IsDone(enrollment.Status))
            await progress.AuditCompletionAsync(new RequestContext(null, "cmi5-statement", null, session.TenantId), enrollment, assignment, credential, ct);
    }

    internal static bool MoveOnSatisfied(string moveOn, IReadOnlySet<string> verbs)
    {
        var completed = verbs.Contains(Cmi5Verbs.Completed);
        var passed = verbs.Contains(Cmi5Verbs.Passed);
        return moveOn switch
        {
            Cmi5MoveOnValues.Completed => completed,
            Cmi5MoveOnValues.Passed => passed,
            Cmi5MoveOnValues.CompletedAndPassed => completed && passed,
            Cmi5MoveOnValues.CompletedOrPassed => completed || passed,
            Cmi5MoveOnValues.NotApplicable => true,
            _ => false,
        };
    }

    // ── State API: LMS.LaunchData only ──────────────────────────────────────────────────────────────────────────────

    public async Task<JsonObject> LaunchDataAsync(LearningLaunchSession session, string? stateId, string? activityId, string? agent, string? registration,
        CancellationToken ct)
    {
        if (!string.Equals(stateId, Cmi5Verbs.LaunchDataStateId, StringComparison.Ordinal))
            throw new Cmi5Exception(404, "state_not_found", "No such state document.");
        if (!string.Equals(activityId, session.ActivityId, StringComparison.Ordinal))
            throw new Cmi5Exception(403, "activity_mismatch", "The activity does not belong to this launch.");
        if (!Guid.TryParse(registration, out var reg) || reg != session.Registration)
            throw new Cmi5Exception(403, "registration_mismatch", "The registration does not belong to this launch.");
        if (!ActorMatches(session, ParseJson(agent)))
            throw new Cmi5Exception(403, "actor_mismatch", "The agent does not belong to this launch.");
        var data = new JsonObject
        {
            ["contextTemplate"] = new JsonObject
            {
                ["extensions"] = new JsonObject { [Cmi5Verbs.SessionIdExtension] = session.Id.ToString() },
            },
            ["launchMode"] = "Normal",
            ["launchMethod"] = "OwnWindow",
            ["moveOn"] = session.MoveOn,
        };
        var courseId = await LearningDb.TenantRows(db.LearningEnrollments, session.TenantId).AsNoTracking()
            .Where(e => e.Id == session.EnrollmentId).Select(e => e.CourseId).FirstAsync(ct);
        var mark = await db.LearningCourses.AsNoTracking().Where(c => c.Id == courseId).Select(c => c.PassMarkPercent).FirstAsync(ct);
        if (mark is decimal m && LearningCatalogService.UsesPassed(session.MoveOn)) data["masteryScore"] = Math.Round(m / 100m, 4);
        if (!string.IsNullOrWhiteSpace(session.ReturnUrl)) data["returnURL"] = session.ReturnUrl;
        return data;
    }

    // ── Sweep: provider failure ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A session whose AU never fetched (fetch window closed) or whose auth window closed without "terminated" is
    /// abandoned: the enrollment simply stays in progress and a relaunch starts a new session.
    /// </summary>
    internal async Task<int> AbandonStaleSessionsAsync(Guid tenantId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var stale = await db.LearningLaunchSessions.Where(s => s.TenantId == tenantId && LearningSessionStatuses.Open.Contains(s.Status)
            && ((s.AuthExpiresAtUtc != null && s.AuthExpiresAtUtc <= now) || (s.FetchUsedAtUtc == null && s.FetchExpiresAtUtc <= now)))
            .Take(500).ToListAsync(ct);
        foreach (var session in stale) Abandon(session, now);
        if (stale.Count > 0) await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    private void Abandon(LearningLaunchSession session, DateTime now) => Abandon(db, session, now);

    private static void Abandon(ZayraDbContext db, LearningLaunchSession session, DateTime now)
    {
        session.Status = LearningSessionStatuses.Abandoned;
        session.EndedAtUtc = now;
        session.Version++;
        db.LearningXapiStatements.Add(LmsStatement(session, Cmi5Verbs.Abandoned, now));
    }

    /// <summary>
    /// After HR rejects a reported completion or revokes its credential: open sessions are abandoned (their tokens stop
    /// working) and the enrollment gets a new registration. Earlier statements stay as history; moveOn is evaluated only
    /// over the new registration's statements, so they can never complete the enrollment again. The caller saves.
    /// </summary>
    internal static async Task StartNewRegistrationAsync(ZayraDbContext db, LearningEnrollment enrollment, DateTime now, CancellationToken ct)
    {
        foreach (var open in await LearningDb.TenantRows(db.LearningLaunchSessions, enrollment.TenantId)
                     .Where(s => s.EnrollmentId == enrollment.Id && LearningSessionStatuses.Open.Contains(s.Status)).ToListAsync(ct))
            Abandon(db, open, now);
        enrollment.Cmi5Registration = Guid.NewGuid();
    }

    // ── Parsing and validation ──────────────────────────────────────────────────────────────────────────────────────

    private sealed record ParsedStatement(Guid Id, string Verb, bool? Success, bool? Completion, decimal? ScoreScaled, decimal? Progress, JsonObject Json);

    private static ParsedStatement Parse(LearningLaunchSession session, JsonElement element, Guid? putStatementId)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new Cmi5Exception(400, "invalid_statement", "A statement must be a JSON object.");
        var json = JsonNode.Parse(element.GetRawText())!.AsObject();

        Guid id;
        var idText = json["id"]?.GetValue<string>();
        if (idText is not null)
        {
            if (!Guid.TryParse(idText, out id)) throw new Cmi5Exception(400, "invalid_statement", "The statement id must be a UUID.");
            if (putStatementId is Guid put && put != id) throw new Cmi5Exception(400, "invalid_statement", "The statementId parameter and the statement id differ.");
        }
        else
        {
            id = putStatementId ?? Guid.NewGuid();
            json["id"] = id.ToString();
        }

        if (!ActorMatches(session, json["actor"]))
            throw new Cmi5Exception(403, "actor_mismatch", "The statement's actor is not the learner of this launch.");
        var verb = json["verb"]?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(verb) || verb.Length > 300) throw new Cmi5Exception(400, "invalid_statement", "The statement has no verb.");
        if (!string.Equals(json["object"]?["id"]?.GetValue<string>(), session.ActivityId, StringComparison.Ordinal))
            throw new Cmi5Exception(403, "activity_mismatch", "The statement's object is not the activity of this launch.");
        if (!Guid.TryParse(json["context"]?["registration"]?.GetValue<string>(), out var registration) || registration != session.Registration)
            throw new Cmi5Exception(403, "registration_mismatch", "The statement's registration does not belong to this launch.");
        var sessionExtension = json["context"]?["extensions"]?[Cmi5Verbs.SessionIdExtension]?.GetValue<string>();
        if (sessionExtension is not null && (!Guid.TryParse(sessionExtension, out var sid) || sid != session.Id))
            throw new Cmi5Exception(403, "session_mismatch", "The statement names another session.");
        // LMS-only verbs: an AU cannot claim satisfaction or abandon for itself.
        if (verb is Cmi5Verbs.Satisfied or Cmi5Verbs.Abandoned or Cmi5Verbs.Launched)
            throw new Cmi5Exception(403, "verb_reserved", "Only the LMS can issue this verb.");

        var result = json["result"];
        decimal? scaled = null;
        if (result?["score"]?["scaled"] is JsonValue s && s.TryGetValue<decimal>(out var sv))
        {
            if (sv is < -1 or > 1) throw new Cmi5Exception(400, "invalid_statement", "score.scaled must be between -1 and 1.");
            scaled = sv;
        }
        decimal? progressValue = null;
        if (result?["extensions"]?[Cmi5Verbs.ProgressExtension] is JsonValue pv && pv.TryGetValue<decimal>(out var pd))
            progressValue = Math.Clamp(pd, 0m, 100m);
        bool? success = result?["success"] is JsonValue sx && sx.TryGetValue<bool>(out var sb) ? sb : null;
        bool? completion = result?["completion"] is JsonValue cx && cx.TryGetValue<bool>(out var cb) ? cb : null;
        // cmi5: "passed" must carry success=true, "failed" success=false, "completed" completion=true.
        if (verb == Cmi5Verbs.Passed && success == false || verb == Cmi5Verbs.Failed && success == true || verb == Cmi5Verbs.Completed && completion == false)
            throw new Cmi5Exception(400, "invalid_statement", "The statement's result contradicts its verb.");
        return new ParsedStatement(id, verb, success, completion, scaled, progressValue, json);
    }

    private static bool ActorMatches(LearningLaunchSession session, JsonNode? actor)
    {
        var account = actor?["account"];
        var name = account?["name"]?.GetValue<string>();
        var homePage = account?["homePage"]?.GetValue<string>();
        return name is not null && homePage is not null
               && string.Equals(name, session.ActorAccountName, StringComparison.Ordinal)
               && string.Equals(homePage.TrimEnd('/'), session.ActorHomePage.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static JsonNode? ParseJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static string Stamp(JsonObject statement, DateTime now)
    {
        var copy = JsonNode.Parse(statement.ToJsonString())!.AsObject();
        copy["stored"] = now.ToString("O");
        copy["timestamp"] ??= now.ToString("O");
        copy["authority"] = new JsonObject { ["objectType"] = "Agent", ["name"] = "KynexOne LMS", ["mbox"] = "mailto:lms@kynexone.invalid" };
        copy["version"] = Cmi5Verbs.XapiVersion;
        return copy.ToJsonString();
    }

    private static LearningXapiStatement LmsStatement(LearningLaunchSession session, string verb, DateTime now, Action<JsonObject>? extra = null)
    {
        var id = Guid.NewGuid();
        var json = new JsonObject
        {
            ["id"] = id.ToString(),
            ["actor"] = new JsonObject
            {
                ["objectType"] = "Agent",
                ["account"] = new JsonObject { ["homePage"] = session.ActorHomePage, ["name"] = session.ActorAccountName },
            },
            ["verb"] = new JsonObject { ["id"] = verb },
            ["object"] = new JsonObject { ["objectType"] = "Activity", ["id"] = session.ActivityId },
            ["context"] = new JsonObject
            {
                ["registration"] = session.Registration.ToString(),
                ["extensions"] = new JsonObject { [Cmi5Verbs.SessionIdExtension] = session.Id.ToString() },
            },
            ["timestamp"] = now.ToString("O"),
        };
        extra?.Invoke(json);
        return new LearningXapiStatement
        {
            TenantId = session.TenantId, SessionId = session.Id, EnrollmentId = session.EnrollmentId, StatementId = id,
            Registration = session.Registration, Verb = verb, IssuedByLms = true, StatementJson = Stamp(json, now), StoredAtUtc = now,
        };
    }

    private async Task RequireEnabledAsync(Guid tenantId, CancellationToken ct)
    {
        var state = await modules.GetStateAsync(tenantId, ct);
        if (!state.IsEnabled(FeatureKeys.Learning))
            throw new Cmi5Exception(403, "feature_not_enabled", "Learning is not enabled for this account.", "3");
    }
}
