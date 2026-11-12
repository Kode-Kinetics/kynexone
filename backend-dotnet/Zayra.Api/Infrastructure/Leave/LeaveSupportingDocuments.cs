using System.Security.Claims;

namespace Zayra.Api.Infrastructure.Leave;

/// <summary>
/// A leave's supporting document (a sick note, often medical) is stored as an ordinary employee document of type
/// "Leave Attachment". The leave rule for it is <see cref="LeaveFileKeyExposure"/>'s: the employee it belongs to and
/// Admin / HR Manager / HR Officer. The HR employee-document endpoints are gated by employees.read / employees.documents,
/// which managers, auditors and a compliance officer hold, so those endpoints apply this rule to the type as well.
///
/// <para>This is deliberately NOT <c>RestrictedEmployeeDocumentTypes</c>: that set is HR evidence the employee may not see
/// or attach at all. A leave attachment is the employee's own upload and must stay listable, downloadable and attachable
/// in Self-Service.</para>
/// </summary>
public static class LeaveSupportingDocuments
{
    public const string DocumentType = "Leave Attachment";

    /// <summary>Lower-case spelling for query filters (compare <c>x.DocumentType.Trim().ToLower()</c>).</summary>
    public static readonly string Lowered = DocumentType.ToLowerInvariant();

    public static bool IsSupportingDocument(string? documentType) =>
        documentType is not null && string.Equals(documentType.Trim(), DocumentType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="user"/> may see a leave attachment of <paramref name="documentEmployeeId"/>.</summary>
    public static bool MayRead(ClaimsPrincipal user, int? callerEmployeeId, int? documentEmployeeId) =>
        (callerEmployeeId is int caller && documentEmployeeId == caller) || IsLeaveAdministrator(user);

    public static bool IsLeaveAdministrator(ClaimsPrincipal user) => LeaveFileKeyExposure.Roles.Any(user.IsInRole);
}
