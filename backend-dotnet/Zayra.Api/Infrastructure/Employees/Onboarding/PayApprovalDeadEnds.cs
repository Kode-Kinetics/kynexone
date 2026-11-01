using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Scope;
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

    /// <summary>Most candidate rows one evaluation reads; beyond this the result says <c>HasMore</c> rather than reading on.</summary>
    public const int MaxScanned = 1000;
    private const int PageSize = 200;

    /// <summary>Triggers after which the set of people who can be told cannot have changed, so the recipient lookup is skipped for an unchanged notice.</summary>
    private static readonly HashSet<string> StableRecipientTriggers = new(StringComparer.Ordinal)
    {
        "pay_submitted", "decision", "setup_view", "draft_discarded", "import",
    };

    /// <summary>
    /// The same candidate set as the pay-approval setup view, bounded at <see cref="MaxChecked"/> packages and
    /// <see cref="MaxScanned"/> candidate rows. Runs with full visibility of the one tenant (every read below pins the
    /// tenant and the company explicitly) so a caller whose own scope omits the company still sees that company's gaps.
    /// </summary>
    public static async Task<Evaluation> EvaluateCompanyAsync(
        ZayraDbContext db, Guid tenantId, Guid companyId, CancellationToken ct, IConfiguration? config = null)
    {
        // Company-scoped rows (the pay gaps) are hidden from a request principal whose scope omits this company; the
        // detection must see them, and nothing here writes or relaxes who may approve.
        using var system = SystemScopeContext.Begin();
        var operators = await PayApprovalRouting.UsableOperatorsAsync(db, tenantId, companyId, ct, config);
        var reviewers = operators.Where(o => o.CanApprovePay).ToArray();
        var candidates = ScopedBypass.NullableTenantWide(db.Employees, tenantId,
                "Pay approval dead ends: pending employees of one company; tenant and company pinned.").AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.CompanyId == companyId && !e.IsDeleted
                && (e.Status == EmployeeStatuses.Draft || e.Status == EmployeeStatuses.Invited
                    || db.EmployeeImportGaps.Any(g => g.TenantId == tenantId && g.EmployeeId == e.Id
                        && g.GapType == CompensationApprovalGate.GapType && g.ResolvedAtUtc == null)));
        var pending = new List<Employee>();
        var lastId = 0;
        var scanned = 0;
        var cutShort = false;
        while (pending.Count <= MaxChecked)
        {
            var lastSeen = lastId;
            var page = await candidates.Where(e => e.Id > lastSeen).OrderBy(e => e.Id).Take(PageSize).ToListAsync(ct);
            if (page.Count == 0) break;
            foreach (var candidate in page)
            {
                scanned++;
                lastId = candidate.Id;
                if (await CompensationApprovalGate.NeedsApprovalAsync(db, tenantId, candidate, ct)) pending.Add(candidate);
                if (pending.Count > MaxChecked) break;
            }
            if (pending.Count > MaxChecked) break;
            if (page.Count < PageSize) break;
            if (scanned >= MaxScanned) { cutShort = true; break; }
        }
        var stranded = new List<int>();
        foreach (var employee in pending.Take(MaxChecked))
        {
            var anyEligible = false;
            foreach (var reviewer in reviewers)
                if (!await CompensationApprovalGate.IsBarredAsync(db, tenantId, employee, reviewer.Id, ct)) { anyEligible = true; break; }
            if (!anyEligible) stranded.Add(employee.Id);
        }
        return new Evaluation(stranded, stranded.Count, pending.Count > MaxChecked || cutShort, pending.Count);
    }

    /// <summary>
    /// Opens, refreshes or closes the admin notification for the company. Best effort: a failure is logged and never
    /// reaches the caller, and anything this call staged or changed is rolled back in memory rather than left for the caller's next save.
    /// </summary>
    public static async Task SyncTaskAsync(
        ZayraDbContext db, Guid tenantId, Guid companyId, Guid? actorUserId, string trigger, RequestContext? context,
        ILogger? logger, CancellationToken ct, IConfiguration? config = null)
    {
        var staged = new List<object>();
        try
        {
            // Every query and write below pins the tenant (and the company in the audit row) itself; the scope flag only lifts
            // the caller's company filter so the evaluated company does not depend on who happened to trigger the check.
            using var system = SystemScopeContext.Begin();
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
                foreach (var n in open) { n.Status = ResolvedStatus; n.ReadAtUtc ??= now; staged.Add(n); }
                var row = AuditRow(ResolvedAction, tenantId, companyId, audit, trigger, result, now);
                db.AuditLogs.Add(row); staged.Add(row);
                await db.SaveChangesAsync(ct);
                return;
            }

            var message = Message(result, companyId);
            var title = "Pay approval has no available reviewer";
            // The recipient lookup loads every login of the tenant: skip it when the notice is unchanged and nothing that
            // ran can have changed who administers the company.
            HashSet<Guid> recipients;
            if (open.Count > 0 && StableRecipientTriggers.Contains(trigger) && open.All(n => n.Message == message && n.Title == title))
                recipients = open.Where(n => n.UserId.HasValue).Select(n => n.UserId!.Value).ToHashSet();
            else
            {
                recipients = await RecipientsAsync(db, tenantId, companyId, ct);
                if (recipients.Count == 0 && actorUserId is Guid actor && await ActorMayBeToldAsync(db, tenantId, companyId, actor, ct))
                    recipients.Add(actor);
            }
            var changed = false;
            var created = 0;
            foreach (var n in open.Where(n => n.UserId is null || !recipients.Contains(n.UserId.Value)))
            { n.Status = ResolvedStatus; n.ReadAtUtc ??= now; staged.Add(n); changed = true; }
            foreach (var recipient in recipients.OrderBy(r => r))
            {
                var existing = open.FirstOrDefault(n => n.UserId == recipient);
                if (existing is null)
                {
                    var notice = new Notification
                    {
                        TenantId = tenantId, UserId = recipient, Title = title, Message = message,
                        EntityName = EntityName, EntityId = entityId, CreatedAtUtc = now,
                    };
                    db.Notifications.Add(notice); staged.Add(notice); changed = true; created++;
                }
                else if (existing.Message != message || existing.Title != title)
                {
                    existing.Message = message; existing.Title = title; staged.Add(existing); changed = true;
                }
            }
            // One "opened" row per opening, and only when somebody was actually told.
            if (open.Count == 0 && created > 0)
            {
                var row = AuditRow(OpenedAction, tenantId, companyId, audit, trigger, result, now);
                db.AuditLogs.Add(row); staged.Add(row);
            }
            if (changed) await db.SaveChangesAsync(ct);
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

    /// <summary>Puts every entity this call touched back as it was: added rows are dropped, changed rows return to their loaded values.</summary>
    private static void Discard(ZayraDbContext db, List<object> staged)
    {
        foreach (var e in staged)
        {
            var entry = db.Entry(e);
            if (entry.State == EntityState.Added) entry.State = EntityState.Detached;
            else if (entry.State == EntityState.Modified)
            {
                entry.CurrentValues.SetValues(entry.OriginalValues);
                entry.State = EntityState.Unchanged;
            }
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
        var companies = await ActiveCompanyIdsAsync(db, tenantId, why, ct);
        var result = new HashSet<Guid>();
        foreach (var user in users)
        {
            if (!PrivilegeCeilingGraph.HoldsAdmin(user, tenantId) && !AuthService.GetPermissions(user).Contains(SecurityManagePermission)) continue;
            if (ReachesCompany(user, companies, companyId)) result.Add(user.Id);
        }
        return result;
    }

    /// <summary>
    /// The fallback addressee when nobody administers the company: the acting login, but only when it is an active login of
    /// this tenant whose own scope reaches the company (a platform user or a login of another company is never addressed).
    /// </summary>
    private static async Task<bool> ActorMayBeToldAsync(ZayraDbContext db, Guid tenantId, Guid companyId, Guid actor, CancellationToken ct)
    {
        const string why = "Pay approval dead ends: check the acting login belongs to this tenant and reaches the company; tenant pinned.";
        var user = await ScopedBypass.TenantWide(db.Users, tenantId, why).AsNoTracking()
            .Where(u => u.Id == actor && u.IsActive && !u.IsDeleted && u.Status == "Active")
            .Include(u => u.EntityAccesses).AsSplitQuery().FirstOrDefaultAsync(ct);
        if (user is null) return false;
        return ReachesCompany(user, await ActiveCompanyIdsAsync(db, tenantId, why, ct), companyId);
    }

    private static Task<List<Guid>> ActiveCompanyIdsAsync(ZayraDbContext db, Guid tenantId, string why, CancellationToken ct) =>
        ScopedBypass.TenantWide(db.Companies, tenantId, why).AsNoTracking()
            .Where(c => c.IsActive && !c.IsDeleted).Select(c => c.Id).ToListAsync(ct);

    private static bool ReachesCompany(User user, IReadOnlyCollection<Guid> companies, Guid companyId)
    {
        var scope = EntityScopeClaims.Resolve(user.IsGroupScope,
            user.EntityAccesses.Where(g => g.IsActive).Select(g => new EntityAccessGrant(g.CompanyId, g.Role, g.GrantMode)).ToArray(), companies);
        return scope.Mode == EntityScopeModes.Group || scope.CompanyIds.Contains(companyId);
    }
}
