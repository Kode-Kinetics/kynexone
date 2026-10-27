using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;

namespace Zayra.Api.Infrastructure.Employees.Onboarding;

/// <summary>
/// Second layer of the company write guard. A group-level caller passes <c>CanAccessCompany</c> for ANY company id,
/// so an explicit company id taken from a request body was trusted without ever being checked against the caller's
/// tenant. This asserts that a company named by an added row belongs to the same tenant as the row, and is not
/// deleted. The answer is remembered per context (valid pairs only), so an import that adds thousands of rows for a
/// handful of companies asks the database a handful of times.
/// </summary>
public static class TenantCompanyMembershipGuard
{
    public const string Code = "company_scope_denied";
    private static readonly ConditionalWeakTable<DbContext, HashSet<(Guid Tenant, Guid Company)>> Verified = new();

    public static async Task AssertAsync(ZayraDbContext db, Guid companyId, Guid tenantId, CancellationToken ct)
    {
        var seen = Verified.GetOrCreateValue(db);
        if (seen.Contains((tenantId, companyId))) return;
        // The ambient company filter would hide a company outside the actor's scope and turn "not yours" into
        // "does not exist"; TenantWide drops only the company dimension and pins the tenant in the predicate.
        var exists = await ScopedBypass.TenantWide(db.Companies, tenantId,
                "Write-guard membership check: a company outside the actor's scope must read as not this tenant's, not as missing.")
            .AsNoTracking()
            .AnyAsync(c => c.Id == companyId && !c.IsDeleted, ct);
        if (!exists)
            throw new UnauthorizedAccessException($"{Code}: the company does not belong to this tenant.");
        seen.Add((tenantId, companyId));
    }
}
