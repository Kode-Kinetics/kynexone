using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Recruitment;

[Authorize]
[ApiController]
[Route("api/recruitment/workforce-planning")]
public class WorkforcePlanningController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public WorkforcePlanningController(ZayraDbContext db) => _db = db;

    private Guid GetTenantId() =>
        Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;

    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    private string GetUserName() => User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "System";

    // GET /api/recruitment/workforce-planning
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int year = 0,
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var tid = GetTenantId();
        if (year == 0) year = DateTime.UtcNow.Year;

        var q = _db.WorkforcePlans
            .Where(x => x.TenantId == tid && !x.IsDeleted && x.PlanYear == year);

        if (!string.IsNullOrEmpty(status))
            q = q.Where(x => x.Status == status);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    // GET /api/recruitment/workforce-planning/{id}
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var plan = await _db.WorkforcePlans.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (plan == null) return NotFound();
        return Ok(plan);
    }

    // POST /api/recruitment/workforce-planning
    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Create([FromBody] CreateWorkforcePlanRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var year = req.PlanYear > 0 ? req.PlanYear : DateTime.UtcNow.Year;
        var count = await _db.WorkforcePlans.CountAsync(x => x.TenantId == tid && x.PlanYear == year, ct);
        var code = $"WFP-{year}-{(count + 1):D3}";
        var planCurrency = !string.IsNullOrWhiteSpace(req.CurrencyCode) ? req.CurrencyCode : await _db.ResolveTenantCurrencyAsync(tid, ct);

        var plan = new WorkforcePlan
        {
            TenantId = tid,
            PlanCode = code,
            PlanYear = year,
            PlanName = req.PlanName,
            DepartmentId = req.DepartmentId,
            DepartmentName = req.DepartmentName ?? string.Empty,
            CurrentHeadcount = req.CurrentHeadcount,
            PlannedHeadcount = req.PlannedHeadcount,
            GapCount = req.PlannedHeadcount - req.CurrentHeadcount,
            BudgetAllocated = req.BudgetAllocated,
            CurrencyCode = planCurrency,
            Notes = req.Notes ?? string.Empty,
            CreatedByUserId = GetUserId(),
            CreatedByName = GetUserName(),
        };

        _db.WorkforcePlans.Add(plan);

        _db.RecruitmentAuditLogs.Add(new RecruitmentAuditLog
        {
            TenantId = tid, EntityType = "WorkforcePlan", EntityId = plan.Id.ToString(),
            Action = "Created", PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { plan.PlanCode, plan.PlanName, plan.Status }),
        });

        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = plan.Id }, plan);
    }

    // PATCH /api/recruitment/workforce-planning/{id}/status
    [HttpPatch("{id:guid}/status")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateStatusRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var plan = await _db.WorkforcePlans.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (plan == null) return NotFound();

        var old = plan.Status;
        plan.Status = req.Status;
        if (req.Status == "Approved") plan.ApprovedAtUtc = DateTime.UtcNow;

        _db.RecruitmentAuditLogs.Add(new RecruitmentAuditLog
        {
            TenantId = tid, EntityType = "WorkforcePlan", EntityId = id.ToString(),
            Action = "StatusChanged", PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
            OldValuesJson = System.Text.Json.JsonSerializer.Serialize(new { Status = old }),
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { Status = req.Status }),
        });

        await _db.SaveChangesAsync(ct);
        return Ok(plan);
    }

    // DELETE /api/recruitment/workforce-planning/{id}
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var plan = await _db.WorkforcePlans.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (plan == null) return NotFound();

        plan.IsDeleted = true;
        _db.RecruitmentAuditLogs.Add(new RecruitmentAuditLog
        {
            TenantId = tid, EntityType = "WorkforcePlan", EntityId = id.ToString(),
            Action = "Deleted", PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
        });

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // GET /api/recruitment/workforce-planning/summary
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] int year = 0, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        if (year == 0) year = DateTime.UtcNow.Year;

        var plans = await _db.WorkforcePlans
            .Where(x => x.TenantId == tid && !x.IsDeleted && x.PlanYear == year)
            .ToListAsync(ct);

        return Ok(new
        {
            totalPlans = plans.Count,
            draft = plans.Count(p => p.Status == "Draft"),
            approved = plans.Count(p => p.Status == "Approved"),
            inProgress = plans.Count(p => p.Status == "InProgress"),
            totalGap = plans.Sum(p => p.GapCount),
            totalBudget = plans.Sum(p => p.BudgetAllocated),
        });
    }
}

public record CreateWorkforcePlanRequest(
    string PlanName, int PlanYear, Guid? DepartmentId, string? DepartmentName,
    int CurrentHeadcount, int PlannedHeadcount, decimal BudgetAllocated,
    string? CurrencyCode, string? Notes);

public record UpdateStatusRequest(string Status);
