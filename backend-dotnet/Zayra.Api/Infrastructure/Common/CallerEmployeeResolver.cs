using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Common;

/// <summary>
/// Which employee record the signed-in caller IS: the <c>employee_id</c> claim, which the token service
/// issues only from the explicit login-to-employee link (EmployeeUserAccounts), and only while that
/// employee exists, is not deleted and belongs to the caller's tenant. One lookup for the data scope,
/// self-service, mobile, notifications and performance, so they cannot disagree about who the caller is.
///
/// <para>There is deliberately NO email fallback. Matching the login's email against an employee's work or
/// personal email let an email binding stand in for the link, and an employee's personal email can be
/// changed through an approved self-service profile request, so it could bind a different login to that
/// record. A login without the link is linked by HR (User Management → Invite employee, or the user's Link to employee record — EssLinkGuidance), not guessed.</para>
/// </summary>
public static class CallerEmployeeResolver
{
    /// <param name="tenantId">The caller's tenant (from their token); an employee of any other tenant never resolves.</param>
    /// <param name="requireActive">Also require the employee's status to be Active (mobile mutations).</param>
    public static async Task<int?> ResolveAsync(
        ZayraDbContext db, ClaimsPrincipal caller, Guid tenantId, CancellationToken ct, bool requireActive = false)
    {
        if (!int.TryParse(caller.FindFirstValue("employee_id"), out var claimed)) return null;

        var exists = await db.Employees.AsNoTracking().AnyAsync(e =>
            e.Id == claimed && e.TenantId == tenantId && !e.IsDeleted
            && (!requireActive || e.Status == EmployeeStatuses.Active), ct);
        return exists ? claimed : null;
    }
}

/// <summary>
/// What Self-Service tells a login that has no employee link: where HR fixes it. "Invite employee" is on the
/// User Management toolbar for every administrator; a login's own "Link to employee record" is on its row.
/// </summary>
public static class EssLinkGuidance
{
    public const string En =
        "Your login is not linked to an employee record. Ask HR to link it in User Management → Invite employee (or the user's Link to employee record).";

    public const string Ar =
        "حسابك غير مرتبط بسجل موظف. اطلب من الموارد البشرية ربطه من إدارة المستخدمين ← دعوة موظف (أو «ربط بسجل موظف» من صف المستخدم).";

    /// <summary>The Arabic for <paramref name="message"/> when it is this guidance; null for any other message.</summary>
    public static string? ArabicFor(string? message) => string.Equals(message, En, StringComparison.Ordinal) ? Ar : null;
}
