using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Organization;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Payroll;

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

    /// <summary>422 with the M-10 text in both languages, the same body the class-change refusal carries.</summary>
    private static ObjectResult ReasonRequired(ControllerBase c) => c.StatusCode(422, CostCentreRuleException.ReasonRequired().ToPayload());

    public static async Task<ActionResult<CostCenterDto>> CreateAsync(
        ControllerBase c, IOrganizationSetupService organization, CostCenterRequest request, RequestContext context,
        Func<CostCenterDto, ActionResult> created, CancellationToken ct)
    {
        var tenantId = c.GetTenantId();
        if (tenantId is null) return c.Unauthorized();
        if (!OrgWriteRules.MayManageCostCentres(c)) return c.Forbid();
        if (!OrgWriteRules.MayWriteCompany(c, request.CompanyId)) return c.Forbid();
        // The functional class routes expense to P&L lines: a finance decision, not an HR one (AM-10).
        if (!string.IsNullOrWhiteSpace(request.FunctionalClass) && !OrgWriteRules.MayDecideFunctionalClass(c)) return c.Forbid();
        // M-10: setting a functional class on create is a finance decision that is recorded with its reason.
        if (!string.IsNullOrWhiteSpace(request.FunctionalClass) && !CostAccountingGuards.HasReason(request.Reason)) return ReasonRequired(c);
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
        if (!OrgWriteRules.MayManageCostCentres(c)) return c.Forbid();
        var (found, current) = await CurrentCompanyAsync(db, tenantId.Value, id, ct);
        if (!found || !OrgWriteRules.MayWriteCompany(c, current)) return c.NotFound();
        if (!OrgWriteRules.MayWriteCompany(c, request.CompanyId)) return c.Forbid();
        if (request.FunctionalClass is not null)
        {
            var currentClass = await db.CostCenters.AsNoTracking().Where(x => x.TenantId == tenantId.Value && x.Id == id)
                .Select(x => x.FunctionalClass).FirstOrDefaultAsync(ct);
            var requested = string.IsNullOrWhiteSpace(request.FunctionalClass) ? null : request.FunctionalClass.Trim().ToUpperInvariant();
            if (!string.Equals(requested, currentClass, StringComparison.Ordinal) && !OrgWriteRules.MayDecideFunctionalClass(c)) return c.Forbid();
        }
        // M-10: deactivating and moving to another company are recorded with a reason (a class change is checked by the service).
        var live = await db.CostCenters.AsNoTracking().Where(x => x.TenantId == tenantId.Value && x.Id == id)
            .Select(x => new { x.IsActive }).FirstAsync(ct);
        if (((live.IsActive && !request.IsActive) || request.CompanyId != current) && !CostAccountingGuards.HasReason(request.Reason)) return ReasonRequired(c);
        try
        {
            var costCenter = await organization.UpdateCostCenterAsync(tenantId.Value, id, request, context, ct);
            return costCenter is null ? c.NotFound() : c.Ok(costCenter);
        }
        catch (CostCentreRuleException ex) { return c.StatusCode(ex.Status, ex.ToPayload()); }
        catch (InvalidOperationException ex) { return c.BadRequest(new { message = ex.Message }); }
    }

    // Never used -> deleted; in use (employees, departments, positions) -> turned off. The server decides. Cost-centre doors keep
    // the cost-accounting rules: the manage permission, a reason on the audit row, and no change while a locked run resolves here.
    public static async Task<IActionResult> DeleteAsync(
        ControllerBase c, IOrganizationSetupService organization, ZayraDbContext db, Guid id, RequestContext context, string? reason, CancellationToken ct)
    {
        var tenantId = c.GetTenantId();
        if (tenantId is null) return c.Unauthorized();
        if (!OrgWriteRules.MayManageCostCentres(c)) return c.Forbid();
        var (found, current) = await CurrentCompanyAsync(db, tenantId.Value, id, ct);
        if (!found || !OrgWriteRules.MayWriteCompany(c, current)) return await OrgWriteRules.RemoveAsync(c, organization, OrgRowKind.CostCenter, id, context, ct);
        if (!CostAccountingGuards.HasReason(reason)) return ReasonRequired(c);
        try { await new CostAttributionGuard(db).EnsureCostCentreUnchangedAsync(tenantId.Value, current, id, ct); }
        catch (CostCentreRuleException ex) { return c.StatusCode(ex.Status, ex.ToPayload()); }
        return await OrgWriteRules.RemoveAsync(c, organization, OrgRowKind.CostCenter, id, context, ct, reason);
    }

    public static async Task<IActionResult> RestoreAsync(
        ControllerBase c, IOrganizationSetupService organization, Guid id, RequestContext context, CancellationToken ct)
    {
        if (!OrgWriteRules.MayManageCostCentres(c)) return c.Forbid();
        return await OrgWriteRules.RestoreAsync(c, organization, OrgRowKind.CostCenter, id, context, ct);
    }
}
