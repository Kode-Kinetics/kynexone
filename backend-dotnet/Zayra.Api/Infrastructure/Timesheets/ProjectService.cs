using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Timesheets;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Timesheets;

/// <summary>
/// W2-G — projects, tasks and employee assignments. Tenant isolation comes from the global
/// query filters on the entities (ITenantOwned + ICompanyScoped); the explicit <c>TenantId</c>
/// predicates are belt and braces so a service constructed without an HTTP context (jobs, tests)
/// cannot cross tenants either.
/// </summary>
public sealed class ProjectService : IProjectService
{
    private readonly ZayraDbContext _db;
    public ProjectService(ZayraDbContext db) => _db = db;

    public async Task<IReadOnlyList<ProjectDto>> ListAsync(Guid tenantId, ProjectQuery query, CancellationToken ct)
    {
        var q = _db.Projects.AsNoTracking().Where(p => p.TenantId == tenantId && !p.IsDeleted);
        if (!string.IsNullOrWhiteSpace(query.Status)) q = q.Where(p => p.Status == query.Status.Trim());
        else if (!query.IncludeClosed) q = q.Where(p => p.Status == ProjectStatuses.Active);
        if (query.CompanyId.HasValue) q = q.Where(p => p.CompanyId == null || p.CompanyId == query.CompanyId.Value);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim().ToLower();
            q = q.Where(p => p.Code.ToLower().Contains(s) || p.Name.ToLower().Contains(s) || p.ClientName.ToLower().Contains(s));
        }
        var projects = await q.Include(p => p.Tasks).Include(p => p.Assignments)
            .OrderBy(p => p.Status).ThenBy(p => p.Code).ToListAsync(ct);
        return await ToDtosAsync(tenantId, projects, ct);
    }

    public async Task<ProjectDto?> GetAsync(Guid tenantId, Guid projectId, CancellationToken ct)
    {
        var project = await LoadAsync(tenantId, projectId, tracked: false, ct);
        return project is null ? null : (await ToDtosAsync(tenantId, new[] { project }, ct))[0];
    }

    public async Task<ProjectDto> CreateAsync(Guid tenantId, SaveProjectRequest request, Guid? userId, CancellationToken ct)
    {
        var project = new Project { TenantId = tenantId, CreatedBy = userId };
        await ApplyAsync(tenantId, project, request, ct);
        _db.Projects.Add(project);
        await SaveOrConflictAsync("A project with that code already exists.", ct);
        return (await GetAsync(tenantId, project.Id, ct))!;
    }

    public async Task<ProjectDto> UpdateAsync(Guid tenantId, Guid projectId, SaveProjectRequest request, Guid? userId, CancellationToken ct)
    {
        var project = await LoadAsync(tenantId, projectId, tracked: true, ct) ?? throw new TimesheetNotFoundException("Project not found.");
        await ApplyAsync(tenantId, project, request, ct);
        project.UpdatedAtUtc = DateTime.UtcNow;
        project.UpdatedBy = userId;
        await SaveOrConflictAsync("A project with that code already exists.", ct);
        return (await GetAsync(tenantId, project.Id, ct))!;
    }

    public async Task DeleteAsync(Guid tenantId, Guid projectId, Guid? userId, CancellationToken ct)
    {
        var project = await LoadAsync(tenantId, projectId, tracked: true, ct) ?? throw new TimesheetNotFoundException("Project not found.");
        // Hours already logged must keep their project: never hard-delete, close and hide instead.
        var hasEntries = await _db.TimesheetEntries.AnyAsync(e => e.TenantId == tenantId && e.ProjectId == projectId, ct);
        if (hasEntries)
            throw new TimesheetConflictException("Hours have been logged to this project. Close it instead of deleting it.");
        project.IsDeleted = true;
        project.Status = ProjectStatuses.Closed;
        project.UpdatedAtUtc = DateTime.UtcNow;
        project.UpdatedBy = userId;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<ProjectDto> SaveTaskAsync(Guid tenantId, Guid projectId, Guid? taskId, SaveProjectTaskRequest request, CancellationToken ct)
    {
        var project = await LoadAsync(tenantId, projectId, tracked: true, ct) ?? throw new TimesheetNotFoundException("Project not found.");
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0) throw new TimesheetValidationException("task_name_required", "Give the task a name.");
        if (project.Tasks.Any(t => t.Id != taskId && t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new TimesheetValidationException("task_duplicate", $"This project already has a task named '{name}'.");
        var typeCode = (request.TaskTypeCode ?? string.Empty).Trim().ToUpperInvariant();
        if (typeCode.Length > 0 && !await IsTaskTypeAsync(tenantId, typeCode, ct))
            throw new TimesheetValidationException("unknown_task_type", $"'{typeCode}' is not an active task type.");

        ProjectTask task;
        if (taskId is { } id)
        {
            task = project.Tasks.FirstOrDefault(t => t.Id == id) ?? throw new TimesheetNotFoundException("Task not found on this project.");
            task.UpdatedAtUtc = DateTime.UtcNow;
        }
        else
        {
            task = new ProjectTask { TenantId = tenantId, CompanyId = project.CompanyId, ProjectId = project.Id };
            project.Tasks.Add(task);
        }
        task.Name = name;
        task.TaskTypeCode = typeCode;
        task.IsBillable = request.IsBillable;
        task.IsActive = request.IsActive;
        task.SortOrder = request.SortOrder;
        await _db.SaveChangesAsync(ct);
        return (await GetAsync(tenantId, project.Id, ct))!;
    }

    public async Task<ProjectDto> RemoveTaskAsync(Guid tenantId, Guid projectId, Guid taskId, CancellationToken ct)
    {
        var project = await LoadAsync(tenantId, projectId, tracked: true, ct) ?? throw new TimesheetNotFoundException("Project not found.");
        var task = project.Tasks.FirstOrDefault(t => t.Id == taskId) ?? throw new TimesheetNotFoundException("Task not found on this project.");
        var used = await _db.TimesheetEntries.AnyAsync(e => e.TenantId == tenantId && e.ProjectTaskId == taskId, ct);
        if (used)
        {
            // Keep history intact: deactivate instead.
            task.IsActive = false;
            task.UpdatedAtUtc = DateTime.UtcNow;
        }
        else
        {
            _db.ProjectTasks.Remove(task);
        }
        await _db.SaveChangesAsync(ct);
        return (await GetAsync(tenantId, project.Id, ct))!;
    }

    public async Task<ProjectDto> SaveAssignmentAsync(Guid tenantId, Guid projectId, SaveProjectAssignmentRequest request, Guid? userId, CancellationToken ct)
    {
        var project = await LoadAsync(tenantId, projectId, tracked: true, ct) ?? throw new TimesheetNotFoundException("Project not found.");
        var employee = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == request.EmployeeId && !e.IsDeleted)
            .Select(e => new { e.Id, e.CompanyId })
            .FirstOrDefaultAsync(ct)
            ?? throw new TimesheetValidationException("unknown_employee", "That employee was not found.");
        // A company-specific project only takes that company's employees; a shared project takes anyone.
        if (project.CompanyId is { } pc && employee.CompanyId != pc)
            throw new TimesheetValidationException("company_mismatch", "This project belongs to a different legal entity than the employee.");
        if (request.AllocationPercent is < 0 or > 100)
            throw new TimesheetValidationException("invalid_allocation", "Allocation must be between 0 and 100 percent.");
        if (request.StartDate is { } sd && request.EndDate is { } ed && ed < sd)
            throw new TimesheetValidationException("invalid_dates", "The assignment cannot end before it starts.");

        var assignment = project.Assignments.FirstOrDefault(a => a.EmployeeId == request.EmployeeId);
        if (assignment is null)
        {
            assignment = new ProjectAssignment { TenantId = tenantId, CompanyId = project.CompanyId, ProjectId = project.Id, EmployeeId = request.EmployeeId, CreatedBy = userId };
            project.Assignments.Add(assignment);
        }
        else
        {
            assignment.UpdatedAtUtc = DateTime.UtcNow;
        }
        assignment.AllocationPercent = request.AllocationPercent;
        assignment.StartDate = request.StartDate;
        assignment.EndDate = request.EndDate;
        assignment.IsActive = request.IsActive;
        await SaveOrConflictAsync("That employee was assigned at the same time. Reload and try again.", ct);
        return (await GetAsync(tenantId, project.Id, ct))!;
    }

    public async Task<ProjectDto> RemoveAssignmentAsync(Guid tenantId, Guid projectId, int employeeId, CancellationToken ct)
    {
        var project = await LoadAsync(tenantId, projectId, tracked: true, ct) ?? throw new TimesheetNotFoundException("Project not found.");
        var assignment = project.Assignments.FirstOrDefault(a => a.EmployeeId == employeeId) ?? throw new TimesheetNotFoundException("That employee is not assigned to this project.");
        var used = await _db.TimesheetEntries.AnyAsync(e => e.TenantId == tenantId && e.ProjectId == projectId && e.EmployeeId == employeeId, ct);
        if (used)
        {
            assignment.IsActive = false;
            assignment.UpdatedAtUtc = DateTime.UtcNow;
        }
        else
        {
            _db.ProjectAssignments.Remove(assignment);
        }
        await _db.SaveChangesAsync(ct);
        return (await GetAsync(tenantId, project.Id, ct))!;
    }

    public async Task<IReadOnlyList<AssignedProjectDto>> GetAssignedProjectsAsync(Guid tenantId, int employeeId, DateOnly? onDate, CancellationToken ct)
    {
        var date = onDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = await _db.ProjectAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.EmployeeId == employeeId && a.IsActive
                        && (a.StartDate == null || a.StartDate <= date) && (a.EndDate == null || a.EndDate >= date))
            .Join(_db.Projects.AsNoTracking().Where(p => p.TenantId == tenantId && !p.IsDeleted && p.Status == ProjectStatuses.Active),
                a => a.ProjectId, p => p.Id, (a, p) => new { a.AllocationPercent, p })
            .OrderBy(x => x.p.Code)
            .ToListAsync(ct);
        if (rows.Count == 0) return Array.Empty<AssignedProjectDto>();
        var ids = rows.Select(x => x.p.Id).ToList();
        var tasks = await _db.ProjectTasks.AsNoTracking()
            .Where(t => t.TenantId == tenantId && ids.Contains(t.ProjectId) && t.IsActive)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .ToListAsync(ct);
        return rows.Select(x => new AssignedProjectDto(x.p.Id, x.p.Code, x.p.Name, x.p.ClientName, x.p.IsBillable, x.AllocationPercent,
            tasks.Where(t => t.ProjectId == x.p.Id).Select(ToTaskDto).ToList())).ToList();
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private async Task ApplyAsync(Guid tenantId, Project project, SaveProjectRequest request, CancellationToken ct)
    {
        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();
        var name = (request.Name ?? string.Empty).Trim();
        var violations = new List<TimesheetViolation>();
        if (code.Length == 0) violations.Add(new(null, "code_required", "Give the project a code."));
        if (name.Length == 0) violations.Add(new(null, "name_required", "Give the project a name."));
        var status = string.IsNullOrWhiteSpace(request.Status) ? project.Status : request.Status.Trim();
        if (status is not (ProjectStatuses.Active or ProjectStatuses.Closed)) violations.Add(new(null, "invalid_status", "Status must be Active or Closed."));
        if (request.BudgetHours is < 0) violations.Add(new(null, "invalid_budget", "Budget hours cannot be negative."));
        if (request.StartDate is { } sd && request.EndDate is { } ed && ed < sd) violations.Add(new(null, "invalid_dates", "The project cannot end before it starts."));
        if (request.CompanyId is { } companyId && !await _db.Companies.AnyAsync(c => c.TenantId == tenantId && c.Id == companyId && !c.IsDeleted, ct))
            violations.Add(new(null, "unknown_company", "That legal entity was not found."));
        if (request.CostCenterId is { } ccId && !await _db.CostCenters.AnyAsync(c => c.TenantId == tenantId && c.Id == ccId && !c.IsDeleted, ct))
            violations.Add(new(null, "unknown_cost_centre", "That cost centre was not found."));
        if (violations.Count > 0) throw new TimesheetValidationException(violations);

        project.Code = code;
        project.Name = name;
        project.ClientName = (request.ClientName ?? string.Empty).Trim();
        project.CompanyId = request.CompanyId;
        project.CostCenterId = request.CostCenterId;
        project.Status = status;
        project.BudgetHours = request.BudgetHours;
        project.IsBillable = request.IsBillable;
        project.Description = (request.Description ?? string.Empty).Trim();
        project.StartDate = request.StartDate;
        project.EndDate = request.EndDate;
    }

    private Task<bool> IsTaskTypeAsync(Guid tenantId, string code, CancellationToken ct)
    {
        var typeIds = _db.MasterDataTypes.Where(t => t.TenantId == tenantId && !t.IsDeleted && t.Code == TimesheetConstants.TaskTypeMasterType).Select(t => t.Id);
        return _db.MasterDataValues.AnyAsync(v => v.TenantId == tenantId && !v.IsDeleted && v.IsActive && typeIds.Contains(v.TypeId) && v.Code == code, ct);
    }

    private async Task<Project?> LoadAsync(Guid tenantId, Guid projectId, bool tracked, CancellationToken ct)
    {
        var q = _db.Projects.Include(p => p.Tasks).Include(p => p.Assignments).Where(p => p.TenantId == tenantId && p.Id == projectId && !p.IsDeleted);
        if (!tracked) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync(ct);
    }

    private async Task<IReadOnlyList<ProjectDto>> ToDtosAsync(Guid tenantId, IReadOnlyList<Project> projects, CancellationToken ct)
    {
        if (projects.Count == 0) return Array.Empty<ProjectDto>();
        var ids = projects.Select(p => p.Id).ToList();
        var ccIds = projects.Where(p => p.CostCenterId.HasValue).Select(p => p.CostCenterId!.Value).Distinct().ToList();
        var costCentres = ccIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.CostCenters.AsNoTracking().Where(c => c.TenantId == tenantId && ccIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var employeeIds = projects.SelectMany(p => p.Assignments).Select(a => a.EmployeeId).Distinct().ToList();
        var employees = employeeIds.Count == 0 ? new Dictionary<int, (string Name, string Code)>()
            : await _db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && employeeIds.Contains(e.Id))
                .Select(e => new { e.Id, e.FullName, e.EmployeeCode }).ToDictionaryAsync(e => e.Id, e => (e.FullName, e.EmployeeCode), ct);
        // Logged minutes count only final (Approved/Locked) timesheets, so a budget reads against approved work.
        var logged = await _db.TimesheetEntries.AsNoTracking()
            .Where(e => e.TenantId == tenantId && ids.Contains(e.ProjectId))
            .Join(_db.Timesheets.AsNoTracking().Where(t => t.TenantId == tenantId && (t.Status == TimesheetStatuses.Approved || t.Status == TimesheetStatuses.Locked)),
                e => e.TimesheetId, t => t.Id, (e, t) => new { e.ProjectId, e.Minutes })
            .GroupBy(x => x.ProjectId)
            .Select(g => new { ProjectId = g.Key, Minutes = g.Sum(x => x.Minutes) })
            .ToDictionaryAsync(x => x.ProjectId, x => x.Minutes, ct);

        return projects.Select(p => new ProjectDto(
            p.Id, p.Code, p.Name, p.ClientName, p.CompanyId, p.CostCenterId,
            p.CostCenterId is { } cc && costCentres.TryGetValue(cc, out var ccName) ? ccName : null,
            p.Status, p.BudgetHours, p.IsBillable, p.Description, p.StartDate, p.EndDate,
            logged.TryGetValue(p.Id, out var m) ? m : 0,
            p.Assignments.Count(a => a.IsActive), p.CreatedAtUtc,
            p.Tasks.OrderBy(t => t.SortOrder).ThenBy(t => t.Name).Select(ToTaskDto).ToList(),
            p.Assignments.OrderBy(a => a.EmployeeId).Select(a => new ProjectAssignmentDto(a.Id, a.EmployeeId,
                employees.TryGetValue(a.EmployeeId, out var e) ? e.Name : string.Empty,
                employees.TryGetValue(a.EmployeeId, out var e2) ? e2.Code : string.Empty,
                a.AllocationPercent, a.StartDate, a.EndDate, a.IsActive)).ToList())).ToList();
    }

    private static ProjectTaskDto ToTaskDto(ProjectTask t) => new(t.Id, t.Name, t.TaskTypeCode, t.IsBillable, t.IsActive, t.SortOrder);

    private async Task SaveOrConflictAsync(string message, CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            throw new TimesheetConflictException(message, ex);
        }
    }
}
