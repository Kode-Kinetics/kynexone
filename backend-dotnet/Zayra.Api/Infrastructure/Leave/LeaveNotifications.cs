using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Leave;

/// <summary>
/// Who is told about a leave request, and when. ONE place, called by every path that submits or decides leave
/// (the Leave screen, Self-Service, the cancellation endpoints), so the paths cannot disagree about who hears.
/// A notice about one person's leave goes to that person's logins and to whoever has to act on it; when nobody
/// can be resolved nothing is sent (never a workspace broadcast; see <see cref="NotificationAudience"/>).
/// </summary>
public static class LeaveNotifications
{
    /// <summary>The roles <c>POST /api/leave/requests/{id}/cancel-approve|cancel-reject</c> admits.</summary>
    public static readonly string[] CancellationDeciderRoles = ["Admin", "HR Manager", "Manager"];

    /// <summary>
    /// A new request: the employee, and whoever decides the first step. The Leave screen, the HR-on-behalf path
    /// and Self-Service all end here.
    /// </summary>
    public static async Task NotifySubmittedAsync(ZayraDbContext db, INotificationService notifications, Guid tenantId,
        LeaveRequest submitted, CancellationToken ct)
    {
        await notifications.NotifyEachAsync(tenantId,
            [.. await NotificationAudience.EmployeeLoginIdsAsync(db, tenantId, submitted.EmployeeId, ct),
             .. await PendingDecidersAsync(db, tenantId, submitted.Id, submitted.EmployeeId, ct)],
            "New Leave Request",
            $"{submitted.EmployeeName} submitted a {submitted.LeaveTypeName} request for {submitted.TotalDays} day(s).",
            nameof(LeaveRequest), submitted.Id.ToString(), ct);
    }

    /// <summary>
    /// Who decides the request's pending step: the named approver, else the holders of its role queue in the
    /// employee's company. Empty when nothing is pending, or for a reporting-line queue with nobody named.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> PendingDecidersAsync(ZayraDbContext db, Guid tenantId, Guid leaveRequestId,
        int employeeId, CancellationToken ct)
    {
        var pending = await db.LeaveApprovals.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.LeaveRequestId == leaveRequestId && a.Decision == "Pending")
            .OrderBy(a => a.StepNumber)
            .FirstOrDefaultAsync(ct);
        if (pending is null) return [];
        if (pending.ApproverId is Guid named && named != Guid.Empty) return [named];
        var companyId = await db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == employeeId)
            .Select(e => e.CompanyId)
            .FirstOrDefaultAsync(ct);
        return await NotificationAudience.QueueHoldersAsync(db, tenantId, pending.ApproverRole, companyId, ct);
    }

    /// <summary>
    /// The logins that may decide a cancellation of this employee's approved leave: exactly the callers
    /// <c>cancel-approve</c> / <c>cancel-reject</c> admit. They hold Admin, HR Manager or Manager, the employee is
    /// inside their data scope (resolved by the same <see cref="DataScopeService"/> rules from the login's
    /// effective permissions), and they are not the employee. A Manager therefore means the employee's own
    /// reporting line, not every manager in the company.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> CancellationDecidersAsync(ZayraDbContext db, Guid tenantId, int employeeId,
        CancellationToken ct)
    {
        var companyId = await db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == employeeId)
            .Select(e => e.CompanyId)
            .FirstOrDefaultAsync(ct);
        var subjectLogins = (await NotificationAudience.EmployeeLoginIdsAsync(db, tenantId, employeeId, ct)).ToHashSet();
        var candidates = (await NotificationAudience.RoleHoldersAsync(db, tenantId, CancellationDeciderRoles, companyId, take: 500, ct))
            .Where(id => !subjectLogins.Contains(id)).ToList();
        if (candidates.Count == 0) return [];

        var scopes = new DataScopeService(db);
        var deciders = new List<Guid>();
        foreach (var user in await PrivilegeCeilingGraph.LoadUsersAsync(db, tenantId, candidates, ct))
        {
            var link = user.EmployeeUserAccounts.Where(x => !x.IsDeleted)
                .OrderByDescending(x => x.IsPrimary).ThenByDescending(x => x.CreatedAtUtc).FirstOrDefault();
            var scope = await scopes.ResolveForPermissionsAsync(AuthService.GetPermissions(user), link?.EmployeeId, tenantId, ct);
            if (!scope.CanAccessEmployee(employeeId) || scope.CallerEmployeeId == employeeId) continue;
            deciders.Add(user.Id);
        }
        return deciders.Distinct().OrderBy(id => id).Take(NotificationAudience.MaxQueueRecipients).ToList();
    }

    /// <summary>The names (as the people know them) of the logins that now hold a cancellation.</summary>
    public static async Task<IReadOnlyList<string>> NamesAsync(ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Guid> userIds,
        CancellationToken ct) =>
        userIds.Count == 0
            ? []
            : await db.Users.AsNoTracking()
                .Where(u => u.TenantId == tenantId && userIds.Contains(u.Id))
                .Select(u => u.FullName).OrderBy(n => n).ToListAsync(ct);

    /// <summary>An employee asked to cancel approved leave: tell everyone who may decide it.</summary>
    public static async Task NotifyCancellationRequestedAsync(ZayraDbContext db, INotificationService notifications, Guid tenantId,
        LeaveRequest leave, string? reason, CancellationToken ct)
    {
        var why = string.IsNullOrWhiteSpace(reason) ? string.Empty : $" Reason: {reason.Trim()}";
        await notifications.NotifyEachAsync(tenantId,
            [.. await CancellationDecidersAsync(db, tenantId, leave.EmployeeId, ct)],
            "Leave Cancellation Requested",
            $"{leave.EmployeeName} asked to cancel approved {leave.LeaveTypeName} ({leave.StartDate:yyyy-MM-dd} to {leave.EndDate:yyyy-MM-dd}).{why}",
            nameof(LeaveRequest), leave.Id.ToString(), ct);
    }

    /// <summary>The outcome of a cancellation request, to the employee it is about.</summary>
    public static async Task NotifyCancellationDecidedAsync(ZayraDbContext db, INotificationService notifications, Guid tenantId,
        LeaveRequest leave, bool approved, string? notes, CancellationToken ct)
    {
        var note = string.IsNullOrWhiteSpace(notes) ? string.Empty : $" Note: {notes.Trim()}";
        await notifications.NotifyEachAsync(tenantId,
            [.. await NotificationAudience.EmployeeLoginIdsAsync(db, tenantId, leave.EmployeeId, ct)],
            approved ? "Leave Cancellation Approved" : "Leave Cancellation Declined",
            approved
                ? $"Your {leave.LeaveTypeName} ({leave.StartDate:yyyy-MM-dd} to {leave.EndDate:yyyy-MM-dd}) was cancelled and the days are back in your balance.{note}"
                : $"Your request to cancel {leave.LeaveTypeName} ({leave.StartDate:yyyy-MM-dd} to {leave.EndDate:yyyy-MM-dd}) was declined: the leave stays approved.{note}",
            nameof(LeaveRequest), leave.Id.ToString(), ct);
    }
}
