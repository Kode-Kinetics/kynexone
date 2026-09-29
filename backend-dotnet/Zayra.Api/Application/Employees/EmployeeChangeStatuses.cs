namespace Zayra.Api.Application.Employees;

/// <summary>
/// The <see cref="Zayra.Api.Models.EmployeeChangeRequest.Status"/> values. The column is free text, and
/// older code still spells these inline; new code uses the constants so a typo cannot create a status
/// that no query looks for (which is how an approved future-dated change was left waiting for ever).
/// </summary>
public static class EmployeeChangeStatuses
{
    public const string PendingApproval = "PendingApproval";

    /// <summary>
    /// Approved, but its effective date had not arrived at approval time. The effective-change job
    /// (<c>employee.effective-changes</c>) applies it once the date arrives in the tenant's timezone,
    /// or returns it for review when the record has moved on since it was approved.
    /// </summary>
    public const string ApprovedPendingEffectiveDate = "ApprovedPendingEffectiveDate";

    public const string ApprovedApplied = "ApprovedApplied";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";

    /// <summary>
    /// Closed without being applied: its approval-time values could not be verified and it was older than
    /// <c>EmployeeEffectiveChanges:UnverifiableMaxAgeDays</c>. Terminal; the change must be submitted again.
    /// </summary>
    public const string Expired = "Expired";
}
