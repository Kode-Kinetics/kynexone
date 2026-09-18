using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Approvals;

/// <summary>
/// The single approval router (F1). See <see cref="IApprovalRouter"/> for the contract.
///
/// <para>Determinism is a requirement, not a nicety: the previous policy resolver chose an "HR"
/// approver with an unordered <c>FirstOrDefaultAsync</c> over a designation substring match, so two
/// identical submissions could route to different people. Every selection here is either a unique
/// key lookup or carries an explicit total order.</para>
/// </summary>
public sealed class ApprovalRouter : IApprovalRouter
{
    /// <summary>The role queue a person-type step falls to when its person cannot act, and the queue for "HR" steps.</summary>
    public const string HrManagerRole = "HR Manager";

    private readonly ZayraDbContext _db;
    private readonly IHrmHierarchyService _hierarchy;

    public ApprovalRouter(ZayraDbContext db, IHrmHierarchyService? hierarchy = null)
    {
        _db = db;
        // Optional with a concrete fallback so direct constructions (tests, legacy call sites) keep working.
        _hierarchy = hierarchy ?? new HrmHierarchyService(db, new AuditService(db));
    }

    public async Task<ApprovalRoute> ResolveAsync(Guid tenantId, int? employeeId, string entityName, CancellationToken ct)
        => await TryResolveAsync(tenantId, employeeId, entityName, ct)
           ?? throw new ApprovalRouteNotConfiguredException(tenantId, entityName, employeeId);

    public async Task<ApprovalRoute?> TryResolveAsync(Guid tenantId, int? employeeId, string entityName, CancellationToken ct)
    {
        var entity = (entityName ?? string.Empty).Trim();
        if (entity.Length == 0) throw new ArgumentException("An entity name is required to route an approval.", nameof(entityName));

        Guid? departmentId = null, gradeId = null;
        if (employeeId.HasValue)
        {
            var employee = await _db.Employees.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.Id == employeeId.Value && !e.IsDeleted)
                .Select(e => new { e.DepartmentId, e.GradeId })
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException($"Employee {employeeId.Value} was not found.");
            departmentId = employee.DepartmentId;
            gradeId = employee.GradeId;
        }

        // Candidate set is small (a tenant's active workflows for one entity); ordering is applied in
        // memory so the rule is identical on every provider (PostgreSQL, SQLite, InMemory).
        var candidates = await _db.ApprovalWorkflows.AsNoTracking()
            .Where(w => w.TenantId == tenantId && w.IsActive && w.EntityName == entity)
            .Select(w => new { w.Id, w.Code, w.Name, w.EntityName, w.DepartmentId, w.GradeId, w.IsDefault, w.CreatedAtUtc })
            .ToListAsync(ct);

        var match = candidates
            .Select(w => new
            {
                Workflow = w,
                Tier = w.DepartmentId.HasValue && w.GradeId.HasValue
                        ? (w.DepartmentId == departmentId && w.GradeId == gradeId && departmentId.HasValue && gradeId.HasValue ? 4 : -1)
                    : w.DepartmentId.HasValue
                        ? (departmentId.HasValue && w.DepartmentId == departmentId ? 3 : -1)
                    : w.GradeId.HasValue
                        ? (gradeId.HasValue && w.GradeId == gradeId ? 2 : -1)
                    : 1
            })
            .Where(x => x.Tier > 0)
            .OrderByDescending(x => x.Tier)
            .ThenByDescending(x => x.Workflow.IsDefault)
            .ThenBy(x => x.Workflow.CreatedAtUtc)
            .ThenBy(x => x.Workflow.Id)
            .FirstOrDefault();
        if (match is null) return null;

        var matchedOn = match.Tier switch
        {
            4 => "DepartmentAndGrade",
            3 => "Department",
            2 => "Grade",
            _ => "Default"
        };
        var w = match.Workflow;
        var steps = await LoadStepsAsync(tenantId, w.Id, ct);
        var route = new ApprovalRoute(w.Id, w.Code, w.Name, w.EntityName, w.DepartmentId, w.GradeId, matchedOn, steps);
        Validate(tenantId, route);
        return route;
    }

    public async Task<ApprovalRoute?> LoadAsync(Guid tenantId, Guid workflowId, CancellationToken ct)
    {
        if (workflowId == Guid.Empty) return null;
        var w = await _db.ApprovalWorkflows.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == workflowId)
            .Select(x => new { x.Id, x.Code, x.Name, x.EntityName, x.DepartmentId, x.GradeId })
            .FirstOrDefaultAsync(ct);
        if (w is null) return null;
        var route = new ApprovalRoute(w.Id, w.Code, w.Name, w.EntityName, w.DepartmentId, w.GradeId, "Pinned",
            await LoadStepsAsync(tenantId, w.Id, ct));
        Validate(tenantId, route);
        return route;
    }

    public async Task<ResolvedApprover> ResolveApproverAsync(Guid tenantId, int? subjectEmployeeId, ApprovalRouteStep step, CancellationToken ct)
    {
        var type = Normalize(step.ApproverType);
        var role = (step.ApproverRole ?? string.Empty).Trim();

        switch (type)
        {
            case "ROLE":
                return new ResolvedApprover("Role", role.Length == 0 ? "Any" : role, null, null, string.Empty, false);
            case "HR":
                // "HR" is a role queue by definition (any HR Manager). It used to pick ONE employee whose
                // designation contained "HR", with no ordering — nondeterministic and not role-based.
                return new ResolvedApprover("HR", HrManagerRole, null, null, string.Empty, false);
        }

        var personType = string.IsNullOrWhiteSpace(step.ApproverType) ? "Role" : step.ApproverType.Trim();
        var personId = await ResolvePersonAsync(tenantId, subjectEmployeeId, type, step, ct);
        if (personId is null || personId == subjectEmployeeId)
            return Escalate(personType);

        var person = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == personId.Value && !e.IsDeleted)
            .Select(e => new { e.Id, e.FullName, e.Designation, e.UserAccountId })
            .FirstOrDefaultAsync(ct);
        if (person is null) return Escalate(personType);

        var label = role.Length > 0 ? role : (string.IsNullOrWhiteSpace(person.Designation) ? personType : person.Designation!);
        return new ResolvedApprover(personType, label, person.Id, person.UserAccountId, person.FullName, false);
    }

    private static ResolvedApprover Escalate(string approverType)
        => new(approverType, HrManagerRole, null, null, string.Empty, true);

    private async Task<int?> ResolvePersonAsync(Guid tenantId, int? subjectEmployeeId, string type, ApprovalRouteStep step, CancellationToken ct)
    {
        if (type == "SPECIFICEMPLOYEE") return step.SpecificEmployeeId;
        if (subjectEmployeeId is null) return null;

        var subject = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == subjectEmployeeId.Value)
            .Select(e => new { e.ManagerEmployeeId, e.SupervisorEmployeeId, e.HRBusinessPartnerEmployeeId, e.DepartmentId })
            .FirstOrDefaultAsync(ct);
        if (subject is null) return null;

        switch (type)
        {
            case "MANAGER":
            case "DIRECTMANAGER":
                return subject.ManagerEmployeeId;
            case "SUPERVISOR":
                return subject.SupervisorEmployeeId;
            case "HRBUSINESSPARTNER":
                return subject.HRBusinessPartnerEmployeeId;
            case "DEPARTMENTHEAD":
                if (subject.DepartmentId.HasValue)
                {
                    var head = await _db.Departments.AsNoTracking()
                        .Where(d => d.TenantId == tenantId && d.Id == subject.DepartmentId.Value)
                        .Select(d => d.ManagerEmployeeId)
                        .FirstOrDefaultAsync(ct);
                    if (head.HasValue && head.Value != subjectEmployeeId.Value) return head;
                }
                return (await HierarchyAsync(tenantId, subjectEmployeeId.Value, ct))?.DepartmentHead?.EmployeeId;
            case "SENIORMANAGER":
            case "SECONDLEVELMANAGER":
                return (await HierarchyAsync(tenantId, subjectEmployeeId.Value, ct))?.SecondLevelManager?.EmployeeId;
            case "COMPANYHEAD":
                return (await HierarchyAsync(tenantId, subjectEmployeeId.Value, ct))?.CompanyHead?.EmployeeId;
            default:
                return null;
        }
    }

    private async Task<HierarchyResolverDto?> HierarchyAsync(Guid tenantId, int employeeId, CancellationToken ct)
    {
        try { return await _hierarchy.ResolveHierarchyAsync(tenantId, employeeId, 20, ct); }
        // A broken (e.g. circular) reporting chain cannot name an approver; the step escalates visibly.
        catch (InvalidOperationException) { return null; }
    }

    private async Task<IReadOnlyList<ApprovalRouteStep>> LoadStepsAsync(Guid tenantId, Guid workflowId, CancellationToken ct)
        => (await _db.ApprovalWorkflowSteps.AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.WorkflowId == workflowId)
                .ToListAsync(ct))
            .OrderBy(s => s.StepOrder).ThenBy(s => s.Id)
            .Select(s => new ApprovalRouteStep(s.StepOrder, s.StepName,
                string.IsNullOrWhiteSpace(s.ApproverType) ? "Role" : s.ApproverType.Trim(),
                s.ApproverRole ?? string.Empty, s.SpecificEmployeeId, s.EscalationAfterHours, s.IsFinalStep))
            .ToList();

    /// <summary>
    /// A route must be able to finish: at least one step, and at least one step marked final. The
    /// first final step completes the request; any step after it is unreachable by design (that is
    /// how a workflow is shortened without deleting steps).
    /// </summary>
    private static void Validate(Guid tenantId, ApprovalRoute route)
    {
        if (route.Steps.Count == 0)
            throw new ApprovalRouteInvalidException(tenantId, route.EntityName, route.WorkflowId, route.Code, "it has no steps.");
        if (!route.Steps.Any(s => s.IsFinalStep))
            throw new ApprovalRouteInvalidException(tenantId, route.EntityName, route.WorkflowId, route.Code,
                "no step is marked final, so no approval could ever complete it.");
    }

    private static string Normalize(string? approverType)
        => string.IsNullOrWhiteSpace(approverType) ? "ROLE" : approverType.Trim().ToUpperInvariant();
}
