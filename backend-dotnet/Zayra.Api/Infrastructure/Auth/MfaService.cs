using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Jobs;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

public class MfaService : IMfaService
{
    private const int ChallengeTtlSeconds = 300; // 5 minutes
    private const string Issuer = "Zayra HRM";

    private readonly ZayraDbContext _db;
    private readonly TotpService _totp;
    private readonly ITokenService _tokenService;
    private readonly IAuditService _audit;

    public MfaService(ZayraDbContext db, TotpService totp, ITokenService tokenService, IAuditService audit)
    {
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
        if (!_totp.Verify(request.TempSecret, request.TotpCode)) return false;

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
        var codeValid = _totp.Verify(request.TempSecret, request.TotpCode);
        var encryptedSecret = codeValid ? _totp.EncryptSecret(request.TempSecret) : null;
        var auditId = Guid.NewGuid();

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
            return true;
        }

        if (!_db.Database.IsRelational()) return await VerifyOnceAsync(ct);
        var strategy = _db.Database.CreateExecutionStrategy();
        var succeeded = await strategy.ExecuteInTransactionAsync(
            VerifyOnceAsync,
            async cancellationToken => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId, cancellationToken),
            IsolationLevel.ReadCommitted,
            ct);
        if (succeeded) return true;

        // A successful COMMIT whose acknowledgement was lost is surfaced as default(bool)
        // after verifySucceeded. Reconcile the exact success marker instead of telling the
        // client that its now-consumed enrollment credential failed.
        _db.ChangeTracker.Clear();
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
                if (string.IsNullOrWhiteSpace(totpCode) || !_totp.Verify(plainSecret, totpCode))
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

    public async Task<bool> VerifyPlatformSetupAsync(Guid platformUserId, MfaVerifySetupRequest request, CancellationToken ct)
    {
        if (!_totp.Verify(request.TempSecret, request.TotpCode)) return false;

        var encryptedSecret = _totp.EncryptSecret(request.TempSecret);
        var configuredAtUtc = DateTime.UtcNow;
        var auditId = Guid.NewGuid();

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
            return true;
        }

        if (!_db.Database.IsRelational()) return await EnableOnceAsync(ct);
        var strategy = _db.Database.CreateExecutionStrategy();
        var succeeded = await strategy.ExecuteInTransactionAsync(
            EnableOnceAsync,
            async cancellationToken => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId && x.Action == "platform.auth.mfa_enabled", cancellationToken),
            IsolationLevel.ReadCommitted,
            ct);
        if (succeeded) return true;
        _db.ChangeTracker.Clear();
        return await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(x => x.Id == auditId && x.Action == "platform.auth.mfa_enabled", ct);
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
                : pu.LockoutEndUtc > completedAtUtc ? "platform_account_locked"
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

            if (!_totp.Verify(plainSecret, totpCode))
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
            pu.FailedLoginCount = 0;
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
            && (!committed.LockoutEndUtc.HasValue || committed.LockoutEndUtc <= DateTime.UtcNow)
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
            if (!_totp.Verify(plainSecret, totpCode)) return false;

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
            async cancellationToken => await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.Id == auditId && x.Action == "platform.auth.mfa_disabled", cancellationToken),
            IsolationLevel.ReadCommitted,
            ct);
        if (succeeded) return true;
        _db.ChangeTracker.Clear();
        return await _db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(x => x.Id == auditId && x.Action == "platform.auth.mfa_disabled", ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task<User?> LoadCompleteTenantGraphAsync(Guid userId, Guid tenantId, CancellationToken ct) =>
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
