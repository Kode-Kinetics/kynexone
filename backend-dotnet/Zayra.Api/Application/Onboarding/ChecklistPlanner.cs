using Zayra.Api.Models;

namespace Zayra.Api.Application.Onboarding;

/// <summary>One task a hire will get, before it is written.</summary>
public sealed record PlannedTask(OnboardingChecklistTemplateTask Template, DateOnly? DueDate);

/// <summary>
/// The pure part of checklist generation: which template tasks apply to a hire and when each is due.
/// Due = joining date + offset (negative offsets allowed). With no joining date a task is still created, with no due date,
/// and is re-based when the date is set (CD54).
/// </summary>
public static class ChecklistPlanner
{
    public static IReadOnlyList<PlannedTask> Plan(IEnumerable<OnboardingChecklistTemplateTask> templates, string nationalityClass, DateOnly? startDate) =>
        templates
            .Where(t => t.IsActive && NationalityClasses.Applies(t.ConditionNationalityClass, nationalityClass))
            .OrderBy(t => t.OrderIndex).ThenBy(t => t.TaskTitle, StringComparer.Ordinal)
            .Select(t => new PlannedTask(t, startDate?.AddDays(t.DueOffsetDays)))
            .ToList();

    /// <summary>A task instance that is a copy of its template task: editing the template later never changes it.</summary>
    public static OnboardingTask Instantiate(PlannedTask planned, Guid tenantId, Guid checklistId, Guid employeePublicId, Guid? applicationId, string source)
    {
        var t = planned.Template;
        return new OnboardingTask
        {
            TenantId = tenantId,
            ChecklistId = checklistId,
            EmployeeId = employeePublicId,
            ApplicationId = applicationId,
            TemplateTaskId = t.Id,
            TaskTitle = t.TaskTitle,
            TitleAr = t.TitleAr,
            TaskDescription = t.TaskDescription,
            Category = t.Category,
            AssignedToName = t.AssignedToName,
            AssignedToUserId = t.AssignedToUserId,
            OwnerRole = t.OwnerRole,
            BlocksActivation = t.BlocksActivation,
            EvidenceRequired = t.EvidenceRequired,
            IsStatutory = t.IsStatutory,
            LinkSection = t.LinkSection,
            DueOffsetDays = t.DueOffsetDays,
            DueDate = planned.DueDate,
            OrderIndex = t.OrderIndex,
            IsMandatory = t.IsMandatory,
            Source = source,
        };
    }

    /// <summary>The new due date for a task whose joining date moved, or null when the task keeps its date (HR set it by hand,
    /// it is finished, or it has no offset to re-base from).</summary>
    public static DateOnly? Rebased(OnboardingTask task, DateOnly newStart)
    {
        if (task.DueOffsetDays is not int offset) return null;
        if (!string.IsNullOrWhiteSpace(task.DueOverrideReason)) return null;
        if (task.Status is "Completed" or "Skipped") return null;
        var due = newStart.AddDays(offset);
        return task.DueDate == due ? null : due;
    }
}
