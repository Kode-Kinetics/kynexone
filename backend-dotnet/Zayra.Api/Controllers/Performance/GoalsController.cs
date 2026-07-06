using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Performance;

[ApiController]
[Route("api/performance/goals")]
[Authorize]
public class GoalsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;

    public GoalsController(ZayraDbContext db, IDataScopeService scopeService)
    {
        _db = db;
        _scopeService = scopeService;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? employeeId,
        [FromQuery] Guid? cycleId,
        [FromQuery] string? status,
        [FromQuery] string? category,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId()!.Value;
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var query = _db.EmployeeGoals.Where(g => g.TenantId == tenantId && !g.IsDeleted);
        if (!scope.IsUnrestricted)
            query = query.Where(g => scope.AllowedEmployeeIds!.Contains(g.EmployeeId));
        if (employeeId.HasValue) query = query.Where(g => g.EmployeeId == employeeId.Value);
        if (cycleId.HasValue)    query = query.Where(g => g.CycleId == cycleId.Value);
        if (!string.IsNullOrWhiteSpace(status))   query = query.Where(g => g.Status == status);
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(g => g.Category == category);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(g => g.CreatedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var goal = await _db.EmployeeGoals
            .FirstOrDefaultAsync(g => g.Id == id && g.TenantId == tenantId, ct);
        if (goal is null) return NotFound();

        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(goal.EmployeeId))
            return Forbid();

        var updates = await _db.GoalProgressUpdates
            .Where(u => u.TenantId == tenantId && u.GoalId == id)
            .OrderByDescending(u => u.UpdatedAtUtc)
            .ToListAsync(ct);

        return Ok(new { goal, updates });
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager,Employee")]
    public async Task<IActionResult> Create([FromBody] GoalRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId   = this.GetUserId();

        var goal = new EmployeeGoal
        {
            TenantId         = tenantId,
            CycleId          = req.CycleId,
            EmployeeId       = req.EmployeeId,
            EmployeeName     = req.EmployeeName,
            Title            = req.Title,
            Description      = req.Description ?? string.Empty,
            Category         = req.Category,
            KpiType          = req.KpiType,
            MeasurementUnit  = req.MeasurementUnit ?? string.Empty,
            TargetValue      = req.TargetValue,
            BaselineValue    = req.BaselineValue ?? 0,
            ActualValue      = req.ActualValue,
            Priority         = req.Priority ?? "Medium",
            StartDate        = req.StartDate,
            DueDate          = req.DueDate,
            Status           = "Draft",
            CreatedByUserId  = userId,
        };
        goal.AchievementPct = goal.TargetValue > 0
            ? Math.Min(100, Math.Round(goal.ActualValue / goal.TargetValue * 100, 1))
            : 0;

        _db.EmployeeGoals.Add(goal);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/performance/goals/{goal.Id}", goal);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager,Employee")]
    public async Task<IActionResult> Update(Guid id, [FromBody] GoalRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var goal = await _db.EmployeeGoals
            .FirstOrDefaultAsync(g => g.Id == id && g.TenantId == tenantId, ct);
        if (goal is null) return NotFound();

        goal.Title           = req.Title;
        goal.Description     = req.Description ?? string.Empty;
        goal.Category        = req.Category;
        goal.KpiType         = req.KpiType;
        goal.MeasurementUnit = req.MeasurementUnit ?? string.Empty;
        goal.TargetValue     = req.TargetValue;
        goal.BaselineValue   = req.BaselineValue ?? goal.BaselineValue;
        goal.ActualValue     = req.ActualValue;
        goal.Weight          = req.Weight;
        goal.Priority        = req.Priority ?? goal.Priority;
        goal.StartDate       = req.StartDate;
        goal.DueDate         = req.DueDate;
        goal.AchievementPct  = goal.TargetValue > 0
            ? Math.Min(100, Math.Round(goal.ActualValue / goal.TargetValue * 100, 1))
            : 0;
        goal.UpdatedAtUtc    = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(goal);
    }

    [HttpPost("{id:guid}/progress")]
    public async Task<IActionResult> UpdateProgress(Guid id, [FromBody] ProgressUpdateRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId   = this.GetUserId();
        var goal = await _db.EmployeeGoals
            .FirstOrDefaultAsync(g => g.Id == id && g.TenantId == tenantId, ct);
        if (goal is null) return NotFound();

        _db.GoalProgressUpdates.Add(new GoalProgressUpdate
        {
            TenantId        = tenantId,
            GoalId          = id,
            UpdatedValue    = req.UpdatedValue,
            Notes           = req.Notes ?? string.Empty,
            UpdatedByUserId = userId,
            UpdatedByName   = req.UpdatedByName ?? string.Empty,
        });

        goal.ActualValue    = req.UpdatedValue;
        goal.AchievementPct = goal.TargetValue > 0
            ? Math.Min(100, Math.Round(req.UpdatedValue / goal.TargetValue * 100, 1))
            : 0;
        if (goal.AchievementPct >= 100) goal.Status = "Completed";
        goal.UpdatedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(goal);
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Roles = "Admin,HR Manager,Manager")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId   = this.GetUserId();
        var goal = await _db.EmployeeGoals
            .FirstOrDefaultAsync(g => g.Id == id && g.TenantId == tenantId, ct);
        if (goal is null) return NotFound();
        goal.ManagerApproved    = true;
        goal.ApprovedByUserId   = userId;
        goal.UpdatedAtUtc       = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(goal);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager,Employee")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var goal = await _db.EmployeeGoals.FirstOrDefaultAsync(g => g.Id == id && g.TenantId == tenantId && !g.IsDeleted, ct);
        if (goal is null) return NotFound();
        if (goal.ManagerApproved) return BadRequest("Cannot delete an approved goal. Set it to Cancelled instead.");
        goal.IsDeleted = true; goal.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { deleted = true });
    }
}

public record GoalRequest(
    int EmployeeId, string EmployeeName, Guid? CycleId,
    string Title, string? Description, string Category, string KpiType,
    string? MeasurementUnit, decimal TargetValue, decimal ActualValue,
    decimal Weight, DateOnly? DueDate,
    string? Priority = "Medium", DateOnly? StartDate = null, decimal? BaselineValue = null);

public record ProgressUpdateRequest(decimal UpdatedValue, string? Notes, string? UpdatedByName);
