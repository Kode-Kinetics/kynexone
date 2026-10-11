using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Http;
using Zayra.Api.Infrastructure.Scope;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Auth;

// ═════════════════════════════════════════════════════════════════════════════════════════════════
// ERP MACHINE ACCESS — tenant API keys, accepted on an explicit allow-list of endpoints ONLY.
//
// THREE INDEPENDENT LOCKS keep a key off every other endpoint, so no single mistake opens one:
//   1. ErpApiKeyRequestGuard.BeforeAuthentication — a request PRESENTING a key to an endpoint without
//      [ErpApiKeyAccess] is refused 401 before any handler runs (as is a request presenting a key AND
//      a bearer token, so two principals can never be merged).
//   2. ErpApiKeyAuthenticationHandler — the scheme returns NoResult unless the matched endpoint carries
//      [ErpApiKeyAccess]. Even `[Authorize(AuthenticationSchemes = "ErpApiKey")]` written by hand on some
//      other action authenticates nobody.
//   3. ErpApiKeyRequestGuard.AfterAuthorization — an API-key principal reaching an endpoint without the
//      attribute is refused before the action runs.
// The default policy and the FallbackPolicy name no scheme, so they resolve through the default (JWT)
// scheme only. ErpApiKeyAllowListRatchetTests pins the attribute to exactly the eight allow-listed actions.
// ═════════════════════════════════════════════════════════════════════════════════════════════════

public static class ErpApiKeyDefaults
{
    public const string Scheme = "ErpApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string AuthorizationScheme = "ApiKey";
    public const string KeyPrefix = "kx_live_";
    /// <summary>Characters of the key shown to people: "kx_live_" + 8 random characters.</summary>
    public const int DisplayPrefixLength = 16;
    public const int MaxLifetimeDays = 365;
    public const int MaxActiveKeysPerTenant = 25;
    public const int MaxIpAllowListEntries = 20;
    public const string RateLimitPolicy = "erp_api_key";
    public const string OpenApiDocument = "erp";

    public const string AuthKindClaim = "auth_kind";
    public const string AuthKindValue = "erp_api_key";
    public const string KeyIdClaim = "erp_api_key_id";
    public const string KeyCreatorClaim = "erp_api_key_created_by";
    public const string ScopeClaim = "erp_api_key_scope";

    internal const string OutcomeItem = "__kynex.erp_api_key.outcome";
    public static readonly TimeSpan LastUsedThrottle = TimeSpan.FromMinutes(1);
}

/// <summary>The fixed scope set, and the permission each one maps to on the journal-export controller.</summary>
public static class ErpApiKeyScopes
{
    public const string ExportsRead = "finance.gl.exports.read";
    public const string ExportsConfirm = "finance.gl.exports.confirm";
    public static readonly IReadOnlyList<string> All = new[] { ExportsRead, ExportsConfirm };

    /// <summary>
    /// The ONLY permissions an API-key principal ever holds. They are what GlJournalExportsController checks
    /// (CanRead / CanConfirm), so a key is authorised by the same code path as a person, and nothing else.
    /// </summary>
    public static IReadOnlyList<string> PermissionsFor(string scope) => scope switch
    {
        ExportsRead => new[] { "finance.gl.read" },
        ExportsConfirm => new[] { "finance.erp.confirm" },
        _ => Array.Empty<string>(),
    };

    public static IReadOnlyList<string> Parse(string? stored) =>
        (stored ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => All.Contains(s, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>
/// OPT-IN marker: the only way an endpoint accepts an ERP API key. Adds the ErpApiKey scheme next to JWT
/// for this action and requires the key to hold <see cref="RequiredScope"/> (a person's JWT is unaffected).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ErpApiKeyAccessAttribute : Attribute, IAuthorizeData, IAuthorizationRequirementData
{
    public ErpApiKeyAccessAttribute(string requiredScope)
    {
        if (!ErpApiKeyScopes.All.Contains(requiredScope, StringComparer.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(requiredScope), requiredScope, "Unknown ERP API key scope.");
        RequiredScope = requiredScope;
    }

    public string RequiredScope { get; }

    // IAuthorizeData: no policy and no roles (so the default authenticated-user policy applies), plus the
    // two schemes. Setters exist only because the interface declares them; they are ignored.
    public string? Policy { get => null; set { } }
    public string? Roles { get => null; set { } }
    public string? AuthenticationSchemes
    {
        get => $"{JwtBearerDefaults.AuthenticationScheme},{ErpApiKeyDefaults.Scheme}";
        set { }
    }

    public IEnumerable<IAuthorizationRequirement> GetRequirements() => new[] { new ErpApiKeyScopeRequirement(RequiredScope) };
}

public sealed record ErpApiKeyScopeRequirement(string Scope) : IAuthorizationRequirement;

/// <summary>People pass (their permissions are checked by the action as before); a key needs the scope.</summary>
public sealed class ErpApiKeyScopeAuthorizationHandler : AuthorizationHandler<ErpApiKeyScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ErpApiKeyScopeRequirement requirement)
    {
        if (!ErpApiKeyPrincipal.IsApiKey(context.User)
            || context.User.HasClaim(ErpApiKeyDefaults.ScopeClaim, requirement.Scope))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public static class ErpApiKeyPrincipal
{
    public static bool IsApiKey(ClaimsPrincipal? user) =>
        user?.Identities.Any(i => i.IsAuthenticated
            && (string.Equals(i.AuthenticationType, ErpApiKeyDefaults.Scheme, StringComparison.Ordinal)
                || i.HasClaim(ErpApiKeyDefaults.AuthKindClaim, ErpApiKeyDefaults.AuthKindValue))) == true;

    /// <summary>The person who created the key the caller is using; null for a person's own session.</summary>
    public static Guid? CreatorOf(ClaimsPrincipal? user) =>
        IsApiKey(user) && Guid.TryParse(user!.FindFirstValue(ErpApiKeyDefaults.KeyCreatorClaim), out var id) ? id : null;

    public static ClaimsPrincipal Build(ErpApiKey key)
    {
        var scopes = ErpApiKeyScopes.Parse(key.Scopes);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, key.Id.ToString()),
            new(ClaimTypes.Name, $"ERP API key: {key.Name} ({key.Prefix}…)"),
            new("tenant_id", key.TenantId.ToString()),
            new(ErpApiKeyDefaults.AuthKindClaim, ErpApiKeyDefaults.AuthKindValue),
            new(ErpApiKeyDefaults.KeyIdClaim, key.Id.ToString()),
            new(ErpApiKeyDefaults.KeyCreatorClaim, key.CreatedByUserId.ToString()),
            // Always strict: a key's scope comes only from its own claim, never from legacy inference.
            new(EntityScopeContext.StrictScopeClaim, "true"),
        };
        claims.AddRange(EntityScopeClaims.Build(
            key.CompanyId is Guid companyId ? EntityScopeDescriptor.Companies(new[] { companyId }) : EntityScopeDescriptor.Group,
            Array.Empty<EntityAccessGrant>()));
        foreach (var scope in scopes)
        {
            claims.Add(new Claim(ErpApiKeyDefaults.ScopeClaim, scope));
            claims.AddRange(ErpApiKeyScopes.PermissionsFor(scope).Select(p => new Claim("permission", p)));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, ErpApiKeyDefaults.Scheme));
    }
}

/// <summary>Key generation and the shapes a presented credential may take.</summary>
public static class ErpApiKeyCodec
{
    /// <summary><c>kx_live_</c> + 32 random bytes, base64url (43 characters).</summary>
    public static string Generate() => ErpApiKeyDefaults.KeyPrefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string DisplayPrefixOf(string key) => key[..Math.Min(key.Length, ErpApiKeyDefaults.DisplayPrefixLength)];

    public static bool LooksLikeKey(string? candidate) =>
        candidate is { Length: >= 40 and <= 128 }
        && candidate.StartsWith(ErpApiKeyDefaults.KeyPrefix, StringComparison.Ordinal)
        && candidate.AsSpan(ErpApiKeyDefaults.KeyPrefix.Length).IndexOfAnyExcept(Base64UrlAlphabet) < 0;

    private static readonly System.Buffers.SearchValues<char> Base64UrlAlphabet =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    /// <summary>
    /// Reads <c>Authorization: ApiKey kx_live_…</c> or <c>X-Api-Key: kx_live_…</c>. <paramref name="ambiguous"/>
    /// is set when the request ALSO carries a bearer token or two different keys — refused outright, so a key
    /// and a person's session are never merged into one principal.
    /// </summary>
    public static bool TryRead(HttpRequest request, out string presented, out bool ambiguous)
    {
        presented = string.Empty;
        ambiguous = false;
        string? fromAuthorization = null;
        var bearer = false;
        foreach (var value in request.Headers.Authorization)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var trimmed = value.Trim();
            if (trimmed.StartsWith(ErpApiKeyDefaults.AuthorizationScheme + " ", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = trimmed[(ErpApiKeyDefaults.AuthorizationScheme.Length + 1)..].Trim();
                if (fromAuthorization is not null && fromAuthorization != candidate) ambiguous = true;
                fromAuthorization = candidate;
            }
            else bearer = true;
        }
        var headerValues = request.Headers[ErpApiKeyDefaults.HeaderName];
        string? fromHeader = null;
        foreach (var value in headerValues)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (fromHeader is not null && fromHeader != value.Trim()) ambiguous = true;
            fromHeader = value.Trim();
        }

        if (fromAuthorization is null && fromHeader is null) return false;
        if (fromAuthorization is not null && fromHeader is not null && fromAuthorization != fromHeader) ambiguous = true;
        if (bearer) ambiguous = true;
        presented = fromAuthorization ?? fromHeader!;
        return true;
    }
}

/// <summary>CIDR allow-list: parse on create, check on every call.</summary>
public static class ErpApiKeyIpAllowList
{
    public static bool TryNormalize(IEnumerable<string>? entries, out List<string> normalized, out string? error)
    {
        normalized = new List<string>();
        error = null;
        foreach (var raw in entries ?? Array.Empty<string>())
        {
            var entry = (raw ?? string.Empty).Trim();
            if (entry.Length == 0) continue;
            if (!TryParseEntry(entry, out var network))
            {
                error = $"'{entry}' is not an IP address or CIDR range (for example 203.0.113.10 or 203.0.113.0/24).";
                return false;
            }
            var text = network.ToString();
            if (!normalized.Contains(text, StringComparer.Ordinal)) normalized.Add(text);
        }
        if (normalized.Count > ErpApiKeyDefaults.MaxIpAllowListEntries)
        {
            error = $"An allow-list holds at most {ErpApiKeyDefaults.MaxIpAllowListEntries} entries.";
            return false;
        }
        return true;
    }

    public static IReadOnlyList<string> Split(string? stored) =>
        (stored ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>An empty list allows any address; otherwise the caller's address must fall in one entry.</summary>
    public static bool Allows(string? stored, string? clientIp)
    {
        var entries = Split(stored);
        if (entries.Count == 0) return true;
        if (!IPAddress.TryParse(clientIp, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        foreach (var entry in entries)
            if (TryParseEntry(entry, out var network) && network.Contains(address)) return true;
        return false;
    }

    private static bool TryParseEntry(string entry, out System.Net.IPNetwork network)
    {
        if (System.Net.IPNetwork.TryParse(entry, out network)) return true;
        if (!entry.Contains('/') && IPAddress.TryParse(entry, out var single))
        {
            if (single.IsIPv4MappedToIPv6) single = single.MapToIPv4();
            network = new System.Net.IPNetwork(single, single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }
        network = default;
        return false;
    }
}

/// <summary>Outcome of checking one presented key. <see cref="Key"/> is set whenever the key was recognised.</summary>
public sealed record ErpApiKeyValidation(bool Succeeded, ErpApiKey? Key, string Reason);

/// <summary>
/// Resolves a presented key. Runs in its OWN DI scope under an explicit system scope: the request has no
/// principal yet, and resolving the request's entity scope now would cache an unauthenticated answer for
/// the rest of the request.
/// </summary>
public sealed class ErpApiKeyValidator
{
    private readonly IServiceScopeFactory _scopes;
    public ErpApiKeyValidator(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<ErpApiKeyValidation> ValidateAsync(string presented, string? clientIp, DateTime nowUtc, CancellationToken ct)
    {
        if (!ErpApiKeyCodec.LooksLikeKey(presented)) return new(false, null, "malformed");

        using var scope = _scopes.CreateScope();
        using var system = SystemScopeContext.Begin();
        var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
        var hash = scope.ServiceProvider.GetRequiredService<ITokenService>().HashToken(presented);

        var key = await ScopedBypass.SystemWide(db.ErpApiKeys, 1,
                "ERP API key authentication: the key hash is the credential and resolves which tenant owns it; " +
                "there is no principal yet. Every later read is pinned to the key's own TenantId.",
                k => k.KeyHash == hash, k => k.Id)
            .FirstOrDefaultAsync(ct);
        // Constant-time re-check of the stored hash (the index lookup is an equality, not a secret compare).
        if (key is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(key.KeyHash)))
            return new(false, null, "unknown");

        if (key.RevokedAtUtc is not null) return new(false, key, "revoked");
        if (key.ExpiresAtUtc <= nowUtc) return new(false, key, "expired");
        if (!ErpApiKeyIpAllowList.Allows(key.IpAllowList, clientIp)) return new(false, key, "ip_not_allowed");
        var tenantActive = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == key.TenantId).Select(t => (bool?)t.IsActive).FirstOrDefaultAsync(ct);
        if (tenantActive != true) return new(false, key, "tenant_inactive");
        // The key acts as the administrator who created it (audit, segregation of duties). When that
        // person is deactivated or removed, their keys stop with them — an integration must not outlive
        // the access of the human who answers for it. Re-issue under an active administrator.
        var creatorActive = await ScopedBypass.TenantWide(db.Users, key.TenantId,
                "ERP API key authentication: is the key's creating administrator still active in the key's own tenant.")
            .AsNoTracking()
            .Where(u => u.Id == key.CreatedByUserId)
            .Select(u => (bool?)(u.IsActive && !u.IsDeleted))
            .FirstOrDefaultAsync(ct);
        if (creatorActive != true) return new(false, key, "creator_inactive");

        // Throttled: at most one write per key per minute, or when the address changes.
        if (key.LastUsedAtUtc is null || nowUtc - key.LastUsedAtUtc.Value >= ErpApiKeyDefaults.LastUsedThrottle
            || !string.Equals(key.LastUsedIp, clientIp, StringComparison.Ordinal))
        {
            key.LastUsedAtUtc = nowUtc;
            key.LastUsedIp = clientIp is { Length: > 64 } ? clientIp[..64] : clientIp;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { /* bookkeeping only — never fail an authenticated call on it */ }
        }
        return new(true, key, "ok");
    }
}

public sealed class ErpApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly ErpApiKeyValidator _validator;
    private readonly IConfiguration _configuration;

    public ErpApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        ErpApiKeyValidator validator, IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _validator = validator;
        _configuration = configuration;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Lock 2: never authenticate on an endpoint that has not opted in.
        if (Context.GetEndpoint()?.Metadata.GetMetadata<ErpApiKeyAccessAttribute>() is null) return AuthenticateResult.NoResult();
        if (!ErpApiKeyCodec.TryRead(Request, out var presented, out var ambiguous)) return AuthenticateResult.NoResult();
        if (ambiguous) return AuthenticateResult.Fail("invalid_api_key");

        var ip = ClientIpResolver.Resolve(Context, _configuration[ClientIpResolver.SecretConfigKey]);
        var outcome = await _validator.ValidateAsync(presented, ip, DateTime.UtcNow, Context.RequestAborted);
        Context.Items[ErpApiKeyDefaults.OutcomeItem] = outcome;
        // One uniform failure: the caller never learns whether the key was unknown, expired, revoked or
        // presented from the wrong address. The reason goes to the audit row and the log only.
        if (!outcome.Succeeded || outcome.Key is null)
        {
            Logger.LogWarning("ERP API key refused ({Reason}) for key {KeyId} on {Path}",
                outcome.Reason, outcome.Key?.Id, Request.Path.Value);
            return AuthenticateResult.Fail("invalid_api_key");
        }
        var principal = ErpApiKeyPrincipal.Build(outcome.Key);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, ErpApiKeyDefaults.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append("WWW-Authenticate", ErpApiKeyDefaults.AuthorizationScheme);
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

/// <summary>Locks 1 and 3, plus the per-call audit row.</summary>
public static class ErpApiKeyRequestGuard
{
    public static async Task BeforeAuthentication(HttpContext context, RequestDelegate next)
    {
        if (!ErpApiKeyCodec.TryRead(context.Request, out _, out var ambiguous))
        {
            await next(context);
            return;
        }

        var optedIn = context.GetEndpoint()?.Metadata.GetMetadata<ErpApiKeyAccessAttribute>() is not null;
        if (!optedIn || ambiguous)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ErpApiKey")
                .LogWarning("ERP API key presented to {Path} refused: {Reason}", context.Request.Path.Value,
                    ambiguous ? "ambiguous_credentials" : "endpoint_not_allow_listed");
            await WriteUnauthorizedAsync(context);
            return;
        }

        await next(context);
        await AuditAsync(context);
    }

    public static Task AfterAuthorization(HttpContext context, RequestDelegate next)
    {
        if (ErpApiKeyPrincipal.IsApiKey(context.User)
            && context.GetEndpoint()?.Metadata.GetMetadata<ErpApiKeyAccessAttribute>() is null)
            return WriteUnauthorizedAsync(context);
        return next(context);
    }

    private static Task WriteUnauthorizedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.Append("WWW-Authenticate", ErpApiKeyDefaults.AuthorizationScheme);
        return Task.CompletedTask;
    }

    /// <summary>
    /// One audit row per call made with a recognised key — accepted or refused — carrying the key id, the
    /// endpoint, the HTTP status and the caller's address. A key nobody issued has no tenant to file it under,
    /// so it is logged (above) instead.
    /// </summary>
    private static async Task AuditAsync(HttpContext context)
    {
        if (context.Items[ErpApiKeyDefaults.OutcomeItem] is not ErpApiKeyValidation { Key: { } key } outcome) return;
        try
        {
            var config = context.RequestServices.GetRequiredService<IConfiguration>();
            using var scope = context.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
            using var system = SystemScopeContext.Begin();
            var db = scope.ServiceProvider.GetRequiredService<ZayraDbContext>();
            var endpoint = context.GetEndpoint();
            db.AuditLogs.Add(new AuditLog
            {
                TenantId = key.TenantId,
                CompanyId = key.CompanyId,
                UserId = key.Id,
                Action = outcome.Succeeded ? "finance.gl.erp_api_key.call" : "finance.gl.erp_api_key.refused",
                EntityName = nameof(ErpApiKey),
                EntityId = key.Id.ToString(),
                IpAddress = Truncate(ClientIpResolver.Resolve(context, config[ClientIpResolver.SecretConfigKey]), 64),
                UserAgent = Truncate(context.Request.Headers.UserAgent.ToString(), 512),
                Metadata = JsonSerializer.Serialize(new
                {
                    keyId = key.Id,
                    keyPrefix = key.Prefix,
                    method = context.Request.Method,
                    path = context.Request.Path.Value,
                    endpoint = endpoint?.DisplayName,
                    status = context.Response.StatusCode,
                    reason = outcome.Succeeded ? null : outcome.Reason,
                }),
                CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ErpApiKey")
                .LogError(ex, "ERP API key call audit could not be written for key {KeyId}", key.Id);
        }
    }

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } v && v.Length > max ? v[..max] : value;
}
