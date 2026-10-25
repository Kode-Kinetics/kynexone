using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;

namespace Zayra.Api.Controllers;

/// <summary>
/// Cost-centre writes, shared by <c>/api/cost-centers</c> and <c>/api/organization/cost-centers</c>.
/// A cost centre belongs to one company (or, with no company, to the whole tenant), so writing it
/// needs that company within the caller's grant (<see cref="OrgWriteRules.MayWriteCompany"/>); on an
/// update both the current and the new company are checked. A cost centre outside the caller's
/// companies reads as not found, as it does on GET.
/// </summary>
internal static class CostCenterEndpoints
{
    /// <summary>The current company of a live cost centre, or <c>found: false</c>.</summary>
    public static async Task<(bool Found, Guid? CompanyId)> CurrentCompanyAsync(ZayraDbContext db, Guid tenantId, Guid id, CancellationToken ct)
    {
        var row = await db.CostCenters.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Id == id && !c.IsDeleted)
            .Select(c => new { c.CompanyId })
            .FirstOrDefaultAsync(ct);
        return row is null ? (false, null) : (true, row.CompanyId);
    }

    public static async Task<ActionResult<CostCenterDto>> CreateAsync(
        ControllerBase c, IOrganizationSetupService organization, CostCenterRequest request, RequestContext context,
        Func<CostCenterDto, ActionResult> created, CancellationToken ct)
    {
        var tenantId = c.GetTenantId();
        if (tenantId is null) return c.Unauthorized();
        if (!OrgWriteRules.MayWriteCompany(c, request.CompanyId)) return c.Forbid();
        try { return created(await organization.CreateCostCenterAsync(tenantId.Value, request, context, ct)); }
        catch (CostCentreRuleException ex) { return c.StatusCode(ex.Status, ex.ToPayload()); }
        catch (InvalidOperationException ex) { return c.BadRequest(new { message = ex.Message }); }
    }

    public static async Task<ActionResult<CostCenterDto>> UpdateAsync(
        ControllerBase c, IOrganizationSetupService organization, ZayraDbContext db, Guid id, CostCenterRequest request,
        RequestContext context, CancellationToken ct)
    {
        var tenantId = c.GetTenantId();
        if (tenantId is null) return c.Unauthorized();
        var (found, current) = await CurrentCompanyAsync(db, tenantId.Value, id, ct);
        if (!found || !OrgWriteRules.MayWriteCompany(c, current)) return c.NotFound();
        if (!OrgWriteRules.MayWriteCompany(c, request.CompanyId)) return c.Forbid();
        try
        {
            var costCenter = await organization.UpdateCostCenterAsync(tenantId.Value, id, request, context, ct);
            return costCenter is null ? c.NotFound() : c.Ok(costCenter);
        }
        catch (CostCentreRuleException ex) { return c.StatusCode(ex.Status, ex.ToPayload()); }
        catch (InvalidOperationException ex) { return c.BadRequest(new { message = ex.Message }); }
    }

    public static async Task<IActionResult> DeleteAsync(
        ControllerBase c, IOrganizationSetupService organization, ZayraDbContext db, Guid id, RequestContext context, CancellationToken ct)
    {
        var tenantId = c.GetTenantId();
        if (tenantId is null) return c.Unauthorized();
        var (found, current) = await CurrentCompanyAsync(db, tenantId.Value, id, ct);
        if (!found || !OrgWriteRules.MayWriteCompany(c, current)) return c.NotFound();
        try { return await organization.DeleteCostCenterAsync(tenantId.Value, id, context, ct) ? c.NoContent() : c.NotFound(); }
        catch (CostCentreRuleException ex) { return c.StatusCode(ex.Status, ex.ToPayload()); }
    }
}
