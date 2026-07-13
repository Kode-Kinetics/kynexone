using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Organization;

public class HrmHierarchyService : IHrmHierarchyService
{
    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;

    public HrmHierarchyService(ZayraDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IReadOnlyList<OrgChartNodeDto>> GetOrgChartAsync(
        Guid tenantId, int? rootEmployeeId, int maxDepth, CancellationToken ct)
    {
        var employees = await _db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == EmployeeStatuses.Active)
            .Select(e => new
            {
                e.Id,
                e.EmployeeCode,
                e.FullName,
                e.Designation,
                e.Department,
                e.ProfilePhotoUrl,
                e.ManagerEmployeeId
            })
            .ToListAsync(ct);

        // Build lookup
        var lookup = employees.ToLookup(e => e.ManagerEmployeeId);

        // Identify roots: employees whose manager is either null, not in this tenant, or equal to rootEmployeeId filter
        IEnumerable<int> rootIds;
        if (rootEmployeeId.HasValue)
            rootIds = new[] { rootEmployeeId.Value };
        else
            rootIds = employees.Where(e => e.ManagerEmployeeId == null || !employees.Any(m => m.Id == e.ManagerEmployeeId)).Select(e => e.Id);

        var allIds = new HashSet<int>(employees.Select(e => e.Id));

        OrgChartNodeDto Build(int id, int depth)
        {
            var e = employees.First(x => x.Id == id);
            var reports = depth < maxDepth
                ? lookup[id].Where(r => allIds.Contains(r.Id)).Select(r => Build(r.Id, depth + 1)).ToList()
                : new List<OrgChartNodeDto>();
            return new OrgChartNodeDto(e.Id, e.EmployeeCode, e.FullName, e.Designation ?? "", e.Department ?? "", e.ProfilePhotoUrl, reports);
        }

        return rootIds
            .Where(id => allIds.Contains(id))
            .Select(id => Build(id, 1))
            .ToList();
    }

    public async Task<IReadOnlyList<ReportingLineDto>> GetReportingLinesAsync(
        Guid tenantId, int employeeId, CancellationToken ct)
    {
        var lines = await _db.ReportingLines
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.EmployeeId == employeeId && r.IsActive)
            .ToListAsync(ct);

        var empIds = lines.Select(r => r.ManagerEmployeeId)
            .Append(employeeId)
            .Distinct()
            .ToList();

        var names = await _db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && empIds.Contains(e.Id))
            .Select(e => new { e.Id, e.FullName })
            .ToDictionaryAsync(e => e.Id, e => e.FullName, ct);

        names.TryGetValue(employeeId, out var empName);

        return lines.Select(r =>
        {
            names.TryGetValue(r.ManagerEmployeeId, out var mgrName);
            return new ReportingLineDto(
                r.Id, r.EmployeeId, empName ?? "", r.ManagerEmployeeId, mgrName ?? "",
                r.RelationshipType, r.EffectiveFrom, r.EffectiveTo, r.IsPrimary, r.IsActive);
        }).ToList();
    }

    public async Task SetManagerAsync(
        Guid tenantId, int employeeId, int? managerEmployeeId, RequestContext context, CancellationToken ct)
    {
        var employee = await _db.Employees
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted, ct)
            ?? throw new InvalidOperationException($"Employee {employeeId} not found.");

        if (managerEmployeeId.HasValue)
        {
            if (managerEmployeeId.Value == employeeId)
                throw new InvalidOperationException("An employee cannot be their own manager.");

            await ValidateNoCircularManagerAsync(tenantId, employeeId, managerEmployeeId.Value, ct);
            await EnsureSameCompanyEmployeesAsync(tenantId, employeeId, managerEmployeeId.Value, ct);
        }

        var previousManagerId = employee.ManagerEmployeeId;
        employee.ManagerEmployeeId = managerEmployeeId;
        employee.UpdatedAtUtc = DateTime.UtcNow;
        employee.UpdatedBy = context.UserId;

        // Deactivate old SolidLine reporting line
        var oldLine = await _db.ReportingLines
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.EmployeeId == employeeId
                && r.RelationshipType == "SolidLine" && r.IsPrimary && r.IsActive, ct);
        if (oldLine is not null)
        {
            oldLine.IsActive = false;
            oldLine.EffectiveTo = DateTime.UtcNow;
            oldLine.UpdatedAtUtc = DateTime.UtcNow;
            oldLine.UpdatedBy = context.UserId;
        }

        // Create new SolidLine reporting line
        if (managerEmployeeId.HasValue)
        {
            _db.ReportingLines.Add(new ReportingLine
            {
                TenantId = tenantId,
                EmployeeId = employeeId,
                ManagerEmployeeId = managerEmployeeId.Value,
                RelationshipType = "SolidLine",
                EffectiveFrom = DateTime.UtcNow,
                IsPrimary = true,
                IsActive = true,
                CreatedBy = context.UserId
            });
        }

        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("employee.manager_changed", nameof(Employee), employeeId.ToString(), context,
            System.Text.Json.JsonSerializer.Serialize(new { previousManagerId, newManagerId = managerEmployeeId }), ct);
    }

    public async Task<ReportingLineDto> AddReportingLineAsync(
        Guid tenantId, int employeeId, AddReportingLineRequest req, RequestContext context, CancellationToken ct)
    {
        if (req.ManagerEmployeeId == employeeId)
            throw new InvalidOperationException("An employee cannot report to themselves.");

        var allowedTypes = new HashSet<string> { "SolidLine", "DottedLine", "Temporary", "Functional" };
        if (!allowedTypes.Contains(req.RelationshipType))
            throw new InvalidOperationException($"Invalid RelationshipType '{req.RelationshipType}'. Use: SolidLine, DottedLine, Temporary, Functional.");

        if (req.RelationshipType == "SolidLine")
            await ValidateNoCircularManagerAsync(tenantId, employeeId, req.ManagerEmployeeId, ct);
        await EnsureSameCompanyEmployeesAsync(tenantId, employeeId, req.ManagerEmployeeId, ct);

        var line = new ReportingLine
        {
            TenantId = tenantId,
            EmployeeId = employeeId,
            ManagerEmployeeId = req.ManagerEmployeeId,
            RelationshipType = req.RelationshipType,
            EffectiveFrom = req.EffectiveFrom ?? DateTime.UtcNow,
            EffectiveTo = req.EffectiveTo,
            IsPrimary = req.IsPrimary,
            IsActive = true,
            CreatedBy = context.UserId
        };
        _db.ReportingLines.Add(line);
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("employee.reporting_line_added", nameof(ReportingLine), line.Id.ToString(), context, null, ct);

        var names = await _db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && (e.Id == employeeId || e.Id == req.ManagerEmployeeId))
            .Select(e => new { e.Id, e.FullName })
            .ToDictionaryAsync(e => e.Id, e => e.FullName, ct);
        names.TryGetValue(employeeId, out var empName);
        names.TryGetValue(req.ManagerEmployeeId, out var mgrName);

        return new ReportingLineDto(line.Id, line.EmployeeId, empName ?? "", line.ManagerEmployeeId, mgrName ?? "",
            line.RelationshipType, line.EffectiveFrom, line.EffectiveTo, line.IsPrimary, line.IsActive);
    }

    public async Task<bool> RemoveReportingLineAsync(
        Guid tenantId, int employeeId, Guid reportingLineId, RequestContext context, CancellationToken ct)
    {
        var line = await _db.ReportingLines
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == reportingLineId && r.EmployeeId == employeeId && r.IsActive, ct);
        if (line is null) return false;
        line.IsActive = false;
        line.EffectiveTo = DateTime.UtcNow;
        line.UpdatedAtUtc = DateTime.UtcNow;
        line.UpdatedBy = context.UserId;
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("employee.reporting_line_removed", nameof(ReportingLine), reportingLineId.ToString(), context, null, ct);
        return true;
    }

    public async Task<HierarchyResolverDto> ResolveHierarchyAsync(Guid tenantId, int employeeId, int maxDepth, CancellationToken ct)
    {
        maxDepth = Math.Clamp(maxDepth, 1, 20);
        var employees = await _db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted)
            .Select(e => new
            {
                e.Id,
                e.EmployeeCode,
                e.FullName,
                e.Designation,
                e.Department,
                e.CompanyId,
                e.ManagerEmployeeId,
                e.Status
            })
            .ToDictionaryAsync(e => e.Id, ct);

        if (!employees.TryGetValue(employeeId, out var employee))
            throw new InvalidOperationException($"Employee {employeeId} not found.");

        var chain = new List<HierarchyPersonDto>();
        var visited = new HashSet<int> { employeeId };
        var current = employee.ManagerEmployeeId;
        var level = 1;
        while (current.HasValue && level <= maxDepth)
        {
            if (!visited.Add(current.Value))
                throw new InvalidOperationException("Circular reporting chain detected.");
            if (!employees.TryGetValue(current.Value, out var manager))
                break;

            chain.Add(ToPerson(manager.Id, manager.EmployeeCode, manager.FullName, manager.Designation, manager.Department, manager.CompanyId, level));
            current = manager.ManagerEmployeeId;
            level++;
        }

        var departmentHead = await ResolveDepartmentHeadAsync(tenantId, employee.Department, employee.CompanyId, employeeId, ct)
            ?? chain.FirstOrDefault();
        var companyHead = chain.LastOrDefault(p => p.CompanyId == employee.CompanyId)
            ?? await ResolveTopCompanyEmployeeAsync(tenantId, employee.CompanyId, employeeId, ct);

        return new HierarchyResolverDto(
            employeeId,
            chain,
            chain.ElementAtOrDefault(0),
            chain.ElementAtOrDefault(1),
            departmentHead,
            companyHead);
    }

    public async Task<WorkflowApproverResolutionDto> ResolveWorkflowApproversAsync(Guid tenantId, int employeeId, string workflowType, CancellationToken ct)
    {
        var hierarchy = await ResolveHierarchyAsync(tenantId, employeeId, 20, ct);
        var type = (workflowType ?? string.Empty).Trim().ToUpperInvariant();
        var approvers = new List<HierarchyPersonDto>();
        var missing = new List<string>();

        void Add(string role, HierarchyPersonDto? person)
        {
            if (person is null) missing.Add(role);
            else if (approvers.All(x => x.EmployeeId != person.EmployeeId)) approvers.Add(person);
        }

        switch (type)
        {
            case "LEAVE":
            case "ATTENDANCE":
            case "OVERTIME":
            case "OT":
                Add("DirectManager", hierarchy.DirectManager);
                Add("DepartmentHead", hierarchy.DepartmentHead);
                break;
            case "APPRAISAL":
            case "PERFORMANCE":
            case "KPI":
                Add("DirectManager", hierarchy.DirectManager);
                Add("SecondLevelManager", hierarchy.SecondLevelManager);
                break;
            case "SALARY":
            case "COMPENSATION":
            case "PAYROLL":
            case "PAYROLL_EXCEPTION":
                Add("DirectManager", hierarchy.DirectManager);
                Add("DepartmentHead", hierarchy.DepartmentHead);
                Add("CompanyHead", hierarchy.CompanyHead);
                break;
            case "TRANSFER":
            case "HR_REQUEST":
            default:
                Add("DirectManager", hierarchy.DirectManager);
                Add("DepartmentHead", hierarchy.DepartmentHead);
                break;
        }

        return new WorkflowApproverResolutionDto(string.IsNullOrWhiteSpace(workflowType) ? type : workflowType, employeeId, approvers, missing);
    }

    public async Task<int> ValidateNoCircularManagerAsync(
        Guid tenantId, int employeeId, int newManagerId, CancellationToken ct)
    {
        // Walk the manager chain from newManagerId upward; if we ever reach employeeId, it's circular.
        var allManagers = await _db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted)
            .Select(e => new { e.Id, e.ManagerEmployeeId })
            .ToDictionaryAsync(e => e.Id, e => e.ManagerEmployeeId, ct);

        var visited = new HashSet<int>();
        var current = (int?)newManagerId;
        var depth = 0;
        while (current.HasValue)
        {
            if (current.Value == employeeId)
                throw new InvalidOperationException(
                    $"Setting employee {newManagerId} as manager of {employeeId} would create a circular reporting chain.");
            if (visited.Contains(current.Value))
                break; // existing circular loop in data — stop but don't throw (that's a data integrity issue)
            visited.Add(current.Value);
            allManagers.TryGetValue(current.Value, out var next);
            current = next;
            depth++;
            if (depth > 50) break; // safety cap
        }
        return depth;
    }

    private async Task<HierarchyPersonDto?> ResolveDepartmentHeadAsync(Guid tenantId, string? departmentName, Guid? companyId, int employeeId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(departmentName)) return null;

        var headId = await _db.Departments.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.IsActive && d.ManagerEmployeeId != null
                && d.NameEn == departmentName)
            .Where(d => d.BranchId != null)
            .Join(_db.Branches.AsNoTracking().Where(b => b.TenantId == tenantId),
                d => d.BranchId!.Value,
                b => b.Id,
                (d, b) => new { d.ManagerEmployeeId, b.CompanyId })
            .Where(x => companyId == null || x.CompanyId == companyId)
            .Select(x => x.ManagerEmployeeId)
            .FirstOrDefaultAsync(ct);

        if (headId is null || headId.Value == employeeId) return null;
        return await ResolvePersonAsync(tenantId, headId.Value, 0, ct);
    }

    private async Task<HierarchyPersonDto?> ResolveTopCompanyEmployeeAsync(Guid tenantId, Guid? companyId, int employeeId, CancellationToken ct)
    {
        if (companyId is null) return null;
        var top = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.CompanyId == companyId && e.Id != employeeId
                && e.ManagerEmployeeId == null && e.Status == EmployeeStatuses.Active)
            .OrderBy(e => e.Id)
            .Select(e => new { e.Id, e.EmployeeCode, e.FullName, e.Designation, e.Department, e.CompanyId })
            .FirstOrDefaultAsync(ct);
        return top is null ? null : ToPerson(top.Id, top.EmployeeCode, top.FullName, top.Designation, top.Department, top.CompanyId, 0);
    }

    private async Task<HierarchyPersonDto?> ResolvePersonAsync(Guid tenantId, int employeeId, int level, CancellationToken ct)
    {
        var person = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Id == employeeId)
            .Select(e => new { e.Id, e.EmployeeCode, e.FullName, e.Designation, e.Department, e.CompanyId })
            .FirstOrDefaultAsync(ct);
        return person is null ? null : ToPerson(person.Id, person.EmployeeCode, person.FullName, person.Designation, person.Department, person.CompanyId, level);
    }

    private static HierarchyPersonDto ToPerson(int id, string code, string name, string? designation, string? department, Guid? companyId, int level)
        => new(id, code, name, designation ?? string.Empty, department ?? string.Empty, companyId, level);

    private async Task EnsureSameCompanyEmployeesAsync(Guid tenantId, int employeeId, int managerEmployeeId, CancellationToken ct)
    {
        var people = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && (e.Id == employeeId || e.Id == managerEmployeeId))
            .Select(e => new { e.Id, e.CompanyId, e.Status })
            .ToListAsync(ct);

        var employee = people.FirstOrDefault(e => e.Id == employeeId)
            ?? throw new InvalidOperationException($"Employee {employeeId} not found.");
        var manager = people.FirstOrDefault(e => e.Id == managerEmployeeId)
            ?? throw new InvalidOperationException($"Manager employee {managerEmployeeId} not found in this tenant.");

        if (employee.CompanyId.HasValue && manager.CompanyId.HasValue && employee.CompanyId != manager.CompanyId)
            throw new InvalidOperationException("Cross-company reporting relationships are not allowed. Use an audited transfer or matrix-assignment flow.");
    }
}
