using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zayra.Api.Models;

namespace Zayra.Api.Application.Common;

/// <summary>
/// Resolved per-request entity (Legal Entity / Company) scope.
/// Mirrors SAP Company Code / Workday Legal Entity access model.
/// Parsed on-demand from JWT — no middleware needed.
///
/// CLAIM FORMAT v2 (Phase 2): a single "entity_scope" claim carrying an explicit decision
/// made at token issuance: {"v":2,"m":"group"|"companies"|"none","c":["guid",...]}.
///   m=group     → all companies in the tenant, now and future (explicit, never inferred)
///   m=companies → exactly the listed company ids (Selected grants + AllCurrent snapshot)
///   m=none      → no company-scoped data (default-deny)
/// A MALFORMED v2 claim ALWAYS fails closed (Empty) — never falls back to legacy parsing.
///
/// LEGACY COMPAT (documented cutover): tokens without "entity_scope" fall back to v1
/// parsing (entity_access rows + is_group_scope). Non-strict absence of everything still
/// resolves GroupLevel until the global StrictMode cutover; strict (global flag or the
/// per-token entity_scope_strict marker) turns absence into default-deny. Once StrictMode
/// is on and one access-token lifetime has elapsed, every live token carries v2 and the
/// legacy path is dead code to be removed in the next phase.
/// </summary>
public sealed class EntityScopeContext
{
    /// <summary>
    /// Claim stamped on high-privilege minted tokens (platform-admin impersonation).
    /// When present, absence of scope claims fails closed (default-deny) regardless of
    /// the global StrictMode cutover — an impersonated session must never inherit
    /// tenant-wide company access by claim omission.
    /// </summary>
    public const string StrictScopeClaim = "entity_scope_strict";

    /// <summary>Claim type for scope format v2.</summary>
    public const string V2ClaimType = "entity_scope";

    public bool IsGroupLevel { get; }
    public IReadOnlyList<Guid> AccessibleCompanyIds { get; }

    private EntityScopeContext(bool isGroupLevel, IReadOnlyList<Guid> ids)
    {
        IsGroupLevel = isGroupLevel;
        AccessibleCompanyIds = ids;
    }

    /// <param name="strictMode">
    /// When true, absence of all scope claims is treated as default-deny (Empty) rather
    /// than backward-compat GroupLevel. Enable after all users have re-authenticated
    /// with v2 tokens (≥ one access-token lifetime after deploy).
    /// </param>
    public static EntityScopeContext FromClaims(ClaimsPrincipal user, bool strictMode = false)
    {
        // Per-token strict marker (impersonation/support) forces fail-closed even before
        // the global StrictMode cutover.
        var strict = strictMode || user.HasClaim(StrictScopeClaim, "true");

        // ── v2 (preferred): one explicit issuance-time decision ──────────────────
        var v2 = user.FindFirst(V2ClaimType)?.Value;
        if (v2 is not null)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<EntityScopeClaimV2>(v2, _jsonOptions);
                return parsed switch
                {
                    { Version: 2, Mode: EntityScopeModes.Group } => GroupLevel,
                    { Version: 2, Mode: EntityScopeModes.Companies, CompanyIds: not null } =>
                        new EntityScopeContext(false, parsed.CompanyIds.Distinct().ToList()),
                    { Version: 2, Mode: EntityScopeModes.None } => Empty,
                    _ => Empty, // unknown version/mode → fail closed
                };
            }
            catch
            {
                return Empty; // malformed v2 → ALWAYS fail closed, never legacy fallback
            }
        }

        // ── Legacy v1 compat path (pre-cutover tokens only) ───────────────────────
        var claims = user.FindAll("entity_access").Select(c => c.Value).ToList();
        if (claims.Count == 0)
        {
            // Explicit is_group_scope=true claim — post-migration group access
            if (user.HasClaim("is_group_scope", "true"))
                return GroupLevel;
            // No claims at all:
            //   Non-strict → backward-compat GroupLevel (pre-migration behavior)
            //   Strict → default-deny (Empty)
            return strict ? Empty : GroupLevel;
        }

        var companyIds = new List<Guid>();
        bool hasGroupGrant = false;
        foreach (var json in claims)
        {
            try
            {
                var g = JsonSerializer.Deserialize<EntityAccessClaim>(json, _jsonOptions);
                if (g is null) continue;
                if (g.CompanyId is null) hasGroupGrant = true;
                else companyIds.Add(g.CompanyId.Value);
            }
            catch { /* ignore malformed legacy claim row */ }
        }
        return new EntityScopeContext(hasGroupGrant, companyIds.Distinct().ToList());
    }

    public bool CanAccessCompany(Guid? companyId)
    {
        if (IsGroupLevel) return true;
        return companyId.HasValue && AccessibleCompanyIds.Contains(companyId.Value);
    }

    public static readonly EntityScopeContext GroupLevel = new(true, Array.Empty<Guid>());
    // Default-deny — no company-owned data visible.
    public static readonly EntityScopeContext Empty = new(false, Array.Empty<Guid>());

    /// <summary>Explicit narrowed scope (company switcher / derived contexts).</summary>
    public static EntityScopeContext ForCompanies(IEnumerable<Guid> companyIds) =>
        new(false, companyIds.Distinct().ToList());

    /// <summary>
    /// Applies the company-switcher header on top of the token scope: a valid selection
    /// the actor can access narrows to that single company; an invalid or inaccessible
    /// selection FAILS CLOSED (Empty) — the header can only ever narrow, never widen.
    /// </summary>
    public EntityScopeContext NarrowTo(string? selectedCompanyHeader)
    {
        if (string.IsNullOrWhiteSpace(selectedCompanyHeader)) return this;
        if (!Guid.TryParse(selectedCompanyHeader, out var selected)) return Empty;
        return CanAccessCompany(selected) ? ForCompanies(new[] { selected }) : Empty;
    }

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record EntityAccessClaim(
        [property: JsonPropertyName("c")] Guid? CompanyId,
        [property: JsonPropertyName("r")] string? Role);

    private sealed record EntityScopeClaimV2(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("m")] string? Mode,
        [property: JsonPropertyName("c")] List<Guid>? CompanyIds);
}

public static class EntityScopeModes
{
    public const string Group = "group";
    public const string Companies = "companies";
    public const string None = "none";
}

/// <summary>The issuance-time scope decision carried into the token as claim v2.</summary>
public sealed record EntityScopeDescriptor(string Mode, IReadOnlyList<Guid> CompanyIds)
{
    public static readonly EntityScopeDescriptor Group = new(EntityScopeModes.Group, Array.Empty<Guid>());
    public static readonly EntityScopeDescriptor None = new(EntityScopeModes.None, Array.Empty<Guid>());
    public static EntityScopeDescriptor Companies(IEnumerable<Guid> ids) =>
        new(EntityScopeModes.Companies, ids.Distinct().ToList());
}

/// <summary>
/// Single source of truth for RESOLVING a user's grants into a scope decision and for
/// EMITTING that decision as claims. Used by normal login, impersonation, and break-glass
/// support tokens so all three can never drift apart again.
/// </summary>
public static class EntityScopeClaims
{
    /// <summary>
    /// Resolution rules (least privilege, explicit):
    ///  1. User.IsGroupScope or an AllCurrentAndFutureCompanies grant → group (dynamic).
    ///  2. Otherwise union of Selected grant companies + (AllCurrentCompanies →
    ///     SNAPSHOT of companies active at issuance, frozen into the token).
    ///  3. Grants exist but resolve to nothing → none (deny — misconfigured grants must
    ///     never widen).
    ///  4. No grants at all → group. This is the documented tenant-default: tenant users
    ///     are tenant-wide until per-user company scoping is assigned. It is an EXPLICIT
    ///     issuance-time decision (auditable, revocable per user), not a parser fallback —
    ///     which is what makes the StrictMode flip safe.
    /// </summary>
    public static EntityScopeDescriptor Resolve(
        bool userIsGroupScope,
        IReadOnlyCollection<EntityAccessGrant> activeGrants,
        IReadOnlyCollection<Guid> activeCompanyIdsAtIssuance)
    {
        if (userIsGroupScope) return EntityScopeDescriptor.Group;
        if (activeGrants.Count == 0) return EntityScopeDescriptor.Group; // tenant default (rule 4)

        // Dynamic group grants, plus legacy null-company Selected rows that pre-date the
        // grant-mode migration (behavior-preserving: null company always meant group).
        if (activeGrants.Any(g => g.Mode == EntityGrantModes.AllCurrentAndFutureCompanies
                                  || (g.CompanyId is null && g.Mode == EntityGrantModes.SelectedCompanies)))
            return EntityScopeDescriptor.Group;

        var ids = new List<Guid>();
        foreach (var grant in activeGrants)
        {
            if (grant.Mode == EntityGrantModes.AllCurrentCompanies) ids.AddRange(activeCompanyIdsAtIssuance);
            else if (grant.CompanyId is Guid cid) ids.Add(cid);
        }
        return ids.Count > 0 ? EntityScopeDescriptor.Companies(ids) : EntityScopeDescriptor.None;
    }

    /// <summary>
    /// Emits claim v2 plus, during the rollout window, the legacy v1 claims so pods still
    /// running the previous release enforce the same boundary (remove with the legacy
    /// parser after the StrictMode cutover).
    /// </summary>
    public static List<Claim> Build(EntityScopeDescriptor scope, IReadOnlyCollection<EntityAccessGrant> legacyGrants)
    {
        var claims = new List<Claim>
        {
            new(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new
            {
                v = 2,
                m = scope.Mode,
                c = scope.Mode == EntityScopeModes.Companies ? scope.CompanyIds : Array.Empty<Guid>(),
            }, _json)),
        };
        if (scope.Mode == EntityScopeModes.Group)
            claims.Add(new Claim("is_group_scope", "true"));
        foreach (var grant in legacyGrants.Where(g => g.CompanyId is not null))
            claims.Add(new Claim("entity_access", JsonSerializer.Serialize(new { c = grant.CompanyId?.ToString(), r = grant.Role }, _json)));
        return claims;
    }

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
}

public sealed record EntityAccessGrant(Guid? CompanyId, string Role, string Mode = EntityGrantModes.SelectedCompanies);
