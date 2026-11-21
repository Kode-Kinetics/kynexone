using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers.Entitlements;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Entitlements.Usage;

namespace Zayra.Api.Controllers.Ess;

/// <summary>
/// "My benefits / مزاياي": the caller's OWN benefits (entitlement, used, left) and requests, and their own request or withdrawal,
/// the way a loan request works in self-service. No employee id is taken from the route, query or body: the employee is the one
/// linked to the signed-in login. Closed unless release_a is on (/api/ess/package).
/// </summary>
[ApiController]
[Route("api/ess/package/usage")]
[Authorize]
public sealed class EssBenefitUsageController(BenefitUsageService usage) : ControllerBase
{
    /// <summary>GET /api/ess/package/usage — the caller's own benefits and requests.</summary>
    [HttpGet]
    [HasPermission("ess.read")]
    public Task<IActionResult> Mine(CancellationToken ct) =>
        Self((tid, uid, employeeId) => usage.OwnAsync(tid, employeeId, uid, ct), ct);

    /// <summary>POST /api/ess/package/usage — request a use of one's own benefit (Pending; Finance decides).</summary>
    [HttpPost]
    [HasPermission(BenefitUsagePermissions.RequestForSelf)]
    public Task<IActionResult> Submit([FromBody] BenefitUsageRequestInput input, CancellationToken ct) =>
        Self((tid, uid, employeeId) => usage.RequestOwnAsync(tid, uid, employeeId, input, ct), ct);

    /// <summary>POST /api/ess/package/usage/{id}/cancel — withdraw one's own Pending request.</summary>
    [HttpPost("{id:guid}/cancel")]
    [HasPermission(BenefitUsagePermissions.RequestForSelf)]
    public Task<IActionResult> Cancel(Guid id, [FromBody] BenefitUsageCancelRequest body, CancellationToken ct) =>
        Self((tid, uid, employeeId) => usage.CancelOwnAsync(tid, uid, employeeId, id, body?.Reason, ct), ct);

    private async Task<IActionResult> Self<T>(Func<Guid, Guid, int, Task<T>> action, CancellationToken ct)
    {
        var accessMode = User.FindFirstValue("access_mode") ?? string.Empty;
        if (accessMode is "NoLogin" or "KioskOnly")
            return BadRequest(new { error = "access_mode", message = "This access mode cannot use self-service." });
        if (this.GetTenantId() is not Guid tid || this.GetUserId() is not Guid uid) return Unauthorized();
        if (await usage.ResolveSelfAsync(tid, User, uid, ct) is not int employeeId)
            return Conflict(new { error = "no_employee_record", message = "Your login is not linked to an employee record. Ask HR to link it." });
        try { return Ok(await action(tid, uid, employeeId)); }
        catch (BenefitUsageRefusedException ex) { return StatusCode(ex.Status, new { error = ex.Code, message = ex.Message }); }
    }
}
