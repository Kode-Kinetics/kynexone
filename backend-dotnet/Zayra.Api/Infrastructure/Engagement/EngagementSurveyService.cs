using Zayra.Api.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Privacy;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Engagement;

/// <summary>Notification event codes. Business identity (part of the outbox dedupe key) — never a GUID.</summary>
public static class EngagementEventCodes
{
    public const string Invite = "SURVEY_INVITE";
    public const string Reminder = "SURVEY_REMINDER";
    public const string ActionDue = "SURVEY_ACTION_DUE";
    /// <summary>Entity name on survey notifications. The entity id is the SURVEY's id (plus a reminder ordinal) —
    /// never an invitation or response id, so the notification ledger holds nothing that points at an answer.</summary>
    public const string SurveyEntity = "EngagementSurvey";
    public const string ActionEntity = "EngagementAction";
}

/// <summary>What a survey create/update asks for.</summary>
public sealed record EngagementSurveyInput(
    Guid TemplateId,
    string TitleEn,
    string TitleAr,
    string Mode,
    Guid? CompanyId,
    EngagementAudience? Audience,
    DateTime OpensAtUtc,
    DateTime ClosesAtUtc,
    string? Recurrence,
    int? MinCohortSize,
    IReadOnlyList<int>? ReminderOffsetsDays,
    string? CommentDimension = null);

/// <summary>
/// Templates, survey cycles and the participation ledger: create, publish, launch, invite, remind, close, recur,
/// delete. Every state change is a guarded transition (<see cref="EngagementStateMachine"/>) with an optimistic
/// concurrency token; every background step is idempotent (unique indexes + the outbox dedupe key). Audit of the
/// admin actions is written by the caller after the business commit — never anything per response.
/// </summary>
public sealed class EngagementSurveyService
{
    private readonly ZayraDbContext _db;
    private readonly INotificationService _notifications;
    private readonly TimeProvider _clock;

    public EngagementSurveyService(ZayraDbContext db, INotificationService notifications, TimeProvider? clock = null)
    {
        _db = db;
        _notifications = notifications;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // ── Tenant settings ──────────────────────────────────────────────────────────────────────────────────────────

    public async Task<(int MinCohortSize, int FreeTextRetentionMonths)> GetSettingsAsync(Guid tenantId, CancellationToken ct)
    {
        var row = await _db.TenantHrConfigs.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => new { x.EngagementMinCohortSize, x.EngagementFreeTextRetentionMonths })
            .FirstOrDefaultAsync(ct);
        return row is null
            ? (CohortSuppression.DefaultK, 24)
            : (Math.Max(CohortSuppression.Floor, row.EngagementMinCohortSize), row.EngagementFreeTextRetentionMonths);
    }

    public async Task<EngagementResult<(int Before, int After, int MonthsBefore, int MonthsAfter)>> UpdateSettingsAsync(
        Guid tenantId, int minCohortSize, int freeTextRetentionMonths, CancellationToken ct)
    {
        if (minCohortSize < CohortSuppression.Floor || minCohortSize > 100)
            return EngagementResult<(int, int, int, int)>.Fail(400, EngagementErrors.InvalidRequest,
                $"The minimum group size must be between {CohortSuppression.Floor} and 100.");
        if (freeTextRetentionMonths is < 1 or > 120)
            return EngagementResult<(int, int, int, int)>.Fail(400, EngagementErrors.InvalidRequest,
                "Comments must be kept between 1 and 120 months.");
        var row = await _db.TenantHrConfigs.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
        if (row is null)
        {
            row = new TenantHrConfig { TenantId = tenantId };
            _db.TenantHrConfigs.Add(row);
        }
        var before = (row.EngagementMinCohortSize, row.EngagementFreeTextRetentionMonths);
        row.EngagementMinCohortSize = minCohortSize;
        row.EngagementFreeTextRetentionMonths = freeTextRetentionMonths;
        row.UpdatedAtUtc = Now;
        await _db.SaveChangesAsync(ct);
        return EngagementResult<(int, int, int, int)>.Success((before.Item1, minCohortSize, before.Item2, freeTextRetentionMonths));
    }

    // ── Templates ────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<EngagementResult<EngagementSurveyTemplate>> CreateTemplateAsync(
        Guid tenantId, Guid? companyId, string nameEn, string nameAr, string? descriptionEn, string? descriptionAr,
        EngagementTemplateContent content, CancellationToken ct)
    {
        if (TemplateProblem(nameEn, nameAr, content, forPublish: false) is { } problem)
            return EngagementResult<EngagementSurveyTemplate>.Fail(400, EngagementErrors.InvalidRequest, problem);
        var id = Guid.NewGuid();
        var template = new EngagementSurveyTemplate
        {
            Id = id,
            TenantId = tenantId,
            CompanyId = companyId,
            FamilyId = id,
            VersionNumber = 1,
            NameEn = nameEn.Trim(),
            NameAr = nameAr.Trim(),
            DescriptionEn = (descriptionEn ?? string.Empty).Trim(),
            DescriptionAr = (descriptionAr ?? string.Empty).Trim(),
            ContentJson = content.Serialize(),
        };
        _db.EngagementSurveyTemplates.Add(template);
        await _db.SaveChangesAsync(ct);
        return EngagementResult<EngagementSurveyTemplate>.Success(template);
    }

    public async Task<EngagementResult<EngagementSurveyTemplate>> UpdateTemplateAsync(
        Guid tenantId, Guid id, int version, string nameEn, string nameAr, string? descriptionEn, string? descriptionAr,
        EngagementTemplateContent content, CancellationToken ct)
    {
        var template = await _db.EngagementSurveyTemplates.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (template is null) return EngagementResult<EngagementSurveyTemplate>.Fail(404, EngagementErrors.NotFound, "Template not found.");
        if (template.Version != version) return Stale<EngagementSurveyTemplate>();
        if (template.Status != EngagementTemplateStatuses.Draft)
            return EngagementResult<EngagementSurveyTemplate>.Fail(409, EngagementErrors.InvalidState,
                "A published template cannot change. Create a new version to edit it.");
        if (TemplateProblem(nameEn, nameAr, content, forPublish: false) is { } problem)
            return EngagementResult<EngagementSurveyTemplate>.Fail(400, EngagementErrors.InvalidRequest, problem);
        template.NameEn = nameEn.Trim();
        template.NameAr = nameAr.Trim();
        template.DescriptionEn = (descriptionEn ?? string.Empty).Trim();
        template.DescriptionAr = (descriptionAr ?? string.Empty).Trim();
        template.ContentJson = content.Serialize();
        template.Version++;
        return await SaveAsync(template, ct);
    }

    public async Task<EngagementResult<EngagementSurveyTemplate>> PublishTemplateAsync(Guid tenantId, Guid id, int version, CancellationToken ct)
    {
        var template = await _db.EngagementSurveyTemplates.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (template is null) return EngagementResult<EngagementSurveyTemplate>.Fail(404, EngagementErrors.NotFound, "Template not found.");
        if (template.Version != version) return Stale<EngagementSurveyTemplate>();
        if (!EngagementStateMachine.CanMoveTemplate(template.Status, EngagementTemplateStatuses.Published))
            return EngagementResult<EngagementSurveyTemplate>.Fail(409, EngagementErrors.InvalidState, "Only a draft template can be published.");
        var content = EngagementTemplateContent.Parse(template.ContentJson);
        if (TemplateProblem(template.NameEn, template.NameAr, content, forPublish: true) is { } problem)
            return EngagementResult<EngagementSurveyTemplate>.Fail(400, EngagementErrors.InvalidRequest, problem);
        template.Status = EngagementTemplateStatuses.Published;
        template.PublishedAtUtc = Now;
        template.Version++;
        return await SaveAsync(template, ct);
    }

    /// <summary>Starts the next version of a published template as a draft copy. The published one stays as it is.</summary>
    public async Task<EngagementResult<EngagementSurveyTemplate>> NewTemplateVersionAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        var source = await _db.EngagementSurveyTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (source is null) return EngagementResult<EngagementSurveyTemplate>.Fail(404, EngagementErrors.NotFound, "Template not found.");
        var latest = await _db.EngagementSurveyTemplates.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.FamilyId == source.FamilyId)
            .OrderByDescending(x => x.VersionNumber).FirstAsync(ct);
        if (latest.Status == EngagementTemplateStatuses.Draft)
            return EngagementResult<EngagementSurveyTemplate>.Fail(409, EngagementErrors.InvalidState,
                $"Version {latest.VersionNumber} of this template is still a draft. Edit that one.");
        var draft = new EngagementSurveyTemplate
        {
            TenantId = tenantId,
            CompanyId = source.CompanyId,
            FamilyId = source.FamilyId,
            VersionNumber = latest.VersionNumber + 1,
            NameEn = latest.NameEn,
            NameAr = latest.NameAr,
            DescriptionEn = latest.DescriptionEn,
            DescriptionAr = latest.DescriptionAr,
            ContentJson = latest.ContentJson,
        };
        _db.EngagementSurveyTemplates.Add(draft);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return Stale<EngagementSurveyTemplate>(); }
        return EngagementResult<EngagementSurveyTemplate>.Success(draft);
    }

    private static string? TemplateProblem(string? nameEn, string? nameAr, EngagementTemplateContent content, bool forPublish)
    {
        if (string.IsNullOrWhiteSpace(nameEn) || string.IsNullOrWhiteSpace(nameAr)) return "Give the template an English and an Arabic name.";
        if (nameEn.Length > 200 || nameAr.Length > 200) return "A template name is at most 200 characters.";
        var errors = content.Validate(forPublish);
        return errors.Count == 0 ? null : string.Join(" ", errors.Take(5));
    }

    // ── Surveys ──────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<EngagementResult<EngagementSurvey>> CreateSurveyAsync(Guid tenantId, EngagementSurveyInput input, CancellationToken ct)
    {
        var (k, _) = await GetSettingsAsync(tenantId, ct);
        if (await SurveyProblemAsync(tenantId, input, ct) is { } problem)
            return EngagementResult<EngagementSurvey>.Fail(400, EngagementErrors.InvalidRequest, problem);
        var id = Guid.NewGuid();
        var survey = new EngagementSurvey { Id = id, TenantId = tenantId, SeriesId = id, CycleNumber = 1 };
        Apply(survey, input, k);
        _db.EngagementSurveys.Add(survey);
        await _db.SaveChangesAsync(ct);
        return EngagementResult<EngagementSurvey>.Success(survey);
    }

    public async Task<EngagementResult<EngagementSurvey>> UpdateSurveyAsync(Guid tenantId, Guid id, int version, EngagementSurveyInput input, CancellationToken ct)
    {
        var survey = await _db.EngagementSurveys.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (survey is null) return EngagementResult<EngagementSurvey>.Fail(404, EngagementErrors.NotFound, "Survey not found.");
        if (survey.Version != version) return Stale<EngagementSurvey>();
        if (survey.Status != EngagementSurveyStatuses.Draft)
            return EngagementResult<EngagementSurvey>.Fail(409, EngagementErrors.InvalidState, "Only a draft survey can be edited.");
        if (await SurveyProblemAsync(tenantId, input, ct) is { } problem)
            return EngagementResult<EngagementSurvey>.Fail(400, EngagementErrors.InvalidRequest, problem);
        var (k, _) = await GetSettingsAsync(tenantId, ct);
        Apply(survey, input, k);
        survey.Version++;
        return await SaveAsync(survey, ct);
    }

    private static void Apply(EngagementSurvey survey, EngagementSurveyInput input, int tenantK)
    {
        survey.TemplateId = input.TemplateId;
        survey.TitleEn = input.TitleEn.Trim();
        survey.TitleAr = input.TitleAr.Trim();
        survey.Mode = input.Mode;
        survey.CompanyId = input.CompanyId;
        survey.AudienceJson = (input.Audience ?? new EngagementAudience()).Serialize();
        survey.OpensAtUtc = DateTime.SpecifyKind(input.OpensAtUtc, DateTimeKind.Utc);
        survey.ClosesAtUtc = DateTime.SpecifyKind(input.ClosesAtUtc, DateTimeKind.Utc);
        survey.Recurrence = string.IsNullOrWhiteSpace(input.Recurrence) ? EngagementRecurrences.None : input.Recurrence;
        survey.MinCohortSize = CohortSuppression.EffectiveK(input.MinCohortSize, tenantK);
        survey.ReminderOffsetsDays = string.Join(",", (input.ReminderOffsetsDays ?? []).Where(d => d > 0).Distinct().Order());
        survey.CommentDimension = string.IsNullOrWhiteSpace(input.CommentDimension) ? "department" : input.CommentDimension;
    }

    private async Task<string?> SurveyProblemAsync(Guid tenantId, EngagementSurveyInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.TitleEn) || string.IsNullOrWhiteSpace(input.TitleAr)) return "Give the survey an English and an Arabic title.";
        if (input.TitleEn.Length > 200 || input.TitleAr.Length > 200) return "A survey title is at most 200 characters.";
        if (!EngagementSurveyModes.IsValid(input.Mode)) return "Choose Identified, Confidential or Anonymous.";
        if (input.Recurrence is { } r && !string.IsNullOrWhiteSpace(r) && !EngagementRecurrences.All.Contains(r, StringComparer.Ordinal))
            return "Recurrence must be None, Monthly, Quarterly, SemiAnnual or Annual.";
        if (input.ClosesAtUtc <= input.OpensAtUtc) return "The closing date must be after the opening date.";
        if ((input.ClosesAtUtc - input.OpensAtUtc).TotalDays > 120) return "A survey may stay open for at most 120 days.";
        if (input.MinCohortSize is int k && k < CohortSuppression.Floor)
            return $"The minimum group size cannot be below {CohortSuppression.Floor}.";
        if (input.MinCohortSize is > 100) return "The minimum group size is at most 100.";
        if (input.ReminderOffsetsDays is { Count: > 5 }) return "At most five reminders.";
        if (input.ReminderOffsetsDays?.Any(d => d < 1 || d >= (input.ClosesAtUtc - input.OpensAtUtc).TotalDays) == true)
            return "Each reminder must fall between the opening and closing dates.";
        if (input.Audience is { ExcludeJoinedWithinDays: < 0 or > 3650 }) return "The joined-within exclusion must be 0–3650 days.";
        if (input.CommentDimension is { } cd && !string.IsNullOrWhiteSpace(cd) && !EngagementCommentDimensions.All.Contains(cd, StringComparer.Ordinal))
            return "Comments can be grouped by none, company, department, designation, grade or tenure.";
        var template = await _db.EngagementSurveyTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == input.TemplateId, ct);
        if (template is null) return "The template was not found.";
        if (template.Status != EngagementTemplateStatuses.Published) return "Publish the template before using it in a survey.";
        if (template.CompanyId is Guid tc && input.CompanyId != tc) return "That template belongs to another company.";
        if (input.CompanyId is Guid company
            && !await _db.Companies.AsNoTracking().AnyAsync(c => c.TenantId == tenantId && c.Id == company, ct))
            return "The company was not found.";
        return null;
    }

    public async Task<EngagementResult<EngagementSurvey>> LaunchAsync(Guid tenantId, Guid id, int version, Guid? userId, CancellationToken ct)
    {
        var survey = await _db.EngagementSurveys.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (survey is null) return EngagementResult<EngagementSurvey>.Fail(404, EngagementErrors.NotFound, "Survey not found.");
        if (survey.Version != version) return Stale<EngagementSurvey>();
        if (!EngagementStateMachine.CanMoveSurvey(survey.Status, EngagementSurveyStatuses.Scheduled))
            return EngagementResult<EngagementSurvey>.Fail(409, EngagementErrors.InvalidState, "Only a draft survey can be launched.");
        if (survey.ClosesAtUtc <= Now)
            return EngagementResult<EngagementSurvey>.Fail(409, EngagementErrors.InvalidState, "The closing date has already passed. Change it first.");
        var eligible = await EligibleEmployeesAsync(survey, ct);
        if (eligible.Count == 0)
            return EngagementResult<EngagementSurvey>.Fail(409, EngagementErrors.InvalidState,
                "No active employee matches this audience, so there is nobody to invite.");

        survey.Status = EngagementSurveyStatuses.Scheduled;
        survey.LaunchedAtUtc = Now;
        survey.LaunchedBy = userId;
        survey.Version++;
        var saved = await SaveAsync(survey, ct);
        if (!saved.Ok) return saved;
        if (survey.OpensAtUtc <= Now) await OpenAndInviteAsync(survey, ct, sendInvitesNow: survey.InvitationCount <= InlineInviteLimit && eligible.Count <= InlineInviteLimit);
        return EngagementResult<EngagementSurvey>.Success(survey);
    }

    /// <summary>Invitations a launch request enqueues itself; a larger audience is left to the survey worker (within
    /// minutes) so a 5,000-person launch never holds an HTTP request open.</summary>
    public const int InlineInviteLimit = 200;

    /// <summary>Scheduled → Open: writes the participation ledger (deduplicated by the unique index) and, unless told to
    /// leave it to the worker, sends invites. Unsent invites leave <c>InvitesSentAtUtc</c> null; the worker sends them.</summary>
    public async Task<int> OpenAndInviteAsync(EngagementSurvey survey, CancellationToken ct, bool sendInvitesNow = true)
    {
        if (survey.Status != EngagementSurveyStatuses.Scheduled) return 0;
        var eligible = await EligibleEmployeesAsync(survey, ct);
        var existing = (await _db.EngagementSurveyInvitations.AsNoTracking()
                .Where(i => i.TenantId == survey.TenantId && i.SurveyId == survey.Id)
                .Select(i => i.EmployeeId).ToListAsync(ct)).ToHashSet();
        var now = Now;
        var added = 0;
        foreach (var employee in eligible.Where(e => !existing.Contains(e.Id)))
        {
            _db.EngagementSurveyInvitations.Add(new EngagementSurveyInvitation
            {
                TenantId = survey.TenantId,
                CompanyId = employee.CompanyId,
                SurveyId = survey.Id,
                EmployeeId = employee.Id,
                Mode = survey.Mode,
                InvitedAtUtc = now,
            });
            added++;
        }
        survey.Status = EngagementSurveyStatuses.Open;
        survey.InvitationCount = existing.Count + added;
        survey.Version++;
        await _db.SaveChangesAsync(ct);
        if (sendInvitesNow) await SendInvitesAsync(survey, ct);
        return added;
    }

    /// <summary>Enqueues SURVEY_INVITE for every invitation not yet submitted. Safe to repeat: the outbox dedupe key
    /// (tenant, event, survey, recipient, channel, content) refuses a second identical notice.</summary>
    public async Task<int> SendInvitesAsync(EngagementSurvey survey, CancellationToken ct)
    {
        var employeeIds = await _db.EngagementSurveyInvitations.AsNoTracking()
            .Where(i => i.TenantId == survey.TenantId && i.SurveyId == survey.Id && i.Status != EngagementInvitationStatuses.Cancelled)
            .Select(i => i.EmployeeId).ToListAsync(ct);
        var title = $"Survey: {survey.TitleEn}";
        var message = $"You are invited to \"{survey.TitleEn}\" ({ModeWord(survey.Mode)}). It closes on "
                      + $"{survey.ClosesAtUtc:yyyy-MM-dd}. Open Self-Service → Surveys to respond.";
        var sent = 0;
        foreach (var employeeId in employeeIds)
        {
            var deliveries = await _notifications.EnqueueAsync(new NotificationRequest
            {
                TenantId = survey.TenantId,
                EmployeeId = employeeId,
                EventCode = EngagementEventCodes.Invite,
                EntityName = EngagementEventCodes.SurveyEntity,
                EntityId = survey.Id.ToString(),
                Title = title,
                Message = message,
            }, ct);
            sent += deliveries.Count > 0 ? 1 : 0;
        }
        var tracked = await _db.EngagementSurveys.FirstAsync(x => x.TenantId == survey.TenantId && x.Id == survey.Id, ct);
        tracked.InvitesSentAtUtc ??= Now;
        await _db.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>
    /// Sends every reminder whose day has come. Reminder n targets, by mode:
    /// Identified/Confidential — invitees who have not submitted; Anonymous — EVERY invitee, whether or not they have
    /// responded, so the notification ledger never singles out who has not (an anonymous ledger records no "opened").
    /// Idempotent: <see cref="EngagementSurveyInvitation.RemindersSent"/> advances once per ordinal, and the entity id
    /// "{survey}:r{n}" makes the outbox refuse a duplicate even if the counter write was lost.
    /// </summary>
    public async Task<int> SendDueRemindersAsync(EngagementSurvey survey, CancellationToken ct)
    {
        if (survey.Status != EngagementSurveyStatuses.Open || string.IsNullOrWhiteSpace(survey.ReminderOffsetsDays)) return 0;
        var offsets = survey.ReminderOffsetsDays.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Order().ToList();
        var dueOrdinal = offsets.Count(d => survey.OpensAtUtc.AddDays(d) <= Now);
        if (dueOrdinal == 0) return 0;

        var query = _db.EngagementSurveyInvitations
            .Where(i => i.TenantId == survey.TenantId && i.SurveyId == survey.Id && i.RemindersSent < dueOrdinal);
        query = survey.Mode == EngagementSurveyModes.Anonymous
            ? query.Where(i => i.Status != EngagementInvitationStatuses.Cancelled)
            : query.Where(i => i.Status == EngagementInvitationStatuses.Invited || i.Status == EngagementInvitationStatuses.Opened);
        var targets = await query.ToListAsync(ct);
        var title = $"Reminder: {survey.TitleEn}";
        var message = $"\"{survey.TitleEn}\" closes on {survey.ClosesAtUtc:yyyy-MM-dd}. If you have not responded yet, "
                      + "open Self-Service → Surveys. If you already have, thank you — please ignore this.";
        foreach (var invitation in targets)
        {
            await _notifications.EnqueueAsync(new NotificationRequest
            {
                TenantId = survey.TenantId,
                EmployeeId = invitation.EmployeeId,
                EventCode = EngagementEventCodes.Reminder,
                EntityName = EngagementEventCodes.SurveyEntity,
                EntityId = $"{survey.Id}:r{dueOrdinal}",
                Title = title,
                Message = message,
            }, ct);
            invitation.RemindersSent = dueOrdinal;
        }
        // Invitations that need no reminder (submitted / opened in anonymous mode) also advance, so the next tick does
        // not reconsider them for this ordinal.
        var targetIds = targets.Select(t => t.Id).ToList();
        await _db.EngagementSurveyInvitations
            .Where(i => i.TenantId == survey.TenantId && i.SurveyId == survey.Id && i.RemindersSent < dueOrdinal)
            .Where(i => !targetIds.Contains(i.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.RemindersSent, dueOrdinal), ct);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone submitted or opened while we reminded them. Their notice is in the outbox already; the next tick
            // re-reads the ledger and the dedupe key refuses a second copy.
            _db.ChangeTracker.Clear();
        }
        return targets.Count;
    }

    /// <summary>Open → Closed. Discards every unsubmitted draft and live session (a draft is never reportable).</summary>
    public async Task<EngagementResult<EngagementSurvey>> CloseAsync(Guid tenantId, Guid id, int? version, CancellationToken ct)
    {
        var survey = await _db.EngagementSurveys.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (survey is null) return EngagementResult<EngagementSurvey>.Fail(404, EngagementErrors.NotFound, "Survey not found.");
        if (version is int v && survey.Version != v) return Stale<EngagementSurvey>();
        if (!EngagementStateMachine.CanMoveSurvey(survey.Status, EngagementSurveyStatuses.Closed))
            return EngagementResult<EngagementSurvey>.Fail(409, EngagementErrors.InvalidState, "Only an open survey can be closed.");
        survey.Status = EngagementSurveyStatuses.Closed;
        survey.ClosedAtUtc = Now;
        // Freeze the k its results are decided at: a later tenant change can only hide more, never re-decide.
        // (Read tenant-wide: the worker closes surveys outside any request.)
        var tenantK = await ScopedBypass.TenantWide(_db.TenantHrConfigs, tenantId, "A survey's k is frozen from its own tenant's setting.")
            .AsNoTracking().Select(c => (int?)c.EngagementMinCohortSize).FirstOrDefaultAsync(ct) ?? CohortSuppression.DefaultK;
        survey.FrozenMinCohortSize = EngagementResultsService.CurrentK(survey, tenantK);
        survey.Version++;
        var saved = await SaveAsync(survey, ct);
        if (!saved.Ok) return saved;
        await DiscardDraftsAsync(survey, ct);
        await CreateNextCycleAsync(survey, ct);
        return saved;
    }

    public async Task<EngagementResult<EngagementSurvey>> CancelAsync(Guid tenantId, Guid id, int version, CancellationToken ct)
    {
        var survey = await _db.EngagementSurveys.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (survey is null) return EngagementResult<EngagementSurvey>.Fail(404, EngagementErrors.NotFound, "Survey not found.");
        if (survey.Version != version) return Stale<EngagementSurvey>();
        if (!EngagementStateMachine.CanMoveSurvey(survey.Status, EngagementSurveyStatuses.Cancelled))
            return EngagementResult<EngagementSurvey>.Fail(409, EngagementErrors.InvalidState,
                "Only a draft or scheduled survey can be cancelled. Close an open survey instead.");
        survey.Status = EngagementSurveyStatuses.Cancelled;
        survey.Version++;
        return await SaveAsync(survey, ct);
    }

    /// <summary>
    /// Deletes a draft, cancelled or closed survey with its invitations, responses and answers (FK CASCADE); follow-up
    /// actions survive with the title snapshot. An open or scheduled survey must be closed or cancelled first.
    /// Returns the number of responses deleted, for the survey-level audit row.
    /// </summary>
    public async Task<EngagementResult<int>> DeleteAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        var survey = await _db.EngagementSurveys.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (survey is null) return EngagementResult<int>.Fail(404, EngagementErrors.NotFound, "Survey not found.");
        if (survey.Status is not (EngagementSurveyStatuses.Draft or EngagementSurveyStatuses.Cancelled or EngagementSurveyStatuses.Closed))
            return EngagementResult<int>.Fail(409, EngagementErrors.InvalidState, "Close or cancel the survey before deleting it.");
        var responses = await _db.EngagementSurveyResponses.CountAsync(r => r.TenantId == tenantId && r.SurveyId == id, ct);
        await _db.EngagementActions.Where(a => a.TenantId == tenantId && a.SurveyId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.SurveyTitle, survey.TitleEn), ct);
        _db.EngagementSurveys.Remove(survey);
        await _db.SaveChangesAsync(ct);
        return EngagementResult<int>.Success(responses);
    }

    public async Task DiscardDraftsAsync(EngagementSurvey survey, CancellationToken ct) =>
        await _db.EngagementSurveyInvitations
            .Where(i => i.TenantId == survey.TenantId && i.SurveyId == survey.Id && i.Status != EngagementInvitationStatuses.Submitted
                        && (i.DraftCipher != null || i.SessionTokenHash != null))
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.DraftCipher, (string?)null)
                .SetProperty(i => i.DraftSavedOn, (DateOnly?)null)
                .SetProperty(i => i.SessionTokenHash, (string?)null), ct);

    /// <summary>
    /// A recurring survey's next cycle: same template, mode, audience and k, dates moved by one period, launched (so
    /// the worker opens it on its date). Exactly once: the unique (tenant, series, cycle) index refuses a second.
    /// </summary>
    public async Task<EngagementSurvey?> CreateNextCycleAsync(EngagementSurvey survey, CancellationToken ct)
    {
        var months = EngagementRecurrences.Months(survey.Recurrence);
        if (months == 0 || survey.Status != EngagementSurveyStatuses.Closed) return null;
        var nextCycle = survey.CycleNumber + 1;
        if (await _db.EngagementSurveys.AnyAsync(x => x.TenantId == survey.TenantId && x.SeriesId == survey.SeriesId && x.CycleNumber == nextCycle, ct))
            return null;
        var opens = survey.OpensAtUtc.AddMonths(months);
        var closes = survey.ClosesAtUtc.AddMonths(months);
        while (closes <= Now) { opens = opens.AddMonths(months); closes = closes.AddMonths(months); }
        var next = new EngagementSurvey
        {
            TenantId = survey.TenantId,
            CompanyId = survey.CompanyId,
            TemplateId = survey.TemplateId,
            TitleEn = survey.TitleEn,
            TitleAr = survey.TitleAr,
            Mode = survey.Mode,
            Status = EngagementSurveyStatuses.Scheduled,
            AudienceJson = survey.AudienceJson,
            OpensAtUtc = opens,
            ClosesAtUtc = closes,
            Recurrence = survey.Recurrence,
            SeriesId = survey.SeriesId,
            CycleNumber = nextCycle,
            MinCohortSize = survey.MinCohortSize,
            ReminderOffsetsDays = survey.ReminderOffsetsDays,
            CommentDimension = survey.CommentDimension,
            LaunchedAtUtc = Now,
            LaunchedBy = survey.LaunchedBy,
        };
        _db.EngagementSurveys.Add(next);
        var tracked = await _db.EngagementSurveys.FirstAsync(x => x.TenantId == survey.TenantId && x.Id == survey.Id, ct);
        tracked.NextCycleCreatedAtUtc = Now;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another instance created it between our check and insert: the unique index did its job.
            _db.ChangeTracker.Clear();
            return null;
        }
        return next;
    }

    /// <summary>The employees a survey's audience selects today: active, in the survey's company (or the audience's
    /// companies), matching every non-empty attribute list, and not joined within the exclusion window.</summary>
    public async Task<List<(int Id, Guid? CompanyId)>> EligibleEmployeesAsync(EngagementSurvey survey, CancellationToken ct)
    {
        var audience = EngagementAudience.Parse(survey.AudienceJson);
        var query = _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == survey.TenantId && !e.IsDeleted && e.Status == EmployeeStatuses.Active);
        if (survey.CompanyId is Guid company) query = query.Where(e => e.CompanyId == company);
        if (audience.CompanyIds is { Count: > 0 } companies) query = query.Where(e => e.CompanyId != null && companies.Contains(e.CompanyId.Value));
        if (audience.DepartmentIds is { Count: > 0 } departments) query = query.Where(e => e.DepartmentId != null && departments.Contains(e.DepartmentId.Value));
        if (audience.DesignationIds is { Count: > 0 } designations) query = query.Where(e => e.DesignationId != null && designations.Contains(e.DesignationId.Value));
        if (audience.GradeIds is { Count: > 0 } grades) query = query.Where(e => e.GradeId != null && grades.Contains(e.GradeId.Value));
        if (audience.ExcludeJoinedWithinDays > 0)
        {
            var cutoff = survey.OpensAtUtc.AddDays(-audience.ExcludeJoinedWithinDays);
            query = query.Where(e => e.JoiningDate <= cutoff);
        }
        var rows = await query.Select(e => new { e.Id, e.CompanyId }).ToListAsync(ct);
        return rows.Select(r => (r.Id, r.CompanyId)).ToList();
    }

    public static string ModeWord(string mode) => mode switch
    {
        EngagementSurveyModes.Identified => "identified",
        EngagementSurveyModes.Confidential => "confidential",
        _ => "anonymous",
    };

    private async Task<EngagementResult<T>> SaveAsync<T>(T entity, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            return EngagementResult<T>.Success(entity);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale<T>();
        }
    }

    private static EngagementResult<T> Stale<T>() =>
        EngagementResult<T>.Fail(409, EngagementErrors.Conflict, "Someone else changed this since you opened it. Reload and try again.");
}

/// <summary>The attribute a survey's comments may be grouped by — one, never combined with another filter.</summary>
public static class EngagementCommentDimensions
{
    public const string None = "none";
    public static readonly string[] All = [None, "company", "department", "designation", "grade", "tenure"];
}

/// <summary>The allowed transitions, stated once. Every state change in the services asks here first.</summary>
public static class EngagementStateMachine
{
    private static readonly Dictionary<string, string[]> Template = new(StringComparer.Ordinal)
    {
        [EngagementTemplateStatuses.Draft] = [EngagementTemplateStatuses.Published],
        [EngagementTemplateStatuses.Published] = [EngagementTemplateStatuses.Retired],
        [EngagementTemplateStatuses.Retired] = [],
    };

    private static readonly Dictionary<string, string[]> Survey = new(StringComparer.Ordinal)
    {
        [EngagementSurveyStatuses.Draft] = [EngagementSurveyStatuses.Scheduled, EngagementSurveyStatuses.Cancelled],
        [EngagementSurveyStatuses.Scheduled] = [EngagementSurveyStatuses.Open, EngagementSurveyStatuses.Cancelled],
        [EngagementSurveyStatuses.Open] = [EngagementSurveyStatuses.Closed],
        [EngagementSurveyStatuses.Closed] = [],
        [EngagementSurveyStatuses.Cancelled] = [],
    };

    private static readonly Dictionary<string, string[]> Invitation = new(StringComparer.Ordinal)
    {
        [EngagementInvitationStatuses.Invited] = [EngagementInvitationStatuses.Opened, EngagementInvitationStatuses.Submitted, EngagementInvitationStatuses.Cancelled],
        [EngagementInvitationStatuses.Opened] = [EngagementInvitationStatuses.Submitted, EngagementInvitationStatuses.Cancelled],
        [EngagementInvitationStatuses.Submitted] = [],
        [EngagementInvitationStatuses.Cancelled] = [],
    };

    private static readonly Dictionary<string, string[]> Action = new(StringComparer.Ordinal)
    {
        [EngagementActionStatuses.Open] = [EngagementActionStatuses.InProgress, EngagementActionStatuses.Done, EngagementActionStatuses.Cancelled],
        [EngagementActionStatuses.InProgress] = [EngagementActionStatuses.Done, EngagementActionStatuses.Cancelled],
        [EngagementActionStatuses.Done] = [EngagementActionStatuses.InProgress],
        [EngagementActionStatuses.Cancelled] = [],
    };

    public static bool CanMoveTemplate(string from, string to) => Template.TryGetValue(from, out var n) && n.Contains(to);
    public static bool CanMoveSurvey(string from, string to) => Survey.TryGetValue(from, out var n) && n.Contains(to);
    public static bool CanMoveInvitation(string from, string to) => Invitation.TryGetValue(from, out var n) && n.Contains(to);
    public static bool CanMoveAction(string from, string to) => from == to || (Action.TryGetValue(from, out var n) && n.Contains(to));
}
