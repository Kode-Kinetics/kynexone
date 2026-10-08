using System.Security.Cryptography;
using System.Text;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// The welcome code HR hands an employee so they can set their own password (contract §4, Amendment 3 F3/F6/F11/F12).
/// <list type="bullet">
///   <item>8 random digits from a CSPRNG; single use.</item>
///   <item>Stored ONLY as HMAC-SHA256(key, linkId ':' normalizedEmail ':' code). Binding the login's username means a
///     work-email change orphans any code printed for the old address even before it is cleared.</item>
///   <item>The key is HKDF-SHA256 over the JWT signing secret with info <c>kynexone.welcome-code.v1</c>. No new
///     environment variable: ROTATING THE JWT SIGNING SECRET INVALIDATES EVERY LIVE WELCOME CODE (HR reissues).</item>
///   <item>Expiry = min(max(issue + 7 days, joining date + 7 days), issue + 30 days).</item>
/// </list>
/// </summary>
public static class WelcomeCodes
{
    public const int Length = 8;
    public static readonly TimeSpan Validity = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaxValidity = TimeSpan.FromDays(30);

    // Per-link failure ladder (F6): 5 misses lock the code 15 minutes, 10 lock it an hour, 20 burn it.
    public const int FirstLockAt = 5;
    public const int SecondLockAt = 10;
    public const int BurnAt = 20;
    public static readonly TimeSpan FirstLock = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan SecondLock = TimeSpan.FromHours(1);

    // Tenant-wide failure budget (F6): past it, answer try_later without counting against any code.
    public const int TenantFailureBudget = 200;
    public static readonly TimeSpan TenantBudgetWindow = TimeSpan.FromMinutes(15);

    private const string HkdfInfo = "kynexone.welcome-code.v1";

    /// <summary>A fresh 8-digit code (leading zeros allowed).</summary>
    public static string Generate() =>
        RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// What the person typed, reduced to ASCII digits: Arabic-Indic (٠-٩) and Persian (۰-۹) digits map to 0-9; spaces,
    /// dashes and other separators are ignored. Returns null unless exactly <see cref="Length"/> digits remain.
    /// </summary>
    public static string? NormalizeInput(string? input)
    {
        if (string.IsNullOrEmpty(input)) return null;
        var sb = new StringBuilder(Length);
        foreach (var ch in NormalizeDigits(input))
        {
            if (ch is >= '0' and <= '9') sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch is '-' or '‐' or '‑' or '‒' or '–' or '—' or '−' or '.' or '_') continue;
            else return null;
            if (sb.Length > Length) return null;
        }
        return sb.Length == Length ? sb.ToString() : null;
    }

    /// <summary>Arabic-Indic (U+0660..0669) and Persian (U+06F0..06F9) digits → ASCII; everything else unchanged.</summary>
    public static string NormalizeDigits(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var changed = false;
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c is >= '٠' and <= '٩') { chars[i] = (char)('0' + (c - '٠')); changed = true; }
            else if (c is >= '۰' and <= '۹') { chars[i] = (char)('0' + (c - '۰')); changed = true; }
        }
        return changed ? new string(chars) : value;
    }

    /// <summary>HKDF-SHA256 key derived from the existing server secret.</summary>
    public static byte[] DeriveKey(string serverSecret)
    {
        if (string.IsNullOrEmpty(serverSecret)) throw new InvalidOperationException("No server secret is configured for welcome codes.");
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(serverSecret), 32,
            salt: Array.Empty<byte>(), info: Encoding.UTF8.GetBytes(HkdfInfo));
    }

    /// <summary>HMAC-SHA256(key, linkId ':' normalizedEmail ':' code), hex.</summary>
    public static string Hash(byte[] key, Guid linkId, string normalizedEmail, string code) =>
        Convert.ToHexString(HMACSHA256.HashData(key,
            Encoding.UTF8.GetBytes($"{linkId:D}:{normalizedEmail}:{code}")));

    public static bool Matches(byte[] key, EmployeeUserAccount link, string normalizedEmail, string code) =>
        link.WelcomeCodeHash is { Length: > 0 } stored
        && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(stored),
            Encoding.ASCII.GetBytes(Hash(key, link.Id, normalizedEmail, code)));

    /// <summary>Expiry rule (F11): min(max(issue + 7d, joining + 7d), issue + 30d). End of that day is not used — exact instants.</summary>
    public static DateTime ExpiresAt(DateTime issuedAtUtc, DateTime? joiningDate)
    {
        var byIssue = issuedAtUtc + Validity;
        var byJoining = joiningDate is { } j && j != default
            ? DateTime.SpecifyKind(j.Date, DateTimeKind.Utc) + Validity
            : byIssue;
        var longer = byJoining > byIssue ? byJoining : byIssue;
        var cap = issuedAtUtc + MaxValidity;
        return longer < cap ? longer : cap;
    }

    /// <summary>A code that can still be redeemed (not used, not expired, not burnt).</summary>
    public static bool IsLive(EmployeeUserAccount link, DateTime nowUtc) =>
        !string.IsNullOrEmpty(link.WelcomeCodeHash)
        && link.WelcomeCodeRedeemedAtUtc is null
        && link.WelcomeCodeExpiresAtUtc is { } exp && exp > nowUtc
        && link.WelcomeCodeFailedAttempts < BurnAt;

    /// <summary>When a code that has failed <paramref name="failures"/> times stays locked from <paramref name="lastFailureUtc"/>.</summary>
    public static DateTime? LockedUntil(int failures, DateTime? lastFailureUtc)
    {
        if (lastFailureUtc is not { } last) return null;
        if (failures >= SecondLockAt) return last + SecondLock;
        if (failures >= FirstLockAt) return last + FirstLock;
        return null;
    }

    /// <summary>A live invitation token on the link (the legacy credential path).</summary>
    public static bool HasLiveInvitation(EmployeeUserAccount link, DateTime nowUtc) =>
        !string.IsNullOrEmpty(link.InvitationTokenHash)
        && link.InvitationAcceptedAtUtc is null
        && link.InvitationExpiresAtUtc is { } exp && exp > nowUtc
        && link.AccessMode != AccessModes.NoLogin;
}
