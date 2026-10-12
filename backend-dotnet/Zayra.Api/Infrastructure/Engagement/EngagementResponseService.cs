using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Engagement;

/// <summary>An open survey as the invited employee sees it in Self-Service.</summary>
public sealed record EssSurveyListItem(
    Guid SurveyId, string TitleEn, string TitleAr, string Mode, DateTime ClosesAtUtc, string Status,
    DateOnly? SubmittedOn, bool HasDraft);

/// <summary>What opening a survey returns: the single-use session token, the questions, the notice and any draft.</summary>
public sealed record EssSurveySession(
    Guid SurveyId, string Token, string TitleEn, string TitleAr, string Mode, int MinCohortSize,
    string PrivacyNoticeEn, string PrivacyNoticeAr, DateTime ClosesAtUtc,
    EngagementTemplateContent Content, IReadOnlyList<EngagementAnswerInput> Draft);

/// <summary>
/// The respondent side: list my open surveys, open one (issue a session token), save a draft, submit once.
///
/// <para><b>PRIVACY CONTRACT (REGISTER §3.2, ENG_DESIGN.md §4).</b> This class writes the only rows that hold
/// answers, and it is written so that nothing it does links them to the respondent:</para>
/// <list type="bullet">
/// <item>the response and answer entities have no identity property and get random GUID ids;</item>
/// <item>the response keeps no date or time at all, only coarse cohort attributes read at submission;</item>
/// <item>nothing here logs, audits or notifies (EngagementPrivacyRatchetTests pins "no log call in this file");</item>
/// <item>the only ledger→response link, <c>IdentifiedResponseId</c>, is written in Identified mode only, and a CHECK
/// refuses it otherwise;</item>
/// <item>the draft lives on the invitation encrypted, no HR endpoint reads it, and it is deleted in the submit
/// transaction;</item>
/// <item>ANONYMOUS ledger rows change exactly once, at submit: opening stores nothing (the session token is a stateless,
/// time-limited protected token, not a stored hash), saving a draft stores the encrypted draft but no date and no version
/// bump, and a CHECK pins version 0 before submit and 1 after (ck_survey_invitations__anonymous_ledger_minimal).</item>
/// </list>
/// <para>What it cannot hide: the response insert and the invitation update commit in one transaction, so someone
/// with direct database access to transaction metadata (xmin, WAL) could correlate them. That operator is outside the
/// anonymity promise by design and the notice the respondent reads says so.</para>
/// </summary>
public sealed class EngagementResponseService
{
    private const string DraftPurpose = "KynexOne.Engagement.SurveyDraft.v1";
    private const string SessionPurpose = "KynexOne.Engagement.AnonymousSurveySession.v1";
    private static readonly TimeSpan AnonymousSessionLifetime = TimeSpan.FromHours(12);
    private readonly ZayraDbContext _db;
    private readonly IDataProtector _drafts;
    private readonly ITimeLimitedDataProtector _anonymousSessions;
    private readonly TimeProvider _clock;

    public EngagementResponseService(ZayraDbContext db, IDataProtectionProvider dataProtection, TimeProvider? clock = null)
    {
        _db = db;
        _drafts = dataProtection.CreateProtector(DraftPurpose);
        _anonymousSessions = dataProtection.CreateProtector(SessionPurpose).ToTimeLimitedDataProtector();
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private DateOnly Today => DateOnly.FromDateTime(Now);

    public async Task<IReadOnlyList<EssSurveyListItem>> ListMineAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var rows = await (
                from i in _db.EngagementSurveyInvitations.AsNoTracking()
                join s in _db.EngagementSurveys.AsNoTracking() on i.SurveyId equals s.Id
                where i.TenantId == tenantId && s.TenantId == tenantId && i.EmployeeId == employeeId
                      && i.Status != EngagementInvitationStatuses.Cancelled
                      && (s.Status == EngagementSurveyStatuses.Open || s.Status == EngagementSurveyStatuses.Closed)
                orderby s.ClosesAtUtc descending
                select new { s.Id, s.TitleEn, s.TitleAr, s.Mode, s.ClosesAtUtc, SurveyStatus = s.Status, i.Status, i.SubmittedOn, HasDraft = i.DraftCipher != null })
            .Take(50).ToListAsync(ct);
        return rows.Select(r => new EssSurveyListItem(r.Id, r.TitleEn, r.TitleAr, r.Mode, r.ClosesAtUtc,
                r.Status == EngagementInvitationStatuses.Submitted ? "Submitted"
                : r.SurveyStatus == EngagementSurveyStatuses.Closed || r.ClosesAtUtc <= Now ? "Closed"
                : r.HasDraft ? "Draft" : "NotStarted",
                r.SubmittedOn, r.HasDraft))
            .ToList();
    }

    /// <summary>
    /// Opens (or re-opens) the survey. Identified / Confidential: a fresh single-use token whose hash is stored (the
    /// previous one stops working) and the Opened state. Anonymous: a stateless time-limited token and NO write at all,
    /// so the ledger never shows who opened the survey.
    /// </summary>
    public async Task<EngagementResult<EssSurveySession>> OpenAsync(Guid tenantId, int employeeId, Guid surveyId, CancellationToken ct)
    {
        var (invitation, survey, failure) = await LoadAsync(tenantId, employeeId, surveyId, ct);
        if (failure is not null) return EngagementResult<EssSurveySession>.Fail(failure.Value.Status, failure.Value.Code, failure.Value.Message);
        var template = await _db.EngagementSurveyTemplates.AsNoTracking().FirstAsync(t => t.TenantId == tenantId && t.Id == survey!.TemplateId, ct);

        string token;
        if (invitation!.Mode == EngagementSurveyModes.Anonymous)
        {
            token = _anonymousSessions.Protect(SessionPayload(invitation), AnonymousSessionLifetime);
        }
        else
        {
            token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            invitation.SessionTokenHash = Hash(token);
            if (invitation.Status == EngagementInvitationStatuses.Invited)
            {
                invitation.Status = EngagementInvitationStatuses.Opened;
                invitation.FirstOpenedOn = Today;
            }
            invitation.Version++;
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException)
            {
                return EngagementResult<EssSurveySession>.Fail(409, EngagementErrors.Conflict, "The survey changed while it was opening. Try again.");
            }
        }

        var draft = ReadDraft(invitation.DraftCipher);
        var (noticeEn, noticeAr) = EngagementPrivacyNotices.For(survey!.Mode, survey.MinCohortSize);
        return EngagementResult<EssSurveySession>.Success(new EssSurveySession(
            survey.Id, token, survey.TitleEn, survey.TitleAr, survey.Mode, survey.MinCohortSize, noticeEn, noticeAr,
            survey.ClosesAtUtc, EngagementTemplateContent.Parse(template.ContentJson), draft));
    }

    public async Task<EngagementResult<DateOnly>> SaveDraftAsync(
        Guid tenantId, int employeeId, Guid surveyId, string token, IReadOnlyList<EngagementAnswerInput> answers, CancellationToken ct)
    {
        var (invitation, _, failure) = await LoadAsync(tenantId, employeeId, surveyId, ct);
        if (failure is not null) return EngagementResult<DateOnly>.Fail(failure.Value.Status, failure.Value.Code, failure.Value.Message);
        if (!TokenMatches(invitation!, token)) return SessionExpired<DateOnly>();
        if (answers.Count > 60) return EngagementResult<DateOnly>.Fail(400, EngagementErrors.InvalidRequest, "Too many answers.");
        var json = JsonSerializer.Serialize(answers, EngagementTemplateContent.Json);
        if (json.Length > 40_000) return EngagementResult<DateOnly>.Fail(400, EngagementErrors.InvalidRequest, "The draft is too long.");
        invitation!.DraftCipher = _drafts.Protect(json);
        // Anonymous: no date and no version bump (the concurrency token still guards the write against a submit).
        if (invitation.Mode != EngagementSurveyModes.Anonymous)
        {
            invitation.DraftSavedOn = Today;
            invitation.Version++;
        }
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return EngagementResult<DateOnly>.Fail(409, EngagementErrors.Conflict, "Your survey changed in another window. Reopen it."); }
        return EngagementResult<DateOnly>.Success(Today);
    }

    /// <summary>
    /// Submits once. The invitation's concurrency token turns a double submit (two tabs, a retried request) into a
    /// 409 <c>survey_already_submitted</c> without a second response row. One transaction: ledger flag, draft and
    /// session discarded, response and answers inserted.
    /// </summary>
    public async Task<EngagementResult<DateOnly>> SubmitAsync(
        Guid tenantId, int employeeId, Guid surveyId, string token, IReadOnlyList<EngagementAnswerInput> answers, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var (invitation, survey, failure) = await LoadAsync(tenantId, employeeId, surveyId, ct);
            if (failure is not null) return EngagementResult<DateOnly>.Fail(failure.Value.Status, failure.Value.Code, failure.Value.Message);
            if (!TokenMatches(invitation!, token)) return SessionExpired<DateOnly>();

            var template = await _db.EngagementSurveyTemplates.AsNoTracking().FirstAsync(t => t.TenantId == tenantId && t.Id == survey!.TemplateId, ct);
            var content = EngagementTemplateContent.Parse(template.ContentJson);
            var (rows, problem) = BuildAnswers(content, answers);
            if (problem is not null) return EngagementResult<DateOnly>.Fail(400, EngagementErrors.InvalidRequest, problem);

            var cohort = await CohortAsync(tenantId, employeeId, ct);
            var today = Today;
            var response = new EngagementSurveyResponse
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SurveyId = survey!.Id,
                CompanyId = cohort.CompanyId,
                DepartmentId = cohort.DepartmentId,
                DesignationId = cohort.DesignationId,
                GradeBand = cohort.GradeBand,
                TenureBand = cohort.TenureBand,
                CohortManagerEmployeeId = cohort.ManagerEmployeeId,
            };

            invitation!.Status = EngagementInvitationStatuses.Submitted;
            // An anonymous ledger keeps only the flag (CHECK ck_survey_invitations__anonymous_ledger_minimal).
            invitation.SubmittedOn = invitation.Mode == EngagementSurveyModes.Anonymous ? null : today;
            invitation.DraftCipher = null;
            invitation.DraftSavedOn = null;
            invitation.SessionTokenHash = null;
            if (invitation.Mode == EngagementSurveyModes.Identified) invitation.IdentifiedResponseId = response.Id;
            // Anonymous rows go 0 → 1 here and nowhere else (CHECK ck_survey_invitations__anonymous_ledger_minimal).
            invitation.Version = invitation.Mode == EngagementSurveyModes.Anonymous ? 1 : invitation.Version + 1;

            _db.EngagementSurveyResponses.Add(response);
            foreach (var row in rows)
            {
                row.TenantId = tenantId;
                row.CompanyId = response.CompanyId;
                row.SurveyId = survey.Id;
                row.ResponseId = response.Id;
                _db.EngagementSurveyAnswers.Add(row);
            }
            try
            {
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                await tx.RollbackAsync(ct);
                _db.ChangeTracker.Clear();
                return EngagementResult<DateOnly>.Fail(409, EngagementErrors.AlreadySubmitted, "You have already submitted this survey.");
            }
            return EngagementResult<DateOnly>.Success(today);
        });
    }

    /// <summary>Validates answers against the template and builds the rows. Unknown keys and out-of-range values are refused.</summary>
    internal static (List<EngagementSurveyAnswer> Rows, string? Problem) BuildAnswers(
        EngagementTemplateContent content, IReadOnlyList<EngagementAnswerInput> answers)
    {
        var rows = new List<EngagementSurveyAnswer>();
        var byKey = new Dictionary<string, EngagementAnswerInput>(StringComparer.Ordinal);
        foreach (var a in answers ?? [])
        {
            if (string.IsNullOrWhiteSpace(a.QuestionKey) || !byKey.TryAdd(a.QuestionKey, a))
                return (rows, "Each question can be answered once.");
        }
        foreach (var key in byKey.Keys)
            if (content.Questions.All(q => q.Key != key)) return (rows, $"'{key}' is not a question in this survey.");

        foreach (var q in content.Questions)
        {
            byKey.TryGetValue(q.Key, out var a);
            var answered = a is not null && (a.Value is not null || a.Choices is { Count: > 0 } || !string.IsNullOrWhiteSpace(a.Text));
            if (!answered)
            {
                if (q.Required) return (rows, $"Please answer: {q.TextEn}");
                continue;
            }
            var row = new EngagementSurveyAnswer { Id = Guid.NewGuid(), QuestionKey = q.Key };
            switch (q.Type)
            {
                case EngagementQuestionTypes.Likert5:
                    if (a!.Value is not (>= 1 and <= 5)) return (rows, $"'{q.TextEn}' takes a value from 1 to 5.");
                    row.NumericValue = a.Value;
                    break;
                case EngagementQuestionTypes.Enps:
                    if (a!.Value is not (>= 0 and <= 10)) return (rows, $"'{q.TextEn}' takes a value from 0 to 10.");
                    row.NumericValue = a.Value;
                    break;
                case EngagementQuestionTypes.SingleChoice:
                case EngagementQuestionTypes.MultiChoice:
                    var choices = (a!.Choices ?? []).Distinct(StringComparer.Ordinal).ToList();
                    var options = (q.Options ?? []).Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
                    if (choices.Count == 0 || choices.Any(c => !options.Contains(c))) return (rows, $"Choose from the options for '{q.TextEn}'.");
                    if (q.Type == EngagementQuestionTypes.SingleChoice && choices.Count != 1) return (rows, $"Choose one option for '{q.TextEn}'.");
                    row.ChoiceKeys = string.Join(",", choices);
                    break;
                case EngagementQuestionTypes.FreeText:
                    var text = (a!.Text ?? string.Empty).Trim();
                    if (text.Length > 2000) return (rows, $"Your answer to '{q.TextEn}' is longer than 2000 characters.");
                    row.TextValue = text;
                    break;
            }
            rows.Add(row);
        }
        return (rows, null);
    }

    private sealed record Cohort(Guid? CompanyId, Guid? DepartmentId, Guid? DesignationId, string GradeBand, string TenureBand, int? ManagerEmployeeId);

    /// <summary>Coarse attributes of the respondent TODAY. Read here and copied onto the response; never a link back.</summary>
    private async Task<Cohort> CohortAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var e = await _db.Employees.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == employeeId)
            .Select(x => new { x.CompanyId, x.DepartmentId, x.DesignationId, x.GradeId, x.JoiningDate, x.ManagerEmployeeId })
            .FirstAsync(ct);
        var gradeBand = string.Empty;
        if (e.GradeId is Guid gradeId)
        {
            var grade = await _db.Grades.AsNoTracking().Where(g => g.TenantId == tenantId && g.Id == gradeId)
                .Select(g => new { g.Band, g.Code }).FirstOrDefaultAsync(ct);
            gradeBand = grade is null ? string.Empty : (!string.IsNullOrWhiteSpace(grade.Band) ? grade.Band : grade.Code);
        }
        return new Cohort(e.CompanyId, e.DepartmentId, e.DesignationId, gradeBand.Length > 60 ? gradeBand[..60] : gradeBand,
            EngagementTenureBands.For(e.JoiningDate, Today), e.ManagerEmployeeId);
    }

    private async Task<(EngagementSurveyInvitation? Invitation, EngagementSurvey? Survey, (int Status, string Code, string Message)? Failure)> LoadAsync(
        Guid tenantId, int employeeId, Guid surveyId, CancellationToken ct)
    {
        var survey = await _db.EngagementSurveys.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == surveyId, ct);
        var invitation = survey is null ? null : await _db.EngagementSurveyInvitations
            .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.SurveyId == surveyId && i.EmployeeId == employeeId, ct);
        if (survey is null || invitation is null || invitation.Status == EngagementInvitationStatuses.Cancelled)
            return (null, null, (404, EngagementErrors.NotInvited, "You are not invited to this survey."));
        if (invitation.Status == EngagementInvitationStatuses.Submitted)
            return (null, null, (409, EngagementErrors.AlreadySubmitted, "You have already submitted this survey. Thank you."));
        if (survey.Status != EngagementSurveyStatuses.Open || survey.ClosesAtUtc <= Now)
            return (null, null, (410, EngagementErrors.SurveyClosed, "This survey has closed."));
        return (invitation, survey, null);
    }

    private IReadOnlyList<EngagementAnswerInput> ReadDraft(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<EngagementAnswerInput>>(_drafts.Unprotect(cipher), EngagementTemplateContent.Json) ?? [];
        }
        catch (CryptographicException)
        {
            // A rotated-away key: the draft is lost, never shown garbled. The employee starts again.
            return [];
        }
    }

    private bool TokenMatches(EngagementSurveyInvitation invitation, string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (invitation.Mode == EngagementSurveyModes.Anonymous)
        {
            try { return string.Equals(_anonymousSessions.Unprotect(token, out _), SessionPayload(invitation), StringComparison.Ordinal); }
            catch (CryptographicException) { return false; }
        }
        return invitation.SessionTokenHash is { } stored
               && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stored), Encoding.ASCII.GetBytes(Hash(token)));
    }

    /// <summary>What an anonymous session token binds: this invitation of this survey (and nothing stored).</summary>
    private static string SessionPayload(EngagementSurveyInvitation invitation) => $"{invitation.TenantId:N}:{invitation.SurveyId:N}:{invitation.Id:N}";

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static EngagementResult<T> SessionExpired<T>() =>
        EngagementResult<T>.Fail(409, EngagementErrors.SessionExpired, "This survey was opened in another window. Reopen it to continue.");
}
