using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Approvals;

/// <summary>
/// "The subject never decides", for decisions taken outside the shared approval workflow and the
/// <see cref="Application.Approvals.ApprovalDecisionGuard"/> modules: an attendance correction, an
/// overtime request, a profile change, an HR letter. The person a record is about may ask for it; they
/// may never approve, reject or issue it, whatever their role. Every login linked to the subject employee
/// is barred, read tenant-wide through <see cref="ApprovalUnblock.SubjectUserIdsAsync"/>, the same lookup
/// the Approval Center and leave use, so a move to another legal entity cannot open a gap.
///
/// <para>A refusal is a 400, as every other separation-of-duties refusal is, with a stable
/// <see cref="ErrorCode"/> so clients and tests can tell it apart from a validation error.</para>
/// </summary>
public static class SubjectDecisionBar
{
    public const string ErrorCode = "subject_cannot_decide";

    /// <summary>True when the caller is one of the logins linked to the subject employee. A caller with no
    /// user id is treated as the subject: refusing is the safe answer when we cannot tell.</summary>
    public static async Task<bool> CallerIsSubjectAsync(
        ZayraDbContext db, Guid tenantId, Guid? callerUserId, int subjectEmployeeId, CancellationToken ct)
    {
        if (callerUserId is not Guid uid || uid == Guid.Empty) return true;
        return (await ApprovalUnblock.SubjectUserIdsAsync(db, tenantId, subjectEmployeeId, ct)).Contains(uid);
    }

    /// <summary>The response body for a refused decision: <c>{ error, message }</c>.</summary>
    public static object Refusal(string message) => new { error = ErrorCode, message };
}
