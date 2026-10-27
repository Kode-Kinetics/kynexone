using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Onboarding;

public sealed record GenerationResult(int Created, int AlreadyThere, string NationalityClass);

/// <summary>
/// Raises a hire's onboarding tasks from a checklist, exactly once. The unique index on (tenant, employee, template task)
/// is the guarantee: two callers that race both read "none yet", both insert, and the loser's whole batch is refused and
/// dropped, so the hire ends with exactly one set. Tasks are COPIES (title, owner, blocking flag), so editing the checklist
/// afterwards never changes a task already raised.
/// </summary>
public sealed class ChecklistGenerator
{
    private readonly ZayraDbContext _db;
    public ChecklistGenerator(ZayraDbContext db) => _db = db;

    /// <param name="startDateKnown">False when the joining date was never entered; tasks then carry no due date until it is set.</param>
    public async Task<GenerationResult> EnsureAsync(Guid tenantId, Employee employee, Guid checklistId, bool startDateKnown, CancellationToken ct)
    {
        var cls = NationalityClasses.Of(employee.Nationality);
        var templates = await _db.OnboardingChecklistTemplateTasks.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.ChecklistId == checklistId && t.IsActive).ToListAsync(ct);
        var have = await _db.OnboardingTasks.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.EmployeeId == employee.PublicId && t.TemplateTaskId != null)
            .Select(t => t.TemplateTaskId!.Value).ToListAsync(ct);
        DateOnly? start = startDateKnown && employee.JoiningDate != default ? DateOnly.FromDateTime(employee.JoiningDate) : null;
        var toAdd = ChecklistPlanner.Plan(templates, cls, start).Where(p => !have.Contains(p.Template.Id)).ToList();
        if (toAdd.Count == 0) return new(0, have.Count, cls);

        var rows = toAdd.Select(p => ChecklistPlanner.Instantiate(p, tenantId, checklistId, employee.PublicId, null, "template")).ToList();
        _db.OnboardingTasks.AddRange(rows);
        try
        {
            await _db.SaveChangesAsync(ct);
            return new(rows.Count, have.Count, cls);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent caller raised them first; ours is dropped whole (one SaveChanges is one transaction).
            foreach (var r in rows) _db.Entry(r).State = EntityState.Detached;
            return new(0, have.Count + rows.Count, cls);
        }
    }

    /// <summary>The joining date was set or moved (CD54): every open task that still follows the joining date follows it.</summary>
    public async Task<int> RebaseAsync(Guid tenantId, Guid employeePublicId, DateOnly newStart, CancellationToken ct)
    {
        var open = await _db.OnboardingTasks
            .Where(t => t.TenantId == tenantId && t.EmployeeId == employeePublicId && t.DueOffsetDays != null).ToListAsync(ct);
        var changed = 0;
        foreach (var task in open)
        {
            if (ChecklistPlanner.Rebased(task, newStart) is not DateOnly due) continue;
            task.DueDate = due;
            task.UpdatedAtUtc = DateTime.UtcNow;
            changed++;
        }
        if (changed > 0) await _db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>The checklist a hire gets: the template's own, else the company's default. Null = nothing to raise.</summary>
    public async Task<Guid?> ResolveChecklistAsync(Guid tenantId, Guid companyId, Guid? templateChecklistId, CancellationToken ct)
    {
        if (templateChecklistId is Guid explicitId
            && await _db.OnboardingChecklists.AsNoTracking().AnyAsync(c => c.TenantId == tenantId && c.Id == explicitId && c.IsActive && !c.IsDeleted
                    && (c.CompanyId == null || c.CompanyId == companyId), ct))
            return explicitId;
        return await _db.OnboardingChecklists.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.CompanyId == companyId && c.IsDefault && c.IsActive && !c.IsDeleted)
            .Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
    }

    internal static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is Npgsql.PostgresException pg && pg.SqlState == "23505") return true;
            if (e.GetType().Name == "SqliteException" && e.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
