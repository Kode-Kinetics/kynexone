using Zayra.Api.Domain.Entities;
namespace Zayra.Api.Models;

/// <summary>
/// One step of an <see cref="ApprovalWorkflow"/>. <see cref="ApproverType"/> decides how the
/// approver is resolved at runtime by <c>IApprovalRouter.ResolveApproverAsync</c>:
///   Role              → anyone holding <see cref="ApproverRole"/> (a role queue)
///   Manager           → the requester's direct manager (Employee.ManagerEmployeeId)
///   SeniorManager     → the requester's second-level manager
///   Supervisor        → Employee.SupervisorEmployeeId
///   DepartmentHead    → the head of the requester's department
///   CompanyHead       → the top of the requester's company hierarchy
///   HR                → the "HR Manager" role queue
///   HRBusinessPartner → Employee.HRBusinessPartnerEmployeeId
///   SpecificEmployee  → <see cref="SpecificEmployeeId"/>
/// A person-type step whose person cannot be resolved (or would be the requester) is routed to the
/// "HR Manager" role queue and marked as escalated — visibly, never silently.
/// Only a step with <see cref="IsFinalStep"/> set completes the request.
/// </summary>
public class ApprovalWorkflowStep : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid WorkflowId { get; set; }
    public int StepOrder { get; set; }
    public string StepName { get; set; } = string.Empty;
    public string ApproverRole { get; set; } = string.Empty;
    /// <summary>Role | Manager | SeniorManager | Supervisor | DepartmentHead | CompanyHead | HR | HRBusinessPartner | SpecificEmployee</summary>
    public string ApproverType { get; set; } = "Role";
    public int? SpecificEmployeeId { get; set; }
    public int? EscalationAfterHours { get; set; }
    public bool IsFinalStep { get; set; }
}
