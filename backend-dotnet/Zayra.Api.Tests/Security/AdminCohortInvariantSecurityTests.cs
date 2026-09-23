using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

[Trait("Category", "Integration")]
[Collection("Integration")]
public sealed class AdminCohortInvariantSecurityTests
{
    private const int RaceCycles = 20;
    private readonly PostgresFixture _fixture;

    public AdminCohortInvariantSecurityTests(PostgresFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("roles")]
    [InlineData("no-login")]
    [InlineData("suspend")]
    [InlineData("lock")]
    [InlineData("delete")]
    public async Task BlockingWriter_CannotRemoveOrBlockLastOperationalAdmin(string writer)
    {
        var seed = await SeedAdminsAsync(1);
        var callerId = Guid.NewGuid();
        await using var db = _fixture.CreateRetryingDb();
        var service = CreateService(db);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => writer switch
        {
            "roles" => AsTask(service.AssignRolesAsync(
                seed.TenantId,
                seed.UserIds[0],
                new AssignRolesRequest(new[] { "Employee" }),
                EntityScopeContext.GroupLevel,
                Context(seed.TenantId, callerId),
                CancellationToken.None)),
            "no-login" => AsTask(service.SetAccessModeAsync(
                seed.TenantId,
                seed.UserIds[0],
                new AccessModeRequest(AccessModes.NoLogin, "test"),
                EntityScopeContext.GroupLevel,
                Context(seed.TenantId, callerId),
                CancellationToken.None)),
            "suspend" => service.SuspendUserAsync(
                seed.TenantId,
                seed.UserIds[0],
                "test",
                EntityScopeContext.GroupLevel,
                Context(seed.TenantId, callerId),
                CancellationToken.None),
            "lock" => service.LockUserAsync(
                seed.TenantId,
                seed.UserIds[0],
                "test",
                EntityScopeContext.GroupLevel,
                Context(seed.TenantId, callerId),
                CancellationToken.None),
            "delete" => AsTask(service.DeleteUserAsync(
                seed.TenantId,
                seed.UserIds[0],
                EntityScopeContext.GroupLevel,
                Context(seed.TenantId, callerId),
                CancellationToken.None)),
            _ => throw new InvalidOperationException("Unknown test writer.")
        });

        Assert.Contains("last administrator", error.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = _fixture.CreateRetryingDb();
        var admin = await verify.Users.IgnoreQueryFilters().AsNoTracking()
            .Include(x => x.UserRoles).ThenInclude(x => x.Role)
            .SingleAsync(x => x.Id == seed.UserIds[0]);
        Assert.True(admin.IsActive);
        Assert.False(admin.IsDeleted);
        Assert.False(admin.IsLocked);
        Assert.Equal("Active", admin.Status);
        Assert.NotEqual(AccessModes.NoLogin, admin.AccessMode);
        Assert.Contains(admin.UserRoles, x => x.Role?.NormalizedName == "ADMIN");
    }

    [Theory]
    [InlineData("no-login")]
    [InlineData("suspend")]
    [InlineData("lock")]
    public async Task BlockingWriter_CannotDisableCallingUser(string writer)
    {
        var seed = await SeedAdminsAsync(2);
        var callerId = seed.UserIds[0];
        await using var db = _fixture.CreateRetryingDb();
        var service = CreateService(db);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => writer switch
        {
            "no-login" => AsTask(service.SetAccessModeAsync(
                seed.TenantId,
                callerId,
                new AccessModeRequest(AccessModes.NoLogin, "self-test"),
                EntityScopeContext.GroupLevel,
                Context(seed.TenantId, callerId),
                CancellationToken.None)),
            "suspend" => service.SuspendUserAsync(
                seed.TenantId,
                callerId,
                "self-test",
                EntityScopeContext.GroupLevel,
                Context(seed.TenantId, callerId),
                CancellationToken.None),
            "lock" => service.LockUserAsync(
                seed.TenantId,
                callerId,
                "self-test",
                EntityScopeContext.GroupLevel,
                Context(seed.TenantId, callerId),
                CancellationToken.None),
            _ => throw new InvalidOperationException("Unknown test writer.")
        });

        Assert.Contains("own account", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentSuspension_OfTwoAdmins_HasExactlyOneWinnerPerCycle()
    {
        for (var cycle = 0; cycle < RaceCycles; cycle++)
        {
            var seed = await SeedAdminsAsync(2);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<bool> SuspendAsync(Guid userId)
            {
                await using var db = _fixture.CreateRetryingDb();
                var service = CreateService(db);
                await gate.Task;
                try
                {
                    await service.SuspendUserAsync(
                        seed.TenantId,
                        userId,
                        "race-test",
                        EntityScopeContext.GroupLevel,
                        Context(seed.TenantId, Guid.NewGuid()),
                        CancellationToken.None);
                    return true;
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.Contains("last administrator", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            var attempts = seed.UserIds.Select(SuspendAsync).ToArray();
            gate.SetResult();
            var outcomes = await Task.WhenAll(attempts);
            Assert.Equal(1, outcomes.Count(x => x));
            Assert.Equal(1, outcomes.Count(x => !x));

            await using var verify = _fixture.CreateRetryingDb();
            var operationalAdmins = await verify.Users.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(x => x.TenantId == seed.TenantId
                    && !x.IsDeleted
                    && x.IsActive
                    && !x.IsLocked
                    && x.Status == "Active"
                    && x.AccessMode != AccessModes.NoLogin
                    && x.UserRoles.Any(ur => ur.Role != null
                        && ur.Role.NormalizedName == "ADMIN"
                        && ur.Role.IsActive
                        && !ur.Role.IsDeleted));
            Assert.Equal(1, operationalAdmins);
        }
    }

    private async Task<Seed> SeedAdminsAsync(int count)
    {
        await using var db = _fixture.CreateRetryingDb();
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var adminRole = new Role
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Admin",
            NormalizedName = "ADMIN",
            Description = "Admin",
            IsActive = true,
            IsEditable = true
        };
        db.Roles.Add(adminRole);
        var userIds = new List<Guid>();
        for (var index = 0; index < count; index++)
        {
            var id = Guid.NewGuid();
            var email = $"admin-{id:N}@example.test";
            var user = new User
            {
                Id = id,
                TenantId = tenantId,
                Email = email,
                NormalizedEmail = AuthService.Normalize(email),
                FullName = $"Admin {index + 1}",
                PasswordHash = "test-only-hash",
                Status = "Active",
                AccessMode = AccessModes.FullPortal,
                IsActive = true,
                IsEmailConfirmed = true
            };
            db.Users.Add(user);
            db.UserRoles.Add(new UserRole { User = user, Role = adminRole });
            userIds.Add(id);
        }
        await db.SaveChangesAsync();
        return new Seed(tenantId, userIds);
    }

    private static RequestContext Context(Guid tenantId, Guid callerId) =>
        new("203.0.113.140", "admin-cohort-invariant-tests", callerId, tenantId);

    private static async Task AsTask<T>(Task<T> task) => _ = await task;

    private static AccessManagementService CreateService(ZayraDbContext db) =>
        new(db, new Pbkdf2PasswordHasher(), new NullAuditService(), new FakeTokenService());

    private sealed record Seed(Guid TenantId, IReadOnlyList<Guid> UserIds);

    private sealed class FakeTokenService : ITokenService
    {
        public string CreateAccessToken(
            User user,
            IReadOnlyCollection<string> roles,
            IReadOnlyCollection<string> permissions,
            Tenant tenant,
            IReadOnlyCollection<EntityAccessGrant> entityAccess,
            EntityScopeDescriptor entityScope,
            out DateTime expiresAtUtc)
        {
            expiresAtUtc = DateTime.UtcNow.AddHours(1);
            return $"fake-access-{user.Id}";
        }

        public string CreateSecureToken() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        public string HashToken(string token) => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();
    }

    private sealed class NullAuditService : IAuditService
    {
        public Task WriteAsync(
            string action,
            string entityName,
            string? entityId,
            RequestContext context,
            string? metadata,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
