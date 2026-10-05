using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Infrastructure.Http;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// Cheap refusals that run BEFORE any password hashing, so a burst against one account, or a spray
/// from one address, costs the server a dictionary lookup instead of 600k PBKDF2 iterations:
/// <list type="bullet">
/// <item><b>Per account</b> — (scope, tenant, normalised email): at most 10 attempts per 15 minutes,
/// counted whether or not they succeed.</item>
/// <item><b>Per client IP</b> — at most 20 FAILED sign-ins per 10 minutes; past that the address is
/// refused until the window drains.</item>
/// </list>
/// The client IP is <see cref="ClientIpResolver"/>'s, so it honours the proxy-asserted address when
/// that is configured. State is in-process (one Render instance today) and bounded: the cache holds
/// at most <c>Auth:LoginThrottle:MaxTrackedKeys</c> keys, and an untracked key is simply not
/// throttled — the IP budget and the hashing gate still apply. Limits are configurable under
/// <c>Auth:LoginThrottle:*</c>.
/// </summary>
public sealed class LoginAbuseGuard : IDisposable
{
    private readonly MemoryCache _windows;
    private readonly int _accountLimit;
    private readonly TimeSpan _accountWindow;
    private readonly int _ipFailureLimit;
    private readonly TimeSpan _ipWindow;
    private readonly string? _proxySecret;

    public LoginAbuseGuard(
        int accountLimit = 10, TimeSpan? accountWindow = null,
        int ipFailureLimit = 20, TimeSpan? ipWindow = null,
        string? proxySecret = null, int maxTrackedKeys = 50_000)
    {
        _accountLimit = accountLimit;
        _accountWindow = accountWindow ?? TimeSpan.FromMinutes(15);
        _ipFailureLimit = ipFailureLimit;
        _ipWindow = ipWindow ?? TimeSpan.FromMinutes(10);
        _proxySecret = proxySecret;
        _windows = new MemoryCache(new MemoryCacheOptions { SizeLimit = maxTrackedKeys });
    }

    public static LoginAbuseGuard FromConfiguration(IConfiguration config) => new(
        Math.Clamp(config.GetValue("Auth:LoginThrottle:AccountAttempts", 10), 1, 1000),
        TimeSpan.FromMinutes(Math.Clamp(config.GetValue("Auth:LoginThrottle:AccountWindowMinutes", 15), 1, 1440)),
        Math.Clamp(config.GetValue("Auth:LoginThrottle:IpFailures", 20), 1, 10_000),
        TimeSpan.FromMinutes(Math.Clamp(config.GetValue("Auth:LoginThrottle:IpWindowMinutes", 10), 1, 1440)),
        config[ClientIpResolver.SecretConfigKey],
        Math.Clamp(config.GetValue("Auth:LoginThrottle:MaxTrackedKeys", 50_000), 1000, 1_000_000));

    public string ClientIp(HttpContext context) => ClientIpResolver.Resolve(context, _proxySecret);

    /// <summary>
    /// Records one sign-in attempt for the account and says whether it may proceed. A refused
    /// attempt is not recorded, so a locked-out caller cannot extend their own window.
    /// </summary>
    public bool TryBeginAccountAttempt(string scope, string tenant, string email, DateTime nowUtc)
    {
        var key = $"acct|{scope}|{tenant.Trim().ToLowerInvariant()}|{email.Trim().ToUpperInvariant()}";
        return Window(key, _accountWindow).TryAdd(nowUtc, _accountWindow, _accountLimit);
    }

    /// <summary>True when this address has used up its failure budget.</summary>
    public bool IsIpBlocked(string ip, DateTime nowUtc)
        => _windows.TryGetValue($"ipfail|{ip}", out SlidingWindow? window)
           && window!.Count(nowUtc, _ipWindow) >= _ipFailureLimit;

    public void RecordFailure(string ip, DateTime nowUtc)
        => Window($"ipfail|{ip}", _ipWindow).TryAdd(nowUtc, _ipWindow, int.MaxValue);

    private SlidingWindow Window(string key, TimeSpan window)
        => _windows.GetOrCreate(key, entry =>
        {
            entry.Size = 1;
            entry.SlidingExpiration = window;
            return new SlidingWindow();
        })!;

    /// <summary>A random 2–6 s Retry-After, so refused clients do not come back in lock-step.</summary>
    public static string JitteredRetryAfterSeconds()
        => System.Security.Cryptography.RandomNumberGenerator.GetInt32(2, 7).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public void Dispose() => _windows.Dispose();

    private sealed class SlidingWindow
    {
        private readonly Queue<DateTime> _hits = new();

        public bool TryAdd(DateTime now, TimeSpan window, int limit)
        {
            lock (_hits)
            {
                Trim(now, window);
                if (_hits.Count >= limit) return false;
                _hits.Enqueue(now);
                return true;
            }
        }

        public int Count(DateTime now, TimeSpan window)
        {
            lock (_hits)
            {
                Trim(now, window);
                return _hits.Count;
            }
        }

        private void Trim(DateTime now, TimeSpan window)
        {
            while (_hits.Count > 0 && now - _hits.Peek() >= window) _hits.Dequeue();
        }
    }
}
