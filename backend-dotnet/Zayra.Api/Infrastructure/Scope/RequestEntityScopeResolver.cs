using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Scope;

/// <summary>
/// The ONE place a request's entity scope is decided. See <see cref="RequestEntityScope"/> for why
/// having had three was a security defect rather than a tidiness problem.
/// </summary>
public interface IRequestEntityScopeResolver
{
    /// <summary>
    /// Resolves the current request's scope. Cached per request, so a controller, an authorization
    /// helper and <c>ZayraDbContext</c> asking within one request always receive the SAME decision.
    /// </summary>
    RequestEntityScope Resolve();

    /// <summary>
    /// Resolves an explicit principal, ignoring any ambient request. For token issuance, tests, and
    /// background work acting deliberately as a given user.
    /// </summary>
    RequestEntityScope ResolveFor(ClaimsPrincipal? user, string? selectedCompanyHeader);
}

/// <inheritdoc />
public sealed class RequestEntityScopeResolver : IRequestEntityScopeResolver
{
    /// <summary>Per-request cache key. One resolution per request, whoever asks first.</summary>
    private const string CacheKey = "__zayra.request_entity_scope";

    private readonly IHttpContextAccessor? _http;
    private readonly IOptions<EntityScopeOptions>? _scopeOptions;
    private readonly IOptions<JwtOptions>? _jwtOptions;

    public RequestEntityScopeResolver(
        IHttpContextAccessor? http = null,
        IOptions<EntityScopeOptions>? scopeOptions = null,
        IOptions<JwtOptions>? jwtOptions = null)
    {
        _http = http;
        _scopeOptions = scopeOptions;
        _jwtOptions = jwtOptions;
    }

    public RequestEntityScope Resolve()
    {
        var ctx = _http?.HttpContext;

        // No HttpContext at all: seeding, migrations, hosted services at startup, unit tests. There is
        // no principal to scope to, and refusing here would break the application's own bootstrap.
        if (ctx is null)
            return RequestEntityScope.System(
                SystemScopeContext.IsActive
                    ? ScopeResolutionSource.ExplicitSystemScope
                    : ScopeResolutionSource.NoHttpContext);

        // A request already resolved this. Returning the cached record is what guarantees invariant 17
        // (controller and DbContext agree) and invariant 18 (a pooled DbContext cannot answer with a
        // previous request's scope — the cache lives on HttpContext, not on the resolver or the context).
        if (ctx.Items.TryGetValue(CacheKey, out var cached) && cached is RequestEntityScope hit)
            return hit;

        var header = ctx.Request.Headers[ZayraDbContext.CompanySelectionHeader].FirstOrDefault();
        var resolved = ResolveFor(ctx.User, header);
        ctx.Items[CacheKey] = resolved;
        return resolved;
    }

    public RequestEntityScope ResolveFor(ClaimsPrincipal? user, string? selectedCompanyHeader)
    {
        // Explicit system scope wins over everything. Invariant 16: background work must ASK for this,
        // never inherit it by accident.
        if (SystemScopeContext.IsActive)
            return RequestEntityScope.System(ScopeResolutionSource.ExplicitSystemScope);

        if (user?.Identity?.IsAuthenticated != true)
            return new RequestEntityScope(
                TenantId: null, IsGroupLevel: false, AuthorizedCompanyIds: Array.Empty<Guid>(),
                SelectedCompanyId: null, IsStrict: true, IsSystemScope: false,
                Source: ScopeResolutionSource.Unauthenticated,
                DenialReason: ScopeDenialReason.NoTenantOnPrincipal,
                LegacyCompatibilityUsed: false);

        var tenantId = ParseGuid(user.FindFirstValue("tenant_id"));

        // Platform administration is a DUAL gate: the claim alone is not enough, the token must also
        // carry the platform audience. A tenant-audience token that somehow acquired the claim is not
        // a platform administrator, which is what keeps impersonation tokens out of this branch.
        if (user.HasClaim("is_platform_admin", "true")
            && _jwtOptions?.Value.PlatformAudience is { Length: > 0 } platformAudience
            && user.HasClaim("aud", platformAudience))
            return RequestEntityScope.System(ScopeResolutionSource.PlatformAdministrator) with { TenantId = tenantId };

        // Strict is the global cutover flag OR the per-token marker stamped on impersonation and
        // break-glass tokens. Invariant 14: those are ALWAYS strict, whatever the global flag says.
        var strict = (_scopeOptions?.Value.StrictMode ?? false)
                     || user.HasClaim(EntityScopeContext.StrictScopeClaim, "true");

        // Authenticated but tenantless, and not a platform admin. Invariant 12: sees nothing. The old
        // behaviour — falling through to a filter that matched null/Guid.Empty tenants — was a leak.
        if (tenantId is null)
            return RequestEntityScope.Denied(null, strict, ScopeDenialReason.NoTenantOnPrincipal);

        var (isGroup, companies, source, denial, legacy) = ResolveClaims(user, strict);
        if (denial != ScopeDenialReason.None)
            return RequestEntityScope.Denied(tenantId, strict, denial);

        // ── The company switcher. It may ONLY narrow. ────────────────────────────────────────────
        Guid? selected = null;
        if (!string.IsNullOrWhiteSpace(selectedCompanyHeader))
        {
            if (!Guid.TryParse(selectedCompanyHeader, out var parsed))
                return RequestEntityScope.Denied(tenantId, strict, ScopeDenialReason.MalformedCompanyHeader);

            // Invariant 11: a header naming a company the token does not grant cannot widen anything.
            // Note this is checked against the TOKEN's scope, before narrowing — so a group user may
            // select any of their tenant's companies, and a selected-company user only their own.
            var permitted = isGroup || companies.Contains(parsed);
            if (!permitted)
                return RequestEntityScope.Denied(tenantId, strict, ScopeDenialReason.UnauthorizedCompanyHeader);

            selected = parsed;
            isGroup = false;                       // narrowed: no longer tenant-wide
            companies = new List<Guid> { parsed };
        }

        return new RequestEntityScope(
            TenantId: tenantId,
            IsGroupLevel: isGroup,
            AuthorizedCompanyIds: companies,
            SelectedCompanyId: selected,
            IsStrict: strict,
            IsSystemScope: false,
            Source: source,
            DenialReason: ScopeDenialReason.None,
            LegacyCompatibilityUsed: legacy);
    }

    /// <summary>
    /// Claim interpretation. Every failure path returns a denial reason rather than a permissive
    /// default — that asymmetry is the whole design.
    /// </summary>
    private static (bool IsGroup, List<Guid> Companies, ScopeResolutionSource Source, ScopeDenialReason Denial, bool Legacy)
        ResolveClaims(ClaimsPrincipal user, bool strict)
    {
        var v2 = user.FindFirst(EntityScopeContext.V2ClaimType)?.Value;
        if (v2 is not null)
        {
            EntityScopeClaimV2? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<EntityScopeClaimV2>(v2, JsonOptions);
            }
            catch
            {
                // Invariant 1. A malformed v2 claim NEVER falls back to legacy parsing — that fallback
                // would turn "I could not read your scope" into "you may see everything".
                return (false, [], ScopeResolutionSource.Denied, ScopeDenialReason.MalformedV2Claim, false);
            }

            if (parsed is null)
                return (false, [], ScopeResolutionSource.Denied, ScopeDenialReason.MalformedV2Claim, false);
            if (parsed.Version != 2)
                return (false, [], ScopeResolutionSource.Denied, ScopeDenialReason.UnknownV2Version, false);

            return parsed.Mode switch
            {
                EntityScopeModes.Group =>
                    (true, [], ScopeResolutionSource.ClaimV2, ScopeDenialReason.None, false),

                // An empty selected-company set is a denial with its own reason, not an accident: it is
                // indistinguishable from m=none in effect, but they arrive for different reasons and an
                // operator debugging a "sees nothing" report needs to know which happened.
                EntityScopeModes.Companies when parsed.CompanyIds is { Count: > 0 } ids =>
                    (false, ids.Distinct().ToList(), ScopeResolutionSource.ClaimV2, ScopeDenialReason.None, false),
                EntityScopeModes.Companies =>
                    (false, [], ScopeResolutionSource.Denied, ScopeDenialReason.EmptySelectedCompanySet, false),

                EntityScopeModes.None =>
                    (false, [], ScopeResolutionSource.Denied, ScopeDenialReason.ExplicitNone, false),

                _ => (false, [], ScopeResolutionSource.Denied, ScopeDenialReason.UnknownV2Mode, false),
            };
        }

        // ── Legacy v1, pre-cutover tokens only ──────────────────────────────────────────────────
        var rows = user.FindAll("entity_access").Select(c => c.Value).ToList();
        if (rows.Count == 0)
        {
            if (user.HasClaim("is_group_scope", "true"))
                return (true, [], ScopeResolutionSource.LegacyClaimV1, ScopeDenialReason.None, true);

            // Invariant 5. Under strict mode an absent claim is a denial. Non-strict keeps the
            // documented backward-compatible group access until the cutover completes — recorded as
            // LegacyCompatibilityUsed so it is visible in evidence rather than silent.
            return strict
                ? (false, [], ScopeResolutionSource.Denied, ScopeDenialReason.StrictMissingClaim, true)
                : (true, [], ScopeResolutionSource.LegacyAbsentNonStrict, ScopeDenialReason.None, true);
        }

        var companyIds = new List<Guid>();
        var hasGroupGrant = false;
        foreach (var json in rows)
        {
            try
            {
                var g = JsonSerializer.Deserialize<EntityAccessClaim>(json, JsonOptions);
                if (g is null) continue;
                if (g.CompanyId is null) hasGroupGrant = true;
                else companyIds.Add(g.CompanyId.Value);
            }
            catch { /* one malformed legacy row is skipped; it cannot widen the result */ }
        }
        return (hasGroupGrant, companyIds.Distinct().ToList(), ScopeResolutionSource.LegacyClaimV1, ScopeDenialReason.None, true);
    }

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var g) ? g : null;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record EntityAccessClaim(
        [property: JsonPropertyName("c")] Guid? CompanyId,
        [property: JsonPropertyName("r")] string? Role);

    private sealed record EntityScopeClaimV2(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("m")] string? Mode,
        [property: JsonPropertyName("c")] List<Guid>? CompanyIds);
}
