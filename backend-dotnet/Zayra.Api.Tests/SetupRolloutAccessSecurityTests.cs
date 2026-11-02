using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Auth;

namespace Zayra.Api.Tests;

/// <summary>M3 and L1 of the Setup rollout security review, on the in-memory provider (the Postgres-backed access suites cover the
/// locking). A restored user cannot sign in with the old password or reuse an old reset link; role impact counts live people.</summary>
public class SetupRolloutAccessSecurityTests
{
    private sealed class Tokens : ITokenService
    {
        public string CreateAccessToken(User user, IReadOnlyCollection<string> roles, IReadOnlyCollection<string> permissions, Tenant tenant, IReadOnlyCollection<EntityAccessGrant> entityAccess, EntityScopeDescriptor entityScope, out DateTime expiresAtUtc)
        { expiresAtUtc = DateTime.UtcNow.AddHours(1); return "t"; }
        public string CreateSecureToken() => Guid.NewGuid().ToString("N");
        public string HashToken(string token) => token;
    }

    private sealed class NoAudit : IAuditService
    {
        public Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static (ZayraDbContext Db, AccessManagementService Svc) Boot()
    {
        var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        return (db, new AccessManagementService(db, new Pbkdf2PasswordHasher(), new NoAudit(), new Tokens()));
    }

    [Fact]
    public async Task A_restored_user_has_no_usable_password_no_second_factor_no_reset_link_and_no_session()
    {
        var (db, svc) = Boot(); var tenant = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "T", Slug = "t" });
        var user = new User
        {
            TenantId = tenant, Email = "a@t.com", NormalizedEmail = "A@T.COM", FullName = "A", PasswordHash = "OLD-HASH", IsDeleted = true, IsActive = false,
            Status = "Deactivated", MFAEnabled = true, MfaSecretEncrypted = "secret", MfaRecoveryCodeHashes = "codes",
        };
        db.Users.Add(user);
        db.PasswordResetTokens.Add(new PasswordResetToken { UserId = user.Id, TokenHash = "x", ExpiresAtUtc = DateTime.UtcNow.AddHours(1) });
        db.RefreshTokens.Add(new RefreshToken { UserId = user.Id, TokenHash = "r", ExpiresAtUtc = DateTime.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();

        (await svc.RestoreUserAsync(tenant, user.Id, EntityScopeContext.GroupLevel, new RequestContext("127.0.0.1", "t", null, tenant), default)).Should().BeTrue();

        var back = await db.Users.IgnoreQueryFilters().SingleAsync();
        back.IsDeleted.Should().BeFalse();
        back.IsActive.Should().BeFalse();
        back.Status.Should().Be("PendingPasswordSetup");
        back.PasswordHash.Should().NotBe("OLD-HASH");
        new Pbkdf2PasswordHasher().Verify("anything", back.PasswordHash).Should().BeFalse();
        back.MFAEnabled.Should().BeFalse();
        back.MfaSecretEncrypted.Should().BeNull();
        back.MfaRecoveryCodeHashes.Should().BeNull();
        (await db.PasswordResetTokens.SingleAsync()).UsedAtUtc.Should().NotBeNull();
        (await db.RefreshTokens.IgnoreQueryFilters().SingleAsync()).RevokedAtUtc.Should().NotBeNull();
        // The old path "just reactivate it" is closed: reactivation refuses a pending-password-setup identity.
        var act = () => svc.ActivateUserAsync(tenant, user.Id, EntityScopeContext.GroupLevel, new RequestContext("127.0.0.1", "t", null, tenant), default);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Role_impact_counts_only_live_people()
    {
        var (db, svc) = Boot(); var tenant = Guid.NewGuid();
        var role = new Role { TenantId = tenant, Name = "Custom", NormalizedName = "CUSTOM" };
        var live = new User { TenantId = tenant, Email = "l@t.com", NormalizedEmail = "L@T.COM", FullName = "L" };
        var gone = new User { TenantId = tenant, Email = "g@t.com", NormalizedEmail = "G@T.COM", FullName = "G", IsDeleted = true };
        db.Roles.Add(role); db.Users.AddRange(live, gone);
        db.UserRoles.AddRange(new UserRole { UserId = live.Id, RoleId = role.Id }, new UserRole { UserId = gone.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var impact = await svc.GetRoleImpactAsync(tenant, role.Id, default);

        impact!.AssignedUsers.Should().Be(1);
    }
}
