using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Zayra.Api.Infrastructure.Licensing;
using Zayra.Api.Tests.Licensing;
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
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The licence's user allowance under concurrency. AccessController checks the seat count before it calls the
/// service, outside any lock, so two creates racing for the last seat both passed it and both committed. The service
/// now recounts after it has locked the tenant row FOR UPDATE; the loser is refused with the same 422
/// <c>user_limit_reached</c> the preflight returns. Real Postgres: the in-memory provider has no row locks.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class UserSeatLimitConcurrencyPostgresTests(PostgresFixture fixture)
{
    private static readonly string[] AdminKeys = ["security.manage", "profile.read"];

    private ILicenceService _licence = FixedLicenceService.Unlimited();

    [Fact]
    public async Task Two_creates_racing_for_the_last_seat_make_exactly_one_user_and_the_loser_gets_user_limit_reached()
    {
        var (tenantId, adminId) = await SeedAsync(maxUsers: 2); // the admin holds one seat: one is left
        // Both requests are held just before the tenant row lock until the other arrives, so both have passed the
        // controller's preflight (1 of 2 seats used) before either creates anything.
        var rendezvous = new TenantLockRendezvous(participants: 2, timeout: TimeSpan.FromSeconds(5));

        async Task<ActionResult<AuthUserDto>> CreateAsync(string email)
        {
            await using var db = Db(rendezvous);
            return await Controller(db, tenantId, adminId).CreateUser(
                new CreateUserRequest(email, "Seat Racer", "Sufficiently-Strong-9!", ["Employee"]), CancellationToken.None);
        }

        var results = await Task.WhenAll(CreateAsync("racer.a@seats.example"), CreateAsync("racer.b@seats.example"));

        rendezvous.Arrived.Should().Be(2, "both creates must have passed the preflight and reached the lock");
        results.Count(r => r.Result is CreatedAtActionResult).Should().Be(1);
        var refused = results.Select(r => r.Result).OfType<UnprocessableEntityObjectResult>().Should().ContainSingle().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        var body = JsonSerializer.SerializeToElement(refused.Value);
        body.GetProperty("error").GetString().Should().Be(UserSeatLimit.Refusal.Code);
        body.GetProperty("current").GetInt32().Should().Be(2);
        body.GetProperty("limit").GetInt32().Should().Be(2);
        body.GetProperty("message").GetString().Should().Contain("2/2");

        await using var verify = fixture.CreateDb();
        (await verify.Users.IgnoreQueryFilters().CountAsync(u => u.TenantId == tenantId && u.IsActive && !u.IsDeleted)).Should().Be(2);
        (await verify.Users.IgnoreQueryFilters().CountAsync(u => u.TenantId == tenantId && u.FullName == "Seat Racer")).Should().Be(1);
    }

    [Fact]
    public async Task The_locked_recount_refuses_with_the_typed_exception_when_the_preflight_is_skipped()
    {
        // The wizard's company super admin and any future door call the service directly: the service itself holds.
        var (tenantId, adminId) = await SeedAsync(maxUsers: 1);
        await using var db = fixture.CreateDb();
        var service = new AccessManagementService(db, new Pbkdf2PasswordHasher(), new AuditService(db), new NoTokens(), licence: _licence);
        var act = () => service.CreateUserAsync(tenantId,
            new CreateUserRequest("over.limit@seats.example", "Over Limit", "Sufficiently-Strong-9!", ["Employee"]),
            new RequestContext("127.0.0.1", "tests", adminId, tenantId), CancellationToken.None);
        var thrown = await act.Should().ThrowAsync<UserSeatLimitReachedException>();
        thrown.Which.Refusal.Should().Be(new UserSeatLimit.Refusal(1, 1));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────────

    private async Task<(Guid TenantId, Guid AdminId)> SeedAsync(int maxUsers)
    {
        await using var db = fixture.CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Seat Race Tenant", Slug = $"seats-{tenantId:N}" });
        // The user allowance is the licence's (WP3): the services below are built with it.
        _licence = FixedLicenceService.WithLimits(maxUsers: maxUsers);
        var adminRole = new Role
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Name = "Admin", NormalizedName = "ADMIN", Description = "Admin",
            IsSystem = true, IsEditable = false, IsActive = true, AuthorityLevel = 1,
        };
        var employeeRole = new Role
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Name = "Employee", NormalizedName = "EMPLOYEE", Description = "Employee",
            IsSystem = true, IsEditable = true, IsActive = true, AuthorityLevel = 50,
        };
        db.Roles.AddRange(adminRole, employeeRole);
        foreach (var key in AdminKeys)
        {
            var permission = await db.Permissions.SingleOrDefaultAsync(x => x.Key == key);
            if (permission is null)
            {
                permission = new Permission { Key = key, Module = "Test", Description = key };
                db.Permissions.Add(permission);
                await db.SaveChangesAsync();
            }
            db.RolePermissions.Add(new RolePermission { RoleId = adminRole.Id, PermissionId = permission.Id });
            if (key == "profile.read") db.RolePermissions.Add(new RolePermission { RoleId = employeeRole.Id, PermissionId = permission.Id });
        }
        var adminId = Guid.NewGuid();
        var email = $"admin.{adminId:N}@seats.example";
        db.Users.Add(new User
        {
            Id = adminId, TenantId = tenantId, Email = email, NormalizedEmail = AuthService.Normalize(email), FullName = "Seat Admin",
            PasswordHash = "test-only-hash", Status = "Active", AccessMode = AccessModes.FullPortal, IsActive = true,
            IsEmailConfirmed = true, IsGroupScope = true,
        });
        db.UserRoles.Add(new UserRole { UserId = adminId, RoleId = adminRole.Id });
        await db.SaveChangesAsync();
        return (tenantId, adminId);
    }

    private ZayraDbContext Db(IInterceptor interceptor) => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(fixture.ConnectionString, PostgresFixture.ProductionProviderOptions)
            .AddInterceptors(RowLockingInterceptor.Instance, AdvisoryXactLockGuardInterceptor.Instance, interceptor)
            .Options);

    private AccessController Controller(ZayraDbContext db, Guid tenantId, Guid callerId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, callerId.ToString()),
            new("permission", "security.manage"),
            new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "group", c = Array.Empty<Guid>() })),
        };
        var service = new AccessManagementService(db, new Pbkdf2PasswordHasher(), new AuditService(db), new NoTokens(), licence: _licence);
        return new AccessController(service, db, null!, licence: _licence)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
    }

    /// <summary>Holds each context at its first locking read of the tenant row until every participant has arrived
    /// (or the timeout passes), so the creates meet at the lock rather than one finishing before the other starts.</summary>
    private sealed class TenantLockRendezvous(int participants, TimeSpan timeout) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<DbConnection, bool> _seen = new();
        private int _arrived;

        public int Arrived => Volatile.Read(ref _arrived);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var text = command.CommandText;
            if (command.Connection is { } connection
                && text.StartsWith("-- " + RowLockingInterceptor.ForUpdateTag, StringComparison.Ordinal)
                && text.Contains("FROM tenants", StringComparison.Ordinal)
                && _seen.TryAdd(connection, true))
            {
                if (Interlocked.Increment(ref _arrived) == participants) _all.TrySetResult();
                await Task.WhenAny(_all.Task, Task.Delay(timeout, cancellationToken));
            }
            return result;
        }
    }

    private sealed class NoTokens : ITokenService
    {
        public string CreateAccessToken(User user, IReadOnlyCollection<string> roles, IReadOnlyCollection<string> permissions, Tenant tenant,
            IReadOnlyCollection<EntityAccessGrant> entityAccess, EntityScopeDescriptor entityScope, out DateTime expiresAtUtc)
        {
            expiresAtUtc = DateTime.UtcNow.AddHours(1);
            return $"fake-access-{user.Id}";
        }
        public string CreateSecureToken() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        public string HashToken(string token) => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }
}
