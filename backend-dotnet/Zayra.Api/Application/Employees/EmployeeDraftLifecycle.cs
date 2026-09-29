namespace Zayra.Api.Application.Employees;

/// <summary>
/// The states an <see cref="Zayra.Api.Models.EmployeeDraft"/> moves through on its way to becoming an
/// employee, and the one place that says which of them are final.
///
/// <para>Open: <c>Draft</c> (being prepared), <c>Submitted</c> (what offer acceptance wrote before it
/// learned to write <c>PendingHrApproval</c>; kept approvable so those hires are not stranded) and
/// <c>PendingHrApproval</c> (waiting for a checker).</para>
///
/// <para>Closed: <c>Activated</c> (the employee exists), <c>Rejected</c> (a checker refused the hire,
/// with a reason) and <c>Cancelled</c> (withdrawn). A closed draft never moves again: it cannot be
/// edited, resubmitted or approved. Before this rule an Activated draft could be resubmitted and
/// approved a second time, which created a second employee record for the same hire.</para>
/// </summary>
public static class EmployeeDraftStatuses
{
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
    public const string PendingHrApproval = "PendingHrApproval";
    public const string Activated = "Activated";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";

    /// <summary>States a draft can still leave. Approval, rejection and withdrawal all start here.</summary>
    public static readonly string[] Open = { Draft, Submitted, PendingHrApproval };

    /// <summary>States a submission moves into PendingHrApproval from.</summary>
    public static readonly string[] Submittable = { Draft, Submitted };

    /// <summary>States that wait on a checker: what the review screen shows first.</summary>
    public static readonly string[] AwaitingApproval = { Submitted, PendingHrApproval };

    public static readonly string[] Closed = { Activated, Rejected, Cancelled };

    public static bool IsOpen(string? status) => status is Draft or Submitted or PendingHrApproval;

    /// <summary>Why a draft in <paramref name="status"/> cannot be submitted, edited or approved, in
    /// words HR can act on.</summary>
    public static string ClosedMessage(string status, string? employeeCode = null) => status switch
    {
        Activated => string.IsNullOrWhiteSpace(employeeCode)
            ? "This hire has already been activated. A draft becomes an employee only once, so open the employee record instead."
            : $"This hire has already been activated as employee {employeeCode}. A draft becomes an employee only once, so open that employee record instead.",
        Rejected => "This draft was rejected, so it can't be submitted, changed or approved. Start a new draft if the hire should go ahead.",
        Cancelled => "This draft was withdrawn, so it can't be submitted, changed or approved. Start a new draft if the hire should go ahead.",
        _ => $"This draft is in an unexpected state ('{status}'), so it can't be submitted, changed or approved.",
    };
}

/// <summary>Audit actions written against a draft (EntityName "EmployeeDraft", EntityId the draft id).
/// They are the durable record of who decided and why: the draft row has no reason or decider columns.</summary>
public static class EmployeeDraftAuditActions
{
    public const string Submitted = "employee.draft_submitted";
    public const string Activated = "employee.draft_activated";
    public const string Rejected = "employee.draft_rejected";
    public const string Cancelled = "employee.draft_cancelled";

    public static readonly string[] Decisions = { Activated, Rejected, Cancelled };
}

public sealed record EmployeeDraftDecisionRequest(string? Reason);

/// <summary>One row of the draft review list: identity, placement and lifecycle only. Pay, bank and
/// identity-document values are deliberately absent, as they are from the employee list; the review
/// detail applies the sensitive-field mask.</summary>
public sealed record EmployeeDraftListItemDto(
    Guid Id,
    string Status,
    string CurrentStep,
    string Name,
    string ArabicName,
    string Department,
    string Designation,
    string Branch,
    DateTime? JoiningDate,
    string Source,
    Guid? ApplicationId,
    string? JobTitle,
    Guid? CreatedByUserId,
    string? CreatedByName,
    bool IsMine,
    bool CanApprove,
    string? ApproveBlockedReason,
    decimal ProfileCompletenessScore,
    DateTime CreatedAtUtc,
    DateTime? SubmittedAtUtc,
    DateTime? DecidedAtUtc,
    string? DecidedByName,
    string? DecisionReason,
    int? ActivatedEmployeeId,
    string? ActivatedEmployeeCode);

public sealed record EmployeeDraftStatusCounts(int AwaitingApproval, int Draft, int Activated, int Rejected, int Cancelled);

public sealed record EmployeeDraftListResponse(
    IReadOnlyCollection<EmployeeDraftListItemDto> Items, int Total, int Page, int PageSize, EmployeeDraftStatusCounts Counts);

/// <summary>One reason activation would be refused, found before anyone presses Approve.</summary>
public sealed record EmployeeDraftActivationProblem(string Key, string Label, string Reason, string Fix);

public sealed record EmployeeDraftActivationCheck(
    bool CanActivate,
    string? ResolvedCompanyName,
    IReadOnlyList<EmployeeDraftActivationProblem> Problems,
    IReadOnlyList<string> Advisories);

public sealed record EmployeeDraftReviewDto(
    EmployeeDraftListItemDto Summary,
    EmployeeDraftDto Draft,
    int DocumentCount,
    EmployeeDraftActivationCheck? ActivationCheck);
