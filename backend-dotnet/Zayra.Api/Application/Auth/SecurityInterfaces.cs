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

    /// <summary>
    /// A STANDALONE event: a row that has no business change in the same request to be atomic with, so there
    /// is nothing to stage ahead of. Denied or failed attempts, refusals, and the after-the-fact summary of a
    /// platform job that committed its own batches. Anything that changes a record must use <see cref="Stage"/>
    /// before that record's save instead; the unstaged-caller ratchet counts <see cref="WriteAsync"/> only,
    /// so using this for a business change would defeat it.
    /// </summary>
    Task RecordEventAsync(string action, string entityName, string? entityId, RequestContext context, string? metadata, CancellationToken cancellationToken)
        => WriteAsync(action, entityName, entityId, context, metadata, cancellationToken);

    /// <summary>
    /// Adds the audit row to the context without saving, so it commits in the SAME SaveChanges (one
    /// transaction) as the business change that follows. Prefer this over <see cref="WriteAsync"/> on
    /// any write path where the change must not exist without its audit row.
    /// </summary>
    /// <remarks>The default writes immediately through <see cref="WriteAsync"/> so test doubles that
    /// record or throw on audit keep their behaviour; the only production implementation
    /// (<c>AuditService</c>) overrides it with a true no-save stage, and a test pins that.</remarks>
    void Stage(string action, string entityName, string? entityId, RequestContext context, string? metadata) =>
        WriteAsync(action, entityName, entityId, context, metadata, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Drops audit rows that were staged but not saved. Call it in a catch block that swallows or
    /// translates a failed save and keeps using the same context, so the unsaved rows cannot commit
    /// later as a phantom record of something that was rolled back. Test doubles: no-op.
    /// </summary>
    void DiscardStaged() { }

    /// <summary>
    /// Adds the audit row to the context WITHOUT saving, so it commits in the SAME SaveChanges as the business change it describes
    /// (<see cref="WriteAsync"/> saves separately, so a failure between the two leaves a change with no audit row). The context seals the
    /// row into the per-tenant hash chain at save, under the same advisory lock every audit write takes.
    /// </summary>
    /// <param name="beforeAfterJson">The metadata, conventionally <c>{"before":…,"after":…,"reason":…}</c>.</param>
    /// <param name="companyId">The company the change belongs to (null for a tenant-level change).</param>
    void Stage(string action, string entityName, string? entityId, RequestContext context, string? beforeAfterJson, Guid? companyId)
        => throw new NotSupportedException($"{GetType().Name} does not support staged audit rows.");
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
    async Task<Zayra.Api.Application.Auth.MfaDisableOutcome> DisableWithOutcomeAsync(Guid userId, Guid tenantId, string? totpCode, string? backupCode, RequestContext context, CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(backupCode)
            ? await DisableWithOutcomeAsync(userId, tenantId, totpCode ?? string.Empty, context, cancellationToken)
            : throw new NotSupportedException("This IMfaService does not support disabling with a backup code.");

    // ── Platform user MFA ─────────────────────────────────────────────────────
    Task<MfaSetupInitDto> InitiatePlatformSetupAsync(Guid platformUserId, CancellationToken cancellationToken);
    /// <summary>Enables the factor; returns the one-time recovery codes (shown once), or null on failure.</summary>
    Task<IReadOnlyList<string>?> VerifyPlatformSetupAsync(Guid platformUserId, Zayra.Api.Application.Auth.MfaVerifySetupRequest request, CancellationToken cancellationToken);
    Task<string> CreatePlatformChallengeAsync(Guid platformUserId, string ip, CancellationToken cancellationToken);
    Task<Zayra.Api.Models.PlatformUser?> VerifyPlatformChallengeAsync(string challengeToken, string totpCode, CancellationToken cancellationToken);
    Task<Zayra.Api.Models.PlatformUser?> CompletePlatformChallengeAsync(string challengeToken, string totpCode, RequestContext context, CancellationToken cancellationToken);
    Task<bool> DisablePlatformAsync(Guid platformUserId, string totpCode, CancellationToken cancellationToken);

    // ── Platform mandatory-MFA enrolment (PrivilegedMfaPolicy) ───────────────
    // Default bodies keep the test doubles that predate mandatory MFA compiling; MfaService
    // implements all three. A double reaching one of them means a test hit the enforcement path
    // without a real MFA service, which should fail loudly.
    /// <summary>Setup-only token for a platform operator who must enrol before any session.</summary>
    Task<string> CreatePlatformEnrollmentChallengeAsync(Guid platformUserId, string ip, CancellationToken cancellationToken)
        => throw new NotSupportedException("This IMfaService does not support platform MFA enrolment.");
    Task<MfaSetupInitDto?> InitiatePlatformEnrollmentSetupAsync(string enrollmentToken, CancellationToken cancellationToken)
        => throw new NotSupportedException("This IMfaService does not support platform MFA enrolment.");
    /// <summary>Enables the factor with a setup-only token; returns the one-time recovery codes, or null.</summary>
    Task<IReadOnlyList<string>?> VerifyPlatformEnrollmentSetupAsync(string enrollmentToken, Zayra.Api.Application.Auth.MfaVerifySetupRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This IMfaService does not support platform MFA enrolment.");
    /// <summary>Completes a platform sign-in challenge with a one-time recovery code instead of TOTP.
    /// The code is consumed; the use is audited.</summary>
    Task<Zayra.Api.Models.PlatformUser?> CompletePlatformChallengeWithRecoveryCodeAsync(string challengeToken, string recoveryCode, RequestContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException("This IMfaService does not support recovery codes.");
    /// <summary>Replaces all recovery codes; requires a current TOTP code. Returns the new codes, or null.</summary>
    Task<IReadOnlyList<string>?> RegeneratePlatformRecoveryCodesAsync(Guid platformUserId, string totpCode, CancellationToken cancellationToken)
        => throw new NotSupportedException("This IMfaService does not support recovery codes.");
    /// <summary>Break-glass: an Owner clears ANOTHER operator's factor (lost device). The operator
    /// re-enrols at next sign-in; all their sessions and pending challenges end.</summary>
    Task<bool> AdminResetPlatformFactorAsync(Guid platformUserId, Guid actingPlatformUserId, RequestContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException("This IMfaService does not support platform factor reset.");
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
