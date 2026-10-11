using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Infrastructure.Common;

/// <summary>
/// One rule for a by-id read or write of a record that is ABOUT an employee (a review, a recommendation,
/// a PIP, a probation review): load it in the tenant, and hand it back only when that employee is in the
/// caller's data scope. Out of scope is reported exactly like a wrong id — the caller returns 404 — so the
/// response never confirms that another company's (or another team's) record exists.
///
/// <para>Performance tables carry no company column, so this is the company boundary for them:
/// <see cref="IDataScopeService"/> materialises a company-scoped caller's organisation-wide scope into the
/// employees of their companies (an employee with no company is in nobody's company scope).</para>
/// </summary>
public static class ScopedRecordLookup
{
    /// <summary>The record, or null when it does not exist OR is about an employee outside the caller's scope.</summary>
    public static async Task<T?> FindInScopeAsync<T>(
        this IDataScopeService scopes, ClaimsPrincipal caller, Guid tenantId,
        IQueryable<T> query, Func<T, int> employeeOf, CancellationToken ct) where T : class
    {
        var row = await query.FirstOrDefaultAsync(ct);
        if (row is null) return null;
        return await scopes.InScopeAsync(caller, tenantId, employeeOf(row), ct) ? row : null;
    }

    /// <summary>True when the caller's scope reaches this employee.</summary>
    public static async Task<bool> InScopeAsync(
        this IDataScopeService scopes, ClaimsPrincipal caller, Guid tenantId, int employeeId, CancellationToken ct)
    {
        var scope = await scopes.ResolveAsync(caller, tenantId, ct);
        return scope.CanAccessEmployee(employeeId);
    }

    /// <summary>The employees in the caller's scope as a list for an EF <c>Contains</c>, or null when unrestricted.</summary>
    public static async Task<List<int>?> AllowedEmployeesAsync(
        this IDataScopeService scopes, ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
    {
        var scope = await scopes.ResolveAsync(caller, tenantId, ct);
        return scope.IsUnrestricted ? null : scope.AllowedEmployeeIds!.ToList();
    }
}
