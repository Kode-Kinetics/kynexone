using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Infrastructure.Http;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>Why a sign-in was refused before hashing; each maps to a distinct error code.</summary>
public enum LoginRefusal
{
    /// <summary>This account is over its attempt budget (from this address, or overall).</summary>
    AccountLimit,
    /// <summary>This client address has failed too many sign-ins against accounts that do not exist.</summary>
    IpFailureBudget,
}

/// <summary>
/// Cheap refusals that run BEFORE any password hashing, so a burst against one account, or a spray
/// from one address, costs a dictionary lookup instead of 600k PBKDF2 iterations.
/// <list type="bullet">
/// <item><b>Per account and address</b> — (scope, tenant, email, client IP): 10 attempts per 15 minutes.
/// The hard limit is keyed on the address so one person cannot lock a named victim out from
/// everywhere.</item>
/// <item><b>Per account, overall</b> — 50 attempts per 15 minutes across all addresses, a looser cap
/// against distributed guessing. A browser holding a valid <b>known-device</b> cookie for that account
/// (issued after a successful sign-in) is exempt from this cap, never from the per-address one.</item>
/// <item><b>Per client address</b> — 150 failures per 10 minutes, counting only attempts against
/// accounts that do not exist (wrong passwords on real accounts are the per-account limits' job).
/// Applied only when the address identifies one client: the proxy asserted it with the shared secret,
/// or the request did not come through the proxy. Behind the proxy without the secret every web user
/// shares one address, and a budget there would let one attacker lock everybody out.</item>
/// </list>
/// State is in-process (one Render instance today) and bounded by <c>Auth:LoginThrottle:MaxTrackedKeys</c>;
/// an untracked key is simply not throttled — the hashing gate still applies. Limits are configurable
/// under <c>Auth:LoginThrottle:*</c>.
/// </summary>
public sealed class LoginAbuseGuard : IDisposable
{
    public const string TenantKnownDeviceCookie = "kx_known_device";
    public const string PlatformKnownDeviceCookie = "kx_platform_known_device";
    public static readonly TimeSpan KnownDeviceLifetime = TimeSpan.FromDays(90);
    private const string KnownDevicePurpose = "KynexOne.Auth.KnownDevice.v1";

    private readonly MemoryCache _windows;
    private readonly int _accountIpLimit;
    private readonly int _accountLimit;
    private readonly TimeSpan _accountWindow;
    private readonly int _ipFailureLimit;
    private readonly TimeSpan _ipWindow;
    private readonly string? _proxySecret;
    private readonly ITimeLimitedDataProtector? _knownDevice;

    public LoginAbuseGuard(
        int accountIpLimit = 10, int accountLimit = 50, TimeSpan? accountWindow = null,
        int ipFailureLimit = 150, TimeSpan? ipWindow = null,
        string? proxySecret = null, int maxTrackedKeys = 50_000,
        IDataProtectionProvider? dataProtection = null)
    {
        _accountIpLimit = accountIpLimit;
        _accountLimit = accountLimit;
        _accountWindow = accountWindow ?? TimeSpan.FromMinutes(15);
        _ipFailureLimit = ipFailureLimit;
        _ipWindow = ipWindow ?? TimeSpan.FromMinutes(10);
        _proxySecret = proxySecret;
        _knownDevice = dataProtection?.CreateProtector(KnownDevicePurpose).ToTimeLimitedDataProtector();
        _windows = new MemoryCache(new MemoryCacheOptions { SizeLimit = maxTrackedKeys });
    }

    public static LoginAbuseGuard FromConfiguration(IConfiguration config, IDataProtectionProvider? dataProtection = null) => new(
        Math.Clamp(config.GetValue("Auth:LoginThrottle:AccountIpAttempts", 10), 1, 1000),
        Math.Clamp(config.GetValue("Auth:LoginThrottle:AccountAttempts", 50), 1, 10_000),
        TimeSpan.FromMinutes(Math.Clamp(config.GetValue("Auth:LoginThrottle:AccountWindowMinutes", 15), 1, 1440)),
        Math.Clamp(config.GetValue("Auth:LoginThrottle:IpFailures", 150), 1, 100_000),
        TimeSpan.FromMinutes(Math.Clamp(config.GetValue("Auth:LoginThrottle:IpWindowMinutes", 10), 1, 1440)),
        config[ClientIpResolver.SecretConfigKey],
        Math.Clamp(config.GetValue("Auth:LoginThrottle:MaxTrackedKeys", 50_000), 1000, 1_000_000),
        dataProtection);

    public bool ProxySecretConfigured => !string.IsNullOrEmpty(_proxySecret);

    public ClientAddress Client(HttpContext context) => ClientIpResolver.ResolveAddress(context, _proxySecret);

    /// <summary>
    /// First gate, before the account is even looked up: the address's unknown-account failure
    /// budget, then this account's attempts from this address. Records the per-address attempt when
    /// allowed; a refused attempt is not recorded, so a refused caller cannot extend the window.
    /// </summary>
    public LoginRefusal? TryBeginFromAddress(string scope, string tenant, string email, ClientAddress client, DateTime nowUtc)
    {
        if (client.IdentifiesOneClient
            && _windows.TryGetValue(IpKey(client.Ip), out SlidingWindow? failures)
            && failures!.Count(nowUtc) >= _ipFailureLimit)
            return LoginRefusal.IpFailureBudget;

        var perAddress = Window($"{AccountKey(scope, tenant, email)}|ip|{client.Ip}", _accountWindow);
        if (perAddress.Count(nowUtc) >= _accountIpLimit) return LoginRefusal.AccountLimit;
        perAddress.Add(nowUtc);
        return null;
    }

    /// <summary>
    /// Second gate, once the account (if any) is loaded and the known-device cookie validated against
    /// it: the account-wide cap, which a trusted known device skips. Still before any hashing.
    /// </summary>
    public LoginRefusal? TryBeginAccountWide(string scope, string tenant, string email, bool knownDevice, DateTime nowUtc)
    {
        var overall = Window(AccountKey(scope, tenant, email), _accountWindow);
        if (!knownDevice && overall.Count(nowUtc) >= _accountLimit) return LoginRefusal.AccountLimit;
        overall.Add(nowUtc);
        return null;
    }

    /// <summary>Both gates at once, for callers that have no account to load (and in tests).</summary>
    public LoginRefusal? TryBegin(string scope, string tenant, string email, ClientAddress client, bool knownDevice, DateTime nowUtc)
        => TryBeginFromAddress(scope, tenant, email, client, nowUtc)
           ?? TryBeginAccountWide(scope, tenant, email, knownDevice, nowUtc);

    /// <summary>Counts a failed sign-in against an account that does not exist, when the address is trustworthy.</summary>
    public void RecordUnknownAccountFailure(ClientAddress client, DateTime nowUtc)
    {
        if (client.IdentifiesOneClient) Window(IpKey(client.Ip), _ipWindow).Add(nowUtc);
    }

    // ── Known device ────────────────────────────────────────────────────────────────────────

    /// <summary>Wrong passwords from ONE known device before that device stops being trusted (15 min).</summary>
    public const int KnownDeviceFailureLimit = 5;

    /// <summary>
    /// Credential version bound into a known-device token: a short fingerprint of the current password
    /// hash and the MFA enrolment state. Changing the password, resetting or re-enrolling MFA, or
    /// re-creating the user (new id, also in the payload) invalidates every cookie issued before.
    /// </summary>
    public static string CredentialVersion(string passwordHash, bool mfaEnabled, DateTime? mfaConfiguredAtUtc)
    {
        var material = $"{passwordHash}|{(mfaEnabled ? 1 : 0)}|{mfaConfiguredAtUtc?.Ticks ?? 0}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)))[..16];
    }

    /// <param name="deviceId">The device's stable id: reuse the one from a still-valid cookie, or pass
    /// <see cref="Guid.NewGuid"/> for a browser seen for the first time.</param>
    public string? IssueKnownDeviceToken(string scope, string tenant, string email, Guid principalId, string credentialVersion,
        Guid deviceId, DateTime nowUtc)
        => _knownDevice?.Protect(Payload(scope, tenant, email, principalId, credentialVersion, deviceId), nowUtc + KnownDeviceLifetime);

    /// <summary>Validates a token for this account and credential version and returns its device id.</summary>
    public bool TryReadKnownDevice(string? token, string scope, string tenant, string email, Guid principalId,
        string credentialVersion, out Guid deviceId)
    {
        deviceId = Guid.Empty;
        if (_knownDevice is null || string.IsNullOrEmpty(token)) return false;
        string payload;
        try
        {
            payload = _knownDevice.Unprotect(token);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false; // tampered, expired, or from another key ring
        }
        var prefix = Payload(scope, tenant, email, principalId, credentialVersion, Guid.Empty)[..^32];
        if (!payload.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(payload[prefix.Length..], "N", out deviceId))
            return false;
        return true;
    }

    public bool IsKnownDevice(string? token, string scope, string tenant, string email, Guid principalId, string credentialVersion)
        => TryReadKnownDevice(token, scope, tenant, email, principalId, credentialVersion, out _);

    /// <summary>
    /// A known device is trusted (lockout bypass, account-wide cap) only while IT has fewer than
    /// <see cref="KnownDeviceFailureLimit"/> recent wrong passwords. Keyed per device, so a stolen
    /// cookie that keeps guessing loses its own trust without touching the owner's other devices.
    /// </summary>
    public bool KnownDeviceStillTrusted(string scope, string tenant, string email, Guid deviceId, DateTime nowUtc)
        => !_windows.TryGetValue(DeviceFailureKey(scope, tenant, email, deviceId), out SlidingWindow? failures)
           || failures!.Count(nowUtc) < KnownDeviceFailureLimit;

    /// <summary>
    /// A wrong password from a known device: counted against that device, never toward the account
    /// lockout. Returns true exactly when this miss reaches the limit (the moment to warn the owner).
    /// </summary>
    public bool RecordKnownDeviceFailure(string scope, string tenant, string email, Guid deviceId, DateTime nowUtc)
    {
        var window = Window(DeviceFailureKey(scope, tenant, email, deviceId), _accountWindow);
        window.Add(nowUtc);
        return window.Count(nowUtc) == KnownDeviceFailureLimit;
    }

    private static string DeviceFailureKey(string scope, string tenant, string email, Guid deviceId)
        => $"{AccountKey(scope, tenant, email)}|kdfail|{deviceId:N}";

    /// <summary>The token's raw cookie value for <paramref name="scope"/>, if the request carries one.</summary>
    public static string? KnownDeviceCookie(HttpRequest request, string scope)
        => request.Cookies[scope == "platform" ? PlatformKnownDeviceCookie : TenantKnownDeviceCookie];

    /// <summary>
    /// Sets the known-device cookie after a successful sign-in: HttpOnly, Secure, SameSite=Strict,
    /// scoped to the sign-in path, protected by the Data Protection key ring and bound to the account,
    /// the principal id and the credential version.
    /// </summary>
    public static void AppendKnownDeviceCookie(HttpResponse response, string scope, string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        var platform = scope == "platform";
        response.Cookies.Append(platform ? PlatformKnownDeviceCookie : TenantKnownDeviceCookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = platform ? "/api/platform/auth" : "/api/auth",
            MaxAge = KnownDeviceLifetime,
            IsEssential = true,
        });
    }

    private static string Payload(string scope, string tenant, string email, Guid principalId, string credentialVersion, Guid deviceId)
        => $"{AccountKey(scope, tenant, email)}|{principalId:N}|{credentialVersion}|{deviceId:N}";

    /// <summary>
    /// True the first time <paramref name="key"/> is seen (until it ages out of the bounded cache):
    /// lets callers send at most one notice per event, e.g. one "locked out" email per lockout.
    /// </summary>
    public bool FirstNotice(string key, TimeSpan remember)
    {
        lock (_windows)
        {
            if (_windows.TryGetValue("notice|" + key, out _)) return false;
            _windows.Set("notice|" + key, true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = remember });
            return true;
        }
    }

    // ── Responses ───────────────────────────────────────────────────────────────────────────

    /// <summary>A random 2–6 s Retry-After, so refused clients do not come back in lock-step.</summary>
    public static string JitteredRetryAfterSeconds()
        => System.Security.Cryptography.RandomNumberGenerator.GetInt32(2, 7).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The wait, in words, for a Retry-After of <paramref name="seconds"/>: the server's messages and
    /// the sign-in pages use the same thresholds so the two never disagree.
    /// </summary>
    public static string WaitPhrase(int seconds) => seconds switch
    {
        <= 10 => "in a few seconds",
        <= 90 => "in about a minute",
        <= 300 => "in a few minutes",
        _ => $"in about {(int)Math.Ceiling(seconds / 60.0)} minutes",
    };

    /// <summary>
    /// Seconds until the window that refused this caller lets one more attempt through — the honest
    /// Retry-After for an account or address refusal (never less than 1).
    /// </summary>
    public int RetryAfterSeconds(LoginRefusal refusal, string scope, string tenant, string email, ClientAddress? client, DateTime nowUtc)
    {
        var account = AccountKey(scope, tenant, email);
        var waits = refusal == LoginRefusal.IpFailureBudget
            ? new[] { Wait(client is { } c ? IpKey(c.Ip) : null, _ipFailureLimit, nowUtc) }
            : new[]
            {
                Wait(client is { } c2 ? $"{account}|ip|{c2.Ip}" : null, _accountIpLimit, nowUtc),
                Wait(account, _accountLimit, nowUtc),
            };
        return Math.Max(1, (int)Math.Ceiling(waits.Max().TotalSeconds));
    }

    private TimeSpan Wait(string? key, int limit, DateTime nowUtc)
        => key is not null && _windows.TryGetValue(key, out SlidingWindow? w) ? w!.TimeUntilBelow(limit, nowUtc) : TimeSpan.Zero;

    /// <summary>Stable error codes clients branch on, and a message whose wait matches Retry-After.</summary>
    public static (string Error, string Message) Describe(LoginRefusal refusal, int retryAfterSeconds) => refusal switch
    {
        LoginRefusal.AccountLimit => ("account_rate_limited",
            $"Too many sign-in attempts for this account. Please try again {WaitPhrase(retryAfterSeconds)}."),
        _ => ("ip_failure_budget",
            $"Too many failed sign-ins from your network. Please try again {WaitPhrase(retryAfterSeconds)}."),
    };

    public const string BusyError = "sign_in_busy";

    private static string AccountKey(string scope, string tenant, string email)
        => $"acct|{scope}|{tenant.Trim().ToLowerInvariant()}|{email.Trim().ToUpperInvariant()}";

    private static string IpKey(string ip) => $"ipfail|{ip}";

    private SlidingWindow Window(string key, TimeSpan window)
        => _windows.GetOrCreate(key, entry =>
        {
            entry.Size = 1;
            entry.SlidingExpiration = window;
            return new SlidingWindow(window);
        })!;

    public void Dispose() => _windows.Dispose();

    private sealed class SlidingWindow(TimeSpan window)
    {
        private readonly Queue<DateTime> _hits = new();

        public void Add(DateTime now)
        {
            lock (_hits)
            {
                Trim(now);
                _hits.Enqueue(now);
            }
        }

        public int Count(DateTime now)
        {
            lock (_hits)
            {
                Trim(now);
                return _hits.Count;
            }
        }

        /// <summary>How long until fewer than <paramref name="limit"/> hits remain in the window.</summary>
        public TimeSpan TimeUntilBelow(int limit, DateTime now)
        {
            lock (_hits)
            {
                Trim(now);
                if (_hits.Count < limit) return TimeSpan.Zero;
                var freeing = _hits.ElementAt(_hits.Count - limit); // the hit whose expiry gets us under
                var wait = freeing + window - now;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
        }

        private void Trim(DateTime now)
        {
            while (_hits.Count > 0 && now - _hits.Peek() >= window) _hits.Dequeue();
        }
    }
}
