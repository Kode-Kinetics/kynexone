using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

/// <summary>
/// DEPRECATED (F1). Frozen legacy step of an <see cref="ApprovalPolicy"/>; copied into
/// <see cref="ApprovalWorkflowStep"/> by migration <c>ConvergeApprovalPolicyIntoWorkflow</c>.
/// See <see cref="ApprovalWorkflowStep"/> for the live model and approver-type semantics.
/// </summary>
public class ApprovalPolicyStep : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PolicyId { get; set; }
    public int StepOrder { get; set; }
    public string StepName { get; set; } = string.Empty;
    /// <summary>Manager | Supervisor | DepartmentHead | HR | HRBusinessPartner | SpecificEmployee | Role</summary>
    public string ApproverType { get; set; } = "Manager";
    public int? SpecificEmployeeId { get; set; }
    public string? ApproverRole { get; set; }
    public int? EscalationAfterHours { get; set; }
    public bool IsFinalStep { get; set; }
}
