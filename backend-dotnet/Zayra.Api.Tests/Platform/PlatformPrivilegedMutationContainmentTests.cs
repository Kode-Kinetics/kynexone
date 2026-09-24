using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Controllers;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Platform;

public class PlatformPrivilegedMutationContainmentTests : PlatformTestBase
{
    [Theory]
    [InlineData("Active", null, null)]
    [InlineData(null, true, null)]
    [InlineData(null, null, "Admin")]
    public async Task EditUser_EligibilityOrRoleInput_IsRejectedBeforeAnyProfileMutation(
        string? status,
        bool? isActive,
        string? roleName)
    {
        await using var db = CreateDb();
        var (_, user) = await SeedTenantUserAsync(db, isActive: false, status: "Suspended");
        var originalStamp = user.UpdatedAtUtc;
        var controller = CreateController(db);

        var result = await controller.EditUser(
            user.Id,
            new EditUserRequest("Changed Name", "changed@example.test", status, isActive, roleName),
            CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        db.ChangeTracker.Clear();
        var current = await db.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        current.FullName.Should().Be("Tenant User");
        current.Email.Should().Be("user@example.test");
        current.Status.Should().Be("Suspended");
        current.IsActive.Should().BeFalse();
        current.UpdatedAtUtc.Should().Be(originalStamp);
        (await db.AdminAuditLogs.CountAsync()).Should().Be(0);
        (await db.AuditLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EditUser_ProfileAndEmailOnly_RemainsAvailableWithoutChangingEligibility()
    {
        await using var db = CreateDb();
        var (_, user) = await SeedTenantUserAsync(db, isActive: false, status: "Suspended");
        var controller = CreateController(db);

        var result = await controller.EditUser(
            user.Id,
            new EditUserRequest("Updated Name", "updated@example.test", null, null, null),
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var current = await db.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        current.FullName.Should().Be("Updated Name");
        current.Email.Should().Be("updated@example.test");
        current.NormalizedEmail.Should().Be("UPDATED@EXAMPLE.TEST");
        current.Status.Should().Be("Suspended");
        current.IsActive.Should().BeFalse();
        (await db.AdminAuditLogs.CountAsync(x => x.Action == "UserEdited")).Should().Be(1);
    }

    [Fact]
    public async Task UnlockUser_UsesHardenedWorkflow_WithoutReactivatingInactiveIdentity()
    {
        await using var db = CreateDb();
        var (tenant, user) = await SeedTenantUserAsync(db, isActive: false, status: "Suspended");
        user.IsLocked = true;
        user.LockoutEnd = DateTime.UtcNow.AddMinutes(30);
        user.FailedLoginCount = 5;
        var refresh = ActiveRefresh(user.Id);
        var challenge = ActiveChallenge(tenant.Id, user.Id);
        db.RefreshTokens.Add(refresh);
        db.MfaChallengeTokens.Add(challenge);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var result = await controller.UnlockUser(user.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var current = await db.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        current.IsLocked.Should().BeFalse();
        current.LockoutEnd.Should().BeNull();
        current.FailedLoginCount.Should().Be(0);
        current.IsActive.Should().BeFalse("unlocking must not revive a suspended identity");
        current.Status.Should().Be("Suspended");
        (await db.RefreshTokens.AsNoTracking().SingleAsync(x => x.Id == refresh.Id)).RevokedAtUtc.Should().NotBeNull();
        (await db.MfaChallengeTokens.AsNoTracking().SingleAsync(x => x.Id == challenge.Id)).UsedAtUtc.Should().NotBeNull();
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(x => x.Action == "access.user_unlocked");
        audit.TenantId.Should().Be(tenant.Id);
        audit.UserId.Should().BeNull("a platform principal is not a tenant-user foreign key");
    }

    [Fact]
    public async Task DeleteTenantUser_UsesHardenedWorkflow_AndInvalidatesAllCredentialArtifacts()
    {
        await using var db = CreateDb();
        var (tenant, user) = await SeedTenantUserAsync(db);
        var refresh = ActiveRefresh(user.Id);
        var challenge = ActiveChallenge(tenant.Id, user.Id);
        db.RefreshTokens.Add(refresh);
        db.MfaChallengeTokens.Add(challenge);
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var result = await controller.DeleteTenantUser(user.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var current = await db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == user.Id);
        current.IsDeleted.Should().BeTrue();
        current.IsActive.Should().BeFalse();
        current.Status.Should().Be("Deactivated");
        (await db.RefreshTokens.AsNoTracking().SingleAsync(x => x.Id == refresh.Id)).RevokedAtUtc.Should().NotBeNull();
        (await db.MfaChallengeTokens.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == challenge.Id)).UsedAtUtc.Should().NotBeNull();
        var audit = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Action == "access.user_deleted");
        audit.TenantId.Should().Be(tenant.Id);
        audit.UserId.Should().BeNull("a platform principal is not a tenant-user foreign key");
    }

    [Fact]
    public async Task DeleteTenantUser_PreservesLastActiveAdminConstraint()
    {
        await using var db = CreateDb();
        var (tenant, user) = await SeedTenantUserAsync(db);
        var adminRole = new Role
        {
            TenantId = tenant.Id,
            Name = "Admin",
            NormalizedName = "ADMIN",
            Description = "Tenant administrator"
        };
        db.Roles.Add(adminRole);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = adminRole.Id });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var result = await controller.DeleteTenantUser(user.Id, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        db.ChangeTracker.Clear();
        var current = await db.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        current.IsDeleted.Should().BeFalse();
        current.IsActive.Should().BeTrue();
        (await db.AuditLogs.CountAsync(x => x.Action == "access.user_deleted")).Should().Be(0);
    }

    [Fact]
    public async Task DisableMfa_DelegatesRecovery_AndDoesNotMutateWhenServiceDeclines()
    {
        await using var db = CreateDb();
        var (tenant, user) = await SeedTenantUserAsync(db);
        user.MFAEnabled = true;
        user.MfaSecretEncrypted = "encrypted-secret";
        user.MfaConfiguredAtUtc = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();
        var mfa = new RecordingMfaService { AdminDisableResult = false };
        var controller = CreateController(db, mfaService: mfa);

        var result = await controller.DisableMfa(user.Id, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        mfa.AdminDisableCalls.Should().Be(1);
        mfa.AdminDisabledUserId.Should().Be(user.Id);
        mfa.AdminDisabledTenantId.Should().Be(tenant.Id);
        mfa.AdminDisableContext.Should().NotBeNull();
        mfa.AdminDisableContext!.UserId.Should().BeNull("platform ids must never populate tenant-user audit fields");
        mfa.AdminDisableContext.TenantId.Should().Be(tenant.Id);
        db.ChangeTracker.Clear();
        var current = await db.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        current.MFAEnabled.Should().BeTrue();
        current.MfaSecretEncrypted.Should().Be("encrypted-secret");
        current.MfaConfiguredAtUtc.Should().NotBeNull();
        (await db.LoginActivities.CountAsync()).Should().Be(0);
        (await db.AdminAuditLogs.CountAsync()).Should().Be(0);
    }

    private static async Task<(Tenant Tenant, User User)> SeedTenantUserAsync(
        Zayra.Api.Data.ZayraDbContext db,
        bool isActive = true,
        string status = "Active")
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Containment Tenant",
            Slug = $"containment-{Guid.NewGuid():N}",
            IsActive = true
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Tenant = tenant,
            FullName = "Tenant User",
            Email = "user@example.test",
            NormalizedEmail = "USER@EXAMPLE.TEST",
            PasswordHash = "not-used",
            IsActive = isActive,
            Status = status,
            UpdatedAtUtc = DateTime.UtcNow.AddDays(-2)
        };
        db.Tenants.Add(tenant);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (tenant, user);
    }

    private static RefreshToken ActiveRefresh(Guid userId) => new()
    {
        UserId = userId,
        TokenHash = Guid.NewGuid().ToString("N"),
        ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
        CreatedByIp = "127.0.0.1"
    };

    private static MfaChallengeToken ActiveChallenge(Guid tenantId, Guid userId) => new()
    {
        TenantId = tenantId,
        UserId = userId,
        TokenHash = Guid.NewGuid().ToString("N"),
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
        CreatedByIp = "127.0.0.1"
    };

    private sealed class RecordingMfaService : IMfaService
    {
        public bool AdminDisableResult { get; init; }
        public int AdminDisableCalls { get; private set; }
        public Guid? AdminDisabledUserId { get; private set; }
        public Guid? AdminDisabledTenantId { get; private set; }
        public RequestContext? AdminDisableContext { get; private set; }

        public Task<bool> AdminDisableAsync(
            Guid userId,
            Guid tenantId,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            AdminDisableCalls++;
            AdminDisabledUserId = userId;
            AdminDisabledTenantId = tenantId;
            AdminDisableContext = context;
            return Task.FromResult(AdminDisableResult);
        }

        public Task<MfaSetupInitDto> InitiateSetupAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> VerifySetupAsync(Guid userId, Guid tenantId, MfaVerifySetupRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<string> CreateEnrollmentChallengeAsync(Guid userId, Guid tenantId, string ip, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<MfaSetupInitDto?> InitiateEnrollmentSetupAsync(string enrollmentToken, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> VerifyEnrollmentSetupAsync(string enrollmentToken, MfaVerifySetupRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<string> CreateChallengeAsync(Guid userId, Guid tenantId, string ip, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> DisableAsync(Guid userId, Guid tenantId, string totpCode, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<MfaSetupInitDto> InitiatePlatformSetupAsync(Guid platformUserId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> VerifyPlatformSetupAsync(Guid platformUserId, MfaVerifySetupRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<string> CreatePlatformChallengeAsync(Guid platformUserId, string ip, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<PlatformUser?> VerifyPlatformChallengeAsync(string challengeToken, string totpCode, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<PlatformUser?> CompletePlatformChallengeAsync(string challengeToken, string totpCode, RequestContext context, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> DisablePlatformAsync(Guid platformUserId, string totpCode, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
