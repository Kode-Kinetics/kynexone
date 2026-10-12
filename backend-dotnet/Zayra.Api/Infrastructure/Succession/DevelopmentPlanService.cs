using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Learning;
using Zayra.Api.Infrastructure.Modules;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Succession;

public sealed record PlanInput(int EmployeeId, string? Purpose, int? OwnerEmployeeId, DateOnly? NextReviewOn, Guid? NominationId);

public sealed record PlanReviewInput(string? Note, DateOnly? NextReviewOn, int Version);

public sealed record ActionInput(string? ActionType, string? Title, string? Description, DateOnly? DueOn, int? OwnerEmployeeId, Guid? LearningPathId,
    Guid? LearningAssignmentId, Guid? EmployeeGoalId, int? MentorEmployeeId);

public sealed record ActionUpdateInput(string? Status, string? ProgressNote, DateOnly? DueOn, int Version, bool? ProposeCompletion = null);

public sealed record PlanProgress(int Total, int Completed, int Cancelled, int Overdue, decimal? PercentComplete, bool OnTrack, bool ReviewOverdue);

public sealed record PlanActionDto(Guid Id, string ActionType, string Title, string? Description, string Status, DateOnly? DueOn, bool IsOverdue,
    PersonRef? Owner, PersonRef? Mentor, Guid? LearningAssignmentId, string? LearningPathName, string? LearningPathNameAr, string? LearningStatus,
    Guid? EmployeeGoalId, string? GoalTitle, string? GoalStatus, string? ProgressNote, DateTime? CompletedAtUtc, string? CompletionMethod, int SortOrder,
    int Version, DateTime? CompletionProposedAtUtc = null);

public sealed record DevelopmentPlanDto(Guid Id, PersonRef Employee, PersonRef? Owner, string Purpose, string Status, DateOnly NextReviewOn,
    DateTime? LastReviewedAtUtc, string? LastReviewNote, DateTime? ActivatedAtUtc, DateTime? CompletedAtUtc, DateTime? CancelledAtUtc, string? CancelReason,
    Guid? NominationId, PlanProgress Progress, IReadOnlyList<PlanActionDto> Actions, DateTime CreatedAtUtc, int Version);

/// <summary>An employee's own view: plans, plus — only when the tenant switched candidate visibility on — their own memberships.</summary>
public sealed record MyDevelopmentDto(DevelopmentPlanDto? Current, IReadOnlyList<DevelopmentPlanDto> History, bool CandidateVisibility,
    IReadOnlyList<MyMembershipDto> Memberships);

public sealed record MyMembershipDto(string Kind, string Title, string? TitleAr, string Stage, string? Readiness);

/// <summary>
/// Development plans (SUC-04): Draft → Active ⇄ OnHold → Completed / Cancelled; a review stamps last_reviewed_at and moves
/// next_review_on forward. Learning actions create (or link) a learning_assignments row through the LRN service — never a
/// copy — and Goal actions link an EmployeeGoal; both complete themselves when the linked record does
/// (LinkedLearningCompleted / LinkedGoalCompleted). The nomination link is HR context: it is serialised only to callers
/// holding succession.slates.read, never to the employee or a line manager.
/// </summary>
public sealed class DevelopmentPlanService(ZayraDbContext db, IAuditService audit, TimeProvider clock, IServiceProvider services,
    ITenantModuleService modules, SuccessionAccess access)
{
    /// <summary>N3: nobody creates, moves, edits or confirms their OWN plan through the manager/HR surface (403).</summary>
    private async Task EnsureNotOwnPlanAsync(SuccessionActor actor, int planEmployeeId, CancellationToken ct)
    {
        if ((await access.CallerEmployeesAsync(actor, ct)).Contains(planEmployeeId))
            throw new SuccessionException(403, "self_decision_forbidden",
                "This is your own development plan: your manager or HR manages it. You can add notes and propose completion in self-service.");
    }

    // ── Reads ───────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DevelopmentPlanDto>> ListAsync(SuccessionActor actor, int? employeeId, string? status, CancellationToken ct)
    {
        var query = db.DevelopmentPlans.AsNoTracking().Where(p => p.TenantId == actor.TenantId);
        if (employeeId is int e) query = query.Where(p => p.EmployeeId == e);
        query = status switch
        {
            null or "" or "open" => query.Where(p => p.Status == DevelopmentPlanStatuses.Draft || DevelopmentPlanStatuses.Current.Contains(p.Status)),
            "all" => query,
            _ => query.Where(p => p.Status == status),
        };
        var rows = (await query.OrderBy(p => p.NextReviewOn).Take(2000).ToListAsync(ct)).Where(p => actor.Scope.CanAccessEmployee(p.EmployeeId)).ToList();
        await SyncLinkedAsync(actor.TenantId, rows.Select(r => r.Id).ToList(), ct);
        return await DtosAsync(actor.TenantId, rows, actor.Holds(SuccessionPermissions.SlatesRead), ct);
    }

    public async Task<DevelopmentPlanDto> GetAsync(SuccessionActor actor, Guid id, CancellationToken ct)
    {
        var plan = await LoadAsync(actor, id, tracked: false, ct);
        await SyncLinkedAsync(actor.TenantId, [plan.Id], ct);
        return (await DtosAsync(actor.TenantId, [plan], actor.Holds(SuccessionPermissions.SlatesRead), ct)).Single();
    }

    /// <summary>ESS: the caller's own plans. The nomination link is never included.</summary>
    public async Task<MyDevelopmentDto> MineAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        var plans = await SuccessionDb.TenantRows(db.DevelopmentPlans, tenantId).AsNoTracking()
            .Where(p => p.EmployeeId == employeeId && p.Status != DevelopmentPlanStatuses.Draft)
            .OrderByDescending(p => p.CreatedAtUtc).ToListAsync(ct);
        await SyncLinkedAsync(tenantId, plans.Select(p => p.Id).ToList(), ct);
        var dtos = await DtosAsync(tenantId, plans, includeNomination: false, ct);
        var current = dtos.FirstOrDefault(d => DevelopmentPlanStatuses.Current.Contains(d.Status));
        var visibility = (await modules.GetStateAsync(tenantId, ct)).IsEnabled(FeatureKeys.SuccessionCandidateVisibility);
        IReadOnlyList<MyMembershipDto> memberships = [];
        if (visibility)
        {
            var mine = await SuccessionDb.TenantRows(db.SuccessionNominations, tenantId).AsNoTracking()
                .Where(n => n.EmployeeId == employeeId && NominationStatuses.Live.Contains(n.Status)).ToListAsync(ct);
            var poolIds = mine.Where(n => n.TalentPoolId != null).Select(n => n.TalentPoolId!.Value).ToList();
            var positionIds = mine.Where(n => n.CriticalPositionId != null).Select(n => n.CriticalPositionId!.Value).ToList();
            var pools = await SuccessionDb.TenantRows(db.TalentPools, tenantId).AsNoTracking().Where(p => poolIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            var positions = await (from c in SuccessionDb.TenantRows(db.SuccessionCriticalPositions, tenantId).AsNoTracking()
                                   join p in SuccessionDb.TenantRows(db.Positions, tenantId).AsNoTracking() on c.PositionId equals p.Id
                                   where positionIds.Contains(c.Id) select new { c.Id, p.Title }).ToDictionaryAsync(x => x.Id, x => x.Title, ct);
            // Never a slate, a 9-box cell, other candidates or an order: only that they are being developed for the target.
            memberships = mine.Select(n => n.TalentPoolId is Guid pool
                    ? new MyMembershipDto("pool", pools.GetValueOrDefault(pool)?.NameEn ?? string.Empty, pools.GetValueOrDefault(pool)?.NameAr,
                        n.Status == NominationStatuses.Proposed ? "Proposed" : "Member", null)
                    : new MyMembershipDto("position", positions.GetValueOrDefault(n.CriticalPositionId!.Value) ?? string.Empty, null,
                        n.Status == NominationStatuses.Proposed ? "Proposed" : "Considered", n.Readiness))
                .ToList();
        }
        return new MyDevelopmentDto(current, dtos.Where(d => d != current).ToList(), visibility, memberships);
    }

    // ── Plan lifecycle (succession.devplans.manage) ─────────────────────────────────────────────────────────────

    public async Task<DevelopmentPlanDto> CreateAsync(SuccessionActor actor, PlanInput input, CancellationToken ct)
    {
        var employee = await InScopeEmployeeAsync(actor, input.EmployeeId, ct);
        await EnsureNotOwnPlanAsync(actor, input.EmployeeId, ct);
        var purpose = SuccessionDb.Reason(input.Purpose, "the plan's purpose", 10, 2000);
        var today = Today();
        var review = input.NextReviewOn ?? today.AddMonths(3);
        if (review <= today) throw SuccessionException.Invalid("The next review date must be in the future.");
        if (input.OwnerEmployeeId is int owner) await ActiveEmployeeAsync(actor.TenantId, owner, "plan owner", ct);
        if (input.NominationId is Guid nominationId)
        {
            if (!actor.Holds(SuccessionPermissions.SlatesRead)) throw SuccessionException.NotFound("Nomination");
            var ok = await db.SuccessionNominations.AsNoTracking().AnyAsync(n => n.TenantId == actor.TenantId && n.Id == nominationId
                && n.EmployeeId == input.EmployeeId, ct);
            if (!ok) throw SuccessionException.NotFound("Nomination");
        }
        var now = clock.GetUtcNow().UtcDateTime;
        var plan = new DevelopmentPlan
        {
            TenantId = actor.TenantId, CompanyId = employee.CompanyId, EmployeeId = input.EmployeeId, NominationId = input.NominationId,
            OwnerEmployeeId = input.OwnerEmployeeId ?? actor.EmployeeId, Purpose = purpose, Status = DevelopmentPlanStatuses.Draft,
            NextReviewOn = review, CreatedByUserId = actor.UserId, CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        db.DevelopmentPlans.Add(plan);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("succession.devplan.created", nameof(DevelopmentPlan), plan.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { planId = plan.Id }), ct);
        return await GetAsync(actor, plan.Id, ct);
    }

    public Task<DevelopmentPlanDto> ActivateAsync(SuccessionActor actor, Guid id, CancellationToken ct) =>
        TransitionAsync(actor, id, "activated", null, plan =>
        {
            if (plan.Status is not (DevelopmentPlanStatuses.Draft or DevelopmentPlanStatuses.OnHold))
                throw SuccessionException.Conflict("invalid_transition", "Only a draft or on-hold plan can be made active.", new { plan.Status });
            plan.Status = DevelopmentPlanStatuses.Active;
            plan.ActivatedAtUtc ??= clock.GetUtcNow().UtcDateTime;
        }, requireActions: true, ct);

    public Task<DevelopmentPlanDto> HoldAsync(SuccessionActor actor, Guid id, string? reason, CancellationToken ct)
    {
        var why = SuccessionDb.Reason(reason, "a reason for putting the plan on hold", 3, 500);
        return TransitionAsync(actor, id, "held", new { reason = why }, plan =>
        {
            if (plan.Status != DevelopmentPlanStatuses.Active)
                throw SuccessionException.Conflict("invalid_transition", "Only an active plan can be put on hold.", new { plan.Status });
            plan.Status = DevelopmentPlanStatuses.OnHold;
            plan.LastReviewNote = $"On hold: {why}";
        }, requireActions: false, ct);
    }

    public async Task<DevelopmentPlanDto> CompleteAsync(SuccessionActor actor, Guid id, CancellationToken ct)
    {
        await SyncLinkedAsync(actor.TenantId, [id], ct);
        var open = await db.DevelopmentPlanActions.CountAsync(a => a.TenantId == actor.TenantId && a.PlanId == id
            && DevelopmentActionStatuses.Open.Contains(a.Status), ct);
        return await TransitionAsync(actor, id, "completed", null, plan =>
        {
            if (plan.Status is not (DevelopmentPlanStatuses.Active or DevelopmentPlanStatuses.OnHold))
                throw SuccessionException.Conflict("invalid_transition", "Only an active or on-hold plan can be completed.", new { plan.Status });
            if (open > 0)
                throw SuccessionException.Conflict("open_actions", $"{open} action(s) are still open. Complete or cancel them first.", new { open });
            plan.Status = DevelopmentPlanStatuses.Completed;
            plan.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
        }, requireActions: false, ct);
    }

    public async Task<DevelopmentPlanDto> CancelAsync(SuccessionActor actor, Guid id, string? reason, CancellationToken ct)
    {
        var why = SuccessionDb.Reason(reason, "a reason for cancelling the plan", 3, 500);
        var plan = await LoadAsync(actor, id, tracked: true, ct);
        await EnsureNotOwnPlanAsync(actor, plan.EmployeeId, ct);
        if (plan.Status is DevelopmentPlanStatuses.Completed or DevelopmentPlanStatuses.Cancelled)
            throw SuccessionException.Conflict("invalid_transition", "This plan is already closed.");
        var now = clock.GetUtcNow().UtcDateTime;
        plan.Status = DevelopmentPlanStatuses.Cancelled;
        plan.CancelledAtUtc = now;
        plan.CancelReason = why;
        plan.UpdatedAtUtc = now;
        plan.Version++;
        foreach (var action in await db.DevelopmentPlanActions.Where(a => a.PlanId == id && DevelopmentActionStatuses.Open.Contains(a.Status)).ToListAsync(ct))
        {
            action.Status = DevelopmentActionStatuses.Cancelled;
            action.UpdatedAtUtc = now;
            action.Version++;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("succession.devplan.cancelled", nameof(DevelopmentPlan), id.ToString(), actor.Request, null, ct);
        return await GetAsync(actor, id, ct);
    }

    public async Task<DevelopmentPlanDto> ReviewAsync(SuccessionActor actor, Guid id, PlanReviewInput input, CancellationToken ct)
    {
        var plan = await LoadAsync(actor, id, tracked: true, ct);
        await EnsureNotOwnPlanAsync(actor, plan.EmployeeId, ct);
        if (plan.Status is not (DevelopmentPlanStatuses.Active or DevelopmentPlanStatuses.OnHold))
            throw SuccessionException.Conflict("invalid_transition", "Only an active or on-hold plan is reviewed.");
        if (plan.Version != input.Version) throw new DbUpdateConcurrencyException("stale version");
        var note = SuccessionDb.Reason(input.Note, "a review note", 5, 2000);
        var next = input.NextReviewOn ?? throw SuccessionException.Invalid("Choose the next review date.");
        if (next <= Today()) throw SuccessionException.Invalid("The next review date must be in the future.");
        var now = clock.GetUtcNow().UtcDateTime;
        var previous = plan.NextReviewOn;
        plan.LastReviewedAtUtc = now;
        plan.LastReviewNote = note;
        plan.NextReviewOn = next;
        plan.ReviewReminderSentFor = null;
        plan.UpdatedAtUtc = now;
        plan.Version++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("succession.devplan.reviewed", nameof(DevelopmentPlan), id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { planId = id }), ct);
        return await GetAsync(actor, id, ct);
    }

    // ── Actions ─────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<DevelopmentPlanDto> AddActionAsync(SuccessionActor actor, Guid planId, ActionInput input, CancellationToken ct)
    {
        var plan = await LoadAsync(actor, planId, tracked: false, ct);
        await EnsureNotOwnPlanAsync(actor, plan.EmployeeId, ct);
        if (plan.Status is DevelopmentPlanStatuses.Completed or DevelopmentPlanStatuses.Cancelled)
            throw SuccessionException.Conflict("plan_closed", "This plan is closed and takes no new actions.");
        var type = SuccessionPositionService.Pick(input.ActionType, DevelopmentActionTypes.All, "action type");
        var title = (input.Title ?? string.Empty).Trim();
        if (title.Length is < 3 or > 200) throw SuccessionException.Invalid("Give the action a title (3 to 200 characters).");
        if (input.DueOn is { } due && due < Today()) throw SuccessionException.Invalid("The due date cannot be in the past.");
        if (input.OwnerEmployeeId is int owner) await ActiveEmployeeAsync(actor.TenantId, owner, "action owner", ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var action = new DevelopmentPlanAction
        {
            TenantId = actor.TenantId, CompanyId = plan.CompanyId, PlanId = plan.Id, ActionType = type, Title = title,
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim()[..Math.Min(2000, input.Description.Trim().Length)],
            DueOn = input.DueOn, OwnerEmployeeId = input.OwnerEmployeeId ?? plan.EmployeeId, CreatedAtUtc = now, UpdatedAtUtc = now,
            SortOrder = (await db.DevelopmentPlanActions.Where(a => a.PlanId == plan.Id).MaxAsync(a => (int?)a.SortOrder, ct) ?? 0) + 1,
        };
        switch (type)
        {
            case DevelopmentActionTypes.Learning:
                action.LearningAssignmentId = await LinkLearningAsync(actor, plan.EmployeeId, input, ct);
                break;
            case DevelopmentActionTypes.Goal:
                if (input.EmployeeGoalId is not Guid goalId) throw SuccessionException.Invalid("Choose the employee's goal this action follows.");
                var goal = await db.EmployeeGoals.AsNoTracking().FirstOrDefaultAsync(g => g.TenantId == actor.TenantId && g.Id == goalId && !g.IsDeleted, ct);
                if (goal is null || goal.EmployeeId != plan.EmployeeId) throw SuccessionException.NotFound("Goal");
                action.EmployeeGoalId = goal.Id;
                break;
            case DevelopmentActionTypes.Mentoring:
                if (input.MentorEmployeeId is not int mentor) throw SuccessionException.Invalid("Choose the mentor.");
                if (mentor == plan.EmployeeId) throw SuccessionException.Invalid("An employee cannot mentor themselves.");
                await ActiveEmployeeAsync(actor.TenantId, mentor, "mentor", ct);
                action.MentorEmployeeId = mentor;
                break;
        }
        db.DevelopmentPlanActions.Add(action);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("succession.devplan.action_added", nameof(DevelopmentPlanAction), action.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { planId, actionId = action.Id }), ct);
        return await GetAsync(actor, planId, ct);
    }

    /// <summary>HR or the line manager updates an action. A linked action's status follows its link; only its note and date change here.</summary>
    public async Task<DevelopmentPlanDto> UpdateActionAsync(SuccessionActor actor, Guid actionId, ActionUpdateInput input, CancellationToken ct)
    {
        var action = await db.DevelopmentPlanActions.FirstOrDefaultAsync(a => a.TenantId == actor.TenantId && a.Id == actionId, ct)
            ?? throw SuccessionException.NotFound("Action");
        var plan = await LoadAsync(actor, action.PlanId, tracked: false, ct);
        await EnsureNotOwnPlanAsync(actor, plan.EmployeeId, ct);
        await ApplyActionUpdateAsync(actor.Request, plan, action, input, allowLinkedStatus: false, ct);
        return await GetAsync(actor, plan.Id, ct);
    }

    /// <summary>
    /// ESS (Integration Owner F5): on their own plan's MANUAL actions the employee may add a progress note, move NotStarted →
    /// InProgress, and PROPOSE completion — the plan owner or HR confirms it (<see cref="ConfirmCompletionAsync"/>). Cancelling
    /// and due dates are the owner's or HR's; a linked (Learning/Goal) action follows its link and is untouchable here.
    /// </summary>
    public async Task<MyDevelopmentDto> UpdateMyActionAsync(Guid tenantId, int employeeId, RequestContext request, Guid actionId, ActionUpdateInput input,
        CancellationToken ct)
    {
        var action = await SuccessionDb.TenantRows(db.DevelopmentPlanActions, tenantId).FirstOrDefaultAsync(a => a.Id == actionId, ct)
            ?? throw SuccessionException.NotFound("Action");
        var plan = await SuccessionDb.TenantRows(db.DevelopmentPlans, tenantId).AsNoTracking().FirstOrDefaultAsync(p => p.Id == action.PlanId, ct);
        if (plan is null || plan.EmployeeId != employeeId || plan.Status == DevelopmentPlanStatuses.Draft) throw SuccessionException.NotFound("Action");
        if (plan.Status is DevelopmentPlanStatuses.Completed or DevelopmentPlanStatuses.Cancelled)
            throw SuccessionException.Conflict("plan_closed", "This plan is closed.");
        if (action.ActionType is DevelopmentActionTypes.Learning or DevelopmentActionTypes.Goal)
            throw SuccessionException.Conflict("linked_action", "This action follows the linked learning or goal; complete that instead.");
        if (action.Version != input.Version) throw new DbUpdateConcurrencyException("stale version");
        if (input.DueOn is not null && input.DueOn != action.DueOn)
            throw new SuccessionException(403, "owner_only", "Only the plan owner or HR can change a due date.");
        if (!DevelopmentActionStatuses.Open.Contains(action.Status))
            throw SuccessionException.Conflict("invalid_transition", "This action is already closed.");
        if (input.Status is not null && input.Status != action.Status)
        {
            if (!(action.Status == DevelopmentActionStatuses.NotStarted && input.Status == DevelopmentActionStatuses.InProgress))
                throw new SuccessionException(403, "owner_only",
                    "You can mark an action as started, or propose that it is done; the plan owner or HR completes or cancels it.");
            action.Status = DevelopmentActionStatuses.InProgress;
        }
        if (input.ProposeCompletion == true && action.CompletionProposedAtUtc is null)
        {
            action.CompletionProposedAtUtc = clock.GetUtcNow().UtcDateTime;
            if (action.Status == DevelopmentActionStatuses.NotStarted) action.Status = DevelopmentActionStatuses.InProgress;
        }
        if (input.ProgressNote is not null) action.ProgressNote = input.ProgressNote.Trim().Length <= 2000 ? input.ProgressNote.Trim() : input.ProgressNote.Trim()[..2000];
        action.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        action.Version++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(input.ProposeCompletion == true ? "succession.devplan.completion_proposed" : "succession.devplan.action_updated",
            nameof(DevelopmentPlanAction), action.Id.ToString(), request, JsonSerializer.Serialize(new { planId = action.PlanId, actionId = action.Id }), ct);
        return await MineAsync(tenantId, employeeId, ct);
    }

    /// <summary>The plan owner or HR confirms a completion the employee proposed (succession.devplans.manage).</summary>
    public async Task<DevelopmentPlanDto> ConfirmCompletionAsync(SuccessionActor actor, Guid actionId, int version, CancellationToken ct)
    {
        var action = await db.DevelopmentPlanActions.FirstOrDefaultAsync(a => a.TenantId == actor.TenantId && a.Id == actionId, ct)
            ?? throw SuccessionException.NotFound("Action");
        var plan = await LoadAsync(actor, action.PlanId, tracked: false, ct);
        await EnsureNotOwnPlanAsync(actor, plan.EmployeeId, ct);
        if (action.CompletionProposedAtUtc is null || !DevelopmentActionStatuses.Open.Contains(action.Status))
            throw SuccessionException.Conflict("no_proposal", "There is no open completion proposal on this action.");
        await ApplyActionUpdateAsync(actor.Request, plan, action, new ActionUpdateInput(DevelopmentActionStatuses.Completed, null, null, version), allowLinkedStatus: false, ct);
        return await GetAsync(actor, plan.Id, ct);
    }

    private async Task ApplyActionUpdateAsync(RequestContext request, DevelopmentPlan plan, DevelopmentPlanAction action, ActionUpdateInput input,
        bool allowLinkedStatus, CancellationToken ct)
    {
        if (plan.Status is DevelopmentPlanStatuses.Completed or DevelopmentPlanStatuses.Cancelled)
            throw SuccessionException.Conflict("plan_closed", "This plan is closed.");
        if (action.Version != input.Version) throw new DbUpdateConcurrencyException("stale version");
        var linked = action.ActionType is DevelopmentActionTypes.Learning or DevelopmentActionTypes.Goal;
        var before = action.Status;
        if (input.Status is not null && input.Status != action.Status)
        {
            var status = SuccessionPositionService.Pick(input.Status, DevelopmentActionStatuses.All, "status");
            if (linked && !allowLinkedStatus && status != DevelopmentActionStatuses.Cancelled)
                throw SuccessionException.Conflict("linked_action",
                    action.ActionType == DevelopmentActionTypes.Learning
                        ? "This action completes itself when the linked learning is completed."
                        : "This action completes itself when the linked goal is completed.");
            if (action.Status is DevelopmentActionStatuses.Completed or DevelopmentActionStatuses.Cancelled)
                throw SuccessionException.Conflict("invalid_transition", "This action is already closed.");
            action.Status = status;
            if (status == DevelopmentActionStatuses.Completed)
            {
                action.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
                action.CompletionMethod = DevelopmentCompletionMethods.Manual;
            }
            else if (status == DevelopmentActionStatuses.InProgress)
                action.CompletionProposedAtUtc = null; // the owner declined the employee's proposal
        }
        if (input.ProgressNote is not null) action.ProgressNote = input.ProgressNote.Trim().Length <= 2000 ? input.ProgressNote.Trim() : input.ProgressNote.Trim()[..2000];
        if (input.DueOn is { } due && due != action.DueOn)
        {
            if (due < Today()) throw SuccessionException.Invalid("The due date cannot be in the past.");
            action.DueOn = due;
            action.OverdueNoticeSentAtUtc = null;
        }
        action.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        action.Version++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("succession.devplan.action_updated", nameof(DevelopmentPlanAction), action.Id.ToString(), request,
            JsonSerializer.Serialize(new { planId = action.PlanId, actionId = action.Id }), ct);
    }

    // ── Linked completion (SUC-04) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Brings open Learning and Goal actions in line with what they link: a completed learning assignment or goal
    /// completes the action (LinkedLearningCompleted / LinkedGoalCompleted, stamped with the link's completion time); a
    /// started one moves NotStarted → InProgress. Idempotent: an action already closed is never touched. Called by the
    /// LRN completion hook, on every read, and by the sweep — whichever comes first does the work.
    /// </summary>
    public async Task<int> SyncLinkedAsync(Guid tenantId, IReadOnlyCollection<Guid>? planIds, CancellationToken ct, Guid? learningAssignmentId = null)
    {
        // Open linked actions, plus linked actions completed BY their link — a revoked certificate reopens the learning
        // (LRN), and the plan action must reopen with it (F8).
        var query = SuccessionDb.TenantRows(db.DevelopmentPlanActions, tenantId)
            .Where(a => (a.ActionType == DevelopmentActionTypes.Learning || a.ActionType == DevelopmentActionTypes.Goal)
                        && (DevelopmentActionStatuses.Open.Contains(a.Status)
                            || (a.Status == DevelopmentActionStatuses.Completed
                                && (a.CompletionMethod == DevelopmentCompletionMethods.LinkedLearningCompleted
                                    || a.CompletionMethod == DevelopmentCompletionMethods.LinkedGoalCompleted))));
        if (planIds is not null) query = query.Where(a => planIds.Contains(a.PlanId));
        if (learningAssignmentId is Guid la) query = query.Where(a => a.LearningAssignmentId == la);
        var actions = await query.Take(5000).ToListAsync(ct);
        if (actions.Count == 0) return 0;
        var assignmentIds = actions.Where(a => a.LearningAssignmentId != null).Select(a => a.LearningAssignmentId!.Value).ToList();
        var goalIds = actions.Where(a => a.EmployeeGoalId != null).Select(a => a.EmployeeGoalId!.Value).ToList();
        var assignments = await SuccessionDb.TenantRows(db.LearningAssignments, tenantId).AsNoTracking().Where(a => assignmentIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);
        var goals = await db.EmployeeGoals.AsNoTracking().Where(g => g.TenantId == tenantId && goalIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct);
        var changed = new List<(DevelopmentPlanAction Action, string From)>();
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var action in actions)
        {
            var from = action.Status;
            if (action.Status == DevelopmentActionStatuses.Completed)
            {
                var stillDone = action.LearningAssignmentId is Guid la2 && assignments.TryGetValue(la2, out var a2)
                    ? a2.Status == LearningAssignmentStatuses.Completed
                    : action.EmployeeGoalId is Guid g2 && goals.TryGetValue(g2, out var goal2) && string.Equals(goal2.Status, "Completed", StringComparison.OrdinalIgnoreCase);
                if (!stillDone)
                {
                    action.Status = DevelopmentActionStatuses.InProgress;
                    action.CompletedAtUtc = null;
                    action.CompletionMethod = null;
                    action.OverdueNoticeSentAtUtc = null;
                }
            }
            else if (action.LearningAssignmentId is Guid aid && assignments.TryGetValue(aid, out var assignment))
            {
                if (assignment.Status == LearningAssignmentStatuses.Completed)
                {
                    action.Status = DevelopmentActionStatuses.Completed;
                    action.CompletedAtUtc = assignment.CompletedAtUtc ?? now;
                    action.CompletionMethod = DevelopmentCompletionMethods.LinkedLearningCompleted;
                }
                else if (assignment.Status == LearningAssignmentStatuses.InProgress && action.Status == DevelopmentActionStatuses.NotStarted)
                    action.Status = DevelopmentActionStatuses.InProgress;
            }
            else if (action.EmployeeGoalId is Guid gid && goals.TryGetValue(gid, out var goal))
            {
                if (string.Equals(goal.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    action.Status = DevelopmentActionStatuses.Completed;
                    action.CompletedAtUtc = goal.UpdatedAtUtc ?? now;
                    action.CompletionMethod = DevelopmentCompletionMethods.LinkedGoalCompleted;
                }
                else if (string.Equals(goal.Status, "Active", StringComparison.OrdinalIgnoreCase) && goal.ActualValue > goal.BaselineValue
                         && action.Status == DevelopmentActionStatuses.NotStarted)
                    action.Status = DevelopmentActionStatuses.InProgress;
            }
            if (action.Status != from)
            {
                action.UpdatedAtUtc = now;
                action.Version++;
                changed.Add((action, from));
            }
        }
        if (changed.Count == 0) return 0;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else synced (or edited) the same action at the same moment; the next read converges.
            db.ChangeTracker.Clear();
            return 0;
        }
        var system = new RequestContext(null, "succession-linked-sync", null, tenantId);
        foreach (var (action, from) in changed)
            await audit.WriteAsync("succession.devplan.action_synced", nameof(DevelopmentPlanAction), action.Id.ToString(), system,
                JsonSerializer.Serialize(new { planId = action.PlanId, actionId = action.Id }), ct);
        return changed.Count;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A Learning action's link: an existing assignment of the employee, or a new one created through
    /// <see cref="LearningAssignmentService"/> (eligibility, duplicate guard and audit included). When the employee
    /// already has the path assigned and not finished, that live assignment is linked instead of failing.
    /// </summary>
    private async Task<Guid> LinkLearningAsync(SuccessionActor actor, int employeeId, ActionInput input, CancellationToken ct)
    {
        if (!(await modules.GetStateAsync(actor.TenantId, ct)).IsEnabled(FeatureKeys.Learning))
            throw SuccessionException.Conflict("learning_not_enabled", "Learning is not switched on for this organisation, so a learning action cannot be linked.");
        if (input.LearningAssignmentId is Guid existing)
        {
            var found = await SuccessionDb.TenantRows(db.LearningAssignments, actor.TenantId).AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == existing && a.EmployeeId == employeeId && a.Status != LearningAssignmentStatuses.Cancelled, ct)
                ?? throw SuccessionException.NotFound("Learning assignment");
            // F8: a finished assignment proves nothing new; link a path instead (which opens a fresh cycle).
            if (found.Status == LearningAssignmentStatuses.Completed)
                throw SuccessionException.Conflict("assignment_completed",
                    "That learning is already completed. Choose the learning path to assign a fresh cycle as the development action.");
            return existing;
        }
        if (input.LearningPathId is not Guid pathId) throw SuccessionException.Invalid("Choose the learning path this action assigns.");
        var learning = services.GetRequiredService<LearningAssignmentService>();
        var learningActor = new LearningActor(actor.TenantId, actor.UserId, actor.IpAddress, actor.UserAgent, actor.Scope);
        try
        {
            var result = await learning.AssignAsync(learningActor, new AssignInput(pathId, null, [employeeId], null, input.DueOn, null, IsMandatory: false), ct);
            var outcome = result.Results.Single();
            if (outcome.AssignmentId is Guid created) return created;
            throw SuccessionException.Conflict("learning_refused", "The learning path could not be assigned to this employee.", outcome.Reasons);
        }
        catch (LearningException ex) when (ex.Code == "duplicate_assignment")
        {
            return await SuccessionDb.TenantRows(db.LearningAssignments, actor.TenantId).AsNoTracking()
                .Where(a => a.EmployeeId == employeeId && a.PathId == pathId && LearningAssignmentStatuses.Active.Contains(a.Status))
                .Select(a => a.Id).FirstAsync(ct);
        }
        catch (LearningException ex)
        {
            throw new SuccessionException(ex.Status, ex.Code, ex.Message, ex.Details);
        }
    }

    private async Task<DevelopmentPlanDto> TransitionAsync(SuccessionActor actor, Guid id, string verb, object? metadata, Action<DevelopmentPlan> apply,
        bool requireActions, CancellationToken ct)
    {
        var plan = await LoadAsync(actor, id, tracked: true, ct);
        await EnsureNotOwnPlanAsync(actor, plan.EmployeeId, ct);
        if (requireActions && !await db.DevelopmentPlanActions.AnyAsync(a => a.PlanId == id && a.Status != DevelopmentActionStatuses.Cancelled, ct))
            throw SuccessionException.Conflict("plan_empty", "Add at least one action before activating the plan.");
        apply(plan);
        plan.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        plan.Version++;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (LearningDb.IsUniqueViolation(ex))
        {
            throw SuccessionException.Conflict("plan_exists", "This employee already has an active development plan. Complete or cancel it first.");
        }
        await audit.WriteAsync($"succession.devplan.{verb}", nameof(DevelopmentPlan), id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { planId = id }), ct);
        return await GetAsync(actor, id, ct);
    }

    private async Task<DevelopmentPlan> LoadAsync(SuccessionActor actor, Guid id, bool tracked, CancellationToken ct)
    {
        var query = db.DevelopmentPlans.Where(p => p.TenantId == actor.TenantId && p.Id == id);
        var plan = await (tracked ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct);
        if (plan is null || !actor.Scope.CanAccessEmployee(plan.EmployeeId)) throw SuccessionException.NotFound("Development plan");
        return plan;
    }

    private async Task<SuccessionCandidateFacts> InScopeEmployeeAsync(SuccessionActor actor, int employeeId, CancellationToken ct)
    {
        var facts = (await SuccessionFacts.LoadAsync(db, actor.TenantId, [employeeId], ct)).GetValueOrDefault(employeeId);
        if (facts is null || !actor.Scope.CanAccessEmployee(employeeId) || facts.CompanyId is null) throw SuccessionException.NotFound("Employee");
        if (!facts.IsActive) throw SuccessionException.Conflict("employee_not_active", "A development plan is for an active employee.");
        return facts;
    }

    private async Task ActiveEmployeeAsync(Guid tenantId, int employeeId, string what, CancellationToken ct)
    {
        var facts = (await SuccessionFacts.LoadAsync(db, tenantId, [employeeId], ct)).GetValueOrDefault(employeeId);
        if (facts is null || !facts.IsActive) throw SuccessionException.Invalid($"Choose an active employee as the {what}.");
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    internal static PlanProgress Progress(DevelopmentPlan plan, IReadOnlyCollection<DevelopmentPlanAction> actions, DateOnly today)
    {
        var total = actions.Count;
        var completed = actions.Count(a => a.Status == DevelopmentActionStatuses.Completed);
        var cancelled = actions.Count(a => a.Status == DevelopmentActionStatuses.Cancelled);
        var overdue = actions.Count(a => DevelopmentActionStatuses.Open.Contains(a.Status) && a.DueOn is { } d && d < today);
        var denominator = total - cancelled;
        var reviewOverdue = DevelopmentPlanStatuses.Current.Contains(plan.Status) && plan.NextReviewOn < today;
        return new PlanProgress(total, completed, cancelled, overdue,
            denominator == 0 ? null : Math.Round(100m * completed / denominator, 1), overdue == 0 && !reviewOverdue, reviewOverdue);
    }

    internal async Task<List<DevelopmentPlanDto>> DtosAsync(Guid tenantId, IReadOnlyList<DevelopmentPlan> plans, bool includeNomination, CancellationToken ct)
    {
        if (plans.Count == 0) return [];
        var ids = plans.Select(p => p.Id).ToList();
        var actions = await SuccessionDb.TenantRows(db.DevelopmentPlanActions, tenantId).AsNoTracking().Where(a => ids.Contains(a.PlanId))
            .OrderBy(a => a.SortOrder).ToListAsync(ct);
        var assignmentIds = actions.Where(a => a.LearningAssignmentId != null).Select(a => a.LearningAssignmentId!.Value).ToList();
        var assignments = await SuccessionDb.TenantRows(db.LearningAssignments, tenantId).AsNoTracking().Where(a => assignmentIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Status, a.PathId }).ToDictionaryAsync(a => a.Id, ct);
        var pathIds = assignments.Values.Select(a => a.PathId).Distinct().ToList();
        var paths = await SuccessionDb.TenantRows(db.LearningPaths, tenantId).AsNoTracking().Where(p => pathIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.NameAr }).ToDictionaryAsync(p => p.Id, ct);
        var goalIds = actions.Where(a => a.EmployeeGoalId != null).Select(a => a.EmployeeGoalId!.Value).ToList();
        var goals = await db.EmployeeGoals.AsNoTracking().Where(g => g.TenantId == tenantId && goalIds.Contains(g.Id))
            .Select(g => new { g.Id, g.Title, g.Status }).ToDictionaryAsync(g => g.Id, ct);
        var people = await SuccessionFacts.LoadAsync(db, tenantId,
            plans.Select(p => p.EmployeeId).Concat(plans.Where(p => p.OwnerEmployeeId != null).Select(p => p.OwnerEmployeeId!.Value))
                .Concat(actions.Where(a => a.OwnerEmployeeId != null).Select(a => a.OwnerEmployeeId!.Value))
                .Concat(actions.Where(a => a.MentorEmployeeId != null).Select(a => a.MentorEmployeeId!.Value)), ct);
        var today = Today();
        return plans.Select(p =>
        {
            var mine = actions.Where(a => a.PlanId == p.Id).ToList();
            var actionDtos = mine.Select(a =>
            {
                var asg = a.LearningAssignmentId is Guid la ? assignments.GetValueOrDefault(la) : null;
                var path = asg is null ? null : paths.GetValueOrDefault(asg.PathId);
                var goal = a.EmployeeGoalId is Guid g ? goals.GetValueOrDefault(g) : null;
                return new PlanActionDto(a.Id, a.ActionType, a.Title, a.Description, a.Status, a.DueOn,
                    DevelopmentActionStatuses.Open.Contains(a.Status) && a.DueOn is { } d && d < today,
                    PersonRef.From(a.OwnerEmployeeId is int o ? people.GetValueOrDefault(o) : null),
                    PersonRef.From(a.MentorEmployeeId is int m ? people.GetValueOrDefault(m) : null),
                    a.LearningAssignmentId, path?.Name, path?.NameAr, asg?.Status, a.EmployeeGoalId, goal?.Title, goal?.Status, a.ProgressNote,
                    a.CompletedAtUtc, a.CompletionMethod, a.SortOrder, a.Version, a.CompletionProposedAtUtc);
            }).ToList();
            return new DevelopmentPlanDto(p.Id,
                PersonRef.From(people.GetValueOrDefault(p.EmployeeId)) ?? new PersonRef(p.EmployeeId, string.Empty, string.Empty, string.Empty, string.Empty, true),
                PersonRef.From(p.OwnerEmployeeId is int o ? people.GetValueOrDefault(o) : null), p.Purpose, p.Status, p.NextReviewOn, p.LastReviewedAtUtc,
                p.LastReviewNote, p.ActivatedAtUtc, p.CompletedAtUtc, p.CancelledAtUtc, p.CancelReason, includeNomination ? p.NominationId : null,
                Progress(p, mine, today), actionDtos, p.CreatedAtUtc, p.Version);
        }).ToList();
    }
}

/// <summary>
/// The LRN → SUC completion hook: <c>LearningProgressService</c> calls every registered listener after an assignment's
/// completion has committed. A failure here never undoes the learner's completion (the read-time and sweep sync converge).
/// </summary>
public sealed class DevelopmentPlanLearningListener(DevelopmentPlanService plans) : ILearningAssignmentCompletedListener
{
    public Task OnAssignmentCompletedAsync(Guid tenantId, Guid assignmentId, CancellationToken ct) =>
        plans.SyncLinkedAsync(tenantId, null, ct, assignmentId);
}
