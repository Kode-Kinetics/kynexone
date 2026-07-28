using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// Tenant-configurable staffing level (band) — e.g. Department Head / Manager / Assistant Manager /
/// Supervisor / Staff. These are seeded per tenant as EDITABLE DATA (EstablishmentSeeder), never a
/// compile-time enum: no code path anywhere may reference a band name literal. An employee's level
/// is always derived via Designation.StaffingLevelId — never stored on the employee, never parsed
/// from title strings.
/// </summary>
public class StaffingLevel : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;      // e.g. "DEPT_HEAD" — seeded data, tenant-editable
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    /// <summary>1 = most senior. Display ordering + mapping-suggestion heuristics only — never enforcement.</summary>
    public int Rank { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
}

/// <summary>
/// One matrix cell: budgeted headcount for (department, staffing level).
/// LOAD-BEARING SEMANTICS: row ABSENT = level uncontrolled (unlimited — exactly the pre-matrix
/// behaviour); row present with BudgetedHeadcount = 0 = level frozen (no additions sanctioned).
/// Tenant-scoped (no CompanyId in v1): budgets inherit company/branch scoping through
/// Department.BranchId. Soft-deleting a row returns the level to uncontrolled (audited).
/// </summary>
public class DepartmentStaffingBudget : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid StaffingLevelId { get; set; }
    public int BudgetedHeadcount { get; set; }             // >= 0
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
}
