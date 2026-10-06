using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// Server-side validation and revocation for privileged platform JWTs.
///
/// Platform tokens are deliberately short-lived, but signature validation alone is not enough:
/// a deactivated or downgraded owner would otherwise retain the claims minted at login for the
/// full token lifetime. The token therefore carries a security stamp derived from the platform
/// user's <see cref="PlatformUser.UpdatedAtUtc"/> value. Every platform request re-checks that the
/// user is still active, still has the claimed role, and still has the same stamp.
/// </summary>
public static class PlatformSessionSecurity
{
    public const string SessionStampClaim = "platform_session_stamp";

    /// <summary>PostgreSQL stores timestamptz values at microsecond precision.</summary>
    private static DateTime TruncateToDatabasePrecision(DateTime value)
        => new(value.ToUniversalTime().Ticks - value.ToUniversalTime().Ticks % 10, DateTimeKind.Utc);

    public static DateTime RotateStamp(PlatformUser user)
        => RotateStamp(user, DateTime.UtcNow);

    public static DateTime RotateStamp(PlatformUser user, DateTime candidateUtc)
    {
        var now = TruncateToDatabasePrecision(candidateUtc);
        var current = user.UpdatedAtUtc.HasValue
            ? TruncateToDatabasePrecision(user.UpdatedAtUtc.Value)
            : DateTime.UnixEpoch;
        if (now <= current) now = current.AddTicks(10);
        user.UpdatedAtUtc = now;
        return now;
    }

    public static string StampValue(DateTime value)
        => (TruncateToDatabasePrecision(value).Ticks / 10).ToString(CultureInfo.InvariantCulture);

    /// <summary>The bypass claim's value: the bypassed LockoutEndUtc in microseconds (database precision).</summary>
    public static string LockoutClaimValue(DateTime lockoutEndUtc)
        => (TruncateToDatabasePrecision(lockoutEndUtc).Ticks / 10).ToString(CultureInfo.InvariantCulture);

    internal static bool BypassCovers(string? claimValue, DateTime currentLockoutEndUtc)
        => long.TryParse(claimValue, NumberStyles.None, CultureInfo.InvariantCulture, out var bypassedMicros)
           && TruncateToDatabasePrecision(currentLockoutEndUtc).Ticks / 10 <= bypassedMicros;

    public static async Task<bool> IsCurrentAsync(
        ClaimsPrincipal principal,
        ZayraDbContext db,
        CancellationToken cancellationToken)
    {
        if (!principal.HasClaim("is_platform_admin", "true")) return true;

        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var claimedRole = principal.FindFirstValue("platform_role");
        var claimedStamp = principal.FindFirstValue(SessionStampClaim);
        if (!Guid.TryParse(subject, out var platformUserId)
            || string.IsNullOrWhiteSpace(claimedRole)
            || string.IsNullOrWhiteSpace(claimedStamp))
            return false;

        var current = await db.PlatformUsers.AsNoTracking()
            .Where(x => x.Id == platformUserId)
            .Select(x => new { x.IsActive, x.Role, x.UpdatedAtUtc, x.LockoutEndUtc })
            .SingleOrDefaultAsync(cancellationToken);

        return current is not null
            && current.IsActive
            && PlatformRoles.All.Contains(current.Role)
            // A session issued through a known-device lockout bypass carries LockoutBypassClaim with
            // the LockoutEndUtc it bypassed. It is accepted only while the current lockout ends at that
            // moment or earlier: a NEW, longer lockout (more attacker failures) is not bypassed by it.
            && (!current.LockoutEndUtc.HasValue || current.LockoutEndUtc <= DateTime.UtcNow
                || BypassCovers(principal.FindFirstValue(Zayra.Api.Controllers.PlatformController.LockoutBypassClaim), current.LockoutEndUtc.Value))
            && string.Equals(current.Role, claimedRole, StringComparison.Ordinal)
            && current.UpdatedAtUtc.HasValue
            && string.Equals(StampValue(current.UpdatedAtUtc.Value), claimedStamp, StringComparison.Ordinal);
    }
}
