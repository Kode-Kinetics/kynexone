using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Email;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

public class MfaService : IMfaService
{
    private const int ChallengeTtlSeconds = 300; // 5 minutes
    /// <summary>
    /// The issuer label authenticator apps show next to the account. Customer-visible, so it carries
    /// the product name. A label only: factors enrolled under the old "Zayra HRM" label keep working,
    /// because codes depend on the shared secret alone.
    /// </summary>
    internal const string Issuer = "KynexOne";

    private readonly ZayraDbContext _db;
    private readonly TotpService _totp;
    private readonly ITokenService _tokenService;
    private readonly IAuditService _audit;

    private readonly IEmailService? _email;
    private readonly ILogger<MfaService>? _log;

    public MfaService(ZayraDbContext db, TotpService totp, ITokenService tokenService, IAuditService audit,
        IEmailService? email = null, ILogger<MfaService>? log = null)
    {
        _email = email;
        _log = log;
        _db = db;
        _totp = totp;
        _tokenService = tokenService;
        _audit = audit;
    }

    // ── Tenant user ───────────────────────────────────────────────────────────

    public async Task<MfaSetupInitDto> InitiateSetupAsync(Guid userId, Guid tenantId, CancellationToken ct)
    {
        var user = await LoadUser(userId, ct) ?? throw new InvalidOperationException("User not found.");
        if (user.TenantId != tenantId || user.MFAEnabled || !string.IsNullOrWhiteSpace(user.MfaSecretEncrypted))
            throw new InvalidOperationException("MFA is already configured. Use the approved recovery flow to replace a factor.");
        var tempSecret = _totp.GenerateBase32Secret();
        var uri = _totp.GenerateProvisioningUri(user.Email, Issuer, tempSecret);
        return new MfaSetupInitDto(uri, tempSecret);
    }

    public async Task<bool> VerifySetupAsync(Guid userId, Guid tenantId, MfaVerifySetupRequest request, CancellationToken ct)
        => await VerifySetupWithOutcomeAsync(userId, tenantId, request, ct) == MfaSetupOutcome.Enabled;

    /// <summary>
    /// Turns two-step on from a signed-in session. Outcomes (Amendment 1): AlreadyEnabled (409) when a factor is on,
    /// SetupInvalid (400) when the temp secret is not one we could have issued or the account cannot enrol now,
    /// CodeInvalid (400) for a wrong code. Success rotates the security stamp (ends sessions, as before) and revokes
    /// every trusted browser (mfa_resetup) in the same transaction.
    /// </summary>
    public async Task<MfaSetupOutcome> VerifySetupWithOutcomeAsync(Guid userId, Guid tenantId, MfaVerifySetupRequest request, CancellationToken ct)
        => (await VerifySetupWithBackupCodesAsync(userId, tenantId, request, ct)).Outcome;

    /// <summary>
    /// <see cref="VerifySetupWithOutcomeAsync"/>, and on success the ten new backup codes (shown once). The set is
    /// generated before the transaction and stored in it, with <c>auth.mfa.backup_codes_generated</c>. Every refresh
    /// token of the user is revoked in the same transaction (Amendment 1: other sessions end; the rotated stamp alone
    /// would leave them able to refresh), and the caller's own session is re-issued afterwards by the controller.
    /// </summary>
    public async Task<MfaSetupResult> VerifySetupWithBackupCodesAsync(
        Guid userId, Guid tenantId, MfaVerifySetupRequest request, CancellationToken ct)
    {
        var current = await _db.Users.AsNoTracking()
            .Where(x => x.Id == userId && x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => new { x.MFAEnabled, x.MfaSecretEncrypted })
            .SingleOrDefaultAsync(ct);
        if (current is null) return new(MfaSetupOutcome.SetupInvalid, null, null);
        if (current.MFAEnabled || !string.IsNullOrWhiteSpace(current.MfaSecretEncrypted)) return new(MfaSetupOutcome.AlreadyEnabled, null, null);
        if (!TotpService.IsWellFormedSecret(request.TempSecret)) return new(MfaSetupOutcome.SetupInvalid, null, null);
        var setupStep = _totp.MatchStep(request.TempSecret, request.TotpCode);
        if (setupStep is null) return new(MfaSetupOutcome.CodeInvalid, null, null);

        var backup = MfaBackupCodes.Generate();
        var encryptedSecret = _totp.EncryptSecret(request.TempSecret);
        // Microsecond-truncated: it survives a Postgres round trip exactly, so the session re-issue can bind to it.
        var setupNow = DateTime.UtcNow;
        var configuredAtUtc = new DateTime(setupNow.Ticks - setupNow.Ticks % 10, DateTimeKind.Utc);
        var auditId = Guid.NewGuid();
        var refusal = MfaSetupOutcome.SetupInvalid;

        async Task<bool> EnableOnceAsync(CancellationToken cancellationToken)
        {
            _db.ChangeTracker.Clear();
            refusal = MfaSetupOutcome.SetupInvalid;
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForShareTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, cancellationToken);
            var user = await _db.Users.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == userId && x.TenantId == tenantId, cancellationToken);
            if (user is not null && (user.MFAEnabled || !string.IsNullOrWhiteSpace(user.MfaSecretEncrypted)))
            {
                refusal = MfaSetupOutcome.AlreadyEnabled;
                return false;
            }
            if (tenant?.IsActive != true
                || user is null
                || user.IsDeleted
                || !user.IsActive
                || !string.Equals(user.Status, "Active", StringComparison.Ordinal))
                return false;

            var graph = await LoadCompleteTenantGraphAsync(user.Id, user.TenantId, cancellationToken);
            if (!await AuthTenantGraphIntegrity.IsValidAsync(graph, _db, cancellationToken))
                return false;
            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var identity = await _db.TenantIdentityProviderSettings.IgnoreQueryFilters().AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == user.TenantId, cancellationToken);
            if (!AuthCurrentEligibility.ForPasswordEntry(
                    graph,
                    AuthCurrentEligibility.IsSsoOnly(graph!, identity),
                    configuredAtUtc).Allowed)
                return false;

            // A new secret: no browser trusted under an earlier factor stays trusted.
            await MfaTrustedDevices.StageRevokeAllAsync(_db, tenantId, [userId], MfaTrustRevokeReasons.MfaResetup,
                configuredAtUtc, new RequestContext(null, null, userId, tenantId), cancellationToken);
            // Every session ends, including this one (re-issued by the controller once this commits).
            await _db.RefreshTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.UserId == userId && x.RevokedAtUtc == null)
                .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(cancellationToken);
            if (_db.Database.IsRelational())
                await _db.RefreshTokens
                    .Where(x => x.UserId == userId && x.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.RevokedAtUtc, configuredAtUtc)
                        .SetProperty(x => x.RevokedByIp, "mfa-enabled"), cancellationToken);
            else
                foreach (var refresh in await _db.RefreshTokens
                    .Where(x => x.UserId == userId && x.RevokedAtUtc == null).ToListAsync(cancellationToken))
                {
                    refresh.RevokedAtUtc = configuredAtUtc;
                    refresh.RevokedByIp = "mfa-enabled";
                }
            graph!.MFAEnabled = true;
            graph.MfaSecretEncrypted = encryptedSecret;
            graph.MfaConfiguredAtUtc = configuredAtUtc;
            graph.MfaLastTotpStep = setupStep;
            graph.MfaFailedCount = 0;
            graph.MfaRecoveryCodeHashes = backup.Stored;
            TenantSessionSecurity.RotateStamp(graph, configuredAtUtc);
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                configuredAtUtc,
                "auth.mfa.enabled",
                "User",
                graph.Id.ToString(),
                new RequestContext(null, null, userId, tenantId),
                "{\"via\":\"authenticated_setup\"}"));
            AddBackupCodesGeneratedAudit(graph, configuredAtUtc, new RequestContext(null, null, userId, tenantId), "setup");
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (!_db.Database.IsRelational())
            return await EnableOnceAsync(ct) ? new(MfaSetupOutcome.Enabled, backup.Codes, configuredAtUtc) : new(refusal, null, null);
        var strategy = _db.Database.CreateExecutionStrategy();
        var succeeded = await strategy.ExecuteInTransactionAsync(
            EnableOnceAsync,
            // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
            async cancellationToken => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId && x.Action == "auth.mfa.enabled", cancellationToken),
            IsolationLevel.ReadCommitted,
            ct);
        if (succeeded) return new(MfaSetupOutcome.Enabled, backup.Codes, configuredAtUtc);
        _db.ChangeTracker.Clear();
        // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
        return await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(x => x.Id == auditId && x.Action == "auth.mfa.enabled", ct)
            ? new(MfaSetupOutcome.Enabled, backup.Codes, configuredAtUtc)
            : new(refusal, null, null);
    }

    public Task<string> CreateEnrollmentChallengeAsync(Guid userId, Guid tenantId, string ip, CancellationToken ct) =>
        CreateTenantChallengeAsync(AuthChallengeTokenCodec.TenantEnrollmentPurpose, userId, tenantId, ip, ct);

    public async Task<MfaSetupInitDto?> InitiateEnrollmentSetupAsync(string enrollmentToken, CancellationToken ct)
    {
        var challenge = await LoadValidTenantChallengeAsync(
            enrollmentToken,
            AuthChallengeTokenCodec.TenantEnrollmentPurpose,
            ct);
        if (challenge is null) return null;

        var user = await LoadUser(challenge.UserId!.Value, ct);
        if (user is null || user.TenantId != challenge.TenantId || user.MFAEnabled || !string.IsNullOrEmpty(user.MfaSecretEncrypted))
            return null;

        return await InitiateSetupAsync(user.Id, user.TenantId, ct);
    }

    public async Task<bool> VerifyEnrollmentSetupAsync(string enrollmentToken, MfaVerifySetupRequest request, CancellationToken ct)
        => await VerifyEnrollmentSetupWithBackupCodesAsync(enrollmentToken, request, ct) is not null;

    /// <summary><see cref="VerifyEnrollmentSetupAsync"/> returning the ten new backup codes on success, null otherwise.</summary>
    public async Task<IReadOnlyList<string>?> VerifyEnrollmentSetupWithBackupCodesAsync(string enrollmentToken, MfaVerifySetupRequest request, CancellationToken ct)
    {
        if (!AuthChallengeTokenCodec.TryParse(
                enrollmentToken,
                AuthChallengeTokenCodec.TenantEnrollmentPurpose,
                out var envelope)
            || envelope.TenantId is null)
            return null;

        var tokenHash = _tokenService.HashToken(enrollmentToken);
        var verifiedAtUtc = DateTime.UtcNow;
        var enrolStep = _totp.MatchStep(request.TempSecret, request.TotpCode);
        var codeValid = enrolStep is not null;
        var encryptedSecret = codeValid ? _totp.EncryptSecret(request.TempSecret) : null;
        var backup = codeValid ? MfaBackupCodes.Generate() : null;
        var auditId = Guid.NewGuid();
        (Guid TenantId, string Email, string Name)? recipient = null;

        async Task<bool> VerifyOnceAsync(CancellationToken cancellationToken)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForShareTag)
                .SingleOrDefaultAsync(x => x.Id == envelope.TenantId.Value, cancellationToken);
            var user = await _db.Users.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == envelope.PrincipalId && x.TenantId == envelope.TenantId.Value, cancellationToken);
            if (user is not null)
            {
                // Lock the complete credential set in one stable order before selecting the
                // presented row. Locking the presented row first lets two different enrollment
                // credentials deadlock when each later tries to consume the other's row.
                await _db.MfaChallengeTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                    .Where(x => x.UserId == user.Id && x.UsedAtUtc == null)
                    .OrderBy(x => x.Id)
                    .Select(x => x.Id)
                    .ToListAsync(cancellationToken);
            }
            var challenge = await _db.MfaChallengeTokens
                .SingleOrDefaultAsync(x => x.Id == envelope.ChallengeId, cancellationToken);
            if (tenant?.IsActive != true
                || user is null
                || challenge is null
                || challenge.TokenHash != tokenHash
                || challenge.UserId != user.Id
                || challenge.PlatformUserId is not null
                || challenge.TenantId != user.TenantId
                || challenge.UsedAtUtc is not null
                || challenge.ExpiresAtUtc <= verifiedAtUtc
                || challenge.FailedAttempts >= MfaChallengeToken.MaxAttempts
                || !string.Equals(envelope.SessionStamp, TenantSessionSecurity.StampValue(user), StringComparison.Ordinal)
                || user.MFAEnabled
                || !string.IsNullOrWhiteSpace(user.MfaSecretEncrypted))
                return false;

            var graph = await LoadCompleteTenantGraphAsync(user.Id, user.TenantId, cancellationToken);
            if (!await AuthTenantGraphIntegrity.IsValidAsync(graph, _db, cancellationToken))
                return false;
            // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
            var identity = await _db.TenantIdentityProviderSettings.IgnoreQueryFilters().AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == user.TenantId, cancellationToken);
            var eligibility = AuthCurrentEligibility.ForPasswordEntry(
                graph,
                AuthCurrentEligibility.IsSsoOnly(graph!, identity),
                verifiedAtUtc);
            if (!eligibility.Allowed)
            {
                challenge.UsedAtUtc = verifiedAtUtc;
                _db.AuditLogs.Add(AuthAuditEntry.Create(
                    auditId,
                    verifiedAtUtc,
                    "auth.mfa_enrollment_rejected_state",
                    "MfaChallengeToken",
                    challenge.Id.ToString(),
                    new RequestContext(null, null, user.Id, user.TenantId),
                    $"{{\"reason\":\"{eligibility.Reason}\"}}"));
                await _db.SaveChangesAsync(cancellationToken);
                return false;
            }

            if (!codeValid)
            {
                challenge.FailedAttempts++;
                user.MfaFailedCount++;
                if (challenge.FailedAttempts >= MfaChallengeToken.MaxAttempts)
                    challenge.UsedAtUtc = verifiedAtUtc;
                _db.AuditLogs.Add(AuthAuditEntry.Create(
                    auditId,
                    verifiedAtUtc,
                    "auth.mfa_enrollment_failed",
                    "MfaChallengeToken",
                    challenge.Id.ToString(),
                    new RequestContext(null, null, user.Id, user.TenantId),
                    $"{{\"failedAttempts\":{challenge.FailedAttempts}}}"));
                await _db.SaveChangesAsync(cancellationToken);
                return false;
            }

            if (_db.Database.IsRelational())
            {
                await _db.MfaChallengeTokens
                    .Where(x => x.UserId == user.Id && x.UsedAtUtc == null && x.Id != challenge.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, verifiedAtUtc), cancellationToken);
            }
            else
            {
                foreach (var sibling in await _db.MfaChallengeTokens
                    .Where(x => x.UserId == user.Id && x.UsedAtUtc == null && x.Id != challenge.Id)
                    .ToListAsync(cancellationToken))
                    sibling.UsedAtUtc = verifiedAtUtc;
            }

            await MfaTrustedDevices.StageRevokeAllAsync(_db, graph!.TenantId, [graph.Id], MfaTrustRevokeReasons.MfaResetup,
                verifiedAtUtc, new RequestContext(null, null, graph.Id, graph.TenantId), cancellationToken);
            // Every session of the user ends with the new factor, as on signed-in setup: the rotated stamp ends access
            // tokens, and the refresh tokens are revoked here so none can mint another. Nothing is re-issued: this path
            // is signed out, and the user signs in again with the new factor.
            await _db.RefreshTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
                .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(cancellationToken);
            if (_db.Database.IsRelational())
                await _db.RefreshTokens
                    .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.RevokedAtUtc, verifiedAtUtc)
                        .SetProperty(x => x.RevokedByIp, "mfa-enabled"), cancellationToken);
            else
                foreach (var refresh in await _db.RefreshTokens
                    .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null).ToListAsync(cancellationToken))
                {
                    refresh.RevokedAtUtc = verifiedAtUtc;
                    refresh.RevokedByIp = "mfa-enabled";
                }
            graph!.MFAEnabled = true;
            graph.MfaSecretEncrypted = encryptedSecret;
            graph.MfaConfiguredAtUtc = verifiedAtUtc;
            graph.MfaLastTotpStep = enrolStep;
            graph.MfaFailedCount = 0;
            graph.MfaRecoveryCodeHashes = backup!.Stored;
            challenge.UsedAtUtc = verifiedAtUtc;
            TenantSessionSecurity.RotateStamp(graph, verifiedAtUtc);
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                verifiedAtUtc,
                "auth.mfa.enabled",
                "User",
                graph.Id.ToString(),
                new RequestContext(null, null, graph.Id, graph.TenantId),
                "{\"via\":\"enrollment_challenge\"}"));
            AddBackupCodesGeneratedAudit(graph, verifiedAtUtc, new RequestContext(null, null, graph.Id, graph.TenantId), "enrollment");
            await _db.SaveChangesAsync(cancellationToken);
            recipient = (graph.TenantId, graph.Email, graph.FullName);
            return true;
        }

        if (!_db.Database.IsRelational())
        {
            var enabled = await VerifyOnceAsync(ct);
            if (enabled) await NotifyFactorEnrolledAsync(recipient, verifiedAtUtc, ct);
            return enabled ? backup!.Codes : null;
        }
        var strategy = _db.Database.CreateExecutionStrategy();
        var succeeded = await strategy.ExecuteInTransactionAsync(
            VerifyOnceAsync,
            // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
            async cancellationToken => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId, cancellationToken),
            IsolationLevel.ReadCommitted,
            ct);
        if (succeeded)
        {
            await NotifyFactorEnrolledAsync(recipient, verifiedAtUtc, ct);
            return backup!.Codes;
        }

        // A successful COMMIT whose acknowledgement was lost is surfaced as default(bool)
        // after verifySucceeded. Reconcile the exact success marker instead of telling the
        // client that its now-consumed enrollment credential failed.
        _db.ChangeTracker.Clear();
        // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
        return backup is not null
            && await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId && x.Action == "auth.mfa.enabled", ct)
            && await _db.Users.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == envelope.PrincipalId
                    && x.TenantId == envelope.TenantId.Value
                    && x.MFAEnabled
                    && x.MfaSecretEncrypted != null, ct)
                ? backup.Codes
                : null;
    }

    public Task<string> CreateChallengeAsync(Guid userId, Guid tenantId, string ip, CancellationToken ct) =>
        CreateTenantChallengeAsync(AuthChallengeTokenCodec.TenantLoginPurpose, userId, tenantId, ip, ct);

    private async Task<string> CreateTenantChallengeAsync(
        string purpose,
        Guid userId,
        Guid tenantId,
        string ip,
        CancellationToken ct)
    {
        var user = await LoadUser(userId, ct);
        if (user is null || user.TenantId != tenantId)
            throw new InvalidOperationException("User not found.");
        var challengeId = Guid.NewGuid();
        var rawToken = AuthChallengeTokenCodec.CreateTenant(
            purpose,
            challengeId,
            userId,
            tenantId,
            TenantSessionSecurity.StampValue(user),
            _tokenService.CreateSecureToken());
        _db.MfaChallengeTokens.Add(new MfaChallengeToken
        {
            Id = challengeId,
            UserId = userId,
            TenantId = tenantId,
            TokenHash = _tokenService.HashToken(rawToken),
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(ChallengeTtlSeconds),
            CreatedByIp = ip
        });
        await _db.SaveChangesAsync(ct);
        return rawToken;
    }

    private async Task<MfaChallengeToken?> LoadValidTenantChallengeAsync(
        string rawToken,
        string purpose,
        CancellationToken ct)
    {
        if (!AuthChallengeTokenCodec.TryParse(rawToken, purpose, out var envelope)
            || envelope.TenantId is null)
            return null;
        var hash = _tokenService.HashToken(rawToken);
        var challenge = await _db.MfaChallengeTokens
            .FirstOrDefaultAsync(x => x.Id == envelope.ChallengeId
                && x.TokenHash == hash
                && x.UserId == envelope.PrincipalId
                && x.TenantId == envelope.TenantId
                && x.PlatformUserId == null, ct);
        if (challenge is null || !challenge.IsValid) return null;
        var user = await LoadUser(envelope.PrincipalId, ct);
        return user is not null
            && user.TenantId == envelope.TenantId
            && string.Equals(envelope.SessionStamp, TenantSessionSecurity.StampValue(user), StringComparison.Ordinal)
                ? challenge
                : null;
    }

    public async Task<bool> DisableAsync(Guid userId, Guid tenantId, string totpCode, CancellationToken ct) =>
        await DisableWithOutcomeAsync(userId, tenantId, totpCode, new RequestContext(null, null, userId, tenantId), ct)
            == MfaDisableOutcome.Disabled;

    /// <summary>
    /// The user turns their own two-step off (Amendment 1). Refused while their role is privileged
    /// (PrivilegedMfaPolicy status != NotRequired) or the company requires two-step for everyone; otherwise needs a fresh
    /// code. Success rotates the stamp (ends sessions) and revokes every trusted browser, as before plus trust.
    /// </summary>
    public Task<MfaDisableOutcome> DisableWithOutcomeAsync(Guid userId, Guid tenantId, string totpCode, RequestContext context, CancellationToken ct) =>
        DisableTenantFactorAsync(
            userId,
            tenantId,
            totpCode,
            privilegedRecovery: false,
            context with { UserId = userId, TenantId = tenantId },
            ct);

    public async Task<bool> AdminDisableAsync(
        Guid userId,
        Guid tenantId,
        RequestContext context,
        CancellationToken ct) =>
        await DisableTenantFactorAsync(
            userId,
            tenantId,
            totpCode: null,
            privilegedRecovery: true,
            context with { UserId = null, TenantId = tenantId },
            ct) == MfaDisableOutcome.Disabled;

    private async Task<MfaDisableOutcome> DisableTenantFactorAsync(
        Guid userId,
        Guid tenantId,
        string? totpCode,
        bool privilegedRecovery,
        RequestContext context,
        CancellationToken ct)
    {
        var disabledAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();
        var auditAction = privilegedRecovery
            ? "platform.auth.tenant_mfa_disabled"
            : "auth.mfa.disabled";

        var outcome = MfaDisableOutcome.Refused;

        async Task<bool> DisableOnceAsync(CancellationToken cancellationToken)
        {
            _db.ChangeTracker.Clear();
            outcome = MfaDisableOutcome.Refused;
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForShareTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, cancellationToken);
            var user = await _db.Users.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == userId && x.TenantId == tenantId, cancellationToken);
            if (tenant?.IsActive != true
                || user is null
                || !user.IsActive
                || user.IsDeleted)
                return false;
            if (!user.MFAEnabled || string.IsNullOrEmpty(user.MfaSecretEncrypted))
            {
                outcome = MfaDisableOutcome.NotEnabled;
                return false;
            }

            if (!privilegedRecovery)
            {
                // The mandatory rules stay: a privileged role, or a company that requires two-step for everyone,
                // cannot switch it off (platform recovery, the lost-device path, is not this method's caller).
                var graph = await LoadCompleteTenantGraphAsync(user.Id, user.TenantId, cancellationToken);
                if (graph is null) return false;
                if (await PrivilegedMfaPolicy.IsPrivilegedTenantUserAsync(_db, graph, cancellationToken))
                {
                    outcome = MfaDisableOutcome.RequiredForRole;
                    return false;
                }
                var policy = await _db.SecuritySettings.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.TenantId == user.TenantId, cancellationToken);
                if (policy?.MfaRequired == true)
                {
                    outcome = MfaDisableOutcome.RequiredByWorkspace;
                    return false;
                }

                string plainSecret;
                try { plainSecret = _totp.DecryptSecret(user.MfaSecretEncrypted); }
                catch { return false; }
                if (string.IsNullOrWhiteSpace(totpCode)
                    || !TotpService.IsFreshStep(_totp.MatchStep(plainSecret, totpCode), user.MfaLastTotpStep))
                {
                    outcome = MfaDisableOutcome.CodeInvalid;
                    return false;
                }
            }

            await MfaTrustedDevices.StageRevokeAllAsync(_db, tenantId, [user.Id],
                privilegedRecovery ? MfaTrustRevokeReasons.MfaRecoveryDisabled : MfaTrustRevokeReasons.MfaDisabled,
                disabledAtUtc, context with { TenantId = tenantId }, cancellationToken);

            await _db.MfaChallengeTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.UserId == user.Id && x.UsedAtUtc == null)
                .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(cancellationToken);
            await _db.RefreshTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
                .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(cancellationToken);
            if (_db.Database.IsRelational())
            {
                await _db.MfaChallengeTokens
                    .Where(x => x.UserId == user.Id && x.UsedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, disabledAtUtc), cancellationToken);
                await _db.RefreshTokens
                        .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(x => x.RevokedAtUtc, disabledAtUtc)
                            .SetProperty(x => x.RevokedByIp, context.IpAddress ?? "mfa-disabled"), cancellationToken);
            }
            else
            {
                foreach (var challenge in await _db.MfaChallengeTokens
                    .Where(x => x.UserId == user.Id && x.UsedAtUtc == null).ToListAsync(cancellationToken))
                    challenge.UsedAtUtc = disabledAtUtc;
                foreach (var refresh in await _db.RefreshTokens
                    .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null).ToListAsync(cancellationToken))
                {
                    refresh.RevokedAtUtc = disabledAtUtc;
                    refresh.RevokedByIp = context.IpAddress ?? "mfa-disabled";
                }
            }

            user.MFAEnabled = false;
            user.MfaSecretEncrypted = null;
            user.MfaConfiguredAtUtc = null;
            user.MfaLastVerifiedAtUtc = null;
            user.MfaLastTotpStep = null;
            user.MfaFailedCount = 0;
            user.MfaRecoveryCodeHashes = null;
            TenantSessionSecurity.RotateStamp(user, disabledAtUtc);
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                disabledAtUtc,
                auditAction,
                "User",
                user.Id.ToString(),
                context,
                privilegedRecovery ? "{\"via\":\"platform_recovery\"}" : null));
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (!_db.Database.IsRelational()) return await DisableOnceAsync(ct) ? MfaDisableOutcome.Disabled : outcome;
        var strategy = _db.Database.CreateExecutionStrategy();
        var succeeded = await strategy.ExecuteInTransactionAsync(
            DisableOnceAsync,
            // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
            async cancellationToken => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId && x.Action == auditAction, cancellationToken),
            IsolationLevel.ReadCommitted,
            ct);
        if (succeeded) return MfaDisableOutcome.Disabled;
        _db.ChangeTracker.Clear();
        // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
        return await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(x => x.Id == auditId && x.Action == auditAction, ct)
            ? MfaDisableOutcome.Disabled
            : outcome;
    }

    // ── Tenant backup codes ───────────────────────────────────────────────────

    private void AddBackupCodesGeneratedAudit(User user, DateTime atUtc, RequestContext context, string via) =>
        _db.AuditLogs.Add(AuthAuditEntry.Create(
            Guid.NewGuid(),
            atUtc,
            "auth.mfa.backup_codes_generated",
            "User",
            user.Id.ToString(),
            context with { TenantId = user.TenantId },
            $"{{\"via\":\"{via}\",\"count\":{MfaBackupCodes.CodeCount}}}"));

    /// <summary>
    /// Replaces the signed-in user's backup codes. A fresh app code proves the factor (and is then spent, like any
    /// accepted step); the old set stops working in the same write. Does not end the session (the save hook treats
    /// the codes and the step as bookkeeping), so the page can show the new codes.
    /// </summary>
    public async Task<(MfaBackupCodesOutcome Outcome, IReadOnlyList<string>? BackupCodes)> RegenerateBackupCodesAsync(
        Guid userId, Guid tenantId, string totpCode, RequestContext context, CancellationToken ct)
    {
        var backup = MfaBackupCodes.Generate();
        var generatedAtUtc = DateTime.UtcNow;
        var outcome = MfaBackupCodesOutcome.Refused;

        async Task<bool> RegenerateOnceAsync(CancellationToken cancellationToken)
        {
            _db.ChangeTracker.Clear();
            outcome = MfaBackupCodesOutcome.Refused;
            var user = await _db.Users.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == userId && x.TenantId == tenantId, cancellationToken);
            if (user is null || user.IsDeleted || !user.IsActive) return false;
            if (!PrivilegedMfaPolicy.IsTenantUserEnrolled(user))
            {
                outcome = MfaBackupCodesOutcome.NotEnabled;
                return false;
            }
            string secret;
            try { secret = _totp.DecryptSecret(user.MfaSecretEncrypted!); }
            catch { return false; }
            var step = string.IsNullOrWhiteSpace(totpCode) ? null : _totp.MatchStep(secret, totpCode);
            if (!TotpService.IsFreshStep(step, user.MfaLastTotpStep))
            {
                outcome = MfaBackupCodesOutcome.CodeInvalid;
                return false;
            }
            user.MfaLastTotpStep = step;
            user.MfaRecoveryCodeHashes = backup.Stored;
            AddBackupCodesGeneratedAudit(user, generatedAtUtc, context with { UserId = userId, TenantId = tenantId }, "regenerate");
            await _db.SaveChangesAsync(cancellationToken);
            outcome = MfaBackupCodesOutcome.Generated;
            return true;
        }

        // Commit evidence: this call's own set is the stored one (users is read under the request's tenant filter).
        Task<bool> CommittedAsync(CancellationToken cancellationToken) =>
            _db.Users.AsNoTracking().AnyAsync(x => x.Id == userId && x.TenantId == tenantId
                && x.MfaRecoveryCodeHashes == backup.Stored, cancellationToken);

        var ok = _db.Database.IsRelational()
            ? await _db.Database.CreateExecutionStrategy().ExecuteInTransactionAsync(
                RegenerateOnceAsync, CommittedAsync, IsolationLevel.ReadCommitted, ct)
            : await RegenerateOnceAsync(ct);
        // A lost COMMIT acknowledgement comes back as false after the attempt reached its success branch.
        if (!ok && outcome == MfaBackupCodesOutcome.Generated && _db.Database.IsRelational())
        {
            _db.ChangeTracker.Clear();
            ok = await CommittedAsync(ct);
        }
        return ok ? (MfaBackupCodesOutcome.Generated, backup.Codes) : (outcome, null);
    }

    // ── Enrolment notices ─────────────────────────────────────────────────────

    private static string FactorEnrolledHtml(string name, DateTime atUtc) => $"""
        <p>Hello {System.Net.WebUtility.HtmlEncode(name)},</p>
        <p>Two-step sign-in was turned on for your KynexOne account on
        {atUtc:yyyy-MM-dd HH:mm} UTC. From now on you will be asked for a code from your authenticator app when you sign in.</p>
        <p><strong>If this was not you</strong>, contact your administrator immediately: someone who knows your
        password has linked their own authenticator to your account.</p>
        """;

    /// <summary>
    /// Best effort, after commit: a factor added through an enrolment token (i.e. right after a password
    /// sign-in) is exactly what an attacker holding a stolen password would do, so the account owner is
    /// told. A delivery failure never undoes the enrolment. Logs carry the account id only.
    /// </summary>
    private async Task NotifyFactorEnrolledAsync((Guid TenantId, string Email, string Name)? to, DateTime atUtc, CancellationToken ct)
    {
        if (_email is null || to is not { } r || string.IsNullOrWhiteSpace(r.Email)) return;
        try
        {
            var result = await _email.DeliverAsync(r.TenantId, r.Email, r.Name,
                "Two-step sign-in was turned on for your KynexOne account", FactorEnrolledHtml(r.Name, atUtc), cancellationToken: ct);
            _log?.LogInformation("MFA enrolment notice for a tenant user in tenant {TenantId}: {Outcome}.", r.TenantId, result.Status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Exception type only: an SMTP exception's message can carry the recipient address.
            _log?.LogWarning("MFA enrolment notice could not be sent (tenant {TenantId}, {ExceptionType}).",
                r.TenantId, ex.GetType().Name);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task<User?> LoadCompleteTenantGraphAsync(Guid userId, Guid tenantId, CancellationToken ct) =>
        // IgnoreQueryFilters is intentional: locked auth/lifecycle graph read; the company filter is dropped and TenantId is re-applied explicitly in this predicate (register §6).
        _db.Users.IgnoreQueryFilters()
            .Include(x => x.Tenant)
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x!.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.EmployeeUserAccounts)
            .Include(x => x.PermissionOverrides)
            .Include(x => x.EntityAccesses)
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == userId && x.TenantId == tenantId, ct);

    private Task<User?> LoadUser(Guid userId, CancellationToken ct) =>
        _db.Users.FirstOrDefaultAsync(x => x.Id == userId && !x.IsDeleted, ct);

}
