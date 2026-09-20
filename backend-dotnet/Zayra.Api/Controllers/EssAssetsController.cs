using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Assets;
using Zayra.Api.Data;

namespace Zayra.Api.Controllers;

/// <summary>
/// W2-C — "My assets" for employee self-service. Read-only, and only ever the CALLER's own custody rows:
/// the employee is taken from the token (<c>employee_id</c>) or the user↔employee link
/// (<c>Employee.UserAccountId</c>), never from the request. Kept out of EmployeeSelfServiceController on purpose
/// (separate stream ownership); it follows the same ESS rules — ess.read/ess.write permission and no
/// NoLogin/KioskOnly access.
/// </summary>
[ApiController]
[Route("api/ess/assets")]
[Authorize]
public class EssAssetsController : ControllerBase
{
    private readonly ZayraDbContext _db;

    public EssAssetsController(ZayraDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<EmployeeAssetsDto>> MyAssets(CancellationToken ct)
    {
        var accessMode = User.FindFirstValue("access_mode") ?? string.Empty;
        if (accessMode is "NoLogin" or "KioskOnly")
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "This access mode cannot use ESS." });
        if (!User.Claims.Any(c => c.Type == "permission" && (c.Value is "ess.read" or "ess.write")))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "ESS read permission is required." });
        if (!Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId))
            return BadRequest(new { message = "Tenant claim is missing. Please log in again." });

        var employeeId = await ResolveOwnEmployeeIdAsync(tenantId, ct);
        if (employeeId is null)
            return NotFound(new { message = "Your user account is not linked to an employee record. Ask HR to link your account." });

        return Ok(await AssetsController.LoadEmployeeAssetsAsync(_db, tenantId, employeeId.Value, ct));
    }

    private async Task<int?> ResolveOwnEmployeeIdAsync(Guid tenantId, CancellationToken ct)
    {
        if (int.TryParse(User.FindFirstValue("employee_id"), out var claimed)
            && await _db.Employees.AnyAsync(e => e.TenantId == tenantId && e.Id == claimed && !e.IsDeleted, ct))
            return claimed;
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var userId))
            return null;
        return await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.UserAccountId == userId && !e.IsDeleted)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
    }
}
