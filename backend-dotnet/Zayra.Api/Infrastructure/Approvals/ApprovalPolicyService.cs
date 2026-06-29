using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Approvals;

public class ApprovalPolicyService : IApprovalPolicyService
{
    private readonly ZayraDbContext _db;

    public ApprovalPolicyService(ZayraDbContext db) => _db = db;

    public async Task<ResolvedApprovalPolicy?> ResolveAsync(
        Guid tenantId, int employeeId, string workflowType, CancellationToken ct)
    {
        // Load employee with hierarchy fields
        var employee = await _db.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted, ct);
        if (employee is null) return null;

        // Find best matching policy: department/grade-specific beats default
        var policies = await _db.ApprovalPolicies
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.WorkflowType == workflowType
                        && p.IsActive && !p.IsDeleted)
            .OrderByDescending(p => p.DepartmentId != null ? 2 : p.GradeId != null ? 1 : 0)
            .ToListAsync(ct);

        var policy = policies.FirstOrDefault(p =>
                p.DepartmentId == employee.DepartmentId && p.GradeId == employee.GradeId)
            ?? policies.FirstOrDefault(p => p.DepartmentId == employee.DepartmentId && p.GradeId == null)
            ?? policies.FirstOrDefault(p => p.DepartmentId == null && p.GradeId == employee.GradeId)
            ?? policies.FirstOrDefault(p => p.IsDefault && p.DepartmentId == null && p.GradeId == null);

        if (policy is null) return null;

        var steps = await _db.ApprovalPolicySteps
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.PolicyId == policy.Id)
            .OrderBy(s => s.StepOrder)
            .ToListAsync(ct);

        // Pre-load names for all employees we might need
        var potentialIds = new HashSet<int?>();
        foreach (var step in steps)
        {
            switch (step.ApproverType)
            {
                case "Manager":        potentialIds.Add(employee.ManagerEmployeeId); break;
                case "Supervisor":     potentialIds.Add(employee.SupervisorEmployeeId); break;
                case "SpecificEmployee": potentialIds.Add(step.SpecificEmployeeId); break;
                case "DepartmentHead": break; // resolved separately below
                case "HR":             break; // resolved separately below
                case "HRBusinessPartner": potentialIds.Add(employee.HRBusinessPartnerEmployeeId); break;
            }
        }
        potentialIds.Remove(null);

        // Resolve department head if needed
        int? departmentHeadId = null;
        if (steps.Any(s => s.ApproverType == "DepartmentHead") && employee.DepartmentId.HasValue)
        {
            departmentHeadId = await _db.Departments
                .AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.Id == employee.DepartmentId)
                .Select(d => (int?)d.ManagerEmployeeId)
                .FirstOrDefaultAsync(ct);
            potentialIds.Add(departmentHeadId);
        }

        // Resolve HR role — find the first active employee with an HR Director/HR Manager designation
        int? hrEmployeeId = null;
        if (steps.Any(s => s.ApproverType == "HR"))
        {
            hrEmployeeId = await _db.Employees
                .AsNoTracking()
                .Where(e => e.TenantId == tenantId && !e.IsDeleted
                    && (e.Designation != null && (e.Designation.Contains("HR") || e.Designation.Contains("Human Resource")))
                    && e.ManagerEmployeeId != null) // not at root
                .Select(e => (int?)e.Id)
                .FirstOrDefaultAsync(ct);
            // Fallback: any employee in an HR department
            hrEmployeeId ??= await _db.Employees
                .AsNoTracking()
                .Where(e => e.TenantId == tenantId && !e.IsDeleted
                    && e.Department != null && e.Department.Contains("HR"))
                .Select(e => (int?)e.Id)
                .FirstOrDefaultAsync(ct);
            potentialIds.Add(hrEmployeeId);
        }

        // Explicit TenantId scope: int employee PKs are global, so a corrupted ManagerEmployeeId/etc.
        // pointing at another tenant must NOT resolve a foreign employee as approver. (Defence-in-depth
        // — the global query filter also covers this when an HTTP tenant context is active.)
        var names = await _db.Employees
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && potentialIds.Contains(e.Id))
            .Select(e => new { e.Id, e.FullName })
            .ToDictionaryAsync(e => e.Id, e => e.FullName, ct);

        var resolvedSteps = steps.Select(step =>
        {
            int? approverId = step.ApproverType switch
            {
                "Manager"             => employee.ManagerEmployeeId,
                "Supervisor"          => employee.SupervisorEmployeeId,
                "DepartmentHead"      => departmentHeadId,
                "HR"                  => hrEmployeeId,
                "HRBusinessPartner"   => employee.HRBusinessPartnerEmployeeId,
                "SpecificEmployee"    => step.SpecificEmployeeId,
                _                     => null // "Role" — caller handles role-based routing
            };

            names.TryGetValue(approverId ?? -1, out var approverName);

            return new ResolvedApprovalStep(
                step.StepOrder,
                step.StepName,
                step.ApproverType,
                approverId,
                approverName,
                step.IsFinalStep);
        }).ToList();

        return new ResolvedApprovalPolicy(policy.Id, policy.Name, resolvedSteps);
    }
}
