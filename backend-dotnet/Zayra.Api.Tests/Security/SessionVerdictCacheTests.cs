using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The per-request session verdict cache: a hit must cost zero database access, and every revocation event must
/// take effect on the VERY NEXT request (no waiting for the TTL).
/// </summary>
public sealed class SessionVerdictCacheTests
{
    private sealed class Rig : IDisposable
    {
        public required ZayraDbContext Db;
        public required MemorySessionVerdictCache Cache;
        public required User User;
        public required Tenant Tenant;
        public required Role Role;
        public required Permission Permission;
        public required ClaimsPrincipal Principal;
        public void Dispose() => Db.Dispose();
    }

    private static MemorySessionVerdictCache NewCache(int ttl = 30) =>
        new(new MemoryCache(new MemoryCacheOptions()), Options.Create(new SessionVerdictCacheOptions { TtlSeconds = ttl }));

    private static async Task<Rig> NewRigAsync()
    {
        var cache = NewCache();
        var interceptor = new SessionVerdictInvalidationInterceptor(cache);
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options;
        var db = new ZayraDbContext(options);
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "T", Slug = "t" };
        var permission = new Permission { Id = Guid.NewGuid(), Key = "dashboard.read", Module = "Dashboard", Description = "Read" };
        var role = new Role { Id = Guid.NewGuid(), TenantId = tenant.Id, Tenant = tenant, Name = "Admin", NormalizedName = "ADMIN", Description = "Admin" };
        var user = new User
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, Tenant = tenant, Email = "a@t.local", NormalizedEmail = "A@T.LOCAL",
            FullName = "A", PasswordHash = "x", IsGroupScope = true
        };
        db.Tenants.Add(tenant);
        db.SecuritySettings.Add(new SecuritySetting { Id = Guid.NewGuid(), TenantId = tenant.Id });
        db.Permissions.Add(permission);
        db.Roles.Add(role);
        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return new Rig
        {
            Db = db, Cache = cache, User = user, Tenant = tenant, Role = role, Permission = permission,
            Principal = Principal(user, tenant)
        };
    }

    private static ClaimsPrincipal Principal(User user, Tenant tenant)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new("tenant_id", tenant.Id.ToString()),
            new(TenantSessionSecurity.SessionStampClaim, TenantSessionSecurity.StampValue(user)),
            new(ClaimTypes.Role, "Admin"),
            new("permission", "dashboard.read")
        };
        claims.AddRange(EntityScopeClaims.Build(EntityScopeDescriptor.Group, Array.Empty<EntityAccessGrant>()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static async Task<bool> CheckAsync(Rig r) =>
        (await CurrentSessionValidator.ValidateAsync(r.Principal, r.Db, r.Cache, CancellationToken.None)).Current;

    [Fact]
    public async Task CacheHit_TouchesNoDatabase()
    {
        using var r = await NewRigAsync();
        Assert.True(await CheckAsync(r));

        // A disposed context throws on any query: the second check must be served from the cache alone.
        var disposed = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await disposed.DisposeAsync();
        var second = await CurrentSessionValidator.ValidateAsync(r.Principal, disposed, r.Cache, CancellationToken.None);
        Assert.True(second.Current);
    }

    [Fact]
    public async Task Disabled_TtlZero_AlwaysRevalidates()
    {
        using var r = await NewRigAsync();
        var off = NewCache(0);
        Assert.True((await CurrentSessionValidator.ValidateAsync(r.Principal, r.Db, off, CancellationToken.None)).Current);
        var key = off.KeyFor(r.Tenant.Id, r.User.Id, CurrentSessionValidator.Fingerprint(r.Principal));
        Assert.False(off.IsKnownCurrent(key));
    }

    [Fact]
    public void Ttl_IsCappedAtThirtySeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(30), new SessionVerdictCacheOptions { TtlSeconds = 9999 }.EffectiveTtl);

    [Fact]
    public async Task NegativeVerdicts_AreNeverCached()
    {
        using var r = await NewRigAsync();
        var bad = new ClaimsPrincipal(new ClaimsIdentity(
            r.Principal.Claims.Where(c => c.Type != "permission"), "test"));
        Assert.False((await CurrentSessionValidator.ValidateAsync(bad, r.Db, r.Cache, CancellationToken.None)).Current);
        Assert.False(r.Cache.IsKnownCurrent(r.Cache.KeyFor(r.Tenant.Id, r.User.Id, CurrentSessionValidator.Fingerprint(bad))));
    }

    // ── Revocation events: the very next request re-validates and fails ─────────────────────────────

    [Fact]
    public async Task SignOut_And_SignOutEverywhere_And_PasswordChange_And_MfaReset_RotateStamp_NextRequestFails()
    {
        // Logout, end-all-sessions, password change and MFA reset all save a User row (stamp rotation).
        using var r = await NewRigAsync();
        Assert.True(await CheckAsync(r));
        TenantSessionSecurity.RotateStamp(r.User);
        await r.Db.SaveChangesAsync();
        Assert.False(await CheckAsync(r));
    }

    [Fact]
    public async Task SuspendDeleteLock_UserRowChangeWithoutStampComparison_StillEvicts()
    {
        using var r = await NewRigAsync();
        Assert.True(await CheckAsync(r));
        r.User.IsActive = false;
        await r.Db.SaveChangesAsync();
        // The token's stamp is the OLD one, so the cached verdict is only invalidated by the version bump.
        Assert.False(await CheckAsync(r));
    }

    [Fact]
    public async Task RoleUnassigned_NextRequestFails()
    {
        using var r = await NewRigAsync();
        Assert.True(await CheckAsync(r));
        r.Db.UserRoles.Remove(await r.Db.UserRoles.SingleAsync());
        await r.Db.SaveChangesAsync();
        Assert.False(await CheckAsync(r));
    }

    [Fact]
    public async Task PermissionRemovedFromRole_NextRequestFails()
    {
        using var r = await NewRigAsync();
        Assert.True(await CheckAsync(r));
        r.Db.RolePermissions.Remove(await r.Db.RolePermissions.SingleAsync());
        await r.Db.SaveChangesAsync();
        Assert.False(await CheckAsync(r));
    }

    [Fact]
    public async Task RoleDeactivated_NextRequestFails()
    {
        using var r = await NewRigAsync();
        Assert.True(await CheckAsync(r));
        var role = await r.Db.Roles.SingleAsync();
        role.IsActive = false;
        await r.Db.SaveChangesAsync();
        Assert.False(await CheckAsync(r));
    }

    [Fact]
    public async Task PermissionOverrideAdded_NextRequestFails()
    {
        using var r = await NewRigAsync();
        Assert.True(await CheckAsync(r));
        var extra = new Permission { Id = Guid.NewGuid(), Key = "x.write", Module = "X", Description = "x" };
        r.Db.Permissions.Add(extra);
        await r.Db.SaveChangesAsync();
        Assert.True(await CheckAsync(r)); // catalogue change alone grants nothing; re-validated and still current
        r.Db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            TenantId = r.Tenant.Id, UserId = r.User.Id, PermissionKey = extra.Key, Effect = "Allow"
        });
        await r.Db.SaveChangesAsync();
        Assert.False(await CheckAsync(r));
    }

    [Fact]
    public async Task ScopeGrantChanged_NextRequestFails()
    {
        using var r = await NewRigAsync();
        Assert.True(await CheckAsync(r));
        r.Db.UserEntityAccesses.Add(new UserEntityAccess
        {
            TenantId = r.Tenant.Id, UserId = r.User.Id, CompanyId = null, Role = "Admin",
            GrantMode = EntityGrantModes.AllCurrentCompanies, IsActive = true
        });
        r.User.IsGroupScope = false;
        await r.Db.SaveChangesAsync();
        Assert.False(await CheckAsync(r));
    }

    [Theory]
    [InlineData("security_settings")]
    [InlineData("identity_provider")]
    [InlineData("company_deactivated")]
    [InlineData("tenant")]
    [InlineData("tenant_role")]
    public async Task TenantLevelChange_EvictsEveryCachedSessionOfTheTenant(string kind)
    {
        using var r = await NewRigAsync();
        Assert.True(await CheckAsync(r));
        var key = r.Cache.KeyFor(r.Tenant.Id, r.User.Id, CurrentSessionValidator.Fingerprint(r.Principal));
        Assert.True(r.Cache.IsKnownCurrent(key));

        switch (kind)
        {
            case "security_settings":
                (await r.Db.SecuritySettings.SingleAsync()).MaxFailedLoginAttempts = 3; break;
            case "identity_provider":
                r.Db.TenantIdentityProviderSettings.Add(new TenantIdentityProviderSetting { TenantId = r.Tenant.Id }); break;
            case "company_deactivated":
                var company = new Company { TenantId = r.Tenant.Id, LegalNameEn = "C", IsActive = true };
                r.Db.Companies.Add(company);
                await r.Db.SaveChangesAsync();
                Assert.True(await CheckAsync(r));
                key = r.Cache.KeyFor(r.Tenant.Id, r.User.Id, CurrentSessionValidator.Fingerprint(r.Principal));
                company.IsActive = false; break;
            case "tenant":
                r.Tenant.Name = "Renamed"; break;
            case "tenant_role":
                r.Role.Description = "changed"; break;
        }
        await r.Db.SaveChangesAsync();

        // A key computed after the write carries the bumped version, so it must not be found.
        key = r.Cache.KeyFor(r.Tenant.Id, r.User.Id, CurrentSessionValidator.Fingerprint(r.Principal));
        Assert.False(r.Cache.IsKnownCurrent(key));
    }

    [Fact]
    public async Task OtherUsersWrite_DoesNotEvictThisUsersVerdict()
    {
        using var r = await NewRigAsync();
        Assert.True(await CheckAsync(r));
        var other = new User
        {
            Id = Guid.NewGuid(), TenantId = r.Tenant.Id, Tenant = r.Tenant, Email = "b@t.local", NormalizedEmail = "B@T.LOCAL",
            FullName = "B", PasswordHash = "x"
        };
        r.Db.Users.Add(other);
        await r.Db.SaveChangesAsync();
        other.FullName = "B2";
        await r.Db.SaveChangesAsync();
        Assert.True(r.Cache.IsKnownCurrent(r.Cache.KeyFor(r.Tenant.Id, r.User.Id, CurrentSessionValidator.Fingerprint(r.Principal))));
    }

    [Fact]
    public async Task VerdictComputedAcrossAConcurrentWrite_IsNotServedAfterIt()
    {
        // The key (with its versions) is taken before the database read. A write that lands between key and store
        // bumps the version, so the stale verdict is stored under a key no later request will compute.
        using var r = await NewRigAsync();
        var fp = CurrentSessionValidator.Fingerprint(r.Principal);
        var staleKey = r.Cache.KeyFor(r.Tenant.Id, r.User.Id, fp);
        r.User.IsActive = false;
        await r.Db.SaveChangesAsync(); // the concurrent write
        r.Cache.RememberCurrent(staleKey);
        Assert.False(r.Cache.IsKnownCurrent(r.Cache.KeyFor(r.Tenant.Id, r.User.Id, fp)));
    }
}
