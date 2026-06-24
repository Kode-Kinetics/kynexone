using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zayra.Api.Application.Common;

/// <summary>
/// Resolved per-request entity (Legal Entity / Company) scope.
/// Mirrors SAP Company Code / Workday Legal Entity access model.
/// Parsed on-demand from JWT — no middleware needed.
/// </summary>
public sealed class EntityScopeContext
{
    public bool IsGroupLevel { get; }
    public IReadOnlyList<Guid> AccessibleCompanyIds { get; }

    private EntityScopeContext(bool isGroupLevel, IReadOnlyList<Guid> ids)
    {
        IsGroupLevel = isGroupLevel;
        AccessibleCompanyIds = ids;
    }

    /// <param name="strictMode">
    /// When true, absence of both entity_access and is_group_scope claims is treated as
    /// default-deny (Empty) rather than backward-compat GroupLevel. Enable after all users
    /// have re-authenticated with the new JWT schema.
    /// </param>
    public static EntityScopeContext FromClaims(ClaimsPrincipal user, bool strictMode = false)
    {
        var claims = user.FindAll("entity_access").Select(c => c.Value).ToList();
        if (claims.Count == 0)
        {
            // Explicit is_group_scope=true claim — post-migration group access
            if (user.HasClaim("is_group_scope", "true"))
                return GroupLevel;
            // No entity_access and no is_group_scope:
            //   Non-strict → backward-compat GroupLevel (pre-migration behavior)
            //   Strict → default-deny (Empty)
            return strictMode ? Empty : GroupLevel;
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
            catch { /* ignore malformed claim */ }
        }
        return new EntityScopeContext(hasGroupGrant, companyIds.Distinct().ToList());
    }

    public bool CanAccessCompany(Guid? companyId)
    {
        if (IsGroupLevel) return true;
        return companyId.HasValue && AccessibleCompanyIds.Contains(companyId.Value);
    }

    public static readonly EntityScopeContext GroupLevel = new(true, Array.Empty<Guid>());
    // No entity_access + StrictMode — deny all company-owned data
    public static readonly EntityScopeContext Empty = new(false, Array.Empty<Guid>());

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record EntityAccessClaim(
        [property: JsonPropertyName("c")] Guid? CompanyId,
        [property: JsonPropertyName("r")] string? Role);
}

public sealed record EntityAccessGrant(Guid? CompanyId, string Role);
