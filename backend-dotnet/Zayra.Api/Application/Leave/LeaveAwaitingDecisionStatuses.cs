namespace Zayra.Api.Application.Leave;

/// <summary>
/// THE rule for which leave statuses mean "someone still has to decide this". Sibling of
/// <see cref="LeaveTakenStatuses"/> (which statuses count as leave taken).
///
/// What LeaveService actually SAVES: a new request is <c>PendingManagerApproval</c> or <c>PendingHRApproval</c> (and stays
/// one of the two while a multi-step chain advances); an approved leave whose employee asked to cancel it is
/// <c>CancellationRequested</c>. <c>Submitted</c> is still accepted by the decision endpoints for older rows but is never
/// written now, and "Pending" is neither written nor decidable. Counters that compared to "Submitted"/"Pending" only read
/// 0 for every real request, and each calendar view spelled its own list. Spell it here, once.
/// </summary>
public static class LeaveAwaitingDecisionStatuses
{
    public const string Submitted = "Submitted";
    public const string PendingManagerApproval = "PendingManagerApproval";
    public const string PendingHRApproval = "PendingHRApproval";
    public const string CancellationRequested = LeaveTakenStatuses.CancellationRequested;

    /// <summary>A new request still waiting for its decision (what "leave requests pending" counts and lists).</summary>
    public static readonly string[] NewRequests = [Submitted, PendingManagerApproval, PendingHRApproval];

    /// <summary>Everything an approver's queue holds: new requests plus cancellations awaiting a decision.</summary>
    public static readonly string[] All = [Submitted, PendingManagerApproval, PendingHRApproval, CancellationRequested];

    /// <summary>
    /// What the leave calendar shows: leave taken (including leave whose cancellation is pending, which is still leave
    /// until the cancellation is approved) and requests still waiting for a decision.
    /// </summary>
    public static readonly string[] ShownOnCalendar = [.. LeaveTakenStatuses.All, .. NewRequests];

    public static bool IsNewRequest(string? status) => status is Submitted or PendingManagerApproval or PendingHRApproval;
}
