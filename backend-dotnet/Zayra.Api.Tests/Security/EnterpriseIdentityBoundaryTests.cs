using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

public class EnterpriseIdentityBoundaryTests
{
    private static readonly RequestContext Ctx = new("127.0.0.1", "tests", Guid.NewGuid(), null);

    [Fact]
    public async Task ValidateSettings_RejectsSsoEnforcementWithoutValidProviderAndDomain()
    {
        using var db = CreateDb();
        var (tenant, _) = await SeedTenantAndAdminAsync(db);
        var svc = BuildService(db);

        await svc.UpdateSettingsAsync(tenant.Id, new UpdateEnterpriseIdentitySettingsRequest(
            SamlEnabled: false,
            OidcEnabled: false,
            ScimEnabled: null,
            EnforceSsoLogin: false,
            ScimDryRun: null,
            AllowedDomains: Array.Empty<string>(),
            SamlEntityId: null,
            SamlSsoUrl: null,
            SamlCertificateThumbprint: null,
            OidcAuthority: null,
            OidcClientId: null,
            OidcClientSecretConfigured: null), Ctx, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.UpdateSettingsAsync(tenant.Id,
            new UpdateEnterpriseIdentitySettingsRequest(null, null, null, EnforceSsoLogin: true, null, null, null, null, null, null, null, null),
            Ctx, CancellationToken.None));

        // Rejected settings must not remain tracked and leak through a later unit of work.
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.False((await db.TenantIdentityProviderSettings.SingleAsync()).EnforceSsoLogin);
    }

    [Fact]
    public async Task MetadataEndpoints_AreConfigurationOnly_NotFederationAdapters()
    {
        using var db = CreateDb();
        var (tenant, _) = await SeedTenantAndAdminAsync(db);
        var svc = BuildService(db);
        await svc.UpdateSettingsAsync(tenant.Id, new UpdateEnterpriseIdentitySettingsRequest(
            SamlEnabled: true,
            OidcEnabled: true,
            ScimEnabled: null,
            EnforceSsoLogin: false,
            ScimDryRun: null,
            AllowedDomains: new[] { "example.com" },
            SamlEntityId: "https://idp.example.com/saml",
            SamlSsoUrl: "https://idp.example.com/sso",
            SamlCertificateThumbprint: "ABCD",
            OidcAuthority: "https://idp.example.com",
            OidcClientId: "client-1",
            OidcClientSecretConfigured: true), Ctx, CancellationToken.None);

        var saml = await svc.GetSamlMetadataAsync(tenant.Slug, "https://app.example.com", CancellationToken.None);
        var oidc = await svc.GetOidcMetadataAsync(tenant.Slug, "https://app.example.com", CancellationToken.None);

        Assert.NotNull(saml);
        Assert.Equal("https://app.example.com/saml/acme", saml!.EntityId);
        Assert.NotNull(oidc);
        Assert.Equal("client-1", oidc!.ClientId);
        Assert.Contains("code", oidc.ResponseTypesSupported);
    }

    [Fact]
    public async Task ScimTokenBoundary_RequiresRotatedBearerToken()
    {
        using var db = CreateDb();
        var (tenant, _) = await SeedTenantAndAdminAsync(db);
        var svc = BuildService(db);
        await svc.UpdateSettingsAsync(tenant.Id, new UpdateEnterpriseIdentitySettingsRequest(
            null, null, ScimEnabled: true, null, ScimDryRun: false, AllowedDomains: new[] { "example.com" },
            null, null, null, null, null, null), Ctx, CancellationToken.None);

        Assert.Null(await svc.ValidateScimTokenAsync("wrong", CancellationToken.None));
        var rotated = await svc.RotateScimTokenAsync(tenant.Id, Ctx, CancellationToken.None);

        Assert.Equal(tenant.Id, await svc.ValidateScimTokenAsync(rotated.Token, CancellationToken.None));
        Assert.DoesNotContain(rotated.Token, (await db.TenantIdentityProviderSettings.SingleAsync()).ScimTokenHash);
    }

    [Fact]
    public async Task ScimUpsertAndDeactivate_CreateJmlLedgerAndRevokeSessions()
    {
        using var db = CreateDb();
        var (tenant, _) = await SeedTenantAndAdminAsync(db);
        var svc = BuildService(db);
        await svc.UpdateSettingsAsync(tenant.Id, new UpdateEnterpriseIdentitySettingsRequest(
            null, null, ScimEnabled: true, null, ScimDryRun: false, AllowedDomains: new[] { "example.com" },
            null, null, null, null, null, null), Ctx, CancellationToken.None);
        await svc.RotateScimTokenAsync(tenant.Id, Ctx, CancellationToken.None);

        var created = await svc.UpsertScimUserAsync(tenant.Id,
            new ScimUserUpsertRequest("ext-1", "person@example.com", true, new ScimName("Pat", "Person", "Pat Person"), new[] { new ScimEmail("person@example.com") }, "Pat Person"),
            Ctx, CancellationToken.None);
        var userId = Guid.Parse(created.Id);
        db.RefreshTokens.Add(new RefreshToken { UserId = userId, TokenHash = "hash", ExpiresAtUtc = DateTime.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();

        Assert.True(await svc.DeactivateScimUserAsync(tenant.Id, userId, Ctx, CancellationToken.None));

        var user = await db.Users.SingleAsync(x => x.Id == userId);
        Assert.False(user.IsActive);
        Assert.Equal(AccessModes.NoLogin, user.AccessMode);
        Assert.NotNull((await db.RefreshTokens.SingleAsync(x => x.UserId == userId)).RevokedAtUtc);
        Assert.True(await db.EnterpriseIdentityProvisioningEvents.AnyAsync(x => x.Action == EnterpriseIdentityEventActions.ScimUserCreated));
        Assert.True(await db.EnterpriseIdentityProvisioningEvents.AnyAsync(x => x.Action == EnterpriseIdentityEventActions.ScimUserDeactivated));
    }

    [Fact]
    public async Task AuthService_BlocksLocalPasswordForSsoProvisionedUsers_WhenSsoEnforced()
    {
        using var db = CreateDb();
        var (tenant, user) = await SeedTenantAndAdminAsync(db);
        user.IdentityProvider = EnterpriseIdentityProtocols.Scim;
        db.TenantIdentityProviderSettings.Add(new TenantIdentityProviderSetting
        {
            TenantId = tenant.Id,
            EnforceSsoLogin = true,
            OidcEnabled = true,
            AllowedDomainsCsv = "example.com",
            OidcAuthority = "https://idp.example.com",
            OidcClientId = "client-1"
        });
        await db.SaveChangesAsync();

        var auth = BuildAuthService(db);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            auth.LoginAsync(new LoginRequest(user.Email, "CorrectPassword1!", tenant.Slug), Ctx, CancellationToken.None));
    }

    private static ZayraDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ZayraDbContext(options);
    }

    private static EnterpriseIdentityService BuildService(ZayraDbContext db) =>
        new(db, new TestTokenService(), new Pbkdf2PasswordHasher(), new AuditService(db));

    private static AuthService BuildAuthService(ZayraDbContext db)
    {
        var jwt = Options.Create(new JwtOptions
        {
            Issuer = "Zayra.Tests",
            TenantAudience = "tenant",
            PlatformAudience = "platform",
            SigningKey = "TEST_SIGNING_KEY_WITH_MORE_THAN_64_CHARACTERS_FOR_AUTH_TESTS",
            AccessTokenMinutes = 30,
            RefreshTokenDays = 7
        });
        return new AuthService(db, new Pbkdf2PasswordHasher(), new JwtTokenService(jwt), new AuditService(db),
            new FakeEmailService(), jwt, new NullMfaService(), NullLogger<AuthService>.Instance);
    }

    private static async Task<(Tenant tenant, User user)> SeedTenantAndAdminAsync(ZayraDbContext db)
    {
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        var role = new Role { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Admin", NormalizedName = "ADMIN", Description = "Admin" };
        var hasher = new Pbkdf2PasswordHasher();
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Tenant = tenant,
            Email = "admin@example.com",
            NormalizedEmail = "ADMIN@EXAMPLE.COM",
            FullName = "Admin User",
            PasswordHash = hasher.Hash("CorrectPassword1!"),
            IsGroupScope = true
        };
        db.Tenants.Add(tenant);
        db.Roles.Add(role);
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return (tenant, user);
    }

    private sealed class TestTokenService : ITokenService
    {
        public string CreateAccessToken(User user, IReadOnlyCollection<string> roles, IReadOnlyCollection<string> permissions, Tenant tenant, IReadOnlyCollection<EntityAccessGrant> entityAccess, EntityScopeDescriptor entityScope, out DateTime expiresAtUtc)
        {
            expiresAtUtc = DateTime.UtcNow.AddMinutes(30);
            return $"access-{user.Id}";
        }

        public string CreateSecureToken() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        public string HashToken(string token)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        }
    }

    private sealed class FakeEmailService : IEmailService
    {
        public Task SendAsync(string toAddress, string toName, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class NullMfaService : IMfaService
    {
        public Task<MfaSetupInitDto> InitiateSetupAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> VerifySetupAsync(Guid userId, Guid tenantId, MfaVerifySetupRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<string> CreateEnrollmentChallengeAsync(Guid userId, Guid tenantId, string ip, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<MfaSetupInitDto?> InitiateEnrollmentSetupAsync(string enrollmentToken, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> VerifyEnrollmentSetupAsync(string enrollmentToken, MfaVerifySetupRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<string> CreateChallengeAsync(Guid userId, Guid tenantId, string ip, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<User?> VerifyChallengeAsync(string challengeToken, string totpCode, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> DisableAsync(Guid userId, Guid tenantId, string totpCode, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<MfaSetupInitDto> InitiatePlatformSetupAsync(Guid platformUserId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> VerifyPlatformSetupAsync(Guid platformUserId, MfaVerifySetupRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<string> CreatePlatformChallengeAsync(Guid platformUserId, string ip, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<PlatformUser?> VerifyPlatformChallengeAsync(string challengeToken, string totpCode, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> DisablePlatformAsync(Guid platformUserId, string totpCode, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
