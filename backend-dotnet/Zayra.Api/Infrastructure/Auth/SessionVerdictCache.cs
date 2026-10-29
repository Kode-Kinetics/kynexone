using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// Short-lived cache of the per-request "is this access token still current" verdict (see
/// <see cref="CurrentSessionValidator"/>). Only POSITIVE verdicts are cached. A cached verdict is bound to
/// the user's, the tenant's and the global invalidation versions at the moment the verdict was
/// computed; every auth-relevant write bumps one of them (see <see cref="SessionVerdictInvalidationInterceptor"/>),
/// so the next request after the write misses and re-validates against the database.
/// Behind an interface so a distributed cache (shared versions) can replace the in-process one.
/// </summary>
public interface ISessionVerdictCache
{
    /// <summary>Snapshots the current versions into a key. Must be taken BEFORE the database is read.</summary>
    SessionVerdictKey KeyFor(Guid tenantId, Guid userId, string claimsFingerprint);
    bool IsKnownCurrent(SessionVerdictKey key);
    void RememberCurrent(SessionVerdictKey key);
    /// <summary>One user's sessions (user, role/permission/scope links, stamp, credentials).</summary>
    void InvalidateUser(Guid userId);
    /// <summary>Every session of a tenant (security settings, IdP, companies, tenant roles).</summary>
    void InvalidateTenant(Guid tenantId);
    /// <summary>Every session (system roles, permission catalogue).</summary>
    void InvalidateAll();
}

public readonly record struct SessionVerdictKey(string Value);

public sealed class SessionVerdictCacheOptions
{
    public const string SectionName = "Auth:SessionVerdictCache";
    public const int MaxTtlSeconds = 30;
    public const int DefaultTtlSeconds = 15;
    /// <summary>Seconds a positive verdict is reused. 0 disables the cache. Clamped to 0..30.</summary>
    public int TtlSeconds { get; set; } = DefaultTtlSeconds;
    /// <summary>Hard cap on cached verdicts (each entry has size 1). Beyond it the cache sheds entries instead of growing.</summary>
    public int MaxEntries { get; set; } = 50_000;
    /// <summary>Cap on the per-user / per-tenant version maps. Reaching it bumps the global version and clears them (everything revalidates once).</summary>
    public int MaxVersionEntries { get; set; } = 100_000;
    public TimeSpan EffectiveTtl => TimeSpan.FromSeconds(Math.Clamp(TtlSeconds, 0, MaxTtlSeconds));
}

public sealed class MemorySessionVerdictCache : ISessionVerdictCache
{
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<Guid, long> _users = new();
    private readonly ConcurrentDictionary<Guid, long> _tenants = new();
    private readonly int _maxVersionEntries;
    private readonly object _pruneLock = new();
    private long _global;

    /// <summary>
    /// <paramref name="cache"/> should be a DEDICATED MemoryCache with a SizeLimit (see <see cref="Create"/>): every entry
    /// here has size 1, so a cache without a limit simply ignores the size.
    /// </summary>
    public MemorySessionVerdictCache(IMemoryCache cache, IOptions<SessionVerdictCacheOptions> options)
    {
        _cache = cache;
        _ttl = options.Value.EffectiveTtl;
        _maxVersionEntries = Math.Max(1, options.Value.MaxVersionEntries);
    }

    /// <summary>The production composition: a private, size-limited MemoryCache (not the shared application cache).</summary>
    public static MemorySessionVerdictCache Create(IOptions<SessionVerdictCacheOptions> options) =>
        new(new MemoryCache(new MemoryCacheOptions { SizeLimit = Math.Max(1, options.Value.MaxEntries) }), options);

    public SessionVerdictKey KeyFor(Guid tenantId, Guid userId, string claimsFingerprint)
    {
        _users.TryGetValue(userId, out var u);
        _tenants.TryGetValue(tenantId, out var t);
        return new SessionVerdictKey(
            $"sess-ok:{Interlocked.Read(ref _global)}:{t}:{u}:{tenantId:N}:{userId:N}:{claimsFingerprint}");
    }

    public bool IsKnownCurrent(SessionVerdictKey key) => _ttl > TimeSpan.Zero && _cache.TryGetValue(key.Value, out _);

    public void RememberCurrent(SessionVerdictKey key)
    {
        if (_ttl <= TimeSpan.Zero) return;
        _cache.Set(key.Value, true, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = _ttl, Size = 1 });
    }

    public void InvalidateUser(Guid userId)
    {
        _users.AddOrUpdate(userId, 1, (_, v) => v + 1);
        PruneIfLarge();
    }

    public void InvalidateTenant(Guid tenantId)
    {
        _tenants.AddOrUpdate(tenantId, 1, (_, v) => v + 1);
        PruneIfLarge();
    }

    public void InvalidateAll() => Interlocked.Increment(ref _global);

    /// <summary>
    /// Version maps only ever grow with distinct written ids. Dropping an entry on its own would reset a version to 0 and
    /// could resurrect an old key, so the prune first bumps the global version (every existing key is dead) and only then clears.
    /// </summary>
    private void PruneIfLarge()
    {
        if (_users.Count <= _maxVersionEntries && _tenants.Count <= _maxVersionEntries) return;
        lock (_pruneLock)
        {
            if (_users.Count <= _maxVersionEntries && _tenants.Count <= _maxVersionEntries) return;
            Interlocked.Increment(ref _global);
            _users.Clear();
            _tenants.Clear();
        }
    }

    public int VersionEntryCount => _users.Count + _tenants.Count;
}

/// <summary>
/// The JwtBearer <c>OnTokenValidated</c> check, with the verdict cache in front. A miss runs exactly the
/// uncached checks (<see cref="PlatformSessionSecurity.IsCurrentAsync"/> then <see cref="TenantSessionSecurity.IsCurrentAsync"/>).
/// </summary>
public static class CurrentSessionValidator
{
    public static async Task<(bool Current, string? FailedCheck)> ValidateAsync(
        ClaimsPrincipal principal, Zayra.Api.Data.ZayraDbContext db, ISessionVerdictCache cache,
        CancellationToken ct, bool mfaDisabledForTesting = false)
    {
        // Impersonation tokens are always rejected by the tenant check; never cache around that.
        var cacheable = !principal.HasClaim(c => c.Type == "impersonated_by");
        Guid.TryParse(principal.FindFirstValue("tenant_id"), out var tenantId);
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub"), out var userId);
        cacheable &= userId != Guid.Empty;

        SessionVerdictKey key = default;
        if (cacheable)
        {
            // Versions are snapshotted before the read: a write that commits mid-read bumps them, so
            // this verdict is stored under a stale key and can never be served.
            key = cache.KeyFor(tenantId, userId, Fingerprint(principal));
            if (cache.IsKnownCurrent(key)) return (true, null);
        }

        if (!await PlatformSessionSecurity.IsCurrentAsync(principal, db, ct))
            return (false, "Platform session is revoked or its authorization is stale.");
        if (!await TenantSessionSecurity.IsCurrentAsync(principal, db, ct, mfaDisabledForTesting))
            return (false, "Tenant session is revoked or its authorization is stale.");
        if (cacheable) cache.RememberCurrent(key);
        return (true, null);
    }

    /// <summary>SHA-256 over every claim (type, value), so any claim change (stamp, roles, permissions, scope, exp) is a different key.</summary>
    public static string Fingerprint(ClaimsPrincipal principal)
    {
        var sb = new StringBuilder();
        foreach (var c in principal.Claims.OrderBy(c => c.Type, StringComparer.Ordinal).ThenBy(c => c.Value, StringComparer.Ordinal))
            sb.Append(c.Type).Append('\u001f').Append(c.Value).Append('\u001e');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

/// <summary>
/// The in-process cache evicts only on the instance that performed the write. With more than one API instance (or a
/// writer outside the API process) a revocation reaches the other instances only when the verdict's TTL lapses.
/// </summary>
public static class SessionVerdictCacheTopology
{
    /// <summary>A startup warning when the deployment looks multi-instance and the cache is on; otherwise null.</summary>
    public static string? WarningFor(Microsoft.Extensions.Configuration.IConfiguration configuration, SessionVerdictCacheOptions options)
    {
        if (options.EffectiveTtl <= TimeSpan.Zero) return null;
        var declared = int.TryParse(configuration[$"{SessionVerdictCacheOptions.SectionName}:InstanceCount"], out var n) ? n : 1;
        var concurrency = int.TryParse(configuration["WEB_CONCURRENCY"], out var w) ? w : 1;
        if (Math.Max(declared, concurrency) <= 1) return null;
        return $"Session verdict cache is per-instance but {Math.Max(declared, concurrency)} instances are configured: a revocation " +
               $"(sign-out, role or permission change) can stay effective on the other instances for up to {options.EffectiveTtl.TotalSeconds:0} s. " +
               $"Set {SessionVerdictCacheOptions.SectionName}:TtlSeconds=0 to disable the cache (docs/runbooks/SESSION_VERDICT_CACHE.md).";
    }
}
