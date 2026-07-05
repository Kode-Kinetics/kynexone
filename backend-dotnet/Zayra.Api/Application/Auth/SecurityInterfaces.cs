using Zayra.Api.Application.Common;
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Application.Auth;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string passwordHash);
}

public interface ITokenService
{
    string CreateAccessToken(User user, IReadOnlyCollection<string> roles, IReadOnlyCollection<string> permissions, Tenant tenant, IReadOnlyCollection<EntityAccessGrant> entityAccess, EntityScopeDescriptor entityScope, out DateTime expiresAtUtc);
    string CreateSecureToken();
    string HashToken(string token);
}

public interface IAuditService
{
    Task WriteAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken);
}

public interface IMfaService
{
    // ── Tenant user MFA ───────────────────────────────────────────────────────
    /// <summary>Generates a new TOTP secret and provisioning URI. Secret is NOT saved yet — call VerifySetupAsync to confirm.</summary>
    Task<MfaSetupInitDto> InitiateSetupAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken);
    /// <summary>Verifies the setup code; if valid, persists the encrypted secret and enables MFA.</summary>
    Task<bool> VerifySetupAsync(Guid userId, Guid tenantId, Zayra.Api.Application.Auth.MfaVerifySetupRequest request, CancellationToken cancellationToken);
    /// <summary>Creates a short-lived MFA challenge token after the user passes password verification.</summary>
    Task<string> CreateChallengeAsync(Guid userId, Guid tenantId, string ip, CancellationToken cancellationToken);
    /// <summary>Verifies the challenge token + TOTP code; returns the user if both are valid.</summary>
    Task<Zayra.Api.Domain.Entities.User?> VerifyChallengeAsync(string challengeToken, string totpCode, CancellationToken cancellationToken);
    /// <summary>Disables MFA for a tenant user (requires valid TOTP code as proof).</summary>
    Task<bool> DisableAsync(Guid userId, Guid tenantId, string totpCode, CancellationToken cancellationToken);

    // ── Platform user MFA ─────────────────────────────────────────────────────
    Task<MfaSetupInitDto> InitiatePlatformSetupAsync(Guid platformUserId, CancellationToken cancellationToken);
    Task<bool> VerifyPlatformSetupAsync(Guid platformUserId, Zayra.Api.Application.Auth.MfaVerifySetupRequest request, CancellationToken cancellationToken);
    Task<string> CreatePlatformChallengeAsync(Guid platformUserId, string ip, CancellationToken cancellationToken);
    Task<Zayra.Api.Models.PlatformUser?> VerifyPlatformChallengeAsync(string challengeToken, string totpCode, CancellationToken cancellationToken);
    Task<bool> DisablePlatformAsync(Guid platformUserId, string totpCode, CancellationToken cancellationToken);
}

public record MfaSetupInitDto(string ProvisioningUri, string TempSecret);

public interface IAuthSeeder
{
    Task SeedAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates the full standard role set (Admin → Employee) for a tenant and returns its Admin role. Idempotent.</summary>
    Task<Zayra.Api.Domain.Entities.Role> EnsureTenantRolesAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
