using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Recruitment;

namespace Zayra.Api.Controllers;

/// <summary>
/// The employee internal job board: the job openings of the caller's OWN company that are still hiring (Open or InProgress). Read-only (applying is a later phase).
/// The caller's employee comes from their own login link (CallerEmployeeResolver); no employee, company or tenant id is ever
/// accepted from the request. The management endpoints under api/recruitment/openings keep their own recruitment permissions.
/// </summary>
[ApiController]
[Route("api/ess/jobs")]
[Authorize]
public class EssJobsController(IEssJobBoardService board) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);
        var (employeeId, early) = await CallerAsync(ct);
        if (early is not null) return early;
        return Ok(await board.ListAsync(TenantId(), employeeId!.Value, q, page, pageSize, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var (employeeId, early) = await CallerAsync(ct);
        if (early is not null) return early;
        // One answer whether the opening does not exist, is no longer hiring (OnHold, Closed, Cancelled), or belongs to another company or tenant: no existence leak.
        return await board.GetAsync(TenantId(), employeeId!.Value, id, ct) is { } job
            ? Ok(job)
            : NotFound(new { message = "Job opening not found.", messageAr = "الوظيفة الشاغرة غير موجودة." });
    }

    private Guid TenantId() => Guid.Parse(User.FindFirstValue("tenant_id")!);

    private async Task<(int? EmployeeId, IActionResult? Early)> CallerAsync(CancellationToken ct)
    {
        var accessMode = User.FindFirstValue("access_mode") ?? string.Empty;
        if (accessMode is "NoLogin" or "KioskOnly")
            return (null, BadRequest(new { message = "This access mode cannot use ESS." }));
        if (!HasPermission("ess.read") && !HasPermission("ess.write"))
            return (null, Forbid());
        if (!Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId))
            return (null, Unauthorized());

        if (await board.ResolveCallerEmployeeAsync(User, tenantId, ct) is not int employeeId)
            return (null, NotFound(new { message = EssLinkGuidance.En, messageAr = EssLinkGuidance.Ar }));
        return (employeeId, null);
    }

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));
}
