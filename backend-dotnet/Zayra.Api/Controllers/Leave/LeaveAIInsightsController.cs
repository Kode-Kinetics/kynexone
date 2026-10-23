using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Leave;

[ApiController]
[Route("api/leave/ai-insights")]
[Authorize]
public class LeaveAIInsightsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly ILeaveService _leaveService;
    private readonly IDataScopeService _scopeService;

    public LeaveAIInsightsController(ZayraDbContext db, ILeaveService leaveService, IDataScopeService scopeService)
    {
        _db = db;
        _leaveService = leaveService;
        _scopeService = scopeService;
    }

    // An insight names an employee and their sick-leave pattern ("took sick leave on a Monday or Friday more
    // than 3 times"). Reading and acknowledging them is leave decision work: leave.approve (HR Manager, HR
    // Director, Manager, Admin), and only for employees inside the caller's data scope, so a line manager sees
    // their own reporting line and never another team's. An insight about no particular employee is for
    // organisation-wide readers only.

    [HttpGet]
    [HasPermission("leave.approve")]
    public async Task<IActionResult> List(
        [FromQuery] string? insightType,
        [FromQuery] string? severity,
        [FromQuery] bool? isAcknowledged,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var query = _db.LeaveAIInsights.Where(i => i.TenantId == tenantId);
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.IsUnrestricted)
        {
            var allowed = scope.AllowedEmployeeIds!.ToList();
            query = query.Where(i => i.AffectedEmployeeId != null && allowed.Contains(i.AffectedEmployeeId.Value));
        }

        if (!string.IsNullOrWhiteSpace(insightType)) query = query.Where(i => i.InsightType == insightType);
        if (!string.IsNullOrWhiteSpace(severity)) query = query.Where(i => i.Severity == severity);
        if (isAcknowledged.HasValue) query = query.Where(i => i.IsAcknowledged == isAcknowledged.Value);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(i => i.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PagedResult<LeaveAIInsight>(items, total, page, pageSize));
    }

    [HttpPost("generate")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Generate(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        await _leaveService.GenerateInsightsAsync(tenantId.Value, ct);
        return Ok(new { message = "AI insight generation completed." });
    }

    [HttpPost("{id:guid}/acknowledge")]
    [HasPermission("leave.approve")]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var insight = await _db.LeaveAIInsights
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);
        if (insight is null) return NotFound();
        // Out of scope reads as absent, as it does in List.
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.IsUnrestricted
            && (insight.AffectedEmployeeId is not int employeeId || !scope.CanAccessEmployee(employeeId)))
            return NotFound();

        insight.IsAcknowledged = true;
        insight.AcknowledgedByName = User.Identity?.Name ?? this.GetUserId()?.ToString() ?? "User";

        await _db.SaveChangesAsync(ct);
        return Ok(insight);
    }
}
