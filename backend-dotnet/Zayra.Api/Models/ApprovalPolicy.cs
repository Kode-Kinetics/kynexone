using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

/// <summary>
/// DEPRECATED (F1 — approval engine convergence). Nothing reads or writes this table any more.
/// Its rows were copied into <see cref="ApprovalWorkflow"/> (same primary key, DepartmentId /
/// GradeId / IsDefault carried over) by migration <c>ConvergeApprovalPolicyIntoWorkflow</c>.
/// The table is retained, frozen, for exactly one release so that an application rollback to the
/// previous build still finds the configuration it reads; a follow-up migration drops it.
/// Configure approvals with <see cref="ApprovalWorkflow"/> via /api/approval-workflows.
/// </summary>
public class ApprovalPolicy : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Leave | Overtime | Payroll | Expense | Recruitment | Travel</summary>
    public string WorkflowType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid? DepartmentId { get; set; }
    public Guid? GradeId { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public Guid? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }

    public ICollection<ApprovalPolicyStep> Steps { get; set; } = new List<ApprovalPolicyStep>();
}
