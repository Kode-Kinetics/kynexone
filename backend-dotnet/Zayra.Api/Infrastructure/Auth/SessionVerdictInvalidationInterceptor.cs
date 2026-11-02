using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// Evicts cached session verdicts the moment an auth-relevant row changes, from the one place every EF write passes
/// through (so AuthService, AccessManagementService, MfaService, user management and security settings are all covered
/// without per-call-site hooks). Mapping, entity to scope:
/// users, platform_users, user_roles, user_permission_overrides, employee_user_accounts, user_entity_accesses = that user;
/// roles (tenant), security_settings, tenant_identity_provider_settings, tenants, companies, employees (delete/tenant) = that tenant;
/// system roles, role_permissions, permissions = everything.
/// The eviction runs after the save (and again at commit when the save joined an outer transaction). Raw SQL or
/// ExecuteUpdate on these tables bypasses it; none exists today, and the cache TTL (max 30 s) bounds such a gap.
/// </summary>
public sealed class SessionVerdictInvalidationInterceptor : SaveChangesInterceptor
{
    private readonly ISessionVerdictCache _cache;
    private static readonly ConditionalWeakTable<DbContext, List<Eviction>> Pending = new();
    private static readonly ConditionalWeakTable<DbContext, List<Eviction>> AwaitingCommit = new();

    public SessionVerdictInvalidationInterceptor(ISessionVerdictCache cache) => _cache = cache;

    private enum Scope { User, Tenant, All }
    private readonly record struct Eviction(Scope Scope, Guid Id);

    private static List<Eviction> Collect(DbContext db)
    {
        var list = new List<Eviction>();
        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            switch (entry.Entity)
            {
                case PlatformUser p: list.Add(new(Scope.User, p.Id)); break;
                case User u: list.Add(new(Scope.User, u.Id)); break;
                case UserRole ur: list.Add(new(Scope.User, ur.UserId)); break;
                case UserPermissionOverride po: list.Add(new(Scope.User, po.UserId)); break;
                case UserEntityAccess ea: list.Add(new(Scope.User, ea.UserId)); break;
                case EmployeeUserAccount eua when eua.UserId is Guid uid: list.Add(new(Scope.User, uid)); break;
                case EmployeeUserAccount eua: list.Add(new(Scope.Tenant, eua.TenantId)); break;
                case Role r: list.Add(r.TenantId is Guid rt ? new(Scope.Tenant, rt) : new(Scope.All, Guid.Empty)); break;
                case RolePermission or Permission: list.Add(new(Scope.All, Guid.Empty)); break;
                case SecuritySetting s: list.Add(new(Scope.Tenant, s.TenantId)); break;
                case TenantIdentityProviderSetting i: list.Add(new(Scope.Tenant, i.TenantId)); break;
                case Tenant t: list.Add(new(Scope.Tenant, t.Id)); break;
                case Company c: list.Add(new(Scope.Tenant, c.TenantId)); break;
                case Employee e when entry.State == EntityState.Deleted
                    || (entry.State == EntityState.Modified && Touches(entry, nameof(Employee.IsDeleted), nameof(Employee.TenantId))):
                    if (e.TenantId is Guid et) list.Add(new(Scope.Tenant, et));
                    break;
            }
        }
        return list.Distinct().ToList();
    }

    private static bool Touches(EntityEntry entry, params string[] names) =>
        names.Any(n => entry.Property(n).IsModified);

    private void Apply(IEnumerable<Eviction> evictions)
    {
        foreach (var e in evictions)
        {
            switch (e.Scope)
            {
                case Scope.User: _cache.InvalidateUser(e.Id); break;
                case Scope.Tenant: _cache.InvalidateTenant(e.Id); break;
                default: _cache.InvalidateAll(); break;
            }
        }
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stage(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stage(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Stage(DbContext? db)
    {
        if (db is null) return;
        Pending.Remove(db);
        var list = Collect(db);
        if (list.Count > 0) Pending.Add(db, list);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Flush(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Flush(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } db) Pending.Remove(db);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        SaveChangesFailed(eventData);
        return Task.CompletedTask;
    }

    private void Flush(DbContext? db)
    {
        if (db is null || !Pending.TryGetValue(db, out var list)) return;
        Pending.Remove(db);
        Apply(list);
        // Inside an outer transaction the write is not visible to other connections yet; a request that
        // re-validates now would re-cache the OLD state under the new version. Evict again at commit.
        if (db.Database.CurrentTransaction is not null)
        {
            // APPEND: an earlier save in the same transaction has its own evictions still waiting for the commit.
            if (AwaitingCommit.TryGetValue(db, out var waiting)) waiting.AddRange(list);
            else AwaitingCommit.Add(db, new List<Eviction>(list));
        }
    }

    /// <summary>Transaction hook that repeats the eviction when the outer transaction commits.</summary>
    public sealed class CommitHook : DbTransactionInterceptor
    {
        private readonly SessionVerdictInvalidationInterceptor _owner;
        public CommitHook(SessionVerdictInvalidationInterceptor owner) => _owner = owner;

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Done(eventData.Context, true);
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        { Done(eventData.Context, true); return Task.CompletedTask; }
        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Done(eventData.Context, false);
        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        { Done(eventData.Context, false); return Task.CompletedTask; }

        private void Done(DbContext? db, bool committed)  // committed or rolled back: both evict
        {
            if (db is null || !AwaitingCommit.TryGetValue(db, out var list)) return;
            AwaitingCommit.Remove(db);
            // Rollback evicts too: the writer's own connection may have cached a verdict computed from its uncommitted state.
            _owner.Apply(list.Distinct());
        }
    }
}
