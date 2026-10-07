using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;
using Xunit;
using Xunit.Abstractions;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// The users section's admin-seat recheck under the lock (ported from the #200 reviewer's probe). Another writer
/// takes the last Admin seat between the access gate's count and the commit's save. The recheck refuses — and the
/// refused section's new Admin must NOT then be written by the save that records the batch as Failed (it was, and
/// the response was a 500 from a null ledger).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class MigrationImportSeatLockRaceTests
{
    private readonly PostgresFixture _fx;
    private readonly ITestOutputHelper _out;
    public MigrationImportSeatLockRaceTests(PostgresFixture fx, ITestOutputHelper o) { _fx = fx; _out = o; }

    private sealed class RacerInterceptor : DbCommandInterceptor
    {
        public Func<Task>? OnLock;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r, CancellationToken ct = default)
        { await Fire(c); return r; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<int> r, CancellationToken ct = default)
        { await Fire(c); return r; }
        private async Task Fire(DbCommand c)
        {
            if (OnLock is { } f && c.CommandText.Contains("pg_advisory_xact_lock") && !c.CommandText.Contains("hashtext"))
            { OnLock = null; await f(); }
        }
    }

    [Fact]
    public async Task SeatRecheckFailure_DoesNotPersistTheNewAdmin()
    {
        Guid tenant, adminRoleId, callerId;
        await using (var seed = _fx.CreateDb())
        {
            tenant = await PostgresFixture.SeedMinimalTenant(seed);
            var perm = await seed.Permissions.FirstOrDefaultAsync(p => p.Key == "security.manage") ?? new Permission { Key = "security.manage", Module = "t", Description = "t" };
            if (perm.Id == Guid.Empty || seed.Entry(perm).State == EntityState.Detached) seed.Permissions.Add(perm);
            var admin = new Role { TenantId = tenant, Name = "Admin", NormalizedName = "ADMIN", IsSystem = true, IsEditable = false, IsActive = true };
            admin.RolePermissions.Add(new RolePermission { RoleId = admin.Id, PermissionId = perm.Id });
            seed.Roles.Add(admin);
            seed.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenant, Status = SubscriptionStatuses.Active, MaxAdminUsers = 2 });
            var caller = new User { TenantId = tenant, Email = "caller@probe.test", NormalizedEmail = "CALLER@PROBE.TEST", FullName = "Caller", PasswordHash = "x", Status = "Active", IsActive = true, IsEmailConfirmed = true };
            seed.Users.Add(caller);
            seed.UserRoles.Add(new UserRole { UserId = caller.Id, RoleId = admin.Id });
            await seed.SaveChangesAsync();
            adminRoleId = admin.Id; callerId = caller.Id;
        }

        var racer = new RacerInterceptor();
        racer.OnLock = async () =>
        {
            await using var other = _fx.CreateDb();
            var u = new User { TenantId = tenant, Email = "racer@probe.test", NormalizedEmail = "RACER@PROBE.TEST", FullName = "Racer", PasswordHash = "x", Status = "Active", IsActive = true, IsEmailConfirmed = true };
            other.Users.Add(u);
            other.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = adminRoleId });
            await other.SaveChangesAsync();
        };
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
            .AddInterceptors(Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance)
            .AddInterceptors(racer)
            .Options;
        await using var db = new ZayraDbContext(options);
        var controller = new MigrationImportController(db, new Pbkdf2PasswordHasher(1_000), new AuditService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, callerId.ToString()), new Claim("sub", callerId.ToString()),
                new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "security.manage"),
                new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
            }, "test")) } },
        };
        ActionResult<MigrationReconciliationDto>? result = null; string thrown = "none";
        try { result = await controller.Commit(new MigrationPackageRequest($"probe-{Guid.NewGuid():N}", new Dictionary<string, string>
        {
            ["users"] = "Email,FullName,PhoneNumber,PreferredLanguage,Timezone,Status,RoleNames,IsGroupScope\nnewadmin@probe.test,New Admin,,en,UTC,Active,Admin,false\n",
        }, false), CancellationToken.None); } catch (Exception ex) { thrown = ex.GetType().Name + ": " + ex.Message; }

        var status = result?.Result is ObjectResult o ? o.StatusCode : -1;
        await using var verify = _fx.CreateDb();
        var activeAdmins = await verify.Users.IgnoreQueryFilters().Where(u => u.TenantId == tenant && u.IsActive && u.UserRoles.Any(ur => ur.RoleId == adminRoleId)).Select(u => u.Email).ToListAsync();
        var batch = await verify.MigrationImportBatches.IgnoreQueryFilters().Where(b => b.TenantId == tenant).Select(b => new { b.Status, b.ErrorJson }).FirstOrDefaultAsync();
        _out.WriteLine($"thrown={thrown} status={status} racerFired={(racer.OnLock is null)} batch={batch?.Status} err={batch?.ErrorJson} activeAdmins=[{string.Join(",", activeAdmins)}]");
        Assert.True(racer.OnLock is null, "the racing writer never ran, so the race was not exercised");
        Assert.Equal("none", thrown);
        Assert.DoesNotContain("newadmin@probe.test", activeAdmins);
        Assert.False(await verify.Users.IgnoreQueryFilters().AnyAsync(u => u.TenantId == tenant && u.NormalizedEmail == "NEWADMIN@PROBE.TEST"),
            "the refused users section was saved by the failure-recording save");
        Assert.Equal(422, status);
        Assert.Equal("Failed", batch?.Status);
        Assert.Contains("administrator", batch?.ErrorJson);
    }

    /// <summary>The last-operational-Admin rule is re-counted under the same lock: the gate saw two operational Admins
    /// and let the package demote one, but the other was demoted elsewhere before the save — the re-check refuses,
    /// and the demotion is not written.</summary>
    [Fact]
    public async Task TheLastAdminRule_IsRecountedUnderTheLock_AndARefusalWritesNothing()
    {
        Guid tenant, adminRoleId, callerId, firstId, secondId;
        await using (var seed = _fx.CreateDb())
        {
            tenant = await PostgresFixture.SeedMinimalTenant(seed);
            var admin = new Role { TenantId = tenant, Name = "Admin", NormalizedName = "ADMIN", IsSystem = true, IsEditable = false, IsActive = true };
            var clerk = new Role { TenantId = tenant, Name = "Clerk", NormalizedName = "CLERK", IsActive = true };
            seed.Roles.AddRange(admin, clerk);
            User Make(string email) => new() { TenantId = tenant, Email = email, NormalizedEmail = email.ToUpperInvariant(), FullName = email, PasswordHash = "x", Status = "Active", IsActive = true, IsEmailConfirmed = true };
            var caller = Make("caller@probe.test"); var first = Make("first.admin@probe.test"); var second = Make("second.admin@probe.test");
            seed.Users.AddRange(caller, first, second);
            seed.UserRoles.AddRange(new UserRole { UserId = first.Id, RoleId = admin.Id }, new UserRole { UserId = second.Id, RoleId = admin.Id });
            await seed.SaveChangesAsync();
            adminRoleId = admin.Id; callerId = caller.Id; firstId = first.Id; secondId = second.Id;
        }

        var racer = new RacerInterceptor();
        racer.OnLock = async () =>
        {
            // Elsewhere, the other Admin is blocked while this import waits for the lock.
            await using var other = _fx.CreateDb();
            var second = await other.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == secondId);
            second.IsActive = false; second.Status = "Suspended";
            await other.SaveChangesAsync();
        };
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
            .AddInterceptors(Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance)
            .AddInterceptors(racer)
            .Options;
        await using var db = new ZayraDbContext(options);
        var controller = new MigrationImportController(db, new Pbkdf2PasswordHasher(1_000), new AuditService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, callerId.ToString()), new Claim("sub", callerId.ToString()),
                new Claim(ClaimTypes.Role, "Admin"), new Claim("permission", "security.manage"),
                new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
            }, "test")) } },
        };
        var result = await controller.Commit(new MigrationPackageRequest($"lastadmin-{Guid.NewGuid():N}", new Dictionary<string, string>
        {
            ["users"] = "Email,FullName,PhoneNumber,PreferredLanguage,Timezone,Status,RoleNames,IsGroupScope\nfirst.admin@probe.test,First Admin,,en,UTC,Active,Clerk,false\n",
        }, false), CancellationToken.None);

        Assert.True(racer.OnLock is null, "the racing writer never ran, so the race was not exercised");
        Assert.Equal(422, Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode);
        await using var verify = _fx.CreateDb();
        Assert.True(await verify.UserRoles.AnyAsync(ur => ur.UserId == firstId && ur.RoleId == adminRoleId), "the last operational Admin was demoted");
        var batch = await verify.MigrationImportBatches.IgnoreQueryFilters().SingleAsync(b => b.TenantId == tenant);
        Assert.Equal("Failed", batch.Status);
        Assert.Contains("last administrator", batch.ErrorJson);
    }
}
