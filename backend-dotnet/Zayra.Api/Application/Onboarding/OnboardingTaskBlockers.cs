using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Employees;

namespace Zayra.Api.Application.Onboarding;

public sealed record OpenBlockingTask(Guid Id, string Title, string? TitleAr, string OwnerRole);

/// <summary>
/// The open tasks that hold a hire back from being activated. Only a task whose template opted in (BlocksActivation) ever
/// appears here; the default is that tasks never block (CD4). Open = Pending, InProgress or Blocked.
/// </summary>
public static class OnboardingTaskBlockers
{
    public const string KeyPrefix = "onboarding:task:";
    public static readonly string[] OpenStatuses = { "Pending", "InProgress", "Blocked" };

    public static async Task<IReadOnlyList<OpenBlockingTask>> OpenForAsync(ZayraDbContext db, Guid tenantId, Guid employeePublicId, CancellationToken ct) =>
        await db.OnboardingTasks.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.EmployeeId == employeePublicId && t.BlocksActivation && OpenStatuses.Contains(t.Status))
            .OrderBy(t => t.OrderIndex).ThenBy(t => t.TaskTitle)
            .Select(t => new OpenBlockingTask(t.Id, t.TaskTitle, t.TitleAr, t.OwnerRole)).ToListAsync(ct);

    /// <summary>The blocker rows a readiness result lists for the open blocking tasks (stable key onboarding:task:{id}, EN label; the
    /// Arabic title travels in <see cref="OpenBlockingTask.TitleAr"/> and the UI maps the key prefix to its bilingual label).</summary>
    public static IReadOnlyList<ReadinessItem> ToItems(IEnumerable<OpenBlockingTask> tasks) =>
        tasks.Select(t => new ReadinessItem(
            Key: KeyPrefix + t.Id, Label: "Onboarding task open: " + t.Title, Category: "onboarding", Reason: t.OwnerRole,
            Jurisdiction: null, Gate: "activate", FixKind: "task", FixTarget: t.Id.ToString(), DocumentType: null)).ToList();

    /// <summary>Throws the structured activation refusal when the hire has an open task that opted in to blocking; otherwise returns the readiness unchanged.</summary>
    public static async Task<EmployeeReadiness> EnsureNoneOpenAsync(
        ZayraDbContext db, Guid tenantId, int employeeId, EmployeeReadiness readiness, ResolvedReadinessPolicy policy, CancellationToken ct)
    {
        var publicId = await db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && e.Id == employeeId)
            .Select(e => (Guid?)e.PublicId).FirstOrDefaultAsync(ct);
        if (publicId is not Guid pid) return readiness;
        var open = await OpenForAsync(db, tenantId, pid, ct);
        if (open.Count == 0) return readiness;
        throw new EmployeeActivationBlockedException(employeeId,
            readiness with { State = "Blocked", Blocking = readiness.Blocking.Concat(ToItems(open)).ToList() }, policy);
    }

    /// <summary>Adds the open blocking tasks to an activation refusal that other blockers already raised, so HR sees everything at once.</summary>
    public static async Task<EmployeeActivationBlockedException> MergeAsync(ZayraDbContext db, Guid tenantId, EmployeeActivationBlockedException blocked, CancellationToken ct)
    {
        var publicId = await db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && e.Id == blocked.EmployeeId).Select(e => (Guid?)e.PublicId).FirstOrDefaultAsync(ct);
        if (publicId is not Guid pid) return blocked;
        var open = await OpenForAsync(db, tenantId, pid, ct);
        return open.Count == 0 ? blocked
            : new EmployeeActivationBlockedException(blocked.EmployeeId, blocked.Readiness with { Blocking = blocked.Readiness.Blocking.Concat(ToItems(open)).ToList() }, blocked.Policy);
    }
}
