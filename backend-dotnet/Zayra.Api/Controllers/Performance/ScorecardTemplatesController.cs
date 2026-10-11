using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

using Zayra.Api.Infrastructure.Authorization;

namespace Zayra.Api.Controllers.Performance;

[ApiController]
[Route("api/performance/templates")]
[Authorize]
public class ScorecardTemplatesController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public ScorecardTemplatesController(ZayraDbContext db) => _db = db;

    [HttpGet]
    [HasPermission(PerformancePermissions.Read, PerformancePermissions.Write, PerformancePermissions.Approve, PerformancePermissions.Setup)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var items = await _db.PerformanceScorecardTemplates
            .Where(t => t.TenantId == tenantId)
            .OrderByDescending(t => t.CreatedAtUtc)
            .ToListAsync(ct);
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    [HasPermission(PerformancePermissions.Read, PerformancePermissions.Write, PerformancePermissions.Approve, PerformancePermissions.Setup)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var t = await _db.PerformanceScorecardTemplates
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        return t is null ? NotFound() : Ok(t);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Create([FromBody] ScorecardTemplateRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;

        if (Math.Abs(req.KpiWeight + req.CompetencyWeight + req.AttendanceWeight +
                     req.ProductivityWeight + req.FeedbackWeight + req.DisciplineWeight - 100) > 0.01m)
            return BadRequest(new { message = "Weights must total exactly 100%." });

        // If this is set as default, clear existing defaults
        if (req.IsDefault)
        {
            var existing = await _db.PerformanceScorecardTemplates
                .Where(t => t.TenantId == tenantId && t.IsDefault).ToListAsync(ct);
            existing.ForEach(t => t.IsDefault = false);
        }

        var template = new PerformanceScorecardTemplate
        {
            TenantId           = tenantId,
            Name               = req.Name,
            DepartmentName     = req.DepartmentName ?? string.Empty,
            DesignationTitle   = req.DesignationTitle ?? string.Empty,
            Grade              = req.Grade ?? string.Empty,
            KpiWeight          = req.KpiWeight,
            CompetencyWeight   = req.CompetencyWeight,
            AttendanceWeight   = req.AttendanceWeight,
            ProductivityWeight = req.ProductivityWeight,
            FeedbackWeight     = req.FeedbackWeight,
            DisciplineWeight   = req.DisciplineWeight,
            MinPassingScore    = req.MinPassingScore,
            RequiresCalibration  = req.RequiresCalibration,
            Requires360Feedback  = req.Requires360Feedback,
            IsDefault          = req.IsDefault,
            RatingLabels       = req.RatingLabels ?? string.Empty,
        };
        _db.PerformanceScorecardTemplates.Add(template);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/performance/templates/{template.Id}", template);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ScorecardTemplateRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var t = await _db.PerformanceScorecardTemplates
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (t is null) return NotFound();

        if (Math.Abs(req.KpiWeight + req.CompetencyWeight + req.AttendanceWeight +
                     req.ProductivityWeight + req.FeedbackWeight + req.DisciplineWeight - 100) > 0.01m)
            return BadRequest(new { message = "Weights must total exactly 100%." });

        if (req.IsDefault && !t.IsDefault)
        {
            var existing = await _db.PerformanceScorecardTemplates
                .Where(x => x.TenantId == tenantId && x.IsDefault && x.Id != id).ToListAsync(ct);
            existing.ForEach(x => x.IsDefault = false);
        }

        t.Name = req.Name; t.DepartmentName = req.DepartmentName ?? string.Empty;
        t.DesignationTitle = req.DesignationTitle ?? string.Empty; t.Grade = req.Grade ?? string.Empty;
        t.KpiWeight = req.KpiWeight; t.CompetencyWeight = req.CompetencyWeight;
        t.AttendanceWeight = req.AttendanceWeight; t.ProductivityWeight = req.ProductivityWeight;
        t.FeedbackWeight = req.FeedbackWeight; t.DisciplineWeight = req.DisciplineWeight;
        t.MinPassingScore = req.MinPassingScore; t.RequiresCalibration = req.RequiresCalibration;
        t.Requires360Feedback = req.Requires360Feedback; t.IsDefault = req.IsDefault;
        t.RatingLabels = req.RatingLabels ?? string.Empty;
        t.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(t);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var t = await _db.PerformanceScorecardTemplates
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (t is null) return NotFound();
        t.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public record ScorecardTemplateRequest(
    string Name,
    string? DepartmentName, string? DesignationTitle, string? Grade,
    decimal KpiWeight, decimal CompetencyWeight, decimal AttendanceWeight,
    decimal ProductivityWeight, decimal FeedbackWeight, decimal DisciplineWeight,
    decimal MinPassingScore,
    bool RequiresCalibration, bool Requires360Feedback, bool IsDefault,
    string? RatingLabels);
