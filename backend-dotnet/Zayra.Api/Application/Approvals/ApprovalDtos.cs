using System.ComponentModel.DataAnnotations;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Approvals;

public record ApprovalWorkflowDto(
    Guid Id,
    string Code,
    string Name,
    string EntityName,
    bool IsActive,
    IReadOnlyCollection<ApprovalWorkflowStepDto> Steps);

public record ApprovalWorkflowStepDto(
    Guid Id,
    int StepOrder,
    string StepName,
    string ApproverRole,
    string ApproverType,
    int? SpecificEmployeeId,
    int? EscalationAfterHours,
    bool IsFinalStep);

public record ApprovalWorkflowRequest(
    [Required, MaxLength(80)] string Code,
    [Required, MaxLength(180)] string Name,
    [Required, MaxLength(120)] string EntityName,
    bool IsActive,
    [Required, MinLength(1)] IReadOnlyCollection<ApprovalWorkflowStepRequest> Steps);

public record ApprovalWorkflowStepRequest(
    [Range(1, 50)] int StepOrder,
    [Required, MaxLength(120)] string StepName,
    [Required, MaxLength(80)] string ApproverRole,
    [MaxLength(60)] string? ApproverType = null,
    int? SpecificEmployeeId = null,
    [Range(1, 720)] int? EscalationAfterHours = null,
    bool IsFinalStep = false);

public record ApprovalRequestDto(
    Guid Id,
    Guid WorkflowId,
    string EntityName,
    string EntityId,
    string Title,
    string Status,
    int CurrentStepOrder,
    Guid? RequestedByUserId,
    int? RequestedForEmployeeId,
    Guid? CompanyId,
    int? CurrentApproverEmployeeId,
    Guid? CurrentApproverUserId,
    string CurrentApproverName,
    string CurrentApproverRole,
    string CurrentApproverType,
    string CurrentQueue,
    int SlaHours,
    DateTime? DueAtUtc,
    bool IsOverdue,
    decimal AgeHours,
    DateTime? LastRoutedAtUtc,
    DateTime? EscalatedAtUtc,
    string EscalatedToRole,
    string Priority,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    IReadOnlyCollection<ApprovalDecisionDto> Decisions);

public record CreateApprovalRequest(
    [Required] Guid WorkflowId,
    [Required, MaxLength(120)] string EntityName,
    [Required, MaxLength(80)] string EntityId,
    [Required, MaxLength(240)] string Title,
    int? RequestedForEmployeeId = null,
    Guid? CompanyId = null,
    [MaxLength(40)] string? Priority = null);

public record ApprovalDecisionRequest(
    [Required, RegularExpression("Approve|Reject", ErrorMessage = "Decision must be Approve or Reject.")] string Decision,
    [MaxLength(1000)] string? Comments);

public record ApprovalDecisionDto(
    Guid Id,
    int StepOrder,
    string Decision,
    string Comments,
    Guid? DecidedByUserId,
    DateTime DecidedAtUtc);

public static class ApprovalMappings
{
    public static ApprovalWorkflowDto ToDto(this ApprovalWorkflow workflow) => new(
        workflow.Id,
        workflow.Code,
        workflow.Name,
        workflow.EntityName,
        workflow.IsActive,
        workflow.Steps.OrderBy(x => x.StepOrder).Select(x => x.ToDto()).ToList());

    public static ApprovalWorkflowStepDto ToDto(this ApprovalWorkflowStep step) => new(
        step.Id,
        step.StepOrder,
        step.StepName,
        step.ApproverRole,
        step.ApproverType,
        step.SpecificEmployeeId,
        step.EscalationAfterHours,
        step.IsFinalStep);

    public static ApprovalRequestDto ToDto(this ApprovalRequest request) => new(
        request.Id,
        request.WorkflowId,
        request.EntityName,
        request.EntityId,
        request.Title,
        request.Status,
        request.CurrentStepOrder,
        request.RequestedByUserId,
        request.RequestedForEmployeeId,
        request.CompanyId,
        request.CurrentApproverEmployeeId,
        request.CurrentApproverUserId,
        request.CurrentApproverName,
        request.CurrentApproverRole,
        request.CurrentApproverType,
        request.CurrentQueue,
        request.SlaHours,
        request.DueAtUtc,
        request.Status == "Pending" && request.DueAtUtc is not null && request.DueAtUtc < DateTime.UtcNow,
        Math.Round((decimal)(DateTime.UtcNow - request.CreatedAtUtc).TotalHours, 1),
        request.LastRoutedAtUtc,
        request.EscalatedAtUtc,
        request.EscalatedToRole,
        request.Priority,
        request.CreatedAtUtc,
        request.CompletedAtUtc,
        request.Decisions.OrderBy(x => x.StepOrder).Select(x => x.ToDto()).ToList());

    public static ApprovalDecisionDto ToDto(this ApprovalDecision decision) => new(
        decision.Id,
        decision.StepOrder,
        decision.Decision,
        decision.Comments,
        decision.DecidedByUserId,
        decision.DecidedAtUtc);
}
