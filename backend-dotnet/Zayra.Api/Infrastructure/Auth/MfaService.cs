using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
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
        var tempSecret = _totp.GenerateBase32Secret();
        var uri = _totp.GenerateProvisioningUri(user.Email, Issuer, tempSecret);
        return new MfaSetupInitDto(uri, tempSecret);
    }

    public async Task<bool> VerifySetupAsync(Guid userId, Guid tenantId, MfaVerifySetupRequest request, CancellationToken ct)
    {
        if (!_totp.Verify(request.TempSecret, request.TotpCode)) return false;

        var user = await LoadUser(userId, ct) ?? throw new InvalidOperationException("User not found.");
        user.MFAEnabled = true;
        user.MfaSecretEncrypted = _totp.EncryptSecret(request.TempSecret);
        user.MfaConfiguredAtUtc = DateTime.UtcNow;
        user.MfaFailedCount = 0;
        user.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("auth.mfa.enabled", "User", user.Id.ToString(),
            new RequestContext(null, null, userId, tenantId), null, ct);
        return true;
    }

    public async Task<string> CreateChallengeAsync(Guid userId, Guid tenantId, string ip, CancellationToken ct)
    {
        var rawToken = _tokenService.CreateSecureToken();
        _db.MfaChallengeTokens.Add(new MfaChallengeToken
        {
            UserId = userId,
            TenantId = tenantId,
            TokenHash = _tokenService.HashToken(rawToken),
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(ChallengeTtlSeconds),
            CreatedByIp = ip
        });
        await _db.SaveChangesAsync(ct);
        return rawToken;
    }

    public async Task<User?> VerifyChallengeAsync(string rawToken, string totpCode, CancellationToken ct)
    {
        var hash = _tokenService.HashToken(rawToken);
        var challenge = await _db.MfaChallengeTokens
            .FirstOrDefaultAsync(x => x.TokenHash == hash && x.UserId != null, ct);
        if (challenge is null || !challenge.IsValid) return null;

        var user = await LoadUser(challenge.UserId!.Value, ct);
        if (user is null || !user.MFAEnabled || string.IsNullOrEmpty(user.MfaSecretEncrypted)) return null;

        string plainSecret;
        try { plainSecret = _totp.DecryptSecret(user.MfaSecretEncrypted); }
        catch { return null; }

        if (!_totp.Verify(plainSecret, totpCode))
        {
            user.MfaFailedCount++;
            user.UpdatedAtUtc = DateTime.UtcNow;
            // Consume the challenge after too many wrong codes so the same token can't be replayed
            // to grind the 6-digit space within the TTL. The user must re-authenticate for a new one.
            challenge.FailedAttempts++;
            if (challenge.FailedAttempts >= MfaChallengeToken.MaxAttempts)
                challenge.UsedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return null;
        }

        challenge.UsedAtUtc = DateTime.UtcNow;
        user.MfaLastVerifiedAtUtc = DateTime.UtcNow;
        user.MfaFailedCount = 0;
        user.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<bool> DisableAsync(Guid userId, Guid tenantId, string totpCode, CancellationToken ct)
    {
        var user = await LoadUser(userId, ct);
        if (user is null || !user.MFAEnabled || string.IsNullOrEmpty(user.MfaSecretEncrypted)) return false;

        string plainSecret;
        try { plainSecret = _totp.DecryptSecret(user.MfaSecretEncrypted); }
        catch { return false; }

        if (!_totp.Verify(plainSecret, totpCode)) return false;

        user.MFAEnabled = false;
        user.MfaSecretEncrypted = null;
        user.MfaConfiguredAtUtc = null;
        user.MfaLastVerifiedAtUtc = null;
        user.MfaFailedCount = 0;
        user.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.WriteAsync("auth.mfa.disabled", "User", user.Id.ToString(),
            new RequestContext(null, null, userId, tenantId), null, ct);
        return true;
    }

    // ── Platform user ─────────────────────────────────────────────────────────

    public async Task<MfaSetupInitDto> InitiatePlatformSetupAsync(Guid platformUserId, CancellationToken ct)
    {
        var pu = await LoadPlatformUser(platformUserId, ct) ?? throw new InvalidOperationException("Platform user not found.");
        var tempSecret = _totp.GenerateBase32Secret();
        var uri = _totp.GenerateProvisioningUri(pu.Email, Issuer, tempSecret);
        return new MfaSetupInitDto(uri, tempSecret);
    }

    public async Task<bool> VerifyPlatformSetupAsync(Guid platformUserId, MfaVerifySetupRequest request, CancellationToken ct)
    {
        if (!_totp.Verify(request.TempSecret, request.TotpCode)) return false;

        var pu = await LoadPlatformUser(platformUserId, ct) ?? throw new InvalidOperationException("Platform user not found.");
        pu.MfaEnabled = true;
        pu.MfaSecretEncrypted = _totp.EncryptSecret(request.TempSecret);
        pu.MfaConfiguredAtUtc = DateTime.UtcNow;
        pu.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<string> CreatePlatformChallengeAsync(Guid platformUserId, string ip, CancellationToken ct)
    {
        var rawToken = _tokenService.CreateSecureToken();
        _db.MfaChallengeTokens.Add(new MfaChallengeToken
        {
            PlatformUserId = platformUserId,
            TokenHash = _tokenService.HashToken(rawToken),
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(ChallengeTtlSeconds),
            CreatedByIp = ip
        });
        await _db.SaveChangesAsync(ct);
        return rawToken;
    }

    public async Task<PlatformUser?> VerifyPlatformChallengeAsync(string rawToken, string totpCode, CancellationToken ct)
    {
        var hash = _tokenService.HashToken(rawToken);
        var challenge = await _db.MfaChallengeTokens
            .FirstOrDefaultAsync(x => x.TokenHash == hash && x.PlatformUserId != null, ct);
        if (challenge is null || !challenge.IsValid) return null;

        var pu = await LoadPlatformUser(challenge.PlatformUserId!.Value, ct);
        if (pu is null || !pu.MfaEnabled || string.IsNullOrEmpty(pu.MfaSecretEncrypted)) return null;

        string plainSecret;
        try { plainSecret = _totp.DecryptSecret(pu.MfaSecretEncrypted); }
        catch { return null; }

        if (!_totp.Verify(plainSecret, totpCode))
        {
            // Same brute-force hardening as the tenant path: consume the challenge after MaxAttempts.
            challenge.FailedAttempts++;
            if (challenge.FailedAttempts >= MfaChallengeToken.MaxAttempts)
                challenge.UsedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return null;
        }

        challenge.UsedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return pu;
    }

    public async Task<bool> DisablePlatformAsync(Guid platformUserId, string totpCode, CancellationToken ct)
    {
        var pu = await LoadPlatformUser(platformUserId, ct);
        if (pu is null || !pu.MfaEnabled || string.IsNullOrEmpty(pu.MfaSecretEncrypted)) return false;

        string plainSecret;
        try { plainSecret = _totp.DecryptSecret(pu.MfaSecretEncrypted); }
        catch { return false; }

        if (!_totp.Verify(plainSecret, totpCode)) return false;

        pu.MfaEnabled = false;
        pu.MfaSecretEncrypted = null;
        pu.MfaConfiguredAtUtc = null;
        pu.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task<User?> LoadUser(Guid userId, CancellationToken ct) =>
        _db.Users.FirstOrDefaultAsync(x => x.Id == userId && !x.IsDeleted, ct);

    private Task<PlatformUser?> LoadPlatformUser(Guid platformUserId, CancellationToken ct) =>
        _db.PlatformUsers.FirstOrDefaultAsync(x => x.Id == platformUserId && x.IsActive, ct);
}
