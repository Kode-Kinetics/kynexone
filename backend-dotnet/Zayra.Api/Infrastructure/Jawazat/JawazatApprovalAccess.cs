using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Jawazat;

/// <summary>Reuses the authoritative employee-scope resolver for central approval callers.</summary>
public static class JawazatApprovalAccess
{
    public static async Task<DataScope> ResolveAsync(ZayraDbContext db, Guid tenantId, RequestContext? context,
        CancellationToken ct, IDataScopeService? scopes = null, IHttpContextAccessor? http = null)
    {
        if (context?.UserId is not Guid userId || context.TenantId != tenantId || userId == Guid.Empty)
            return new DataScope { Level = DataScopeLevel.Own, AllowedEmployeeIds = Array.Empty<int>() };

        // A role/permission-only RequestContext cannot prove legal-entity access. In particular,
        // reconstruction without explicit scope claims must not enter the legacy group fallback.
        // Direct/background callers receive no Jawazat authority unless a matching authenticated
        // principal with an explicit, nonempty company scope is supplied.
        var principal = http?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true
            || !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub"), out var currentUser)
            || currentUser != userId || !Guid.TryParse(principal.FindFirstValue("tenant_id"), out var currentTenant) || currentTenant != tenantId)
            return new DataScope { Level = DataScopeLevel.Own, AllowedEmployeeIds = Array.Empty<int>() };
        var companyScope = new Zayra.Api.Infrastructure.Scope.RequestEntityScopeResolver(http,
            Microsoft.Extensions.Options.Options.Create(new EntityScopeOptions { StrictMode = true }))
            .ResolveFor(principal, http!.HttpContext!.Request.Headers[ZayraDbContext.CompanySelectionHeader].FirstOrDefault())
            .ToEntityScopeContext();
        if (!companyScope.IsGroupLevel && companyScope.AccessibleCompanyIds.Count == 0)
            return new DataScope { Level = DataScopeLevel.Own, AllowedEmployeeIds = Array.Empty<int>() };

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()), new("tenant_id", tenantId.ToString())
        };
        claims.AddRange((context.Roles ?? Array.Empty<string>()).Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange((context.Permissions ?? Array.Empty<string>()).Select(p => new Claim("permission", p)));

        claims.AddRange(principal.Claims.Where(c => c.Type is "entity_scope" or "entity_access" or "is_group_scope" or "entity_scope_strict"));

        // RequestContext has no employee claim. Derive one from the canonical account link; do not
        // guess by email or copy a client employee ID. Only identity IDs are read across companies.
        var ownIds = await ScopedBypass.NullableTenantWide(db.Employees, tenantId,
            "Jawazat approval scope identity: tenant and authenticated account bound an identity-only read; duplicate mappings do not become a caller employee claim.")
            .AsNoTracking().Where(e => e.UserAccountId == userId && !e.IsDeleted).Select(e => e.Id).Take(2).ToListAsync(ct);
        if (ownIds.Count == 1) claims.Add(new Claim("employee_id", ownIds[0].ToString()));

        var caller = new ClaimsPrincipal(new ClaimsIdentity(claims, "JawazatApprovalContext"));
        // Direct constructor call sites use exactly the production resolver, never an unrestricted
        // stand-in. A role/override permission alone therefore resolves to own/empty employee scope.
        return await (scopes ?? new DataScopeService(db, http: http)).ResolveAsync(caller, tenantId, ct);
    }
}
