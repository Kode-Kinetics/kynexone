using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;

namespace Zayra.Api.Controllers;

/// <summary>
/// The write rules for organization master data (companies, branches, departments, designations,
/// grades, cost centres), shared by every endpoint that writes them so a second door cannot apply a
/// weaker rule than the first. <c>OrgWriteDoorParityRatchetTests</c> enforces that each such write
/// action goes through here, <see cref="CompanyEndpoints"/>, or the group-grant check directly.
/// <para>Rules are judged on the caller's GRANT. The X-Company-Id switcher narrows what a request
/// reads; it never takes away what a group grant may write.</para>
/// </summary>
internal static class OrgWriteRules
{
    /// <summary>Tenant-wide master data (departments, designations, grades, company lifecycle):
    /// a group grant is required.</summary>
    public static bool GroupGrant(ControllerBase c) => c.HasGroupEntityScope();

    /// <summary>Data owned by one company (branches, company-scoped cost centres): the company must be
    /// within the caller's grant. A null company is tenant-wide and needs a group grant.</summary>
    public static bool MayWriteCompany(ControllerBase c, Guid? companyId) =>
        c.HasGroupEntityScope() || (companyId is not null && c.GetEntityScope().CanAccessCompany(companyId));

    /// <summary>A tenant-wide write: group grant, then the service call; an
    /// <see cref="InvalidOperationException"/> is the service's validation refusal (400). A null result
    /// from the service means the record was not found.</summary>
    public static async Task<ActionResult<T>> GroupWriteAsync<T>(
        ControllerBase c, Func<Guid, Task<T?>> write, Func<T, ActionResult> respond) where T : class
    {
        var tenantId = c.GetTenantId();
        if (tenantId is null) return c.Unauthorized();
        if (!GroupGrant(c)) return c.Forbid();
        try
        {
            var result = await write(tenantId.Value);
            return result is null ? c.NotFound() : respond(result);
        }
        catch (InvalidOperationException ex) { return c.BadRequest(new { message = ex.Message }); }
    }
}
