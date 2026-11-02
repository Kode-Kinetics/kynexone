using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Approvals;

/// <summary>
/// "Is there anyone who COULD approve this?", asked up front, before a maker stages something only a second person can
/// decide (a salary change, a salary CSV, a bonus batch, a requisition). A tenant whose only Finance person is the maker
/// used to stage the work and find out at approval time, with a queue nobody could drain. Now the maker is told at once, with
/// a stable <see cref="ErrorCode"/> and a bilingual message that says what to do: add a second person.
///
/// <para>Eligible = an active login, in this tenant, holding the permission by the SAME resolver token issuance uses
/// (<see cref="AuthService.GetPermissions"/>: role grants, access-mode grants, Allow overrides, minus Deny overrides), who is
/// neither the maker nor any login of a subject employee, and whose entity scope covers the company (group-scope, an
/// all-companies grant, or a grant for that company). One batched, split read; no query per candidate.</para>
/// </summary>
public static class SecondApproverCheck
{
    public const string ErrorCode = "no_eligible_approver";

    public static async Task<bool> ExistsAsync(ZayraDbContext db, Guid tenantId, string permission, Guid? makerUserId,
        IReadOnlyCollection<int>? subjectEmployeeIds, Guid? companyId, CancellationToken ct,
        IReadOnlyCollection<Guid>? alsoExcludeUserIds = null)
    {
        var excluded = new HashSet<Guid>(alsoExcludeUserIds ?? Array.Empty<Guid>());
        if (makerUserId is Guid maker) excluded.Add(maker);
        foreach (var employeeId in subjectEmployeeIds ?? Array.Empty<int>())
        {
            foreach (var id in await ApprovalUnblock.SubjectUserIdsAsync(db, tenantId, employeeId, ct)) excluded.Add(id);
            foreach (var id in await db.EmployeeUserAccounts.AsNoTracking()
                         .Where(l => l.TenantId == tenantId && l.EmployeeId == employeeId && l.UserId != null)
                         .Select(l => l.UserId!.Value).ToListAsync(ct)) excluded.Add(id);
        }
        var excludedIds = excluded.ToArray();
        var users = await db.Users.AsNoTracking().AsSplitQuery()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(rp => rp.Permission)
            .Include(u => u.EmployeeUserAccounts)
            .Include(u => u.PermissionOverrides)
            .Include(u => u.EntityAccesses)
            .Where(u => u.TenantId == tenantId && u.IsActive && !u.IsDeleted && !excludedIds.Contains(u.Id))
            .ToListAsync(ct);
        return users.Any(u => AuthService.GetPermissions(u).Contains(permission, StringComparer.OrdinalIgnoreCase) && InScope(u, companyId));
    }

    private static bool InScope(Domain.Entities.User user, Guid? companyId)
    {
        if (companyId is null || user.IsGroupScope) return true;
        return user.EntityAccesses.Any(a => a.IsActive
            && (!string.Equals(a.GrantMode, EntityGrantModes.SelectedCompanies, StringComparison.OrdinalIgnoreCase) || a.CompanyId == companyId));
    }

    /// <summary>The words for a Finance approver: "Finance approval rights (payroll.approve)".</summary>
    public static (string En, string Ar) FinanceRights(string permission) =>
        ($"Finance approval rights ({permission})", $"صلاحية الموافقة المالية ({permission})");

    /// <summary>The 422 body: <c>{ error, code, permission, message, messageAr }</c>. <paramref name="actionEn"/> completes
    /// "Add a second person with … before ___" in each language, e.g. "proposing salary changes".</summary>
    public static Refused Refusal(string permission, string actionEn, string actionAr, (string En, string Ar)? approver = null)
    {
        var who = approver ?? FinanceRights(permission);
        return new Refused(ErrorCode, ErrorCode, permission, MessageEn(who.En, actionEn),
            $"أضف شخصاً ثانياً يملك {who.Ar} قبل {actionAr}. لا يوجد من يستطيع الموافقة عليه غيرك، وسيبقى معلقاً دون قرار.");
    }

    /// <summary>The refusal body (lower-case names: it is serialised as is).</summary>
    public sealed record Refused(string error, string code, string permission, string message, string messageAr);

    internal static string MessageEn(string whoEn, string actionEn) =>
        $"Add a second person with {whoEn} before {actionEn}. Nobody else can approve it, so it would wait forever.";

    /// <summary>Thrown by service-layer code that must refuse before it writes; controllers render <see cref="Body"/> as a 422.</summary>
    public sealed class NoEligibleApproverException(string permission, string actionEn, string actionAr)
        : InvalidOperationException(MessageEn(FinanceRights(permission).En, actionEn))
    {
        public Refused Body { get; } = Refusal(permission, actionEn, actionAr);
    }

    /// <summary>422 <see cref="Refusal"/> when nobody else can approve; null when someone can.</summary>
    public static async Task<IActionResult?> RefuseIfNoneAsync(ZayraDbContext db, Guid tenantId, string permission, Guid? makerUserId,
        IReadOnlyCollection<int>? subjectEmployeeIds, Guid? companyId, string actionEn, string actionAr, CancellationToken ct,
        IReadOnlyCollection<Guid>? alsoExcludeUserIds = null)
        => await ExistsAsync(db, tenantId, permission, makerUserId, subjectEmployeeIds, companyId, ct, alsoExcludeUserIds)
            ? null
            : new UnprocessableEntityObjectResult(Refusal(permission, actionEn, actionAr));
}
