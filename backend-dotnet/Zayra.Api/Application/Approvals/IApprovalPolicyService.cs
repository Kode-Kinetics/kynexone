namespace Zayra.Api.Application.Approvals;

/// <summary>
/// Resolves the ordered list of approver employee IDs for a given employee and workflow type
/// by reading ApprovalPolicy + ApprovalPolicyStep records and walking the DB hierarchy.
/// </summary>
public interface IApprovalPolicyService
{
    /// <summary>
    /// Returns the matching policy's steps with concrete approver employee IDs resolved.
    /// Returns null if no active policy matches (caller should fall back to legacy role routing).
    /// </summary>
    Task<ResolvedApprovalPolicy?> ResolveAsync(
        Guid tenantId, int employeeId, string workflowType, CancellationToken ct);
}

public record ResolvedApprovalPolicy(
    Guid PolicyId,
    string PolicyName,
    IReadOnlyList<ResolvedApprovalStep> Steps);

public record ResolvedApprovalStep(
    int StepOrder,
    string StepName,
    string ApproverType,
    /// <summary>Null when ApproverType = "Role" or approver employee could not be resolved.</summary>
    int? ApproverEmployeeId,
    string? ApproverEmployeeName,
    bool IsFinalStep,
    string? ApproverRole = null);
