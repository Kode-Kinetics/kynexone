using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Entitlements.Usage;

namespace Zayra.Api.Controllers.Entitlements;

/// <summary>
/// Benefit utilisation for HR and Finance, the way loans work: entitlement (the FROZEN package), pending, approved and remaining;
/// HR requests a use for an employee, Finance approves or rejects it, HR may withdraw a Pending one. No money moves. Closed unless
/// release_a is on (FeatureFlagGuardFilter covers /api/entitlements). Each write re-checks its own key, the company scope and
/// separation of duties in <see cref="BenefitUsageService"/>.
/// </summary>
[Authorize]
[ApiController]
[Route("api/entitlements/usage")]
public sealed class BenefitUsageController(BenefitUsageService usage) : ControllerBase
{
    /// <summary>GET /api/entitlements/usage?employeeId=&amp;asOf= — utilisation rows.</summary>
    [HttpGet]
    [HasPermission(BenefitUsagePermissions.Read, BenefitUsagePermissions.Decide)]
    public Task<IActionResult> Utilisation([FromQuery] int? employeeId, [FromQuery] DateOnly? asOf, CancellationToken ct) =>
        Run(actor => usage.UtilisationAsync(actor, employeeId, asOf, ct));

    /// <summary>GET /api/entitlements/usage/requests?employeeId=&amp;status= — requests (status=Pending is the Finance queue).</summary>
    [HttpGet("requests")]
    [HasPermission(BenefitUsagePermissions.Read, BenefitUsagePermissions.Decide)]
    public Task<IActionResult> Requests([FromQuery] int? employeeId, [FromQuery] string? status, CancellationToken ct) =>
        Run(actor => usage.RequestsAsync(actor, employeeId, status, ct));

    /// <summary>POST /api/entitlements/usage/requests — HR requests a use for an employee (Pending). Send a fresh requestId.</summary>
    [HttpPost("requests")]
    [HasPermission(BenefitUsagePermissions.RequestForEmployee)]
    public Task<IActionResult> Submit([FromBody] BenefitUsageRequestInput input, CancellationToken ct) =>
        Run(actor => usage.RequestAsync(actor, input, ct));

    /// <summary>POST /api/entitlements/usage/requests/{id}/decision — Finance: { decision: Approved|Rejected, reason }.</summary>
    [HttpPost("requests/{id:guid}/decision")]
    [HasPermission(BenefitUsagePermissions.Decide)]
    public Task<IActionResult> Decide(Guid id, [FromBody] BenefitUsageDecisionRequest body, CancellationToken ct) =>
        Run(actor => usage.DecideAsync(actor, id, body?.Decision ?? "", body?.Reason, ct));

    /// <summary>POST /api/entitlements/usage/requests/{id}/cancel — HR withdraws a Pending request, with a reason.</summary>
    [HttpPost("requests/{id:guid}/cancel")]
    [HasPermission(BenefitUsagePermissions.RequestForEmployee)]
    public Task<IActionResult> Cancel(Guid id, [FromBody] BenefitUsageCancelRequest body, CancellationToken ct) =>
        Run(actor => usage.CancelAsync(actor, id, body?.Reason, ct));

    private async Task<IActionResult> Run<T>(Func<BenefitUsageActor, Task<T>> action)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var scope = this.GetRequestScope();
        var permissions = User.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToArray();
        try { return Ok(await action(new BenefitUsageActor(tid, this.GetUserId(), permissions, scope.CanAccessCompany))); }
        catch (BenefitUsageRefusedException ex) { return StatusCode(ex.Status, new { error = ex.Code, message = ex.Message }); }
    }
}

public sealed record BenefitUsageDecisionRequest(string? Decision, string? Reason);
public sealed record BenefitUsageCancelRequest(string? Reason);
