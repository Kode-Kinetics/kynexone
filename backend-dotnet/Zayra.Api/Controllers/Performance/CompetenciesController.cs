using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Controllers.Performance;

[ApiController]
[Route("api/performance/competencies")]
[Authorize]
public class CompetenciesController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public CompetenciesController(ZayraDbContext db) => _db = db;

    [HttpGet]
    [HasPermission(PerformancePermissions.Read, PerformancePermissions.Write, PerformancePermissions.Approve, PerformancePermissions.Setup)]
    public async Task<IActionResult> List([FromQuery] string? category, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var query = _db.Competencies.Where(c => c.TenantId == tenantId && c.IsActive);
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(c => c.Category == category);
        return Ok(await query.OrderBy(c => c.Category).ThenBy(c => c.Name).ToListAsync(ct));
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Create([FromBody] CompetencyRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var comp = new Competency
        {
            TenantId              = tenantId,
            Name                  = req.Name,
            Category              = req.Category,
            Description           = req.Description ?? string.Empty,
            BehavioralIndicators  = req.BehavioralIndicators ?? string.Empty,
        };
        _db.Competencies.Add(comp);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/performance/competencies/{comp.Id}", comp);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CompetencyRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var comp = await _db.Competencies
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct);
        if (comp is null) return NotFound();
        comp.Name = req.Name; comp.Category = req.Category;
        comp.Description = req.Description ?? string.Empty;
        comp.BehavioralIndicators = req.BehavioralIndicators ?? string.Empty;
        await _db.SaveChangesAsync(ct);
        return Ok(comp);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var comp = await _db.Competencies
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct);
        if (comp is null) return NotFound();
        comp.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public record CompetencyRequest(
    string Name, string Category, string? Description, string? BehavioralIndicators);
