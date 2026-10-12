using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Engagement;

public sealed record EngagementActionInput(
    Guid? SurveyId, string SourceKind, string? SourceRef, string Title, string? Description, int OwnerEmployeeId, DateOnly DueOn);

public sealed record EngagementProgressEntry(DateTime AtUtc, string ByName, string Status, string Note);

public sealed record EngagementActionDto(
    Guid Id, Guid? SurveyId, string SurveyTitle, string SourceKind, string SourceRef, string Title, string Description,
    int OwnerEmployeeId, string OwnerName, DateOnly DueOn, string Status, bool Overdue, DateTime? CompletedAtUtc,
    IReadOnlyList<EngagementProgressEntry> Progress, int Version);

/// <summary>Follow-up actions: create, update, progress, overdue reminders. Owners act on their own; managers and HR
/// within their scope (the controller decides who may touch which action).</summary>
public sealed class EngagementActionService
{
    public static readonly string[] SourceKinds = ["Overall", "Driver", "Item", "Cohort"];
    private readonly ZayraDbContext _db;
    private readonly INotificationService _notifications;
    private readonly TimeProvider _clock;

    public EngagementActionService(ZayraDbContext db, INotificationService notifications, TimeProvider? clock = null)
    {
        _db = db;
        _notifications = notifications;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private DateOnly Today => DateOnly.FromDateTime(Now);

    public async Task<IReadOnlyList<EngagementActionDto>> ListAsync(IQueryable<EngagementAction> query, CancellationToken ct)
    {
        var rows = await query.AsNoTracking().OrderBy(a => a.Status == EngagementActionStatuses.Done || a.Status == EngagementActionStatuses.Cancelled)
            .ThenBy(a => a.DueOn).Take(500).ToListAsync(ct);
        var ownerIds = rows.Select(r => r.OwnerEmployeeId).Distinct().ToList();
        var names = await _db.Employees.AsNoTracking().Where(e => ownerIds.Contains(e.Id)).Select(e => new { e.Id, e.FullName })
            .ToDictionaryAsync(e => e.Id, e => e.FullName, ct);
        return rows.Select(a => ToDto(a, names.GetValueOrDefault(a.OwnerEmployeeId, "—"))).ToList();
    }

    public EngagementActionDto ToDto(EngagementAction a, string ownerName) => new(
        a.Id, a.SurveyId, a.SurveyTitle, a.SourceKind, a.SourceRef, a.Title, a.Description, a.OwnerEmployeeId, ownerName,
        a.DueOn, a.Status, a.DueOn < Today && a.Status is EngagementActionStatuses.Open or EngagementActionStatuses.InProgress,
        a.CompletedAtUtc, ParseProgress(a.ProgressJson), a.Version);

    public async Task<EngagementResult<EngagementAction>> CreateAsync(Guid tenantId, EngagementActionInput input, string byName, CancellationToken ct)
    {
        if (Problem(input) is { } problem) return EngagementResult<EngagementAction>.Fail(400, EngagementErrors.InvalidRequest, problem);
        var owner = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == input.OwnerEmployeeId && !e.IsDeleted)
            .Select(e => new { e.Id, e.CompanyId }).FirstOrDefaultAsync(ct);
        if (owner is null) return EngagementResult<EngagementAction>.Fail(400, EngagementErrors.InvalidRequest, "Choose an owner from your employees.");
        string surveyTitle = string.Empty;
        if (input.SurveyId is Guid surveyId)
        {
            var survey = await _db.EngagementSurveys.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == surveyId, ct);
            if (survey is null) return EngagementResult<EngagementAction>.Fail(400, EngagementErrors.InvalidRequest, "The survey was not found.");
            surveyTitle = survey.TitleEn;
        }
        var action = new EngagementAction
        {
            TenantId = tenantId,
            CompanyId = owner.CompanyId,
            SurveyId = input.SurveyId,
            SurveyTitle = surveyTitle,
            SourceKind = input.SourceKind,
            SourceRef = (input.SourceRef ?? string.Empty).Trim(),
            Title = input.Title.Trim(),
            Description = (input.Description ?? string.Empty).Trim(),
            OwnerEmployeeId = owner.Id,
            DueOn = input.DueOn,
            ProgressJson = JsonSerializer.Serialize(new[] { new EngagementProgressEntry(Now, byName, EngagementActionStatuses.Open, "Action created.") }, EngagementTemplateContent.Json),
        };
        _db.EngagementActions.Add(action);
        await _db.SaveChangesAsync(ct);
        return EngagementResult<EngagementAction>.Success(action);
    }

    public async Task<EngagementResult<EngagementAction>> UpdateAsync(EngagementAction action, int version, EngagementActionInput input, CancellationToken ct)
    {
        if (action.Version != version) return Stale();
        if (Problem(input) is { } problem) return EngagementResult<EngagementAction>.Fail(400, EngagementErrors.InvalidRequest, problem);
        if (input.OwnerEmployeeId != action.OwnerEmployeeId)
        {
            var owner = await _db.Employees.AsNoTracking()
                .Where(e => e.TenantId == action.TenantId && e.Id == input.OwnerEmployeeId && !e.IsDeleted)
                .Select(e => new { e.Id, e.CompanyId }).FirstOrDefaultAsync(ct);
            if (owner is null) return EngagementResult<EngagementAction>.Fail(400, EngagementErrors.InvalidRequest, "Choose an owner from your employees.");
            if (owner.CompanyId != action.CompanyId)
                return EngagementResult<EngagementAction>.Fail(400, EngagementErrors.InvalidRequest, "The new owner must work in the same company as the action.");
            action.OwnerEmployeeId = owner.Id;
        }
        action.Title = input.Title.Trim();
        action.Description = (input.Description ?? string.Empty).Trim();
        action.SourceKind = input.SourceKind;
        action.SourceRef = (input.SourceRef ?? string.Empty).Trim();
        if (action.DueOn != input.DueOn) action.OverdueRemindedOn = null;
        action.DueOn = input.DueOn;
        action.Version++;
        return await SaveAsync(action, ct);
    }

    public async Task<EngagementResult<EngagementAction>> ProgressAsync(EngagementAction action, int version, string status, string? note, string byName, CancellationToken ct)
    {
        if (action.Version != version) return Stale();
        if (!EngagementActionStatuses.All.Contains(status, StringComparer.Ordinal))
            return EngagementResult<EngagementAction>.Fail(400, EngagementErrors.InvalidRequest, "Status must be Open, InProgress, Done or Cancelled.");
        if (!EngagementStateMachine.CanMoveAction(action.Status, status))
            return EngagementResult<EngagementAction>.Fail(409, EngagementErrors.InvalidState, $"An action that is {action.Status} cannot become {status}.");
        var text = (note ?? string.Empty).Trim();
        if (text.Length > 1000) return EngagementResult<EngagementAction>.Fail(400, EngagementErrors.InvalidRequest, "A progress note is at most 1000 characters.");
        if (text.Length == 0 && status == action.Status)
            return EngagementResult<EngagementAction>.Fail(400, EngagementErrors.InvalidRequest, "Write a note or change the status.");
        var progress = ParseProgress(action.ProgressJson).ToList();
        progress.Add(new EngagementProgressEntry(Now, byName, status, text));
        action.ProgressJson = JsonSerializer.Serialize(progress.TakeLast(100), EngagementTemplateContent.Json);
        action.Status = status;
        action.CompletedAtUtc = status == EngagementActionStatuses.Done ? Now : null;
        action.Version++;
        return await SaveAsync(action, ct);
    }

    /// <summary>One SURVEY_ACTION_DUE to the owner the day after the due date passes. Idempotent: OverdueRemindedOn
    /// is set in the same save, and the outbox dedupe key refuses a repeat if that write was lost.</summary>
    public async Task<int> RemindOverdueAsync(Guid tenantId, CancellationToken ct)
    {
        var today = Today;
        var overdue = await _db.EngagementActions
            .Where(a => a.TenantId == tenantId && a.DueOn < today && a.OverdueRemindedOn == null
                        && (a.Status == EngagementActionStatuses.Open || a.Status == EngagementActionStatuses.InProgress))
            .OrderBy(a => a.DueOn).Take(200).ToListAsync(ct);
        foreach (var action in overdue)
        {
            await _notifications.EnqueueAsync(new NotificationRequest
            {
                TenantId = tenantId,
                EmployeeId = action.OwnerEmployeeId,
                EventCode = EngagementEventCodes.ActionDue,
                EntityName = EngagementEventCodes.ActionEntity,
                EntityId = $"{action.Id}:{action.DueOn:yyyyMMdd}",
                Title = $"Survey action overdue: {action.Title}",
                Message = $"\"{action.Title}\" was due on {action.DueOn:yyyy-MM-dd}. Record progress in Self-Service → Surveys → My actions.",
            }, ct);
            action.OverdueRemindedOn = today;
        }
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { _db.ChangeTracker.Clear(); }
        return overdue.Count;
    }

    private static string? Problem(EngagementActionInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 200) return "Give the action a title of up to 200 characters.";
        if ((input.Description?.Length ?? 0) > 2000) return "The description is at most 2000 characters.";
        if (!SourceKinds.Contains(input.SourceKind, StringComparer.Ordinal)) return "Source must be Overall, Driver, Item or Cohort.";
        if ((input.SourceRef?.Length ?? 0) > 200) return "The source reference is at most 200 characters.";
        if (input.OwnerEmployeeId <= 0) return "Choose an owner.";
        if (input.DueOn == default) return "Choose a due date.";
        return null;
    }

    public static IReadOnlyList<EngagementProgressEntry> ParseProgress(string json)
    {
        try { return JsonSerializer.Deserialize<List<EngagementProgressEntry>>(json, EngagementTemplateContent.Json) ?? []; }
        catch (JsonException) { return []; }
    }

    private async Task<EngagementResult<EngagementAction>> SaveAsync(EngagementAction action, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            return EngagementResult<EngagementAction>.Success(action);
        }
        catch (DbUpdateConcurrencyException) { return Stale(); }
    }

    private static EngagementResult<EngagementAction> Stale() =>
        EngagementResult<EngagementAction>.Fail(409, EngagementErrors.Conflict, "Someone else changed this action. Reload and try again.");
}
