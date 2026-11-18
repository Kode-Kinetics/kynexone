using System.Collections.Concurrent;
using Zayra.Api.Infrastructure.Government;

namespace Zayra.Api.Infrastructure.Qiwa;

/// <summary>
/// Process-wide in-memory cache of Qiwa OAuth2 access tokens, keyed by tenant + body + environment + credential version
/// (<see cref="GovernmentTokenCacheKey"/>), so a sandbox token is never used against production and a rotated credential never
/// reuses a token minted under the old one (security R8; it used to be keyed by tenant only).
/// Tokens are refreshed by callers when <see cref="TryGet"/> reports expiry. Registered as a singleton.
/// </summary>
public sealed class QiwaOAuthTokenCache
{
    private sealed record Entry(string Token, DateTime ExpiresAtUtc);

    private readonly ConcurrentDictionary<GovernmentTokenCacheKey, Entry> _tokens = new();

    /// <summary>Skew applied before expiry so a token is refreshed slightly early.</summary>
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);

    /// <summary>Returns a non-expired token for exactly this key, or null if absent/expired.</summary>
    public string? TryGet(GovernmentTokenCacheKey key)
        => _tokens.TryGetValue(key, out var e) && DateTime.UtcNow < e.ExpiresAtUtc - Skew
            ? e.Token
            : null;

    public void Set(GovernmentTokenCacheKey key, string token, DateTime expiresAtUtc)
        => _tokens[key] = new Entry(token, expiresAtUtc);

    /// <summary>Default Qiwa access-token lifetime is 5 minutes; assume that when unknown.</summary>
    public void Set(GovernmentTokenCacheKey key, string token, int? expiresInSeconds)
        => Set(key, token, DateTime.UtcNow.AddSeconds(expiresInSeconds ?? 300));

    /// <summary>Drops every token of the tenant, whatever its environment or credential version.</summary>
    public void Invalidate(Guid tenantId)
    {
        foreach (var key in _tokens.Keys)
            if (key.TenantId == tenantId) _tokens.TryRemove(key, out _);
    }
}
