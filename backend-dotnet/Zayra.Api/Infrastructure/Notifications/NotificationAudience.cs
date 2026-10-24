using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Notifications;

/// <summary>
/// Who a notice is FOR. A notification row with no user is a broadcast: <c>NotificationsController.Recent</c>
/// shows it to every login in the tenant, self-service employees included. A notice about one person (their
/// leave, an attendance correction, a candidate, a new hire, a transfer, a document) goes to the people
/// involved in it: the employee's own login and whoever has to act. When nobody can be resolved, nothing is
/// sent; it never falls back to the whole workspace.
/// </summary>
public static class NotificationAudience
{
    /// <summary>
    /// Entity types whose notices are about one person or that person's HR record. ONE list, read by three
    /// places so they cannot disagree: <see cref="NotificationService"/> refuses to write a broadcast of one,
    /// <c>NotificationsController.Recent</c> hides legacy broadcast rows of one written before routing, and
    /// <c>NotificationAudienceRatchetTests</c> refuses a new null-user <c>NotifyAsync</c> for one.
    /// </summary>
    public static readonly IReadOnlyList<string> PersonSpecificEntityTypes =
    [
        nameof(LeaveRequest),
        nameof(AttendanceRegularizationRequest),
        // Recruitment: candidates, their interviews and offers, and the hiring request behind them.
        nameof(JobApplication),
        nameof(InterviewSchedule),
        nameof(OfferLetter),
        nameof(Candidate),
        nameof(ManpowerRequisition),
        // An employee's record and the changes, drafts, transfers, documents and loans made to it.
        nameof(Employee),
        nameof(EmployeeDraft),
        nameof(EmployeeTransferRequest),
        nameof(EmployeeChangeRequest),
        nameof(EmployeeDocument),
        nameof(EmployeeLoan),
        // An approval is always about its subject (a sensitive change lists the fields being changed).
        nameof(ApprovalRequest),
    ];

    public static bool IsPersonSpecific(string? entityName) =>
        !string.IsNullOrWhiteSpace(entityName)
        && PersonSpecificEntityTypes.Contains(entityName.Trim(), StringComparer.Ordinal);

    /// <summary>A role queue is told by bell to at most this many people; the Approval Center lists the rest.</summary>
    public const int MaxQueueRecipients = 20;

    /// <summary>
    /// Every login linked to an employee: the explicit link (EmployeeUserAccounts, which the employee_id claim is
    /// issued from) and the legacy <c>Employee.UserAccountId</c>. Empty for an employee with no login.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> EmployeeLoginIdsAsync(ZayraDbContext db, Guid tenantId, int employeeId,
        CancellationToken ct)
    {
        var linked = await db.EmployeeUserAccounts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId && x.UserId != null)
            .Select(x => x.UserId!.Value)
            .ToListAsync(ct);
        var legacy = await db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted && e.UserAccountId != null)
            .Select(e => e.UserAccountId!.Value)
            .ToListAsync(ct);
        return linked.Concat(legacy).Distinct().ToList();
    }

    /// <summary>
    /// The active logins that work a ROLE queue (an approval step routed to a role rather than a named person),
    /// limited to those whose company access covers <paramref name="companyId"/>. Mirrors the role mapping the
    /// leave screen decides with: blank is the HR Manager queue, "HR" is HR Manager or HR Officer.
    ///
    /// <para>Reporting-line queues (Manager, Supervisor, Department head) return nobody. The router names the
    /// person for those steps whenever one exists; a role-wide fan-out would tell every manager in the company
    /// about one employee's request.</para>
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> QueueHoldersAsync(ZayraDbContext db, Guid tenantId, string? routedRole,
        Guid? companyId, CancellationToken ct)
    {
        var role = (routedRole ?? string.Empty).Trim();
        if (role.StartsWith("Role:", StringComparison.OrdinalIgnoreCase)) role = role["Role:".Length..].Trim();
        string[] roles = role.ToUpperInvariant() switch
        {
            "" => ["HR Manager"],
            "HR" or "HRBUSINESSPARTNER" => ["HR Manager", "HR Officer"],
            "MANAGER" or "DIRECTMANAGER" or "SUPERVISOR" or "DEPARTMENTHEAD" or "ANY" => [],
            _ => [role],
        };
        if (roles.Length == 0) return [];
        var normalized = roles.Select(r => r.ToUpperInvariant()).ToArray();

        return await (
                from user in db.Users.AsNoTracking()
                join assignment in db.UserRoles.AsNoTracking() on user.Id equals assignment.UserId
                join r in db.Roles.AsNoTracking() on assignment.RoleId equals r.Id
                where user.TenantId == tenantId && user.IsActive && !user.IsDeleted
                      && (r.TenantId == tenantId || r.TenantId == null) && r.IsActive && !r.IsDeleted
                      && normalized.Contains(r.NormalizedName)
                      && (user.IsGroupScope || (companyId != null && db.UserEntityAccesses.Any(x =>
                          x.TenantId == tenantId && x.UserId == user.Id && x.IsActive
                          && (x.CompanyId == companyId || x.GrantMode == "AllCurrentCompanies" || x.GrantMode == "AllCurrentAndFutureCompanies"))))
                select user.Id)
            .Distinct()
            .OrderBy(id => id)
            .Take(MaxQueueRecipients)
            .ToListAsync(ct);
    }

    /// <summary>Who must act on an approval now: its named approver, else the holders of its role queue.</summary>
    public static async Task<IReadOnlyList<Guid>> ApprovalDecidersAsync(ZayraDbContext db, ApprovalRequest approval,
        CancellationToken ct)
    {
        if (approval.CurrentApproverUserId is Guid named && named != Guid.Empty) return [named];
        var queue = string.IsNullOrWhiteSpace(approval.CurrentApproverRole) ? approval.CurrentQueue : approval.CurrentApproverRole;
        return await QueueHoldersAsync(db, approval.TenantId, queue, approval.CompanyId, ct);
    }

    /// <summary>
    /// One addressed notice per distinct recipient. Sends nothing when there is nobody: never a broadcast.
    /// Returns how many were addressed.
    /// </summary>
    public static async Task<int> NotifyEachAsync(this INotificationService notifications, Guid tenantId,
        IEnumerable<Guid?> recipients, string title, string message, string entityName, string? entityId,
        CancellationToken ct)
    {
        var distinct = recipients
            .Where(x => x is Guid id && id != Guid.Empty)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();
        foreach (var userId in distinct)
            await notifications.NotifyAsync(tenantId, userId, title, message, entityName, entityId, ct);
        return distinct.Count;
    }
}
