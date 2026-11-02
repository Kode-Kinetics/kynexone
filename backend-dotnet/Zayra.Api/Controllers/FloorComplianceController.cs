using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Compliance;

namespace Zayra.Api.Controllers;

/// <summary>
/// "Rules below the legal minimum": every configured rule that sits below a statutory minimum, whether or not an
/// administrator has acknowledged it, so nothing is hidden (owner decision 2026-10-25: statutory values are defaults,
/// lower is allowed with an audited acknowledgement). Read-only; assembled live by <see cref="BelowMinimumReportService"/>
/// from the leave policies, the end-of-service rates and the company payroll rules the caller may see.
/// </summary>
[ApiController]
[Route("api/compliance")]
public class FloorComplianceController(BelowMinimumReportService report) : ControllerBase
{
    [HttpGet("below-minimum")]
    [HasPermission("security.manage", "leave.policy_manage")]
    public async Task<IActionResult> BelowMinimum(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        return Ok(new { items = await report.BuildAsync(tenantId.Value, this.GetEntityScope(), ct) });
    }
}
