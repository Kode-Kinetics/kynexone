using System.ComponentModel.DataAnnotations;

namespace Zayra.Api.Application.Approvals;

// ── W2-E: send back, resubmission, the distinct-approver rule and workflow configuration ─────────

/// <summary>Statuses an <see cref="Models.ApprovalRequest"/> can hold besides Pending/Approved/Rejected/Cancelled.</summary>
public static class ApprovalStatuses
{
    public const string Pending = "Pending";

    /// <summary>
    /// An approver sent the request back to its requester for changes. It is out of every approver
    /// queue, any reserved balance is released, and the requester can edit and resubmit it. A
    /// resubmission starts a new <see cref="Models.ApprovalRequest.SubmissionRound"/> at step 1.
    /// </summary>
    public const string ReturnedToRequester = "ReturnedToRequester";

    /// <summary>The <see cref="Models.ApprovalDecision.Decision"/> value recorded for a send back.</summary>
    public const string SentBackDecision = "SentBack";

    /// <summary>Notification event code sent to the requester when their request is sent back.</summary>
    public const string SentBackEventCode = "approval.sent_back";
}

/// <summary>Body of <c>POST /api/approval-requests/{id}/send-back</c>. The comment is required: the requester has to know what to fix.</summary>
public record SendBackApprovalRequest(
    [Required(AllowEmptyStrings = false, ErrorMessage = "A comment explaining what to change is required.")]
    [StringLength(1000, MinimumLength = 1, ErrorMessage = "Comments must be between 1 and 1000 characters.")]
    string Comments);

/// <summary>
/// Body of <c>POST /api/approval-requests/{id}/resubmit</c>. <paramref name="Leave"/> carries optional
/// edits when the source is a leave request; omitted fields keep their current value.
/// </summary>
public record ResubmitApprovalRequest(
    [MaxLength(1000)] string? Comments = null,
    LeaveResubmitChanges? Leave = null);

public record LeaveResubmitChanges(
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    [MaxLength(20)] string? DayType = null,
    [Range(0, 8)] decimal? HoursRequested = null,
    [MaxLength(2000)] string? Reason = null);

/// <summary>Per-tenant approval governance settings (stored as SystemSettings, Category "Approvals").</summary>
public record ApprovalGovernanceSettingsDto(bool RequireDistinctApproverPerStep);

/// <summary>An entity type the approval router can route, with an honest note on who consumes it today.</summary>
public record ApprovalEntityDescriptor(string EntityName, string Label, bool EnforcedByModule, string Note);

public record ApprovalRoutePreviewDto(
    string EntityName,
    int? EmployeeId,
    string EmployeeName,
    string Outcome, // Routed | NotConfigured | Invalid
    string? ErrorCode,
    string? Message,
    Guid? WorkflowId,
    string? WorkflowCode,
    string? WorkflowName,
    string? MatchedOn,
    bool RequireDistinctApproverPerStep,
    IReadOnlyList<ApprovalRoutePreviewStepDto> Steps,
    IReadOnlyList<string> Warnings);

public record ApprovalRoutePreviewStepDto(
    int StepOrder,
    string StepName,
    string ApproverType,
    string ApproverRole,
    bool IsFinalStep,
    int? EscalationAfterHours,
    string QueueRole, // the role queue that acts when no concrete person resolved (or the person's role label)
    int? ApproverEmployeeId,
    Guid? ApproverUserId,
    string ApproverName,
    bool Escalated);

/// <summary>Base for the W2-E typed approval errors. Subclasses InvalidOperationException so every
/// pre-existing catch site still turns it into a 4xx instead of a 500.</summary>
public abstract class ApprovalActionException : InvalidOperationException
{
    protected ApprovalActionException(string code, string message) : base(message) => Code = code;

    /// <summary>Stable machine-readable error code for API clients.</summary>
    public string Code { get; }
}

/// <summary>The request is not in a state that allows the action (e.g. send back after completion). HTTP 409.</summary>
public sealed class ApprovalStateConflictException : ApprovalActionException
{
    public const string ErrorCode = "approval_state_conflict";
    public ApprovalStateConflictException(string message) : base(ErrorCode, message) { }
}

/// <summary>The caller may not take this action on this request (not the current approver, not the requester). HTTP 403.</summary>
public sealed class ApprovalNotPermittedException : ApprovalActionException
{
    public const string ErrorCode = "approval_not_permitted";
    public ApprovalNotPermittedException(string message) : base(ErrorCode, message) { }
}

/// <summary>
/// The tenant requires a different person at each step, and the caller already decided an earlier
/// step of this submission. HTTP 403. The request is untouched and stays in the queue for another
/// eligible approver.
/// </summary>
public sealed class ApprovalDistinctApproverException : ApprovalActionException
{
    public const string ErrorCode = "approval_distinct_approver_required";

    public ApprovalDistinctApproverException(int earlierStep, int currentStep)
        : base(ErrorCode,
            $"This organisation requires a different person at each approval step. You decided step {earlierStep} of this request, " +
            $"so step {currentStep} must be decided by another eligible approver. The request stays in the queue for them.")
    {
        EarlierStep = earlierStep;
        CurrentStep = currentStep;
    }

    public int EarlierStep { get; }
    public int CurrentStep { get; }
}

/// <summary>A workflow create/update was refused by a configuration rule. HTTP 400 with <see cref="ApprovalActionException.Code"/>.</summary>
public sealed class ApprovalWorkflowValidationException : ApprovalActionException
{
    public const string ScopeOverlap = "workflow_scope_overlap";
    public const string NoFinalStep = "workflow_no_final_step";
    public const string MultipleFinalSteps = "workflow_multiple_final_steps";
    public const string FinalStepNotLast = "workflow_final_step_not_last";
    public const string DuplicateStepOrder = "workflow_duplicate_step_order";
    public const string InvalidApproverType = "workflow_invalid_approver_type";
    public const string SpecificEmployeeRequired = "workflow_specific_employee_required";
    public const string RoleRequired = "workflow_role_required";
    public const string ScopeInvalid = "workflow_scope_invalid";
    public const string CodeTaken = "workflow_code_taken";
    public const string EntityRequired = "workflow_entity_required";

    public ApprovalWorkflowValidationException(string code, string message) : base(code, message) { }
}
