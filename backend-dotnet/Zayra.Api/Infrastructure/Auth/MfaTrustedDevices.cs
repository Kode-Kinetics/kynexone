using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// Trusted browsers for tenant two-step sign-in (contract: scratchpad mfa-contract.md, Amendment 1).
///
/// <para><b>What trust skips.</b> Only the CODE step of a user who already has a factor enrolled, and only after the
/// password was verified in the same sign-in. It never skips enrolment (mfaEnrollmentRequired), the password, the
/// lockout or the abuse windows, and platform operators (/api/platform/auth) never get it.</para>
///
/// <para><b>The token.</b> 32 bytes from <see cref="RandomNumberGenerator"/>, base64url in the HttpOnly
/// <see cref="CookieName"/> cookie; the row stores its SHA-256 only and is looked up by that hash, so no comparison
/// against a secret ever runs in variable time. A malformed cookie is never hashed or looked up.</para>
///
/// <para><b>When a row counts</b> (<see cref="IsUsable"/>): same tenant and user as the password step, not revoked,
/// not expired, created no earlier than the user's current factor and current password, inside the tenant's CURRENT
/// lifetime (lowering the setting, or setting it to 0, shortens trust already given), and the user still has the
/// factor. The caller has already established that tenant and user are active.</para>
///
/// <para><b>Revocation.</b> <see cref="StageRevokeAllAsync"/> is the one method every path uses: the explicit calls
/// (password change/reset, disable, platform recovery disable, re-setup, the user's own "sign out all") and the
/// ZayraDbContext save hook, which catches every other password, factor, deactivation and deletion change of a
/// login. It stages tracked changes and audit rows; the caller's SaveChanges commits them with its own change.</para>
/// </summary>
public static class MfaTrustedDevices
{
    public const string CookieName = "kx_mfa_trust";
    public const string CookiePath = "/api/auth";
    public const string TrustedAuditAction = "auth.mfa.device_trusted";
    public const string UsedAuditAction = "auth.mfa.trusted_device_used";
    public const string RevokedAuditAction = "auth.mfa.trusted_device_revoked";

    /// <summary>base64url of 32 bytes, unpadded.</summary>
    private const int TokenLength = 43;

    // ── Token and cookie ────────────────────────────────────────────────────────────────────

    public static (string Raw, string Hash) NewToken()
    {
        var raw = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (raw, Sha256(raw));
    }

    /// <summary>The stored hash of a presented cookie, or null when it is absent or not a token we could have issued.</summary>
    public static string? HashPresented(string? raw)
    {
        if (raw is null || raw.Length != TokenLength) return null;
        foreach (var c in raw)
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return null;
        return Sha256(raw);
    }

    public static string? ReadCookie(HttpRequest request) => request.Cookies[CookieName];

    /// <summary>Same shape as LoginAbuseGuard.AppendKnownDeviceCookie: HttpOnly, Secure, SameSite=Strict, /api/auth.</summary>
    public static void AppendCookie(HttpResponse response, string? token, int days)
    {
        if (string.IsNullOrEmpty(token) || days <= 0) return;
        response.Cookies.Append(CookieName, token, Options(TimeSpan.FromDays(days)));
    }

    public static void ClearCookie(HttpResponse response) =>
        response.Cookies.Delete(CookieName, Options(null));

    private static CookieOptions Options(TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
        MaxAge = maxAge,
        IsEssential = true,
    };

    // ── Policy ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The tenant's trust lifetime in days; 30 when the tenant has no settings row; always 0..90.</summary>
    public static int EffectiveDays(SecuritySetting? policy) =>
        Math.Clamp(policy?.MfaTrustedDeviceDays ?? SecuritySetting.DefaultMfaTrustedDeviceDays, 0, SecuritySetting.MaxMfaTrustedDeviceDays);

    public static bool IsUsable(MfaTrustedDevice? row, User user, int tenantDays, DateTime nowUtc) =>
        row is not null
        && row.TenantId == user.TenantId
        && row.UserId == user.Id
        && row.RevokedAtUtc is null
        && row.ExpiresAtUtc > nowUtc
        && tenantDays > 0
        && row.CreatedAtUtc.AddDays(tenantDays) > nowUtc
        && PrivilegedMfaPolicy.IsTenantUserEnrolled(user)
        && (user.MfaConfiguredAtUtc is not { } configured || row.CreatedAtUtc >= configured)
        && (user.LastPasswordChangedAt is not { } changed || row.CreatedAtUtc >= changed);

    public static MfaTrustedDevice NewRow(Guid id, Guid tenantId, Guid userId, string tokenHash, DateTime nowUtc, int days, RequestContext context) => new()
    {
        Id = id,
        TenantId = tenantId,
        UserId = userId,
        TokenHash = tokenHash,
        CreatedAtUtc = nowUtc,
        ExpiresAtUtc = nowUtc.AddDays(days),
        UserAgent = Truncate(context.UserAgent, 256),
        IpAddress = Truncate(context.IpAddress, 64),
    };

    // ── Revocation ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stages the revocation of every live trusted browser of <paramref name="userIds"/> in <paramref name="tenantId"/>,
    /// with one <see cref="RevokedAuditAction"/> row per user that had any. Nothing is saved here: the caller's
    /// SaveChanges commits it together with the change that caused it. Idempotent — a row already revoked (in the
    /// database or earlier in this unit of work) is left alone, so a second call is a no-op. Returns rows revoked.
    /// </summary>
    public static async Task<int> StageRevokeAllAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyCollection<Guid> userIds, string reason, DateTime atUtc,
        RequestContext context, CancellationToken ct, bool synchronous = false)
    {
        if (userIds.Count == 0) return 0;
        var ids = userIds.Distinct().ToList();
        var query = db.MfaTrustedDevices.Where(x => x.TenantId == tenantId && ids.Contains(x.UserId) && x.RevokedAtUtc == null);
        var rows = synchronous ? query.ToList() : await query.ToListAsync(ct);
        // Rows added earlier in this same unit of work are not in the database yet.
        rows.AddRange(db.ChangeTracker.Entries<MfaTrustedDevice>()
            .Where(e => e.State == EntityState.Added && e.Entity.TenantId == tenantId && ids.Contains(e.Entity.UserId))
            .Select(e => e.Entity));
        return Revoke(db, rows.Distinct(), reason, atUtc, context);
    }

    /// <summary>Revokes one of the CALLER's own rows. False when the id is not theirs (the endpoint answers 404).</summary>
    public static async Task<(bool Found, MfaTrustedDevice? Row)> StageRevokeOneAsync(
        ZayraDbContext db, Guid tenantId, Guid userId, Guid id, DateTime atUtc, RequestContext context, CancellationToken ct)
    {
        var row = await db.MfaTrustedDevices.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && x.UserId == userId, ct);
        if (row is null) return (false, null);
        Revoke(db, [row], MfaTrustRevokeReasons.UserRevoked, atUtc, context);
        return (true, row);
    }

    private static int Revoke(ZayraDbContext db, IEnumerable<MfaTrustedDevice> rows, string reason, DateTime atUtc, RequestContext context)
    {
        var revoked = 0;
        foreach (var group in rows.Where(r => r.RevokedAtUtc is null).GroupBy(r => (r.TenantId, r.UserId)))
        {
            var count = 0;
            foreach (var row in group)
            {
                row.RevokedAtUtc = atUtc;
                row.RevokedReason = reason;
                count++;
            }
            revoked += count;
            db.AuditLogs.Add(AuthAuditEntry.Create(
                Guid.NewGuid(), atUtc, RevokedAuditAction, "User", group.Key.UserId.ToString(),
                context with { TenantId = group.Key.TenantId },
                $"{{\"reason\":\"{reason}\",\"count\":{count}}}"));
        }
        return revoked;
    }

    // ── Listing ─────────────────────────────────────────────────────────────────────────────

    public static IQueryable<MfaTrustedDevice> Live(ZayraDbContext db, Guid tenantId, Guid userId, DateTime nowUtc) =>
        db.MfaTrustedDevices.Where(x => x.TenantId == tenantId && x.UserId == userId && x.RevokedAtUtc == null && x.ExpiresAtUtc > nowUtc);

    /// <summary>"Firefox on macOS": a short human summary of a User-Agent. Never a raw string, never a guess beyond
    /// these; null when nothing is recognised (the UI shows "Unknown browser").</summary>
    public static string? DescribeBrowser(string? userAgent)
    {
        var ua = userAgent ?? string.Empty;
        string? browser =
            ua.Contains("Edg/", StringComparison.Ordinal) || ua.Contains("EdgA/", StringComparison.Ordinal) || ua.Contains("EdgiOS/", StringComparison.Ordinal) ? "Edge"
            : ua.Contains("OPR/", StringComparison.Ordinal) || ua.Contains("Opera", StringComparison.Ordinal) ? "Opera"
            : ua.Contains("SamsungBrowser/", StringComparison.Ordinal) ? "Samsung Internet"
            : ua.Contains("Firefox/", StringComparison.Ordinal) || ua.Contains("FxiOS/", StringComparison.Ordinal) ? "Firefox"
            : ua.Contains("Chrome/", StringComparison.Ordinal) || ua.Contains("CriOS/", StringComparison.Ordinal) ? "Chrome"
            : ua.Contains("Safari/", StringComparison.Ordinal) ? "Safari"
            : null;
        string? os =
            ua.Contains("iPhone", StringComparison.Ordinal) || ua.Contains("iPad", StringComparison.Ordinal) ? "iOS"
            : ua.Contains("Android", StringComparison.Ordinal) ? "Android"
            : ua.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : ua.Contains("Mac OS X", StringComparison.Ordinal) || ua.Contains("Macintosh", StringComparison.Ordinal) ? "macOS"
            : ua.Contains("CrOS", StringComparison.Ordinal) ? "ChromeOS"
            : ua.Contains("Linux", StringComparison.Ordinal) ? "Linux"
            : null;
        return (browser, os) switch
        {
            ({ } b, { } o) => $"{b} on {o}",
            ({ } b, null) => b,
            (null, { } o) => $"A browser on {o}",
            _ => null,
        };
    }

    private static string Sha256(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(raw)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
