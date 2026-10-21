using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Recruitment;

public enum DefaultChecklistOutcome
{
    /// <summary>The draft did not come from an accepted offer, so there is no hire journey to attach to.</summary>
    NotAnOfferHire,
    /// <summary>The tenant has no active checklist that applies to everyone and has active template tasks.</summary>
    NoDefaultChecklist,
    /// <summary>More than one checklist applies to everyone; choosing one would be a guess, so HR picks it.</summary>
    AmbiguousDefaultChecklist,
    /// <summary>Tasks from the default checklist already exist for this hire (a retry, or HR raised them by hand).</summary>
    AlreadyAttached,
    Attached,
}

public sealed record DefaultChecklistResult(DefaultChecklistOutcome Outcome, Guid? ChecklistId, int TaskCount);

/// <summary>
/// When an accepted offer's onboarding draft is activated, the hire gets the tenant's default onboarding checklist:
/// the single active checklist whose ApplicableTo is "All" and that has active template tasks. None, or more than one,
/// attaches nothing (the caller logs it) and HR raises tasks by hand as before. Runs inside the activation transaction,
/// after the pre-joining tasks were linked, and adds tracked rows only — the caller saves.
/// </summary>
public static class OnboardingDefaultChecklist
{
    public const string ApplicableToAll = "All";

    public static async Task<DefaultChecklistResult> AttachForActivatedDraftAsync(
        this ZayraDbContext db, Guid tenantId, Guid draftId, Employee employee, Guid? actorUserId, CancellationToken ct)
    {
        var applicationId = await db.JobApplications
            .Where(x => x.TenantId == tenantId && x.OnboardingDraftId == draftId)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(ct);
        if (applicationId is null) return new(DefaultChecklistOutcome.NotAnOfferHire, null, 0);

        var defaults = await db.OnboardingChecklists
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted && c.ApplicableTo == ApplicableToAll
                        && db.OnboardingChecklistTemplateTasks.Any(t => t.TenantId == tenantId && t.ChecklistId == c.Id && t.IsActive))
            .Select(c => c.Id)
            .Take(2)
            .ToListAsync(ct);
        if (defaults.Count == 0) return new(DefaultChecklistOutcome.NoDefaultChecklist, null, 0);
        if (defaults.Count > 1) return new(DefaultChecklistOutcome.AmbiguousDefaultChecklist, null, 0);
        var checklistId = defaults[0];

        // Exactly once per hire: tasks already raised from this checklist (by a previous run, or by HR before joining)
        // are kept and nothing is added. The pre-joining tasks were linked to the employee just before this runs.
        if (await db.OnboardingTasks.AnyAsync(x => x.TenantId == tenantId && x.ChecklistId == checklistId
                && (x.ApplicationId == applicationId || x.EmployeeId == employee.PublicId), ct))
            return new(DefaultChecklistOutcome.AlreadyAttached, checklistId, 0);

        var templates = await db.OnboardingChecklistTemplateTasks
            .Where(t => t.TenantId == tenantId && t.ChecklistId == checklistId && t.IsActive)
            .OrderBy(t => t.OrderIndex).ThenBy(t => t.TaskTitle)
            .ToListAsync(ct);
        var startDate = DateOnly.FromDateTime(employee.JoiningDate);
        foreach (var t in templates)
        {
            db.OnboardingTasks.Add(new OnboardingTask
            {
                TenantId = tenantId,
                ChecklistId = checklistId,
                EmployeeId = employee.PublicId,
                ApplicationId = applicationId,
                TaskTitle = t.TaskTitle,
                TaskDescription = t.TaskDescription,
                Category = t.Category,
                AssignedToName = t.AssignedToName,
                AssignedToUserId = t.AssignedToUserId,
                DueDate = startDate.AddDays(t.DueOffsetDays),
                OrderIndex = t.OrderIndex,
                IsMandatory = t.IsMandatory,
            });
        }
        db.RecruitmentAuditLogs.Add(new RecruitmentAuditLog
        {
            TenantId = tenantId,
            EntityType = "Onboarding",
            EntityId = applicationId.Value.ToString(),
            Action = "DefaultChecklistAttached",
            PerformedByUserId = actorUserId,
            PerformedByName = "Hire activation",
            NewValuesJson = JsonSerializer.Serialize(new { checklistId, draftId, employeePublicId = employee.PublicId, taskCount = templates.Count }),
        });
        return new(DefaultChecklistOutcome.Attached, checklistId, templates.Count);
    }
}
