namespace Zayra.Api.Application.Leave;

/// <summary>
/// THE rule for whether a leave request counts as leave TAKEN: approved, or approved and awaiting a decision on the
/// employee's request to cancel it. A cancellation request does not undo the leave until it is approved, so until then
/// the days are leave for attendance (an untouched day is "On leave", never Absent), for rosters and self-service, and
/// for the leave and analytics reports. Matching "Approved" alone made a leave vanish from all of them the moment its
/// cancellation was requested, and an untouched day became an Absent day with a 480-minute deduction.
/// </summary>
public static class LeaveTakenStatuses
{
    public const string Approved = "Approved";
    public const string CancellationRequested = "CancellationRequested";

    /// <summary>The statuses that count as taken; use with <c>All.Contains(x.Status)</c> in a query.</summary>
    public static readonly string[] All = [Approved, CancellationRequested];

    public static bool CountsAsTaken(string? status) => status is Approved or CancellationRequested;
}
