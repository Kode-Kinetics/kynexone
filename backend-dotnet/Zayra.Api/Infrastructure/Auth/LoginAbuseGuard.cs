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
    /// Decides whether a sign-in may proceed and, if so, records the attempt against both account
    /// budgets. A refused attempt is not recorded, so a refused caller cannot extend the window.
    /// </summary>
    public LoginRefusal? TryBegin(string scope, string tenant, string email, ClientAddress client, bool knownDevice, DateTime nowUtc)
    {
        if (client.IdentifiesOneClient
            && _windows.TryGetValue(IpKey(client.Ip), out SlidingWindow? failures)
            && failures!.Count(nowUtc) >= _ipFailureLimit)
            return LoginRefusal.IpFailureBudget;

        var account = AccountKey(scope, tenant, email);
        var perAddress = Window($"{account}|ip|{client.Ip}", _accountWindow);
        var overall = Window(account, _accountWindow);
        if (perAddress.Count(nowUtc) >= _accountIpLimit) return LoginRefusal.AccountLimit;
        if (!knownDevice && overall.Count(nowUtc) >= _accountLimit) return LoginRefusal.AccountLimit;

        perAddress.Add(nowUtc);
        overall.Add(nowUtc);
        return null;
    }

    /// <summary>Counts a failed sign-in against an account that does not exist, when the address is trustworthy.</summary>
    public void RecordUnknownAccountFailure(ClientAddress client, DateTime nowUtc)
    {
        if (client.IdentifiesOneClient) Window(IpKey(client.Ip), _ipWindow).Add(nowUtc);
    }

    // ── Known device ────────────────────────────────────────────────────────────────────────

    public string? IssueKnownDeviceToken(string scope, string tenant, string email, DateTime nowUtc)
        => _knownDevice?.Protect(AccountKey(scope, tenant, email), nowUtc + KnownDeviceLifetime);

    public bool IsKnownDevice(string? token, string scope, string tenant, string email)
    {
        if (_knownDevice is null || string.IsNullOrEmpty(token)) return false;
        try
        {
            return string.Equals(_knownDevice.Unprotect(token), AccountKey(scope, tenant, email), StringComparison.Ordinal);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false; // tampered, expired, or from another key ring
        }
    }

    /// <summary>
    /// Sets the known-device cookie after a successful sign-in: HttpOnly, Secure, SameSite=Strict,
    /// scoped to the sign-in path, protected by the Data Protection key ring and bound to the account.
    /// </summary>
    public void AppendKnownDeviceCookie(HttpResponse response, string scope, string tenant, string email)
    {
        var token = IssueKnownDeviceToken(scope, tenant, email, DateTime.UtcNow);
        if (token is null) return;
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

    public bool RequestCarriesKnownDevice(HttpRequest request, string scope, string tenant, string email)
        => IsKnownDevice(request.Cookies[scope == "platform" ? PlatformKnownDeviceCookie : TenantKnownDeviceCookie],
            scope, tenant, email);

    // ── Responses ───────────────────────────────────────────────────────────────────────────

    /// <summary>A random 2–6 s Retry-After, so refused clients do not come back in lock-step.</summary>
    public static string JitteredRetryAfterSeconds()
        => System.Security.Cryptography.RandomNumberGenerator.GetInt32(2, 7).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Stable error codes clients branch on (the sign-in page words each differently).</summary>
    public static (string Error, string Message) Describe(LoginRefusal refusal) => refusal switch
    {
        LoginRefusal.AccountLimit => ("account_rate_limited",
            "Too many sign-in attempts for this account. Please wait a few minutes and try again."),
        _ => ("ip_failure_budget",
            "Too many failed sign-ins from your network. Please wait a few minutes and try again."),
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

        private void Trim(DateTime now)
        {
            while (_hits.Count > 0 && now - _hits.Peek() >= window) _hits.Dequeue();
        }
    }
}
