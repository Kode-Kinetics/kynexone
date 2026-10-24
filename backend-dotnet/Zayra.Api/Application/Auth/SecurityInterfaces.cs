using Zayra.Api.Application.Common;
using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Application.Auth;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string passwordHash);

    /// <summary>
    /// True when <paramref name="passwordHash"/> verifies under an older work factor than
    /// <see cref="Hash"/> would produce today. Callers that have JUST verified the plaintext
    /// re-hash it and store the result (rehash-on-login), so old hashes upgrade transparently
    /// without ever being invalidated. Default false keeps simple test doubles valid.
    /// </summary>
    bool NeedsRehash(string passwordHash) => false;
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
    /// <summary>Creates a short-lived setup-only token after password verification when tenant policy requires first-time MFA enrollment.</summary>
    Task<string> CreateEnrollmentChallengeAsync(Guid userId, Guid tenantId, string ip, CancellationToken cancellationToken);
    /// <summary>Generates a TOTP setup URI only when a valid setup-only enrollment token is presented.</summary>
    Task<MfaSetupInitDto?> InitiateEnrollmentSetupAsync(string enrollmentToken, CancellationToken cancellationToken);
    /// <summary>Verifies setup for a setup-only enrollment token. Consumes the token on success or after repeated bad codes.</summary>
    Task<bool> VerifyEnrollmentSetupAsync(string enrollmentToken, Zayra.Api.Application.Auth.MfaVerifySetupRequest request, CancellationToken cancellationToken);
    /// <summary>Creates a short-lived MFA challenge token after the user passes password verification.</summary>
    Task<string> CreateChallengeAsync(Guid userId, Guid tenantId, string ip, CancellationToken cancellationToken);
    /// <summary>Disables MFA for a tenant user (requires valid TOTP code as proof).</summary>
    Task<bool> DisableAsync(Guid userId, Guid tenantId, string totpCode, CancellationToken cancellationToken);
    /// <summary>Privileged recovery: disables a tenant factor under platform authorization and full credential invalidation.</summary>
    Task<bool> AdminDisableAsync(Guid userId, Guid tenantId, RequestContext context, CancellationToken cancellationToken);
    /// <summary>VerifySetupAsync with the reason a failure happened (Amendment 1 coded errors).</summary>
    async Task<Zayra.Api.Application.Auth.MfaSetupOutcome> VerifySetupWithOutcomeAsync(Guid userId, Guid tenantId, Zayra.Api.Application.Auth.MfaVerifySetupRequest request, CancellationToken cancellationToken)
        => await VerifySetupAsync(userId, tenantId, request, cancellationToken)
            ? Zayra.Api.Application.Auth.MfaSetupOutcome.Enabled
            : Zayra.Api.Application.Auth.MfaSetupOutcome.CodeInvalid;
    /// <summary>The user turns two-step off: refused for a privileged role or a company-wide mandate (Amendment 1).</summary>
    /// <summary>VerifySetupWithOutcomeAsync that also returns the new backup codes (shown once) and the factor's
    /// configured-at stamp when it enabled the factor (the controller re-issues the caller's session against it).</summary>
    async Task<Zayra.Api.Application.Auth.MfaSetupResult> VerifySetupWithBackupCodesAsync(
        Guid userId, Guid tenantId, Zayra.Api.Application.Auth.MfaVerifySetupRequest request, CancellationToken cancellationToken)
        => new(await VerifySetupWithOutcomeAsync(userId, tenantId, request, cancellationToken), null, null);
    /// <summary>VerifyEnrollmentSetupAsync that returns the new backup codes on success (null on failure).</summary>
    async Task<IReadOnlyList<string>?> VerifyEnrollmentSetupWithBackupCodesAsync(string enrollmentToken, Zayra.Api.Application.Auth.MfaVerifySetupRequest request, CancellationToken cancellationToken)
        => await VerifyEnrollmentSetupAsync(enrollmentToken, request, cancellationToken) ? Array.Empty<string>() : null;
    /// <summary>Replaces the tenant user's backup codes; needs a current app code. The old set stops working.</summary>
    Task<(Zayra.Api.Application.Auth.MfaBackupCodesOutcome Outcome, IReadOnlyList<string>? BackupCodes)> RegenerateBackupCodesAsync(
        Guid userId, Guid tenantId, string totpCode, RequestContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException("This IMfaService does not support backup codes.");
    async Task<Zayra.Api.Application.Auth.MfaDisableOutcome> DisableWithOutcomeAsync(Guid userId, Guid tenantId, string totpCode, RequestContext context, CancellationToken cancellationToken)
        => await DisableAsync(userId, tenantId, totpCode, cancellationToken)
            ? Zayra.Api.Application.Auth.MfaDisableOutcome.Disabled
            : Zayra.Api.Application.Auth.MfaDisableOutcome.CodeInvalid;

}

public record MfaSetupInitDto(string ProvisioningUri, string TempSecret);

public interface IEnterpriseIdentityService
{
    Task<EnterpriseIdentitySettingsDto> GetSettingsAsync(Guid tenantId, CancellationToken ct);
    Task<EnterpriseIdentitySettingsDto> UpdateSettingsAsync(Guid tenantId, UpdateEnterpriseIdentitySettingsRequest request, RequestContext context, CancellationToken ct);
    Task<RotateScimTokenResponse> RotateScimTokenAsync(Guid tenantId, RequestContext context, CancellationToken ct);
    Task<SamlServiceProviderMetadataDto?> GetSamlMetadataAsync(string tenantSlug, string baseUrl, CancellationToken ct);
    Task<OidcTenantMetadataDto?> GetOidcMetadataAsync(string tenantSlug, string baseUrl, CancellationToken ct);
    Task<EnterpriseIdentityValidationResult> ValidateSettingsAsync(Guid tenantId, CancellationToken ct);
    Task<Guid?> ValidateScimTokenAsync(string rawToken, CancellationToken ct);
    Task<ScimListResponse> ListScimUsersAsync(Guid tenantId, int startIndex, int count, CancellationToken ct);
    Task<ScimUserResource> UpsertScimUserAsync(Guid tenantId, ScimUserUpsertRequest request, RequestContext context, CancellationToken ct);
    Task<ScimUserResource?> PatchScimUserAsync(Guid tenantId, Guid userId, ScimPatchRequest request, RequestContext context, CancellationToken ct);
    Task<bool> DeactivateScimUserAsync(Guid tenantId, Guid userId, RequestContext context, CancellationToken ct);
}

public interface IAuthSeeder
{
    Task SeedAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates the full standard role set (Admin → Employee) for a tenant and returns its Admin role. Idempotent.</summary>
    Task<Zayra.Api.Domain.Entities.Role> EnsureTenantRolesAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
