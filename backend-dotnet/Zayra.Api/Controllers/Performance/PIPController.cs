using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Performance;

[ApiController]
[Route("api/performance/pip")]
[Authorize]
public class PIPController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;
    public PIPController(ZayraDbContext db, IDataScopeService scopeService)
    {
        _db = db;
        _scopeService = scopeService;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? employeeId,
        [FromQuery] string? status,
        CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        // Performance Improvement Plans are sensitive/disciplinary-adjacent. Scope reads so an employee
        // sees only their own PIP and a manager only their team's; HR/Admin (org-wide) see all. Previously
        // any authenticated tenant user could list every PIP in the tenant (IDOR, CWE-639).
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var (singleId, setFilter) = scope.Constrain(employeeId);
        var query = _db.PerformanceImprovementPlans.Where(p => p.TenantId == tenantId);
        if (setFilter is not null)                 query = query.Where(p => setFilter.Contains(p.EmployeeId));
        else if (singleId.HasValue)                query = query.Where(p => p.EmployeeId == singleId.Value);
        if (!string.IsNullOrWhiteSpace(status))    query = query.Where(p => p.Status == status);
        return Ok(await query.OrderByDescending(p => p.CreatedAtUtc).ToListAsync(ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var pip = await _db.PerformanceImprovementPlans
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);
        if (pip is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(pip.EmployeeId)) return Forbid();

        var checkIns = await _db.PIPCheckIns
            .Where(c => c.TenantId == tenantId && c.PipId == id)
            .OrderByDescending(c => c.CheckInDate)
            .ToListAsync(ct);

        return Ok(new { pip, checkIns });
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager")]
    public async Task<IActionResult> Create([FromBody] PIPRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId   = this.GetUserId();
        var userName = HttpContext.User.FindFirst("FullName")?.Value ?? "HR";
        if (req.EndDate < req.StartDate)
            return BadRequest(new { error = "invalid_date_range", message = "PIP end date must be on or after its start date." });

        var employee = await _db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == req.EmployeeId && e.TenantId == tenantId && !e.IsDeleted, ct);
        if (employee is null) return BadRequest(new { error = "employee_not_found", message = "Employee was not found in this tenant." });
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(employee.Id)) return Forbid();
        if (req.TriggerReviewId is { } reviewId
            && !await _db.AppraisalReviews.AsNoTracking().AnyAsync(
                r => r.Id == reviewId && r.TenantId == tenantId && r.EmployeeId == employee.Id, ct))
            return BadRequest(new { error = "review_employee_mismatch", message = "Trigger review does not belong to this employee and tenant." });

        var pip = new PerformanceImprovementPlan
        {
            TenantId          = tenantId,
            EmployeeId        = employee.Id,
            EmployeeName      = employee.FullName,
            DepartmentName    = employee.Department ?? string.Empty,
            TriggerReviewId   = req.TriggerReviewId,
            PerformanceGaps   = req.PerformanceGaps,
            ImprovementGoals  = req.ImprovementGoals,
            SupportPlan       = req.SupportPlan ?? string.Empty,
            StartDate         = req.StartDate,
            EndDate           = req.EndDate,
            HrNotes           = req.HrNotes ?? string.Empty,
            InitiatedByUserId = userId,
            InitiatedByName   = userName,
        };
        _db.PerformanceImprovementPlans.Add(pip);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/performance/pip/{pip.Id}", pip);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager")]
    public async Task<IActionResult> Update(Guid id, [FromBody] PIPUpdateRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var pip = await _db.PerformanceImprovementPlans
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);
        if (pip is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(pip.EmployeeId)) return Forbid();
        if (pip.Status is not ("Active" or "Extended"))
            return Conflict(new { error = "pip_closed", message = $"A PIP in '{pip.Status}' status cannot be edited." });
        if (req.EndDate is { } endDate && endDate < pip.StartDate)
            return BadRequest(new { error = "invalid_date_range", message = "PIP end date must be on or after its start date." });

        pip.PerformanceGaps  = req.PerformanceGaps ?? pip.PerformanceGaps;
        pip.ImprovementGoals = req.ImprovementGoals ?? pip.ImprovementGoals;
        pip.SupportPlan      = req.SupportPlan ?? pip.SupportPlan;
        pip.EndDate          = req.EndDate ?? pip.EndDate;
        pip.HrNotes          = req.HrNotes ?? pip.HrNotes;
        pip.ManagerNotes     = req.ManagerNotes ?? pip.ManagerNotes;
        pip.EmployeeComments = req.EmployeeComments ?? pip.EmployeeComments;
        await _db.SaveChangesAsync(ct);
        return Ok(pip);
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] PIPStatusRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var pip = await _db.PerformanceImprovementPlans
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);
        if (pip is null) return NotFound();
        if (req.Status is not ("Improved" or "Extended" or "Failed" or "TerminationRecommended"))
            return BadRequest(new { error = "invalid_status", message = "Status must be Improved, Extended, Failed, or TerminationRecommended." });
        if (pip.Status is not ("Active" or "Extended"))
            return Conflict(new { error = "invalid_transition", message = $"A PIP in '{pip.Status}' status cannot transition again." });
        if (req.Status == "TerminationRecommended" && string.IsNullOrWhiteSpace(req.Notes))
            return BadRequest(new { error = "reason_required", message = "A reason is required for a termination recommendation." });

        pip.Status    = req.Status;
        pip.HrNotes   = (pip.HrNotes + "\n" + req.Notes).Trim();
        pip.ClosedAtUtc = req.Status == "Extended" ? null : DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(pip);
    }

    [HttpPost("{id:guid}/checkin")]
    [Authorize(Roles = "Admin,HR Manager,Manager")]
    public async Task<IActionResult> AddCheckIn(Guid id, [FromBody] CheckInRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId   = this.GetUserId();
        var userName = HttpContext.User.FindFirst("FullName")?.Value ?? "HR";

        if (req.Outcome is not ("OnTrack" or "AtRisk" or "Improved" or "Deteriorated"))
            return BadRequest(new { error = "invalid_outcome", message = "Outcome must be OnTrack, AtRisk, Improved, or Deteriorated." });
        var pip = await _db.PerformanceImprovementPlans
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);
        if (pip is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(pip.EmployeeId)) return Forbid();
        if (pip.Status is not ("Active" or "Extended"))
            return Conflict(new { error = "pip_closed", message = $"Check-ins cannot be added to a PIP in '{pip.Status}' status." });

        _db.PIPCheckIns.Add(new PIPCheckIn
        {
            TenantId        = tenantId,
            PipId           = id,
            CheckInDate     = req.CheckInDate,
            Notes           = req.Notes,
            Outcome         = req.Outcome,
            CheckedByUserId = userId,
            CheckedByName   = userName,
        });
        await _db.SaveChangesAsync(ct);
        return Ok();
    }
}

public record PIPRequest(
    int EmployeeId, string EmployeeName, string DepartmentName,
    Guid? TriggerReviewId, string PerformanceGaps, string ImprovementGoals,
    string? SupportPlan, DateOnly StartDate, DateOnly EndDate, string? HrNotes);

public record PIPUpdateRequest(
    string? PerformanceGaps, string? ImprovementGoals, string? SupportPlan,
    DateOnly? EndDate, string? HrNotes, string? ManagerNotes, string? EmployeeComments);

public record PIPStatusRequest(string Status, string? Notes);

public record CheckInRequest(DateOnly CheckInDate, string Notes, string Outcome);
