using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

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
        // IgnoreQueryFilters is intentional: the ambient filter would hide a company outside the actor's scope and
        // turn "not yours" into "does not exist"; the explicit TenantId predicate keeps the check tenant-contained.
        var exists = await db.Companies.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(c => c.Id == companyId && c.TenantId == tenantId && !c.IsDeleted, ct);
        if (!exists)
            throw new UnauthorizedAccessException($"{Code}: the company does not belong to this tenant.");
        seen.Add((tenantId, companyId));
    }
}
