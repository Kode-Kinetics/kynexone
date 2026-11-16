using System.Security.Claims;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Leave;

/// <summary>
/// Who is handed a leave request's attachment STORAGE KEY. The key is the handle to the employee's sick note or other
/// medical document. The leave List/Get returned the whole <see cref="LeaveRequest"/> entity, key included, to every
/// reader of the leave (a line manager, an organisation-wide reader), and the legacy ESS upload turned any tenant key
/// into a downloadable document of the caller's. The key is now shown only to the leave's own employee and to HR/Admin;
/// nobody's UI uses it (the web shows no attachment link), so nothing else changes.
/// </summary>
public static class LeaveFileKeyExposure
{
    /// <summary>The roles that administer leave and so may see an attachment key.</summary>
    public static readonly string[] Roles = ["Admin", "HR Manager", "HR Officer"];

    /// <param name="callerEmployeeId">
    /// The caller's own employee record when the data scope cannot say: an organisation-wide scope carries no
    /// <c>CallerEmployeeId</c>, which hid their OWN document from Payroll and Finance users. Resolve it with
    /// <see cref="Zayra.Api.Infrastructure.Common.CallerEmployeeResolver"/>.
    /// </param>
    public static bool MaySeeKey(ClaimsPrincipal user, DataScope scope, int leaveEmployeeId, int? callerEmployeeId = null) =>
        scope.CallerEmployeeId == leaveEmployeeId || callerEmployeeId == leaveEmployeeId || Roles.Any(user.IsInRole);

    /// <summary>
    /// The attachment column of the leave CSV export: the key for HR/Admin, empty for any other exporter (an Auditor reads
    /// the leave history, not the employees' medical documents).
    /// </summary>
    public static string KeyForExport(ClaimsPrincipal user, string? attachmentPath) =>
        Roles.Any(user.IsInRole) ? attachmentPath ?? string.Empty : string.Empty;

    /// <summary>
    /// The request as <paramref name="user"/> may see it. For a reader who may not see the key this is a DETACHED copy
    /// with the key blanked: the tracked entity is never changed, so a later SaveChanges can never erase the stored key.
    /// </summary>
    public static LeaveRequest ForReader(ZayraDbContext db, LeaveRequest request, ClaimsPrincipal user, DataScope scope, int? callerEmployeeId = null)
    {
        if (string.IsNullOrEmpty(request.AttachmentPath) || MaySeeKey(user, scope, request.EmployeeId, callerEmployeeId)) return request;
        var copy = (LeaveRequest)db.Entry(request).CurrentValues.ToObject();
        copy.AttachmentPath = string.Empty;
        return copy;
    }
}
