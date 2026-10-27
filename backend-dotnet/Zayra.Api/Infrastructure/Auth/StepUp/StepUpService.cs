using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Employees.Ess;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Auth.StepUp;

/// <summary>
/// Contract C-2. Proof of a fresh password (and authenticator code, when the account has one) as a short-lived,
/// HMAC-signed token. Stateless: nothing is stored, so there is nothing to purge and nothing a database reader could replay.
/// The key is derived (HKDF) from the JWT signing secret under its own label, so a step-up token is never a valid session token
/// and a session token is never a valid step-up token.
/// </summary>
public sealed class StepUpService : IStepUpService
{
    private const string KeyLabel = "kynexone.step-up.v1";
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ZayraDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly TotpService _totp;
    private readonly PasswordVerificationGate? _gate;
    private readonly Func<DateTime> _clock;
    private readonly byte[] _key;

    public StepUpService(ZayraDbContext db, IPasswordHasher hasher, TotpService totp, IConfiguration config,
        PasswordVerificationGate? gate = null, Func<DateTime>? clock = null)
    {
        _db = db; _hasher = hasher; _totp = totp; _gate = gate; _clock = clock ?? (() => DateTime.UtcNow);
        var secret = config["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32) throw new InvalidOperationException("Jwt:SigningKey is required to sign step-up tokens.");
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32, info: Encoding.UTF8.GetBytes(KeyLabel));
    }

    public Task<StepUpResult> VerifyAsync(Guid userId, string purpose, StepUpCredential credential, CancellationToken ct) =>
        VerifyCoreAsync(userId, purpose, credential, null, null, countFailures: true, ct);

    public Task<StepUpResult> VerifyDeviceAsync(Guid employeeUserId, Guid witnessUserId, string bind, StepUpCredential credential, CancellationToken ct) =>
        VerifyCoreAsync(employeeUserId, StepUpPurposes.Esign, credential, bind, witnessUserId, countFailures: false, ct);

    private async Task<StepUpResult> VerifyCoreAsync(Guid userId, string purpose, StepUpCredential credential, string? bind, Guid? witness, bool countFailures, CancellationToken ct)
    {
        if (!StepUpPurposes.IsKnown(purpose)) return new(false, null, null, "unknown_purpose");
        var now = _clock();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive || user.IsDeleted) return new(false, null, null, EssProblemCodes.InvalidCredentials);
        if (AuthCurrentEligibility.IsFailureLockoutActive(user, now)) return new(false, null, null, EssProblemCodes.Locked);

        var sec = await _db.SecuritySettings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == user.TenantId, ct);
        var maxAttempts = sec?.MaxFailedLoginAttempts ?? 5;
        var lockoutMinutes = sec?.LockoutDurationMinutes ?? 15;

        var password = credential.Password ?? string.Empty;
        var passwordOk = password.Length > 0 && await PasswordVerificationGate.RunAsync(_gate, () => _hasher.Verify(password, user.PasswordHash), ct);
        var needsCode = user.MFAEnabled && !string.IsNullOrEmpty(user.MfaSecretEncrypted);
        long? step = null;
        if (passwordOk && needsCode)
        {
            if (string.IsNullOrWhiteSpace(credential.MfaCode)) return new(false, null, null, "mfa_required");
            try { step = _totp.MatchStep(_totp.DecryptSecret(user.MfaSecretEncrypted!), credential.MfaCode); } catch { step = null; }
            if (!TotpService.IsFreshStep(step, user.MfaLastTotpStep)) passwordOk = false;
        }
        if (!passwordOk)
        {
            if (countFailures)
            {
                // The same counter and the same lockout as sign-in: this endpoint is not a cheaper way to guess a password.
                user.FailedLoginCount++;
                if (user.FailedLoginCount >= maxAttempts) { user.IsLocked = true; user.LockoutEnd = now.AddMinutes(lockoutMinutes); }
                await _db.SaveChangesAsync(ct);
            }
            return new(false, null, null, EssProblemCodes.InvalidCredentials);
        }
        if (countFailures) user.FailedLoginCount = 0;
        if (step is not null) user.MfaLastTotpStep = step;
        await _db.SaveChangesAsync(ct);
        var max = purpose == StepUpPurposes.Esign ? StepUpPurposes.EsignMaxAge : StepUpPurposes.EssSensitiveMaxAge;
        return new(true, Issue(user.Id, user.TenantId, purpose, now, bind, witness), now.Add(max), null);
    }

    public bool IsFresh(string? token, ClaimsPrincipal principal, string purpose, TimeSpan max)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return Guid.TryParse(sub, out var userId) && Guid.TryParse(principal.FindFirstValue("tenant_id"), out var tenantId)
            && IsFresh(token, userId, tenantId, purpose, max);
    }

    public bool IsFresh(string? token, Guid userId, Guid tenantId, string purpose, TimeSpan max, string? bind = null, Guid? witness = null) =>
        Read(token, userId, tenantId, purpose, max, bind, witness) is not null;

    public string? ReadJti(string? token, Guid userId, Guid tenantId, string purpose, string? bind = null, Guid? witness = null) =>
        Read(token, userId, tenantId, purpose, TimeSpan.FromHours(1), bind, witness)?.Jti;

    internal string Issue(Guid userId, Guid tenantId, string purpose, DateTime nowUtc, string? bind = null, Guid? witness = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(userId, tenantId, purpose, new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToUnixTimeSeconds(), Guid.NewGuid().ToString("N"), bind, witness), Json);
        var body = Base64Url(payload);
        return body + "." + Base64Url(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(body)));
    }

    private Payload? Read(string? token, Guid userId, Guid tenantId, string purpose, TimeSpan max, string? bind = null, Guid? witness = null)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024) return null;
        var parts = token.Split('.');
        if (parts.Length != 2) return null;
        try
        {
            var expected = HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(parts[0]));
            if (!CryptographicOperations.FixedTimeEquals(expected, FromBase64Url(parts[1]))) return null;
            var p = JsonSerializer.Deserialize<Payload>(FromBase64Url(parts[0]), Json);
            if (p is null || p.Sub != userId || p.Tenant != tenantId || !string.Equals(p.Purpose, purpose, StringComparison.Ordinal)) return null;
            // A bound token opens only the thing it was bound to, for the HR user it was issued to; an unbound one can never open a bound thing.
            if (!string.Equals(p.Bind, bind, StringComparison.Ordinal) || p.Wit != witness) return null;
            var age = _clock() - DateTimeOffset.FromUnixTimeSeconds(p.Iat).UtcDateTime;
            return age >= -Skew && age <= max ? p : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException) { return null; }
    }

    private sealed record Payload(Guid Sub, Guid Tenant, string Purpose, long Iat, string Jti, string? Bind, Guid? Wit = null);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '='));
    }
}
