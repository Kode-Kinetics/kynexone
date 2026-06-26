using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

public class Department : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? ParentDepartmentId { get; set; }
    public Guid? CostCenterId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public int? ManagerEmployeeId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    // ── Establishment / budgeting (headcount + budget envelope) ──────────────
    /// <summary>Approved/budgeted headcount for this department (0 = not set). Live current
    /// headcount is computed from active employees; gap = approved − current.</summary>
    public int ApprovedHeadcount { get; set; }
    /// <summary>Approved monthly salary budget for this department (0 = not set).</summary>
    public decimal MonthlyBudgetAmount { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedBy { get; set; }
}
