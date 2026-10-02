using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Leave;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Leave;

[ApiController]
[Route("api/leave/ai-insights")]
[Authorize]
public class LeaveAIInsightsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly ILeaveService _leaveService;

    public LeaveAIInsightsController(ZayraDbContext db, ILeaveService leaveService)
    {
        _db = db;
        _leaveService = leaveService;
    }

    [HttpGet]
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
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var insight = await _db.LeaveAIInsights
            .FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);
        if (insight is null) return NotFound();

        insight.IsAcknowledged = true;
        insight.AcknowledgedByName = User.Identity?.Name ?? this.GetUserId()?.ToString() ?? "User";

        await _db.SaveChangesAsync(ct);
        return Ok(insight);
    }
}
