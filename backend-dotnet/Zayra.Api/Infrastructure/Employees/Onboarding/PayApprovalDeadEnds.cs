using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// Finds pending pay packages of one company that no usable reviewer may decide (every reviewer who could sign in and
/// approve is barred from that package), and keeps one admin notification per recipient in step with the result.
/// Read-only with respect to the approval itself: nothing here approves, rejects or clears a package; only
/// <c>PayApprovalsController.Decide</c> does that. The notification closes by itself once the gap is gone.
/// </summary>
public static class PayApprovalDeadEnds
{
    public const int MaxChecked = 100;
    public const string EntityName = "PayApprovalDeadEnd";
    public const string OpenStatus = "Unread";
    public const string ResolvedStatus = "Resolved";
    public const string OpenedAction = "company.pay_review_dead_end_opened";
    public const string ResolvedAction = "company.pay_review_dead_end_resolved";
    public const string SecurityManagePermission = "security.manage";

    public sealed record Evaluation(IReadOnlyList<int> StrandedEmployeeIds, int StrandedCount, bool HasMore, int PendingCount);

    /// <summary>The same candidate set as the pay-approval setup view, bounded at <see cref="MaxChecked"/> packages.</summary>
    public static async Task<Evaluation> EvaluateCompanyAsync(
        ZayraDbContext db, Guid tenantId, Guid companyId, CancellationToken ct, IConfiguration? config = null)
    {
        var operators = await PayApprovalRouting.UsableOperatorsAsync(db, tenantId, companyId, ct, config);
        var reviewers = operators.Where(o => o.CanApprovePay).ToArray();
        var candidates = await ScopedBypass.NullableTenantWide(db.Employees, tenantId,
                "Pay approval dead ends: pending employees of one company; tenant and company pinned.").AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.CompanyId == companyId && !e.IsDeleted
                && (e.Status == EmployeeStatuses.Draft || e.Status == EmployeeStatuses.Invited
                    || db.EmployeeImportGaps.Any(g => g.TenantId == tenantId && g.EmployeeId == e.Id
                        && g.GapType == CompensationApprovalGate.GapType && g.ResolvedAtUtc == null)))
            .OrderBy(e => e.Id).ToListAsync(ct);
        var pending = new List<Employee>();
        foreach (var candidate in candidates)
            if (await CompensationApprovalGate.NeedsApprovalAsync(db, tenantId, candidate, ct)) pending.Add(candidate);
        var stranded = new List<int>();
        foreach (var employee in pending.Take(MaxChecked))
        {
            var anyEligible = false;
            foreach (var reviewer in reviewers)
                if (!await CompensationApprovalGate.IsBarredAsync(db, tenantId, employee, reviewer.Id, ct)) { anyEligible = true; break; }
            if (!anyEligible) stranded.Add(employee.Id);
        }
        return new Evaluation(stranded, stranded.Count, pending.Count > MaxChecked, pending.Count);
    }

    /// <summary>
    /// Opens, refreshes or closes the admin notification for the company. Best effort: a failure is logged and never
    /// reaches the caller, and anything this call staged is discarded rather than left for the caller's next save.
    /// </summary>
    public static async Task SyncTaskAsync(
        ZayraDbContext db, Guid tenantId, Guid companyId, Guid? actorUserId, string trigger, RequestContext? context,
        ILogger? logger, CancellationToken ct, IConfiguration? config = null)
    {
        var staged = new List<object>();
        try
        {
            var result = await EvaluateCompanyAsync(db, tenantId, companyId, ct, config);
            var entityId = companyId.ToString();
            var open = await db.Notifications
                .Where(n => n.TenantId == tenantId && n.EntityName == EntityName && n.EntityId == entityId && n.Status != ResolvedStatus)
                .ToListAsync(ct);
            var now = DateTime.UtcNow;
            var audit = context ?? new RequestContext(null, null, actorUserId, tenantId);

            if (result.StrandedCount == 0)
            {
                if (open.Count == 0) return;
                foreach (var n in open) { n.Status = ResolvedStatus; n.ReadAtUtc ??= now; }
                var row = AuditRow(ResolvedAction, tenantId, companyId, audit, trigger, result, now);
                db.AuditLogs.Add(row); staged.Add(row);
                await db.SaveChangesAsync(ct);
                return;
            }

            var recipients = await RecipientsAsync(db, tenantId, companyId, ct);
            if (recipients.Count == 0 && actorUserId is Guid actor) recipients.Add(actor);
            var message = Message(result, companyId);
            var title = "Pay approval has no available reviewer";
            var changed = false;
            foreach (var n in open.Where(n => n.UserId is null || !recipients.Contains(n.UserId.Value)))
            { n.Status = ResolvedStatus; n.ReadAtUtc ??= now; changed = true; }
            foreach (var recipient in recipients.OrderBy(r => r))
            {
                var existing = open.FirstOrDefault(n => n.UserId == recipient);
                if (existing is null)
                {
                    var created = new Notification
                    {
                        TenantId = tenantId, UserId = recipient, Title = title, Message = message,
                        EntityName = EntityName, EntityId = entityId, CreatedAtUtc = now,
                    };
                    db.Notifications.Add(created); staged.Add(created); changed = true;
                }
                else if (existing.Message != message || existing.Title != title)
                {
                    existing.Message = message; existing.Title = title; changed = true;
                }
            }
            if (open.Count == 0)
            {
                var row = AuditRow(OpenedAction, tenantId, companyId, audit, trigger, result, now);
                db.AuditLogs.Add(row); staged.Add(row);
            }
            if (changed || staged.Count > 0) await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Discard(db, staged);
        }
        catch (Exception ex)
        {
            Discard(db, staged);
            logger?.LogWarning(ex, "Pay approval dead-end sync failed for company {CompanyId} (trigger {Trigger}).", companyId, trigger);
        }
    }

    /// <summary>Counts only: no names, figures or account details.</summary>
    internal static string Message(Evaluation r, Guid companyId)
    {
        var count = r.HasMore ? $"{r.StrandedCount} or more" : r.StrandedCount.ToString(CultureInfo.InvariantCulture);
        return $"{count} pending pay package(s) in this company have no eligible reviewer. "
            + "Add or enable a Finance approver who did not enter or receive the pay, then they can review. "
            + $"Open {Link(companyId)}";
    }

    private static string Link(Guid companyId) => $"/setup?tab=users&companyId={companyId}";

    private static AuditLog AuditRow(string action, Guid tenantId, Guid companyId, RequestContext context, string trigger, Evaluation r, DateTime now)
    {
        var row = AuthAuditEntry.Create(Guid.NewGuid(), now, action, "Company", companyId.ToString(), context with { TenantId = tenantId },
            JsonSerializer.Serialize(new { trigger, strandedCount = r.StrandedCount, pendingCount = r.PendingCount, hasMore = r.HasMore }));
        row.CompanyId = companyId;
        return row;
    }

    private static void Discard(ZayraDbContext db, List<object> staged)
    {
        foreach (var e in staged)
        {
            var entry = db.Entry(e);
            if (entry.State == EntityState.Added) entry.State = EntityState.Detached;
        }
    }

    /// <summary>Active tenant admins and security.manage holders who can reach the company.</summary>
    private static async Task<HashSet<Guid>> RecipientsAsync(ZayraDbContext db, Guid tenantId, Guid companyId, CancellationToken ct)
    {
        const string why = "Pay approval dead ends: resolve who administers access for this company; tenant pinned, scope resolved per user.";
        var users = await ScopedBypass.TenantWide(db.Users, tenantId, why).AsNoTracking()
            .Where(u => u.IsActive && !u.IsDeleted && u.Status == "Active")
            .Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r!.RolePermissions).ThenInclude(p => p.Permission)
            .Include(u => u.PermissionOverrides).Include(u => u.EntityAccesses)
            .AsSplitQuery().ToListAsync(ct);
        var companies = await ScopedBypass.TenantWide(db.Companies, tenantId, why).AsNoTracking()
            .Where(c => c.IsActive && !c.IsDeleted).Select(c => c.Id).ToListAsync(ct);
        var result = new HashSet<Guid>();
        foreach (var user in users)
        {
            if (!PrivilegeCeilingGraph.HoldsAdmin(user, tenantId) && !AuthService.GetPermissions(user).Contains(SecurityManagePermission)) continue;
            var scope = EntityScopeClaims.Resolve(user.IsGroupScope,
                user.EntityAccesses.Where(g => g.IsActive).Select(g => new EntityAccessGrant(g.CompanyId, g.Role, g.GrantMode)).ToArray(), companies);
            if (scope.Mode == EntityScopeModes.Group || scope.CompanyIds.Contains(companyId)) result.Add(user.Id);
        }
        return result;
    }
}
