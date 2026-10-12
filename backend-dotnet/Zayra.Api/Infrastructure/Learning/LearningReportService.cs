using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Learning;

/// <summary>One row of the assignment status matrix: one person, one path, one cycle.</summary>
public sealed record StatusRow(Guid AssignmentId, int EmployeeId, string EmployeeCode, string EmployeeName, Guid? DepartmentId, string Department,
    Guid PathId, string Path, int Cycle, string Status, bool Mandatory, DateOnly? DueDate, bool Overdue, int RequiredItems, int CompletedItems,
    decimal ProgressPercent, DateTime AssignedAtUtc, DateTime? CompletedAtUtc, string EmployeeStatus, bool AwaitingVerification);

/// <summary>Completion for one group (a path or a department). Assigned = Completed + InProgress + NotStarted.</summary>
public sealed record CompletionRow(string GroupBy, Guid? GroupId, string Group, int Assigned, int Completed, int InProgress, int NotStarted,
    int Overdue, decimal CompletionRatePercent);

public sealed record ExpiringRow(Guid CredentialId, int EmployeeId, string EmployeeCode, string EmployeeName, string CourseCode, string Course,
    string CertificateNumber, DateOnly ExpiresOn, int DaysLeft, Guid? RenewalAssignmentId);

/// <summary>A KPI with its definition and the report that lists the rows behind it.</summary>
public sealed record LearningKpi(string Key, int Value, string Definition, string DrillReport, string? DrillFilter);

public sealed record LearningSummary(IReadOnlyList<LearningKpi> Kpis, decimal CompletionRatePercent, string Scope, DateOnly AsOf);

/// <summary>
/// LRN-11 reporting. One base population for every number: the assignments (not cancelled) of the employees the caller
/// may see — <see cref="Application.Common.DataScope"/> resolves HR to their companies and a manager to their own
/// reporting tree, and the ambient company filter applies on top. Every KPI is a count of rows one of the list reports
/// returns with the same filter, so a number always reconciles to the rows behind it. Departments are the department
/// the person was in when assigned (the snapshot on the assignment), so a transfer does not rewrite last quarter.
/// </summary>
public sealed class LearningReportService(ZayraDbContext db, ITenantClock tenantClock)
{
    public async Task<IReadOnlyList<StatusRow>> StatusMatrixAsync(LearningActor actor, Guid? pathId, Guid? departmentId, string? status,
        bool? overdueOnly, CancellationToken ct)
    {
        var today = await tenantClock.TodayAsync(actor.TenantId, ct);
        var query = Population(actor);
        if (pathId is Guid p) query = query.Where(a => a.PathId == p);
        if (departmentId is Guid d) query = query.Where(a => a.DepartmentId == d);
        if (string.Equals(status, "open", StringComparison.OrdinalIgnoreCase)) query = query.Where(a => LearningAssignmentStatuses.Active.Contains(a.Status));
        else if (!string.IsNullOrWhiteSpace(status)) query = query.Where(a => a.Status == status);
        // One definition of overdue (LearningOverdue): chased people only, and not when only HR verification is left.
        if (overdueOnly == true) query = Overdue(query, actor.TenantId, today);
        var rows = await query.OrderBy(a => a.DueDate).ThenBy(a => a.EmployeeId).Take(5000).ToListAsync(ct);
        return await ToRowsAsync(actor.TenantId, rows, today, ct);
    }

    public async Task<IReadOnlyList<CompletionRow>> CompletionAsync(LearningActor actor, string? groupBy, CancellationToken ct)
    {
        var today = await tenantClock.TodayAsync(actor.TenantId, ct);
        var byDepartment = string.Equals(groupBy, "department", StringComparison.OrdinalIgnoreCase);
        var rows = await Population(actor).Select(a => new { a.Id, a.PathId, a.DepartmentId, a.Status, a.DueDate, a.EmployeeId }).ToListAsync(ct);
        var overdueIds = (await Overdue(Population(actor), actor.TenantId, today).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        Dictionary<Guid, string> names;
        if (byDepartment)
        {
            var ids = rows.Select(r => r.DepartmentId).OfType<Guid>().Distinct().ToList();
            names = await db.Departments.AsNoTracking().Where(d => d.TenantId == actor.TenantId && ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.NameEn, ct);
        }
        else
        {
            var ids = rows.Select(r => r.PathId).Distinct().ToList();
            names = await db.LearningPaths.AsNoTracking().Where(p => p.TenantId == actor.TenantId && ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        }
        return rows.GroupBy(r => byDepartment ? r.DepartmentId : r.PathId)
            .Select(g =>
            {
                var assigned = g.Count();
                var completed = g.Count(r => r.Status == LearningAssignmentStatuses.Completed);
                var inProgress = g.Count(r => r.Status == LearningAssignmentStatuses.InProgress);
                var notStarted = g.Count(r => r.Status == LearningAssignmentStatuses.Assigned);
                var overdue = g.Count(r => overdueIds.Contains(r.Id));
                return new CompletionRow(byDepartment ? "department" : "path", g.Key,
                    g.Key is Guid id ? names.GetValueOrDefault(id, string.Empty) : string.Empty,
                    assigned, completed, inProgress, notStarted, overdue, Rate(completed, assigned));
            })
            .OrderBy(r => r.Group).ToList();
    }

    public async Task<IReadOnlyList<ExpiringRow>> ExpiringAsync(LearningActor actor, int days, CancellationToken ct)
    {
        var today = await tenantClock.TodayAsync(actor.TenantId, ct);
        var horizon = today.AddDays(Math.Clamp(days, 0, 365));
        var query = db.LearningCredentials.AsNoTracking().Where(c => c.TenantId == actor.TenantId && c.Status == LearningCredentialStatuses.Active
            && c.ExpiresOn != null && c.ExpiresOn <= horizon);
        if (!actor.Scope.IsUnrestricted)
        {
            var allowed = actor.Scope.AllowedEmployeeIds!.ToList();
            query = query.Where(c => allowed.Contains(c.EmployeeId));
        }
        var rows = await query.OrderBy(c => c.ExpiresOn).Take(5000).ToListAsync(ct);
        var employeeIds = rows.Select(r => r.EmployeeId).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking().Where(e => e.TenantId == actor.TenantId && employeeIds.Contains(e.Id))
            .Select(e => new { e.Id, e.EmployeeCode, e.FullName }).ToDictionaryAsync(e => e.Id, ct);
        var courseIds = rows.Select(r => r.CourseId).Distinct().ToList();
        var courses = await db.LearningCourses.AsNoTracking().Where(c => courseIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Title, ct);
        return rows.Select(r => new ExpiringRow(r.Id, r.EmployeeId, employees.GetValueOrDefault(r.EmployeeId)?.EmployeeCode ?? string.Empty,
            employees.GetValueOrDefault(r.EmployeeId)?.FullName ?? string.Empty, r.CourseCode, courses.GetValueOrDefault(r.CourseId, r.CourseCode),
            r.CertificateNumber, r.ExpiresOn!.Value, r.ExpiresOn.Value.DayNumber - today.DayNumber, r.RenewalAssignmentId)).ToList();
    }

    public async Task<LearningSummary> SummaryAsync(LearningActor actor, CancellationToken ct)
    {
        var today = await tenantClock.TodayAsync(actor.TenantId, ct);
        var population = Population(actor);
        var assigned = await population.CountAsync(ct);
        var completed = await population.CountAsync(a => a.Status == LearningAssignmentStatuses.Completed, ct);
        var live = await population.CountAsync(a => LearningAssignmentStatuses.Active.Contains(a.Status), ct);
        var overdue = await Overdue(population, actor.TenantId, today).CountAsync(ct);
        var expiring = (await ExpiringAsync(actor, 30, ct)).Count;
        var populationIds = population.Select(a => a.Id);
        // Assignments (not items), so the number equals the rows its drill-down lists.
        var locked = await db.LearningEnrollments.AsNoTracking()
            .Where(e => e.TenantId == actor.TenantId && e.Status == LearningEnrollmentStatuses.Locked && populationIds.Contains(e.AssignmentId))
            .Select(e => e.AssignmentId).Distinct().CountAsync(ct);
        // Assignments (not items), so the number equals the rows its drill-down lists.
        var pending = await db.LearningEnrollments.AsNoTracking()
            .Where(e => e.TenantId == actor.TenantId && e.Status == LearningEnrollmentStatuses.PendingVerification && populationIds.Contains(e.AssignmentId))
            .Select(e => e.AssignmentId).Distinct().CountAsync(ct);
        return new LearningSummary(
        [
            new("assigned", assigned, "Assignments of the people you can see, not cancelled, in any state.", "status", null),
            new("open", live, "Assigned or in progress — not yet completed.", "status", "open"),
            new("completed", completed, "Assignments whose every required course has completion evidence.", "status", LearningAssignmentStatuses.Completed),
            new("overdue", overdue, "Open assignments whose due date has passed and that still need the employee to act (not those only waiting for HR verification).", "overdue", null),
            new("expiring", expiring, "Current certificates that expire within 30 days.", "expiring", "30"),
            new("locked", locked, "Assignments with a course locked after its last failed assessment attempt, waiting for an HR reset.", "locked", null),
            new("pending", pending, "Assignments with a completion reported by course content, waiting for HR verification before a certificate is issued.", "pending", null),
        ], Rate(completed, assigned), actor.Scope.IsUnrestricted ? "organization" : actor.Scope.Level.ToString().ToLowerInvariant(), today);
    }

    /// <summary>Courses locked after their last failed attempt, as status rows of the assignments they belong to.</summary>
    public Task<IReadOnlyList<StatusRow>> LockedAsync(LearningActor actor, CancellationToken ct) =>
        WithEnrollmentStatusAsync(actor, LearningEnrollmentStatuses.Locked, ct);

    /// <summary>Content-reported completions waiting for HR verification, as status rows of their assignments.</summary>
    public Task<IReadOnlyList<StatusRow>> PendingVerificationAsync(LearningActor actor, CancellationToken ct) =>
        WithEnrollmentStatusAsync(actor, LearningEnrollmentStatuses.PendingVerification, ct);

    private async Task<IReadOnlyList<StatusRow>> WithEnrollmentStatusAsync(LearningActor actor, string status, CancellationToken ct)
    {
        var populationIds = Population(actor).Select(a => a.Id);
        var assignmentIds = await db.LearningEnrollments.AsNoTracking()
            .Where(e => e.TenantId == actor.TenantId && e.Status == status && populationIds.Contains(e.AssignmentId))
            .Select(e => e.AssignmentId).Distinct().ToListAsync(ct);
        var rows = await db.LearningAssignments.AsNoTracking().Where(a => assignmentIds.Contains(a.Id)).ToListAsync(ct);
        return await ToRowsAsync(actor.TenantId, rows, await tenantClock.TodayAsync(actor.TenantId, ct), ct);
    }

    private IQueryable<LearningAssignment> Overdue(IQueryable<LearningAssignment> query, Guid tenantId, DateOnly today)
    {
        var chased = LearningOverdue.ChasedEmployees(db, tenantId, today);
        var awaiting = LearningOverdue.AwaitingVerification(db, tenantId);
        return query.Where(a => a.IsMandatory && LearningAssignmentStatuses.Active.Contains(a.Status) && a.DueDate != null && a.DueDate < today
            && chased.Contains(a.EmployeeId) && !awaiting.Contains(a.Id));
    }

    private IQueryable<LearningAssignment> Population(LearningActor actor)
    {
        var query = db.LearningAssignments.AsNoTracking().Where(a => a.TenantId == actor.TenantId && a.Status != LearningAssignmentStatuses.Cancelled);
        if (!actor.Scope.IsUnrestricted)
        {
            var allowed = actor.Scope.AllowedEmployeeIds!.ToList();
            query = query.Where(a => allowed.Contains(a.EmployeeId));
        }
        return query;
    }

    private async Task<IReadOnlyList<StatusRow>> ToRowsAsync(Guid tenantId, IReadOnlyList<LearningAssignment> rows, DateOnly today, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(r => r.Id).ToList();
        var counts = await db.LearningEnrollments.AsNoTracking().Where(e => ids.Contains(e.AssignmentId) && e.IsRequired)
            .GroupBy(e => e.AssignmentId)
            .Select(g => new
            {
                g.Key, Required = g.Count(), Done = g.Count(e => e.Status == LearningEnrollmentStatuses.Completed),
                Pending = g.Count(e => e.Status == LearningEnrollmentStatuses.PendingVerification),
            })
            .ToDictionaryAsync(x => x.Key, ct);
        var employeeIds = rows.Select(r => r.EmployeeId).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && employeeIds.Contains(e.Id))
            .Select(e => new { e.Id, e.EmployeeCode, e.FullName, e.Status, e.JoiningDate }).ToDictionaryAsync(e => e.Id, ct);
        var departmentIds = rows.Select(r => r.DepartmentId).OfType<Guid>().Distinct().ToList();
        var departments = await db.Departments.AsNoTracking().Where(d => d.TenantId == tenantId && departmentIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.NameEn, ct);
        var pathIds = rows.Select(r => r.PathId).Distinct().ToList();
        var paths = await db.LearningPaths.AsNoTracking().Where(p => pathIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return rows.Select(a =>
        {
            var c = counts.GetValueOrDefault(a.Id);
            var required = c?.Required ?? 0;
            var done = c?.Done ?? 0;
            var employee = employees.GetValueOrDefault(a.EmployeeId);
            var awaiting = c is not null && c.Pending > 0 && c.Done + c.Pending == c.Required;
            var chased = employee is not null && LearningOverdue.IsChased(employee.Status, employee.JoiningDate, today);
            return new StatusRow(a.Id, a.EmployeeId, employee?.EmployeeCode ?? string.Empty, employee?.FullName ?? string.Empty, a.DepartmentId,
                a.DepartmentId is Guid d ? departments.GetValueOrDefault(d, string.Empty) : string.Empty, a.PathId, paths.GetValueOrDefault(a.PathId, string.Empty),
                a.Cycle, a.Status, a.IsMandatory, a.DueDate,
                LearningOverdue.IsOverdue(a.Status, a.IsMandatory, a.DueDate, today, chased, awaiting),
                required, done, Rate(done, required), a.AssignedAtUtc, a.CompletedAtUtc, employee?.Status ?? string.Empty, awaiting);
        }).ToList();
    }

    private static decimal Rate(int part, int whole) => whole == 0 ? 0m : Math.Round(part * 100m / whole, 2, MidpointRounding.AwayFromZero);

}
