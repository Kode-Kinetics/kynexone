using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Succession;

namespace Zayra.Api.Controllers.Ess;

/// <summary>
/// "My development plan" (SUC-04, SUC-07): the caller's OWN plan and its actions. No employee id in any route — the
/// employee is the one linked to the signed-in user, so another person's plan is simply "not found". The plan's
/// nomination link is never returned. Talent-pool memberships appear only when the tenant switched
/// <c>succession_candidate_visibility</c> on (default off); a slate, a 9-box cell or another candidate never does.
/// Closed unless the tenant has the succession opt-in flag (/api/ess/development-plan in OptInFeatures).
/// </summary>
[ApiController]
[Authorize]
[SuccessionErrors]
[Route("api/ess/development-plan")]
public sealed class EssDevelopmentPlanController(ZayraDbContext db, DevelopmentPlanService plans) : ControllerBase
{
    [HttpGet]
    [HasPermission(SuccessionPermissions.Self)]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var (tenantId, employeeId) = await SelfAsync(ct);
        return Ok(await plans.MineAsync(tenantId, employeeId, ct));
    }

    /// <summary>PUT …/actions/{id} — progress note on any of my actions; status on my unlinked ones (linked ones follow their link).</summary>
    [HttpPut("actions/{id:guid}")]
    [HasPermission(SuccessionPermissions.Self)]
    public async Task<IActionResult> UpdateAction(Guid id, [FromBody] ActionUpdateInput input, CancellationToken ct)
    {
        var (tenantId, employeeId) = await SelfAsync(ct);
        var request = new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), this.GetUserId(), tenantId);
        return Ok(await plans.UpdateMyActionAsync(tenantId, employeeId, request, id, input, ct));
    }

    private async Task<(Guid TenantId, int EmployeeId)> SelfAsync(CancellationToken ct)
    {
        var accessMode = User.FindFirstValue("access_mode") ?? string.Empty;
        if (accessMode is "NoLogin" or "KioskOnly")
            throw new SuccessionException(403, "access_mode", "This access mode cannot use self-service.");
        if (this.GetTenantId() is not Guid tenantId) throw new SuccessionException(401, "unauthenticated", "Sign in again.");
        if (await CallerEmployeeResolver.ResolveAsync(db, User, tenantId, ct) is not int employeeId)
            throw new SuccessionException(409, "no_employee_record", EssLinkGuidance.En);
        return (tenantId, employeeId);
    }
}
