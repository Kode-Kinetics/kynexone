using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// The call sites' single entry point to <see cref="IPayApprovalDeadEndMonitor"/>: works out which companies a change
/// can affect and re-evaluates only those that have pending pay work. Every method is best effort and must be called
/// AFTER the change is committed; none of them ever throws into the caller. Read-only with respect to the approval.
/// </summary>
public sealed class PayApprovalDeadEndHooks
{
    /// <summary>Companies re-evaluated by one call; the backstop sweep picks up the rest.</summary>
    public const int MaxCompaniesPerCall = 25;

    private readonly ZayraDbContext _db;
    private readonly IPayApprovalDeadEndMonitor? _monitor;
    private readonly ILogger? _logger;

    public PayApprovalDeadEndHooks(ZayraDbContext db, IPayApprovalDeadEndMonitor? monitor, ILogger? logger = null)
    {
        _db = db; _monitor = monitor; _logger = logger;
    }

    /// <summary>For code that has no constructor seam: the monitor comes from the request's services (none when absent, so the hook is a no-op).</summary>
    public static PayApprovalDeadEndHooks For(ZayraDbContext db, Microsoft.AspNetCore.Http.HttpContext? http) =>
        new(db, http?.RequestServices?.GetService(typeof(IPayApprovalDeadEndMonitor)) as IPayApprovalDeadEndMonitor);

    /// <summary>One company, straight to the monitor (submit-time and company-level changes).</summary>
    public async Task SyncCompanyAsync(Guid tenantId, Guid? companyId, string trigger, CancellationToken ct = default)
    {
        if (_monitor is null || companyId is not Guid company) return;
        if (Defer(tenantId, trigger, _ => Task.FromResult(new List<Guid> { company }))) return;
        try { await _monitor.SyncCompanyAsync(tenantId, company, trigger, ct); }
        catch (Exception ex) { Warn(ex, trigger); }
    }

    /// <summary>Pay was entered or changed for an employee: re-check the employee's own company.</summary>
    public async Task AfterEmployeePaySubmittedAsync(Guid tenantId, int employeeId, string trigger, CancellationToken ct = default)
    {
        if (_monitor is null) return;
        if (Defer(tenantId, trigger, async c => (await EmployeeCompaniesAsync(tenantId, [employeeId], c)))) return;
        try
        {
            var companies = await EmployeeCompaniesAsync(tenantId, [employeeId], ct);
            foreach (var company in companies) await SyncCompanyAsync(tenantId, company, trigger, ct);
        }
        catch (Exception ex) { Warn(ex, trigger); }
    }

    /// <summary>Pay was changed for several employees in one go (a job pass): each affected company is checked once, after the loop.</summary>
    public async Task AfterEmployeesPaySubmittedAsync(Guid tenantId, IReadOnlyCollection<int> employeeIds, string trigger, CancellationToken ct = default)
    {
        if (_monitor is null || employeeIds.Count == 0) return;
        try
        {
            var companies = await EmployeeCompaniesAsync(tenantId, employeeIds, ct);
            foreach (var company in PayApprovalDeadEndRotation.OldestFirst(tenantId, companies).Take(MaxCompaniesPerCall))
                await SyncCompanyAsync(tenantId, company, trigger, ct);
        }
        catch (Exception ex) { Warn(ex, trigger); }
    }

    private Task<List<Guid>> EmployeeCompaniesAsync(Guid tenantId, IReadOnlyCollection<int> employeeIds, CancellationToken ct) =>
        ScopedBypass.NullableTenantWide(_db.Employees, tenantId, "Pay approval dead ends: companies of the employees whose pay changed; tenant pinned.")
            .AsNoTracking().Where(e => employeeIds.Contains(e.Id) && e.CompanyId != null).Select(e => e.CompanyId!.Value).Distinct().ToListAsync(ct);

    /// <summary>The companies one login can reach (its grants, or every company for group scope), including grants just switched off.</summary>
    public Task AfterUserChangedAsync(Guid tenantId, Guid userId, string trigger, bool allCompanies = false, CancellationToken ct = default) =>
        RunAsync(tenantId, trigger, c => UserCompaniesAsync(tenantId, new[] { userId }, allCompanies, c), ct);

    /// <summary>Every login that holds the role.</summary>
    public Task AfterRoleChangedAsync(Guid tenantId, Guid roleId, string trigger, CancellationToken ct = default) =>
        RunAsync(tenantId, trigger, async c =>
        {
            const string why = "Pay approval dead ends: logins holding one role; tenant pinned.";
            var holders = await ScopedBypass.TenantWide(_db.Users, tenantId, why).AsNoTracking()
                .Where(u => u.UserRoles.Any(r => r.RoleId == roleId)).Select(u => u.Id).ToListAsync(c);
            return await UserCompaniesAsync(tenantId, holders, false, c);
        }, ct);

    /// <summary>Several roles at once (a bulk permission save).</summary>
    public Task AfterRolesChangedAsync(Guid tenantId, IReadOnlyCollection<Guid> roleIds, string trigger, CancellationToken ct = default) =>
        RunAsync(tenantId, trigger, async c =>
        {
            const string why = "Pay approval dead ends: logins holding the changed roles; tenant pinned.";
            var holders = await ScopedBypass.TenantWide(_db.Users, tenantId, why).AsNoTracking()
                .Where(u => u.UserRoles.Any(r => roleIds.Contains(r.RoleId))).Select(u => u.Id).ToListAsync(c);
            return await UserCompaniesAsync(tenantId, holders, false, c);
        }, ct);

    /// <summary>A tenant-wide change (security policy) or one company (null = every company of the tenant).</summary>
    public Task AfterCompanyChangedAsync(Guid tenantId, Guid? companyId, string trigger, CancellationToken ct = default) =>
        RunAsync(tenantId, trigger, async c => companyId is Guid one ? new List<Guid> { one } : await AllCompaniesAsync(tenantId, c), ct);

    /// <summary>An employee's lifecycle changed: their own company (pending pay) and every company a linked login can reach.</summary>
    public Task AfterEmployeeChangedAsync(Guid tenantId, int employeeId, string trigger, CancellationToken ct = default) =>
        RunAsync(tenantId, trigger, async c =>
        {
            const string why = "Pay approval dead ends: the logins linked to one employee and the employee's company; tenant pinned.";
            var employee = await ScopedBypass.NullableTenantWide(_db.Employees, tenantId, why).AsNoTracking()
                .Where(e => e.Id == employeeId).Select(e => new { e.CompanyId, e.UserAccountId }).FirstOrDefaultAsync(c);
            var linked = await ScopedBypass.TenantWide(_db.EmployeeUserAccounts, tenantId, why).AsNoTracking()
                .Where(l => l.EmployeeId == employeeId && l.UserId != null).Select(l => l.UserId!.Value).ToListAsync(c);
            if (employee?.UserAccountId is Guid pointer) linked.Add(pointer);
            var companies = await UserCompaniesAsync(tenantId, linked.Distinct().ToList(), false, c);
            if (employee?.CompanyId is Guid own && !companies.Contains(own)) companies.Add(own);
            return companies;
        }, ct);

    private async Task RunAsync(Guid tenantId, string trigger, Func<CancellationToken, Task<List<Guid>>> resolve, CancellationToken ct)
    {
        if (_monitor is null) return;
        if (Defer(tenantId, trigger, resolve)) return;
        try
        {
            var candidates = await resolve(ct);
            var worth = await CompaniesWithPendingWorkAsync(_db, tenantId, candidates, MaxCompaniesPerCall, ct);
            foreach (var company in worth)
                await SyncCompanyAsync(tenantId, company, trigger, ct);
        }
        catch (Exception ex) { Warn(ex, trigger); }
    }

    // ── batching ────────────────────────────────────────────────────────────────────────────────────
    private static readonly AsyncLocal<Batch?> Current = new();

    /// <summary>
    /// Opens a batch on the current async flow: hook calls made while it is open are only recorded, and
    /// <see cref="Batch.FlushAsync"/> then checks each affected company once. Use around a per-row loop that would
    /// otherwise re-evaluate the same company for every row. Dispose it (a <c>using</c>) whether or not it was flushed.
    /// </summary>
    public static Batch BeginBatch()
    {
        var batch = new Batch(Current.Value);
        Current.Value = batch;
        return batch;
    }

    private bool Defer(Guid tenantId, string trigger, Func<CancellationToken, Task<List<Guid>>> resolve)
    {
        if (Current.Value is not { IsOpen: true } batch) return false;
        batch.Add(this, tenantId, trigger, resolve);
        return true;
    }

    public sealed class Batch : IDisposable
    {
        private const int MaxRecorded = 5_000;
        private readonly Batch? _previous;
        private readonly List<(PayApprovalDeadEndHooks Hooks, Guid Tenant, string Trigger, Func<CancellationToken, Task<List<Guid>>> Resolve)> _entries = [];
        private bool _disposed;

        internal Batch(Batch? previous) => _previous = previous;

        internal bool IsOpen { get; private set; } = true;

        internal void Add(PayApprovalDeadEndHooks hooks, Guid tenantId, string trigger, Func<CancellationToken, Task<List<Guid>>> resolve)
        {
            // Past the cap further rows add nothing a later row did not already name for the sweep to find.
            if (_entries.Count < MaxRecorded) _entries.Add((hooks, tenantId, trigger, resolve));
        }

        /// <summary>Resolves every recorded change, then checks each distinct company once (capped and rotated per tenant). Never throws.</summary>
        public async Task FlushAsync(CancellationToken ct = default)
        {
            IsOpen = false;
            var entries = _entries.ToList();
            _entries.Clear();
            var groups = new Dictionary<(IPayApprovalDeadEndMonitor Monitor, Guid Tenant), (PayApprovalDeadEndHooks Runner, string Trigger, HashSet<Guid> Companies)>();
            foreach (var entry in entries)
            {
                if (entry.Hooks._monitor is not { } monitor) continue;
                try
                {
                    var ids = await entry.Resolve(ct);
                    var key = (monitor, entry.Tenant);
                    if (!groups.TryGetValue(key, out var group)) groups[key] = group = (entry.Hooks, entry.Trigger, []);
                    foreach (var id in ids) group.Companies.Add(id);
                }
                catch (Exception ex) { entry.Hooks.Warn(ex, entry.Trigger); }
            }
            foreach (var ((_, tenant), group) in groups)
            {
                try
                {
                    var worth = await CompaniesWithPendingWorkAsync(group.Runner._db, tenant, group.Companies.ToList(), MaxCompaniesPerCall, ct);
                    foreach (var company in worth) await group.Runner.SyncCompanyAsync(tenant, company, group.Trigger, ct);
                }
                catch (Exception ex) { group.Runner.Warn(ex, group.Trigger); }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            IsOpen = false;
            Current.Value = _previous;
        }
    }

    private async Task<List<Guid>> AllCompaniesAsync(Guid tenantId, CancellationToken ct) =>
        await ScopedBypass.TenantWide(_db.Companies, tenantId, "Pay approval dead ends: every company of one tenant; tenant pinned.")
            .AsNoTracking().Where(c => !c.IsDeleted).Select(c => c.Id).ToListAsync(ct);

    private async Task<List<Guid>> UserCompaniesAsync(Guid tenantId, IReadOnlyCollection<Guid> userIds, bool allCompanies, CancellationToken ct)
    {
        if (userIds.Count == 0) return [];
        const string why = "Pay approval dead ends: which companies changed logins can reach; tenant pinned.";
        var wide = allCompanies || await ScopedBypass.TenantWide(_db.Users, tenantId, why).AsNoTracking()
            .AnyAsync(u => userIds.Contains(u.Id) && u.IsGroupScope, ct);
        var grants = await ScopedBypass.TenantWide(_db.UserEntityAccesses, tenantId, why).AsNoTracking()
            .Where(g => userIds.Contains(g.UserId)).Select(g => new { g.CompanyId, g.GrantMode }).ToListAsync(ct);
        wide |= grants.Any(g => g.CompanyId is null || g.GrantMode != EntityGrantModes.SelectedCompanies);
        if (wide) return await AllCompaniesAsync(tenantId, ct);
        return grants.Select(g => g.CompanyId!.Value).Distinct().ToList();
    }

    /// <summary>
    /// Companies, out of <paramref name="within"/> (or every company when null), that hold a pending pay candidate or an
    /// open dead-end notification. A superset of what the evaluation inspects, so skipping the rest loses nothing.
    /// </summary>
    public static async Task<List<Guid>> CompaniesWithPendingWorkAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Guid>? within, int max, CancellationToken ct)
    {
        var withCandidates = ScopedBypass.NullableTenantWide(db.Employees, tenantId,
                "Pay approval dead ends: which companies hold pending pay candidates; tenant pinned.").AsNoTracking()
            .Where(e => !e.IsDeleted && e.CompanyId != null
                && (e.Status == EmployeeStatuses.Draft || e.Status == EmployeeStatuses.Invited
                    || db.EmployeeImportGaps.Any(g => g.TenantId == tenantId && g.EmployeeId == e.Id
                        && g.GapType == CompensationApprovalGate.GapType && g.ResolvedAtUtc == null)))
            .Select(e => e.CompanyId!.Value);
        if (within is not null) withCandidates = withCandidates.Where(c => within.Contains(c));
        var found = await withCandidates.Distinct().OrderBy(c => c).Take(Pool).ToListAsync(ct);

        var openIds = await db.Notifications.AsNoTracking()
            .Where(n => n.TenantId == tenantId && n.EntityName == PayApprovalDeadEnds.EntityName && n.Status != PayApprovalDeadEnds.ResolvedStatus)
            .Select(n => n.EntityId).Distinct().Take(Pool).ToListAsync(ct);
        foreach (var raw in openIds)
            if (Guid.TryParse(raw, out var id) && (within is null || within.Contains(id)) && !found.Contains(id)) found.Add(id);
        // The longest-unchecked companies first, so a cap never starves the same ones.
        return PayApprovalDeadEndRotation.OldestFirst(tenantId, found).Take(max).ToList();
    }

    /// <summary>Most companies read before the cap and the rotation are applied.</summary>
    private const int Pool = 1000;

    private void Warn(Exception ex, string trigger) =>
        _logger?.LogWarning(ex, "Pay approval dead-end hook failed (trigger {Trigger}); the change itself was already saved.", trigger);
}
