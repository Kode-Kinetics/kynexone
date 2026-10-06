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
    {
        var setupStep = _totp.MatchStep(request.TempSecret, request.TotpCode);
        if (setupStep is null) return false;

        var encryptedSecret = _totp.EncryptSecret(request.TempSecret);
        var configuredAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();

        async Task<bool> EnableOnceAsync(CancellationToken cancellationToken)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForShareTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, cancellationToken);
            var user = await _db.Users.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == userId && x.TenantId == tenantId, cancellationToken);
            if (tenant?.IsActive != true
                || user is null
                || user.IsDeleted
                || !user.IsActive
                || !string.Equals(user.Status, "Active", StringComparison.Ordinal)
                || user.MFAEnabled
                || !string.IsNullOrWhiteSpace(user.MfaSecretEncrypted))
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

            graph!.MFAEnabled = true;
            graph.MfaSecretEncrypted = encryptedSecret;
            graph.MfaConfiguredAtUtc = configuredAtUtc;
            graph.MfaLastTotpStep = setupStep;
            graph.MfaFailedCount = 0;
            TenantSessionSecurity.RotateStamp(graph, configuredAtUtc);
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                configuredAtUtc,
                "auth.mfa.enabled",
                "User",
                graph.Id.ToString(),
                new RequestContext(null, null, userId, tenantId),
                "{\"via\":\"authenticated_setup\"}"));
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (!_db.Database.IsRelational()) return await EnableOnceAsync(ct);
        var strategy = _db.Database.CreateExecutionStrategy();
        var succeeded = await strategy.ExecuteInTransactionAsync(
            EnableOnceAsync,
            // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
            async cancellationToken => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId && x.Action == "auth.mfa.enabled", cancellationToken),
            IsolationLevel.ReadCommitted,
            ct);
        if (succeeded) return true;
        _db.ChangeTracker.Clear();
        return await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(x => x.Id == auditId && x.Action == "auth.mfa.enabled", ct);
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
    {
        if (!AuthChallengeTokenCodec.TryParse(
                enrollmentToken,
                AuthChallengeTokenCodec.TenantEnrollmentPurpose,
                out var envelope)
            || envelope.TenantId is null)
            return false;

        var tokenHash = _tokenService.HashToken(enrollmentToken);
        var verifiedAtUtc = DateTime.UtcNow;
        var enrolStep = _totp.MatchStep(request.TempSecret, request.TotpCode);
        var codeValid = enrolStep is not null;
        var encryptedSecret = codeValid ? _totp.EncryptSecret(request.TempSecret) : null;
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

            graph!.MFAEnabled = true;
            graph.MfaSecretEncrypted = encryptedSecret;
            graph.MfaConfiguredAtUtc = verifiedAtUtc;
            graph.MfaLastTotpStep = enrolStep;
            graph.MfaFailedCount = 0;
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
            await _db.SaveChangesAsync(cancellationToken);
            recipient = (graph.TenantId, graph.Email, graph.FullName);
            return true;
        }

        if (!_db.Database.IsRelational())
        {
            var enabled = await VerifyOnceAsync(ct);
            if (enabled) await NotifyFactorEnrolledAsync(recipient, verifiedAtUtc, ct);
            return enabled;
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
            return true;
        }

        // A successful COMMIT whose acknowledgement was lost is surfaced as default(bool)
        // after verifySucceeded. Reconcile the exact success marker instead of telling the
        // client that its now-consumed enrollment credential failed.
        _db.ChangeTracker.Clear();
        // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
        return await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId && x.Action == "auth.mfa.enabled", ct)
            && await _db.Users.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == envelope.PrincipalId
                    && x.TenantId == envelope.TenantId.Value
                    && x.MFAEnabled
                    && x.MfaSecretEncrypted != null, ct);
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

    public Task<bool> DisableAsync(Guid userId, Guid tenantId, string totpCode, CancellationToken ct) =>
        DisableTenantFactorAsync(
            userId,
            tenantId,
            totpCode,
            privilegedRecovery: false,
            new RequestContext(null, null, userId, tenantId),
            ct);

    public Task<bool> AdminDisableAsync(
        Guid userId,
        Guid tenantId,
        RequestContext context,
        CancellationToken ct) =>
        DisableTenantFactorAsync(
            userId,
            tenantId,
            totpCode: null,
            privilegedRecovery: true,
            context with { UserId = null, TenantId = tenantId },
            ct);

    private async Task<bool> DisableTenantFactorAsync(
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

        async Task<bool> DisableOnceAsync(CancellationToken cancellationToken)
        {
            _db.ChangeTracker.Clear();
            var tenant = await _db.Tenants.TagWith(RowLockingInterceptor.ForShareTag)
                .SingleOrDefaultAsync(x => x.Id == tenantId, cancellationToken);
            var user = await _db.Users.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == userId && x.TenantId == tenantId, cancellationToken);
            if (tenant?.IsActive != true
                || user is null
                || !user.IsActive
                || user.IsDeleted
                || !user.MFAEnabled
                || string.IsNullOrEmpty(user.MfaSecretEncrypted))
                return false;

            if (!privilegedRecovery)
            {
                string plainSecret;
                try { plainSecret = _totp.DecryptSecret(user.MfaSecretEncrypted); }
                catch { return false; }
                if (string.IsNullOrWhiteSpace(totpCode)
                    || !TotpService.IsFreshStep(_totp.MatchStep(plainSecret, totpCode), user.MfaLastTotpStep))
                    return false;
            }

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

        if (!_db.Database.IsRelational()) return await DisableOnceAsync(ct);
        var strategy = _db.Database.CreateExecutionStrategy();
        var succeeded = await strategy.ExecuteInTransactionAsync(
            DisableOnceAsync,
            // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
            async cancellationToken => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId && x.Action == auditAction, cancellationToken),
            IsolationLevel.ReadCommitted,
            ct);
        if (succeeded) return true;
        _db.ChangeTracker.Clear();
        return await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(x => x.Id == auditId && x.Action == auditAction, ct);
    }

    // ── Platform user ─────────────────────────────────────────────────────────

    public async Task<MfaSetupInitDto> InitiatePlatformSetupAsync(Guid platformUserId, CancellationToken ct)
    {
        var pu = await LoadPlatformUser(platformUserId, ct) ?? throw new InvalidOperationException("Platform user not found.");
        if (pu.MfaEnabled || !string.IsNullOrWhiteSpace(pu.MfaSecretEncrypted))
            throw new InvalidOperationException("MFA is already configured. Use the approved recovery flow to replace a factor.");
        var tempSecret = _totp.GenerateBase32Secret();
        var uri = _totp.GenerateProvisioningUri(pu.Email, Issuer, tempSecret);
        return new MfaSetupInitDto(uri, tempSecret);
    }

    public async Task<IReadOnlyList<string>?> VerifyPlatformSetupAsync(Guid platformUserId, MfaVerifySetupRequest request, CancellationToken ct)
    {
        var setupStep = _totp.MatchStep(request.TempSecret, request.TotpCode);
        if (setupStep is null) return null;
        var recovery = NewRecoveryCodes();

        var encryptedSecret = _totp.EncryptSecret(request.TempSecret);
        var configuredAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();
        (string Email, string Name)? recipient = null;

        async Task<bool> EnableOnceAsync(CancellationToken cancellationToken)
        {
            _db.ChangeTracker.Clear();
            var pu = await _db.PlatformUsers.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == platformUserId, cancellationToken);
            if (pu is null
                || !pu.IsActive
                || !PlatformRoles.All.Contains(pu.Role)
                || pu.MfaEnabled
                || !string.IsNullOrWhiteSpace(pu.MfaSecretEncrypted))
                return false;

            pu.MfaEnabled = true;
            pu.MfaSecretEncrypted = encryptedSecret;
            pu.MfaConfiguredAtUtc = configuredAtUtc;
            pu.MfaRecoveryCodeHashes = recovery.Hashes;
            pu.MfaLastTotpStep = setupStep;
            PlatformSessionSecurity.RotateStamp(pu, configuredAtUtc);
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                configuredAtUtc,
                "platform.auth.mfa_enabled",
                "PlatformUser",
                pu.Id.ToString(),
                new RequestContext(null, null, null, null),
                "{\"via\":\"authenticated_setup\"}"));
            await _db.SaveChangesAsync(cancellationToken);
            recipient = (pu.Email, pu.FullName);
            return true;
        }

        if (!_db.Database.IsRelational())
        {
            if (!await EnableOnceAsync(ct)) return null;
            await NotifyPlatformFactorEnrolledAsync(recipient, configuredAtUtc, ct);
            return recovery.Codes;
        }
        var strategy = _db.Database.CreateExecutionStrategy();
        var succeeded = await strategy.ExecuteInTransactionAsync(
            EnableOnceAsync,
            // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
            async cancellationToken => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId && x.Action == "platform.auth.mfa_enabled", cancellationToken),
            IsolationLevel.ReadCommitted,
            ct);
        if (succeeded)
        {
            await NotifyPlatformFactorEnrolledAsync(recipient, configuredAtUtc, ct);
            return recovery.Codes;
        }
        _db.ChangeTracker.Clear();
        // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
        return await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(x => x.Id == auditId && x.Action == "platform.auth.mfa_enabled", ct)
            ? recovery.Codes : null;
    }

    public async Task<string> CreatePlatformChallengeAsync(Guid platformUserId, string ip, CancellationToken ct)
    {
        var platformUser = await LoadPlatformUser(platformUserId, ct)
            ?? throw new InvalidOperationException("Platform user not found.");
        if (!platformUser.UpdatedAtUtc.HasValue)
            PlatformSessionSecurity.RotateStamp(platformUser);
        var challengeId = Guid.NewGuid();
        var rawToken = AuthChallengeTokenCodec.CreatePlatform(
            challengeId,
            platformUserId,
            PlatformSessionSecurity.StampValue(platformUser.UpdatedAtUtc!.Value),
            _tokenService.CreateSecureToken());
        _db.MfaChallengeTokens.Add(new MfaChallengeToken
        {
            Id = challengeId,
            PlatformUserId = platformUserId,
            TokenHash = _tokenService.HashToken(rawToken),
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(ChallengeTtlSeconds),
            CreatedByIp = ip
        });
        await _db.SaveChangesAsync(ct);
        return rawToken;
    }

    public Task<PlatformUser?> VerifyPlatformChallengeAsync(string rawToken, string totpCode, CancellationToken ct) =>
        CompletePlatformChallengeAsync(rawToken, totpCode, new RequestContext(null, null, null, null), ct);

    public async Task<PlatformUser?> CompletePlatformChallengeAsync(
        string rawToken,
        string totpCode,
        RequestContext context,
        CancellationToken ct)
    {
        if (!AuthChallengeTokenCodec.TryParse(
                rawToken,
                AuthChallengeTokenCodec.PlatformLoginPurpose,
                out var envelope))
            return null;
        var hash = _tokenService.HashToken(rawToken);
        var completedAtUtc = DateTime.UtcNow;
        var activityId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        PlatformUser? preparedUser = null;

        async Task<bool> CompleteOnceAsync(CancellationToken cancellationToken)
        {
            _db.ChangeTracker.Clear();
            preparedUser = null;
            var pu = await _db.PlatformUsers
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == envelope.PrincipalId, cancellationToken);
            var challenge = await _db.MfaChallengeTokens
                .TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == envelope.ChallengeId, cancellationToken);
            if (pu is null
                || challenge is null
                || challenge.TokenHash != hash
                || challenge.PlatformUserId != pu.Id
                || challenge.UserId is not null
                || challenge.TenantId is not null
                || challenge.UsedAtUtc is not null
                || challenge.ExpiresAtUtc <= completedAtUtc
                || challenge.FailedAttempts >= MfaChallengeToken.MaxAttempts)
                return false;

            var stateReason = !pu.IsActive ? "platform_user_inactive"
                : !PlatformRoles.All.Contains(pu.Role) ? "platform_role_invalid"
                // A known device the endpoint already validated gets through an active lockout
                // (without clearing it); everyone else is refused while it lasts.
                : pu.LockoutEndUtc > completedAtUtc && !context.KnownDeviceVerified ? "platform_account_locked"
                : !pu.UpdatedAtUtc.HasValue ? "platform_stamp_missing"
                : !string.Equals(envelope.SessionStamp, PlatformSessionSecurity.StampValue(pu.UpdatedAtUtc.Value), StringComparison.Ordinal) ? "platform_stamp_changed"
                : !pu.MfaEnabled || string.IsNullOrWhiteSpace(pu.MfaSecretEncrypted) ? "platform_mfa_state_invalid"
                : null;
            if (stateReason is not null)
            {
                challenge.UsedAtUtc = DateTime.UtcNow;
                _db.LoginActivities.Add(new LoginActivity
                {
                    Id = activityId,
                    UserId = pu.Id,
                    EmailAttempted = pu.Email,
                    EventType = LoginEventTypes.PlatformLoginFailed,
                    FailureReason = stateReason,
                    IpAddress = context.IpAddress,
                    UserAgent = context.UserAgent,
                    OccurredAtUtc = completedAtUtc
                });
                _db.AuditLogs.Add(AuthAuditEntry.Create(
                    auditId,
                    completedAtUtc,
                    "platform.auth.mfa_rejected_state",
                    "PlatformUser",
                    pu.Id.ToString(),
                    context with { UserId = null, TenantId = null },
                    $"{{\"platformUserId\":\"{pu.Id:D}\",\"reason\":\"{stateReason}\"}}"));
                await _db.SaveChangesAsync(cancellationToken);
                return false;
            }

            string plainSecret;
            try { plainSecret = _totp.DecryptSecret(pu.MfaSecretEncrypted!); }
            catch
            {
                challenge.UsedAtUtc = completedAtUtc;
                _db.AuditLogs.Add(AuthAuditEntry.Create(
                    auditId,
                    completedAtUtc,
                    "platform.auth.mfa_rejected_state",
                    "PlatformUser",
                    pu.Id.ToString(),
                    context with { UserId = null, TenantId = null },
                    $"{{\"platformUserId\":\"{pu.Id:D}\",\"reason\":\"platform_mfa_secret_unavailable\"}}"));
                await _db.SaveChangesAsync(cancellationToken);
                return false;
            }

            var matchedStep = _totp.MatchStep(plainSecret, totpCode);
            if (!TotpService.IsFreshStep(matchedStep, pu.MfaLastTotpStep))
            {
                challenge.FailedAttempts++;
                if (challenge.FailedAttempts >= MfaChallengeToken.MaxAttempts)
                    challenge.UsedAtUtc = completedAtUtc;
                _db.LoginActivities.Add(new LoginActivity
                {
                    Id = activityId,
                    UserId = pu.Id,
                    EmailAttempted = pu.Email,
                    EventType = LoginEventTypes.PlatformLoginFailed,
                    FailureReason = "mfa_code_mismatch",
                    IpAddress = context.IpAddress,
                    UserAgent = context.UserAgent,
                    OccurredAtUtc = completedAtUtc
                });
                _db.AuditLogs.Add(AuthAuditEntry.Create(
                    auditId,
                    completedAtUtc,
                    "platform.auth.mfa_failed",
                    "MfaChallengeToken",
                    challenge.Id.ToString(),
                    context with { UserId = null, TenantId = null },
                    $"{{\"platformUserId\":\"{pu.Id:D}\",\"failedAttempts\":{challenge.FailedAttempts}}}"));
                await _db.SaveChangesAsync(cancellationToken);
                return false;
            }

            challenge.UsedAtUtc = completedAtUtc;
            pu.MfaLastTotpStep = matchedStep;
            if (!(context.KnownDeviceVerified && pu.LockoutEndUtc > completedAtUtc))
                pu.FailedLoginCount = 0; // a bypass of an active lockout leaves the shared counter alone
            pu.LastLoginAtUtc = completedAtUtc;
            pu.LastLoginIp = context.IpAddress;
            _db.LoginActivities.Add(new LoginActivity
            {
                Id = activityId,
                UserId = pu.Id,
                EmailAttempted = pu.Email,
                EventType = LoginEventTypes.PlatformLoginSuccess,
                IpAddress = context.IpAddress,
                UserAgent = context.UserAgent,
                OccurredAtUtc = completedAtUtc
            });
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                completedAtUtc,
                "platform.auth.mfa_login",
                "PlatformUser",
                pu.Id.ToString(),
                context with { UserId = null, TenantId = null },
                $"{{\"platformUserId\":\"{pu.Id:D}\",\"challengeId\":\"{challenge.Id:D}\"}}"));
            await _db.SaveChangesAsync(cancellationToken);
            preparedUser = pu;
            return true;
        }

        bool succeeded;
        if (_db.Database.IsRelational())
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            succeeded = await strategy.ExecuteInTransactionAsync(
                CompleteOnceAsync,
                // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
                async cancellationToken => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                    .AnyAsync(x => x.Id == auditId, cancellationToken),
                IsolationLevel.ReadCommitted,
                ct);
        }
        else
        {
            succeeded = await CompleteOnceAsync(ct);
        }

        if (!succeeded && _db.Database.IsRelational())
        {
            // See tenant completion above: default(bool) is ambiguous after verified COMMIT.
            _db.ChangeTracker.Clear();
            // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
            succeeded = await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId && x.Action == "platform.auth.mfa_login", ct);
        }
        if (!succeeded) return null;
        if (preparedUser is not null) return preparedUser;
        var committed = await _db.PlatformUsers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == envelope.PrincipalId, ct);
        return committed is not null
            && committed.IsActive
            && committed.UpdatedAtUtc.HasValue
            && PlatformRoles.All.Contains(committed.Role)
            && (!committed.LockoutEndUtc.HasValue || committed.LockoutEndUtc <= DateTime.UtcNow || context.KnownDeviceVerified)
            && string.Equals(envelope.SessionStamp, PlatformSessionSecurity.StampValue(committed.UpdatedAtUtc.Value), StringComparison.Ordinal)
                ? committed
                : null;
    }

    public async Task<bool> DisablePlatformAsync(Guid platformUserId, string totpCode, CancellationToken ct)
    {
        var disabledAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();

        async Task<bool> DisableOnceAsync(CancellationToken cancellationToken)
        {
            _db.ChangeTracker.Clear();
            var pu = await _db.PlatformUsers.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == platformUserId, cancellationToken);
            if (pu is null
                || !pu.IsActive
                || !PlatformRoles.All.Contains(pu.Role)
                || !pu.MfaEnabled
                || string.IsNullOrEmpty(pu.MfaSecretEncrypted))
                return false;

            string plainSecret;
            try { plainSecret = _totp.DecryptSecret(pu.MfaSecretEncrypted); }
            catch { return false; }
            if (!TotpService.IsFreshStep(_totp.MatchStep(plainSecret, totpCode), pu.MfaLastTotpStep)) return false;

            await _db.MfaChallengeTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                .Where(x => x.PlatformUserId == pu.Id && x.UsedAtUtc == null)
                .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(cancellationToken);
            if (_db.Database.IsRelational())
            {
                await _db.MfaChallengeTokens
                    .Where(x => x.PlatformUserId == pu.Id && x.UsedAtUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAtUtc, disabledAtUtc), cancellationToken);
            }
            else
            {
                foreach (var challenge in await _db.MfaChallengeTokens
                    .Where(x => x.PlatformUserId == pu.Id && x.UsedAtUtc == null).ToListAsync(cancellationToken))
                    challenge.UsedAtUtc = disabledAtUtc;
            }

            pu.MfaEnabled = false;
            pu.MfaSecretEncrypted = null;
            pu.MfaConfiguredAtUtc = null;
            pu.MfaRecoveryCodeHashes = null;
            pu.MfaLastTotpStep = null;
            PlatformSessionSecurity.RotateStamp(pu, disabledAtUtc);
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                disabledAtUtc,
                "platform.auth.mfa_disabled",
                "PlatformUser",
                pu.Id.ToString(),
                new RequestContext(null, null, null, null)));
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (!_db.Database.IsRelational()) return await DisableOnceAsync(ct);
        var strategy = _db.Database.CreateExecutionStrategy();
        var succeeded = await strategy.ExecuteInTransactionAsync(
            DisableOnceAsync,
            // IgnoreQueryFilters is intentional: commit verification of this command's own audit marker by its server-generated id; no tenant data is read (register §6).
            async cancellationToken => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId && x.Action == "platform.auth.mfa_disabled", cancellationToken),
            IsolationLevel.ReadCommitted,
            ct);
        if (succeeded) return true;
        _db.ChangeTracker.Clear();
        return await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(x => x.Id == auditId && x.Action == "platform.auth.mfa_disabled", ct);
    }

    // ── Platform mandatory-MFA enrolment ──────────────────────────────────────

    public async Task<string> CreatePlatformEnrollmentChallengeAsync(Guid platformUserId, string ip, CancellationToken ct)
    {
        var platformUser = await LoadPlatformUser(platformUserId, ct)
            ?? throw new InvalidOperationException("Platform user not found.");
        if (!platformUser.UpdatedAtUtc.HasValue)
            PlatformSessionSecurity.RotateStamp(platformUser);
        var challengeId = Guid.NewGuid();
        var rawToken = AuthChallengeTokenCodec.CreatePlatform(
            AuthChallengeTokenCodec.PlatformEnrollmentPurpose,
            challengeId,
            platformUserId,
            PlatformSessionSecurity.StampValue(platformUser.UpdatedAtUtc!.Value),
            _tokenService.CreateSecureToken());
        _db.MfaChallengeTokens.Add(new MfaChallengeToken
        {
            Id = challengeId,
            PlatformUserId = platformUserId,
            TokenHash = _tokenService.HashToken(rawToken),
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(ChallengeTtlSeconds),
            CreatedByIp = ip
        });
        await _db.SaveChangesAsync(ct);
        return rawToken;
    }

    public async Task<MfaSetupInitDto?> InitiatePlatformEnrollmentSetupAsync(string enrollmentToken, CancellationToken ct)
    {
        if (!AuthChallengeTokenCodec.TryParse(enrollmentToken, AuthChallengeTokenCodec.PlatformEnrollmentPurpose, out var envelope))
            return null;
        var hash = _tokenService.HashToken(enrollmentToken);
        var challenge = await _db.MfaChallengeTokens.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == envelope.ChallengeId
                && x.TokenHash == hash
                && x.PlatformUserId == envelope.PrincipalId
                && x.UserId == null
                && x.TenantId == null, ct);
        if (challenge is null || !challenge.IsValid) return null;
        var pu = await LoadPlatformUser(envelope.PrincipalId, ct);
        if (pu is null
            || !PlatformRoles.All.Contains(pu.Role)
            || !pu.UpdatedAtUtc.HasValue
            || !string.Equals(envelope.SessionStamp, PlatformSessionSecurity.StampValue(pu.UpdatedAtUtc.Value), StringComparison.Ordinal)
            || pu.MfaEnabled
            || !string.IsNullOrWhiteSpace(pu.MfaSecretEncrypted))
            return null;
        return await InitiatePlatformSetupAsync(pu.Id, ct);
    }

    public async Task<IReadOnlyList<string>?> VerifyPlatformEnrollmentSetupAsync(string enrollmentToken, MfaVerifySetupRequest request, CancellationToken ct)
    {
        if (!AuthChallengeTokenCodec.TryParse(enrollmentToken, AuthChallengeTokenCodec.PlatformEnrollmentPurpose, out var envelope))
            return null;
        var hash = _tokenService.HashToken(enrollmentToken);
        var recovery = NewRecoveryCodes();
        // Microsecond-truncated so the commit check below can match it exactly after a Postgres round trip.
        var now = DateTime.UtcNow;
        var verifiedAtUtc = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        var enrolStep = _totp.MatchStep(request.TempSecret, request.TotpCode);
        var codeValid = enrolStep is not null;
        var encryptedSecret = codeValid ? _totp.EncryptSecret(request.TempSecret) : null;
        var auditId = Guid.NewGuid();
        (string Email, string Name)? recipient = null;

        async Task<bool> VerifyOnceAsync(CancellationToken cancellationToken)
        {
            _db.ChangeTracker.Clear();
            var pu = await _db.PlatformUsers.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == envelope.PrincipalId, cancellationToken);
            var challenge = await _db.MfaChallengeTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == envelope.ChallengeId, cancellationToken);
            if (pu is null
                || challenge is null
                || challenge.TokenHash != hash
                || challenge.PlatformUserId != pu.Id
                || challenge.UserId is not null
                || challenge.TenantId is not null
                || challenge.UsedAtUtc is not null
                || challenge.ExpiresAtUtc <= verifiedAtUtc
                || challenge.FailedAttempts >= MfaChallengeToken.MaxAttempts
                || !pu.IsActive
                || !PlatformRoles.All.Contains(pu.Role)
                || !pu.UpdatedAtUtc.HasValue
                || !string.Equals(envelope.SessionStamp, PlatformSessionSecurity.StampValue(pu.UpdatedAtUtc.Value), StringComparison.Ordinal)
                || pu.MfaEnabled
                || !string.IsNullOrWhiteSpace(pu.MfaSecretEncrypted))
                return false;

            if (!codeValid)
            {
                challenge.FailedAttempts++;
                if (challenge.FailedAttempts >= MfaChallengeToken.MaxAttempts)
                    challenge.UsedAtUtc = verifiedAtUtc;
                _db.AuditLogs.Add(AuthAuditEntry.Create(
                    auditId,
                    verifiedAtUtc,
                    "platform.auth.mfa_enrollment_failed",
                    "MfaChallengeToken",
                    challenge.Id.ToString(),
                    new RequestContext(null, null, null, null),
                    $"{{\"platformUserId\":\"{pu.Id:D}\",\"failedAttempts\":{challenge.FailedAttempts}}}"));
                await _db.SaveChangesAsync(cancellationToken);
                return false;
            }

            foreach (var sibling in await _db.MfaChallengeTokens
                .Where(x => x.PlatformUserId == pu.Id && x.UsedAtUtc == null)
                .ToListAsync(cancellationToken))
                sibling.UsedAtUtc = verifiedAtUtc;
            pu.MfaEnabled = true;
            pu.MfaSecretEncrypted = encryptedSecret;
            pu.MfaConfiguredAtUtc = verifiedAtUtc;
            pu.MfaRecoveryCodeHashes = recovery.Hashes;
            pu.MfaLastTotpStep = enrolStep;
            PlatformSessionSecurity.RotateStamp(pu, verifiedAtUtc);
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                verifiedAtUtc,
                "platform.auth.mfa_enabled",
                "PlatformUser",
                pu.Id.ToString(),
                new RequestContext(null, null, null, null),
                "{\"via\":\"enrollment_challenge\"}"));
            await _db.SaveChangesAsync(cancellationToken);
            recipient = (pu.Email, pu.FullName);
            return true;
        }

        // Commit evidence is the enabled factor itself (platform_users has no tenant filter), so a
        // lost COMMIT acknowledgement is reconciled without widening the query-filter bypass register.
        Task<bool> EnabledByThisCallAsync(CancellationToken cancellationToken) =>
            _db.PlatformUsers.AsNoTracking().AnyAsync(x => x.Id == envelope.PrincipalId
                && x.MfaEnabled && x.MfaConfiguredAtUtc == verifiedAtUtc, cancellationToken);

        if (!_db.Database.IsRelational())
        {
            if (!await VerifyOnceAsync(ct)) return null;
            await NotifyPlatformFactorEnrolledAsync(recipient, verifiedAtUtc, ct);
            return recovery.Codes;
        }
        var strategy = _db.Database.CreateExecutionStrategy();
        var succeeded = await strategy.ExecuteInTransactionAsync(
            VerifyOnceAsync, EnabledByThisCallAsync, IsolationLevel.ReadCommitted, ct);
        if (succeeded)
        {
            await NotifyPlatformFactorEnrolledAsync(recipient, verifiedAtUtc, ct);
            return recovery.Codes;
        }
        _db.ChangeTracker.Clear();
        return codeValid && await EnabledByThisCallAsync(ct) ? recovery.Codes : null;
    }

    public async Task<bool> AdminResetPlatformFactorAsync(
        Guid platformUserId, Guid actingPlatformUserId, RequestContext context, CancellationToken ct)
    {
        // Never self-service: resetting your own factor without proving it would make MFA optional.
        if (platformUserId == actingPlatformUserId) return false;
        var resetAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();

        async Task<bool> ResetOnceAsync(CancellationToken cancellationToken)
        {
            _db.ChangeTracker.Clear();
            var pu = await _db.PlatformUsers.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == platformUserId, cancellationToken);
            if (pu is null || (!pu.MfaEnabled && string.IsNullOrEmpty(pu.MfaSecretEncrypted)))
                return false;
            foreach (var challenge in await _db.MfaChallengeTokens
                .Where(x => x.PlatformUserId == pu.Id && x.UsedAtUtc == null)
                .ToListAsync(cancellationToken))
                challenge.UsedAtUtc = resetAtUtc;
            pu.MfaEnabled = false;
            pu.MfaSecretEncrypted = null;
            pu.MfaConfiguredAtUtc = null;
            pu.MfaRecoveryCodeHashes = null;
            pu.MfaLastTotpStep = null;
            PlatformSessionSecurity.RotateStamp(pu, resetAtUtc);
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                auditId,
                resetAtUtc,
                "platform.auth.mfa_reset_by_owner",
                "PlatformUser",
                pu.Id.ToString(),
                context with { UserId = null, TenantId = null },
                $"{{\"platformUserId\":\"{pu.Id:D}\",\"resetBy\":\"{actingPlatformUserId:D}\"}}"));
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (!_db.Database.IsRelational()) return await ResetOnceAsync(ct);
        var strategy = _db.Database.CreateExecutionStrategy();
        // Lost COMMIT acknowledgement: the reset is idempotent, so "no factor left" is the success
        // condition and a retry is harmless.
        return await strategy.ExecuteInTransactionAsync(
            ResetOnceAsync,
            cancellationToken => _db.PlatformUsers.AsNoTracking().AnyAsync(x => x.Id == platformUserId
                && !x.MfaEnabled && x.MfaSecretEncrypted == null, cancellationToken),
            IsolationLevel.ReadCommitted,
            ct);
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

    private Task NotifyPlatformFactorEnrolledAsync((string Email, string Name)? to, DateTime atUtc, CancellationToken ct)
        => NotifyPlatformAsync(to, "Two-step sign-in was turned on for your KynexOne platform account",
            FactorEnrolledHtml(to?.Name ?? string.Empty, atUtc), "enrolment", ct);

    private static string RecoveryNoticeHtml(string name, string what, DateTime atUtc) => $"""
        <p>Hello {System.Net.WebUtility.HtmlEncode(name)},</p>
        <p>{what} on your KynexOne platform account on {atUtc:yyyy-MM-dd HH:mm} UTC.</p>
        <p><strong>If this was not you</strong>, tell another platform Owner immediately and have your factor reset.</p>
        """;

    private async Task NotifyPlatformAsync((string Email, string Name)? to, string subject, string html, string kind, CancellationToken ct)
    {
        if (_email is null || to is not { } r || string.IsNullOrWhiteSpace(r.Email)) return;
        try
        {
            var result = await _email.DeliverPlatformAsync(r.Email, r.Name, subject, html, cancellationToken: ct);
            _log?.LogInformation("MFA {Kind} notice for a platform operator: {Outcome}.", kind, result.Status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Exception type only: an SMTP exception's message can carry the recipient address.
            _log?.LogWarning("MFA {Kind} notice for a platform operator could not be sent ({ExceptionType}).",
                kind, ex.GetType().Name);
        }
    }

    // ── Platform recovery codes ───────────────────────────────────────────────

    private const int RecoveryCodeCount = 10;
    private static readonly char[] RecoveryAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();

    private const int RecoveryCodeLength = 20;

    /// <summary>
    /// Ten fresh one-time codes, 20 symbols each from a 32-symbol alphabet without 0/O/1/I — 100 bits
    /// of entropy — shown as XXXX-XXXX-XXXX-XXXX-XXXX, with their newline-joined SHA-256 hashes. At
    /// 100 bits an offline guess against a leaked hash is infeasible, so an unsalted fast hash is
    /// sufficient (the standard the repo applies to reset and refresh tokens, ITokenService.HashToken).
    /// </summary>
    private (IReadOnlyList<string> Codes, string Hashes) NewRecoveryCodes()
    {
        var codes = new List<string>(RecoveryCodeCount);
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var chars = new char[RecoveryCodeLength];
            for (var c = 0; c < chars.Length; c++)
                chars[c] = RecoveryAlphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(RecoveryAlphabet.Length)];
            codes.Add(string.Join('-', Enumerable.Range(0, RecoveryCodeLength / 4).Select(g => new string(chars, g * 4, 4))));
        }
        return (codes, string.Join('\n', codes.Select(c => _tokenService.HashToken(NormalizeRecoveryCode(c)))));
    }

    /// <summary>Drops spaces, dashes and anything else that is not a letter or digit; upper-cases.</summary>
    private static string NormalizeRecoveryCode(string code)
        => new string(code.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static List<string> RecoveryHashes(string? stored)
        => (stored ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static int RecoveryCodesRemaining(PlatformUser pu) => RecoveryHashes(pu.MfaRecoveryCodeHashes).Count;

    public async Task<PlatformUser?> CompletePlatformChallengeWithRecoveryCodeAsync(
        string rawToken, string recoveryCode, RequestContext context, CancellationToken ct)
    {
        if (!AuthChallengeTokenCodec.TryParse(rawToken, AuthChallengeTokenCodec.PlatformLoginPurpose, out var envelope))
            return null;
        var hash = _tokenService.HashToken(rawToken);
        var presented = NormalizeRecoveryCode(recoveryCode ?? string.Empty);
        var presentedHash = presented.Length == RecoveryCodeLength ? _tokenService.HashToken(presented) : null;
        var now = DateTime.UtcNow;
        var completedAtUtc = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        PlatformUser? prepared = null;
        var remainingAfterUse = 0;

        async Task<bool> CompleteOnceAsync(CancellationToken cancellationToken)
        {
            _db.ChangeTracker.Clear();
            prepared = null;
            var pu = await _db.PlatformUsers.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == envelope.PrincipalId, cancellationToken);
            var challenge = await _db.MfaChallengeTokens.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == envelope.ChallengeId, cancellationToken);
            if (pu is null
                || challenge is null
                || challenge.TokenHash != hash
                || challenge.PlatformUserId != pu.Id
                || challenge.UserId is not null
                || challenge.TenantId is not null
                || challenge.UsedAtUtc is not null
                || challenge.ExpiresAtUtc <= completedAtUtc
                || challenge.FailedAttempts >= MfaChallengeToken.MaxAttempts
                || !pu.IsActive
                || !PlatformRoles.All.Contains(pu.Role)
                || (pu.LockoutEndUtc > completedAtUtc && !context.KnownDeviceVerified)
                || !pu.UpdatedAtUtc.HasValue
                || !string.Equals(envelope.SessionStamp, PlatformSessionSecurity.StampValue(pu.UpdatedAtUtc.Value), StringComparison.Ordinal)
                || !pu.MfaEnabled)
                return false;

            var remaining = RecoveryHashes(pu.MfaRecoveryCodeHashes);
            var index = presentedHash is null ? -1 : remaining.FindIndex(h => string.Equals(h, presentedHash, StringComparison.Ordinal));
            if (index < 0)
            {
                challenge.FailedAttempts++;
                if (challenge.FailedAttempts >= MfaChallengeToken.MaxAttempts)
                    challenge.UsedAtUtc = completedAtUtc;
                _db.LoginActivities.Add(new LoginActivity
                {
                    UserId = pu.Id, EmailAttempted = pu.Email, EventType = LoginEventTypes.PlatformLoginFailed,
                    FailureReason = "mfa_recovery_code_mismatch", IpAddress = context.IpAddress,
                    UserAgent = context.UserAgent, OccurredAtUtc = completedAtUtc,
                });
                _db.AuditLogs.Add(AuthAuditEntry.Create(
                    Guid.NewGuid(), completedAtUtc, "platform.auth.mfa_recovery_code_failed", "MfaChallengeToken",
                    challenge.Id.ToString(), context with { UserId = null, TenantId = null },
                    $"{{\"platformUserId\":\"{pu.Id:D}\",\"failedAttempts\":{challenge.FailedAttempts}}}"));
                await _db.SaveChangesAsync(cancellationToken);
                return false;
            }

            remaining.RemoveAt(index);
            pu.MfaRecoveryCodeHashes = remaining.Count == 0 ? null : string.Join('\n', remaining);
            challenge.UsedAtUtc = completedAtUtc;
            if (!(context.KnownDeviceVerified && pu.LockoutEndUtc > completedAtUtc))
                pu.FailedLoginCount = 0; // a bypass of an active lockout leaves the shared counter alone
            pu.LastLoginAtUtc = completedAtUtc;
            pu.LastLoginIp = context.IpAddress;
            _db.LoginActivities.Add(new LoginActivity
            {
                UserId = pu.Id, EmailAttempted = pu.Email, EventType = LoginEventTypes.PlatformLoginSuccess,
                IpAddress = context.IpAddress, UserAgent = context.UserAgent, OccurredAtUtc = completedAtUtc,
            });
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                Guid.NewGuid(), completedAtUtc, "platform.auth.mfa_recovery_code_used", "PlatformUser",
                pu.Id.ToString(), context with { UserId = null, TenantId = null },
                $"{{\"platformUserId\":\"{pu.Id:D}\",\"remaining\":{remaining.Count}}}"));
            await _db.SaveChangesAsync(cancellationToken);
            prepared = pu;
            remainingAfterUse = remaining.Count;
            return true;
        }

        // Commit evidence: this call's login stamp on the operator row (platform_users is unfiltered).
        Task<bool> CommittedAsync(CancellationToken cancellationToken) =>
            _db.PlatformUsers.AsNoTracking().AnyAsync(x => x.Id == envelope.PrincipalId && x.LastLoginAtUtc == completedAtUtc, cancellationToken);

        bool succeeded;
        if (_db.Database.IsRelational())
        {
            succeeded = await _db.Database.CreateExecutionStrategy().ExecuteInTransactionAsync(
                CompleteOnceAsync, CommittedAsync, IsolationLevel.ReadCommitted, ct);
            if (!succeeded && presentedHash is not null)
            {
                _db.ChangeTracker.Clear();
                succeeded = await CommittedAsync(ct);
            }
        }
        else
        {
            succeeded = await CompleteOnceAsync(ct);
        }
        if (!succeeded) return null;
        var signedIn = prepared ?? await _db.PlatformUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == envelope.PrincipalId, ct);
        if (signedIn is not null)
            await NotifyPlatformAsync((signedIn.Email, signedIn.FullName),
                "A recovery code was used to sign in to your KynexOne platform account",
                RecoveryNoticeHtml(signedIn.FullName,
                    $"A one-time recovery code was used to sign in ({remainingAfterUse} code(s) left)", completedAtUtc),
                "recovery-code-use", ct);
        return signedIn;
    }

    public async Task<IReadOnlyList<string>?> RegeneratePlatformRecoveryCodesAsync(Guid platformUserId, string totpCode, CancellationToken ct)
    {
        var recovery = NewRecoveryCodes();
        var now = DateTime.UtcNow;
        (string Email, string Name)? recipient = null;

        async Task<bool> RegenerateOnceAsync(CancellationToken cancellationToken)
        {
            _db.ChangeTracker.Clear();
            var pu = await _db.PlatformUsers.TagWith(RowLockingInterceptor.ForUpdateTag)
                .SingleOrDefaultAsync(x => x.Id == platformUserId, cancellationToken);
            if (pu is null || !pu.IsActive || !pu.MfaEnabled || string.IsNullOrWhiteSpace(pu.MfaSecretEncrypted))
                return false;
            string secret;
            try { secret = _totp.DecryptSecret(pu.MfaSecretEncrypted); }
            catch { return false; }
            var step = _totp.MatchStep(secret, totpCode);
            if (!TotpService.IsFreshStep(step, pu.MfaLastTotpStep)) return false;
            pu.MfaLastTotpStep = step;
            pu.MfaRecoveryCodeHashes = recovery.Hashes;
            _db.AuditLogs.Add(AuthAuditEntry.Create(
                Guid.NewGuid(), now, "platform.auth.mfa_recovery_codes_regenerated", "PlatformUser",
                pu.Id.ToString(), new RequestContext(null, null, null, null),
                $"{{\"platformUserId\":\"{pu.Id:D}\",\"count\":{RecoveryCodeCount}}}"));
            await _db.SaveChangesAsync(cancellationToken);
            recipient = (pu.Email, pu.FullName);
            return true;
        }

        Task<bool> CommittedAsync(CancellationToken cancellationToken) =>
            _db.PlatformUsers.AsNoTracking().AnyAsync(x => x.Id == platformUserId && x.MfaRecoveryCodeHashes == recovery.Hashes, cancellationToken);

        var ok = _db.Database.IsRelational()
            ? await _db.Database.CreateExecutionStrategy().ExecuteInTransactionAsync(
                RegenerateOnceAsync, CommittedAsync, IsolationLevel.ReadCommitted, ct)
            : await RegenerateOnceAsync(ct);
        if (!ok) return null;
        await NotifyPlatformAsync(recipient, "New recovery codes were generated for your KynexOne platform account",
            RecoveryNoticeHtml(recipient?.Name ?? string.Empty, "New recovery codes were generated (all earlier codes stopped working)", now),
            "recovery-codes-regenerated", ct);
        return recovery.Codes;
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

    private Task<PlatformUser?> LoadPlatformUser(Guid platformUserId, CancellationToken ct) =>
        _db.PlatformUsers.FirstOrDefaultAsync(x => x.Id == platformUserId && x.IsActive, ct);
}
