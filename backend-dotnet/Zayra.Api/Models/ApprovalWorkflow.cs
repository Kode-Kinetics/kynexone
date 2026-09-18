using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

/// <summary>
/// The single approval-configuration model (F1 — approval engine convergence). One row is one
/// ordered approval chain for one <see cref="EntityName"/> (e.g. <c>LeaveRequest</c>), optionally
/// narrowed to a department and/or grade.
///
/// <para>Routing is owned by <c>IApprovalRouter</c>. For an employee and an entity it picks the
/// active workflow in this order of specificity: department + grade → department → grade →
/// unscoped (tenant-wide). Among unscoped rows <see cref="IsDefault"/> wins; any remaining tie is
/// broken by <see cref="CreatedAtUtc"/> then <see cref="Id"/> so the choice is deterministic.</para>
///
/// <para>This replaces the deprecated <see cref="ApprovalPolicy"/> model, whose rows were copied
/// here (same primary key) by migration <c>ConvergeApprovalPolicyIntoWorkflow</c>.</para>
/// </summary>
public class ApprovalWorkflow : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    /// <summary>When set, the workflow applies only to employees in this department.</summary>
    public Guid? DepartmentId { get; set; }
    /// <summary>When set, the workflow applies only to employees on this grade.</summary>
    public Guid? GradeId { get; set; }
    /// <summary>
    /// Marks the tenant-wide workflow for <see cref="EntityName"/>. Only meaningful on an unscoped
    /// row (no department, no grade); a scoped row cannot be the default.
    /// </summary>
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<ApprovalWorkflowStep> Steps { get; set; } = new List<ApprovalWorkflowStep>();
}
