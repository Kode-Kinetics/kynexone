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
        // LEFT JOIN onto the latest check-in so the list can show the trajectory. This is the other read
        // site PIPCheckIn.Outcome never had: before this the check-ins a manager recorded were invisible
        // anywhere except one plan's detail payload.
        var items = await query
            .OrderByDescending(p => p.CreatedAtUtc)
            .Select(p => new PipListItem(
                p.Id, p.EmployeeId, p.EmployeeName, p.DepartmentName, p.TriggerReviewId,
                p.PerformanceGaps, p.ImprovementGoals, p.SupportPlan, p.StartDate, p.EndDate,
                p.Status, p.HrNotes, p.ManagerNotes, p.EmployeeComments, p.InitiatedByName,
                p.CreatedAtUtc, p.ClosedAtUtc,
                _db.PIPCheckIns
                    .Where(c => c.TenantId == p.TenantId && c.PipId == p.Id)
                    .OrderByDescending(c => c.CheckInDate)
                    .Select(c => c.Outcome)
                    .FirstOrDefault(),
                _db.PIPCheckIns.Count(c => c.TenantId == p.TenantId && c.PipId == p.Id)))
            .ToListAsync(ct);
        return Ok(items);
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

        return Ok(new
        {
            pip,
            checkIns,
            // PIPCheckIn.Outcome had no read site anywhere in the repository — the monitoring record a
            // PIP exists to produce was write-only. Surfaced here and on the list so the screen can show
            // the trajectory, and read by UpdateStatus below, which refuses a closing outcome that no
            // check-in supports.
            latestCheckInOutcome = checkIns.Count == 0 ? null : checkIns[0].Outcome,
            checkInCount = checkIns.Count,
        });
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

    /// <summary>
    /// The PIPs whose termination recommendation is still waiting on an employment decision.
    ///
    /// <para>THE DEFECT. <c>UpdateStatus</c> demanded a written reason for
    /// <c>TerminationRecommended</c> and then routed it nowhere: the word went into a column, the PIP
    /// dropped off the "Active" list, and no screen, queue or notification ever raised it with anyone.
    /// A manager who recommended termination and an HR team who never heard about it both believed the
    /// other was acting.</para>
    ///
    /// <para>The product's own position — stated on the screen — is that this is a recommendation and
    /// "HR and leadership must make the final employment decision". So the fix is NOT to terminate
    /// anybody automatically. It is to make the recommendation land somewhere a person sees it, and to
    /// let it CLEAR when the decision is actually taken. The queue is derived, not stored: a
    /// recommendation leaves it as soon as that employee has a live <c>EmployeeOffboarding</c> (raised
    /// through the offboarding screen or the terminate command, both of which already exist) or the
    /// employee is no longer employed. Nothing to backfill, nothing to keep in sync, and no second
    /// termination path invented alongside the one that works.</para>
    /// </summary>
    [HttpGet("termination-queue")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> TerminationQueue(CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var query = _db.PerformanceImprovementPlans.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.Status == "TerminationRecommended");
        if (!scope.IsUnrestricted)
            query = query.Where(p => scope.AllowedEmployeeIds!.Contains(p.EmployeeId));
        var candidates = await query.OrderBy(p => p.ClosedAtUtc).ToListAsync(ct);
        if (candidates.Count == 0)
            return Ok(new { items = Array.Empty<object>(), total = 0 });

        var employeeIds = candidates.Select(p => p.EmployeeId).Distinct().ToList();
        var settledEmployeeIds = await _db.EmployeeOffboardings.AsNoTracking()
            .Where(o => o.TenantId == tenantId && employeeIds.Contains(o.EmployeeId) && o.Status != "Cancelled")
            .Select(o => o.EmployeeId)
            .Distinct()
            .ToListAsync(ct);
        var stillEmployed = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && employeeIds.Contains(e.Id) && !e.IsDeleted
                     && e.Status != EmployeeStatuses.Terminated && e.Status != EmployeeStatuses.Archived
                     && e.Status != EmployeeStatuses.Exited)
            .Select(e => e.Id)
            .ToListAsync(ct);

        var items = candidates
            .Where(p => !settledEmployeeIds.Contains(p.EmployeeId) && stillEmployed.Contains(p.EmployeeId))
            .Select(p => new
            {
                p.Id, p.EmployeeId, p.EmployeeName, p.DepartmentName,
                p.PerformanceGaps, p.StartDate, p.EndDate, p.ClosedAtUtc,
                RecommendationReason = p.HrNotes,
                Action = "Decide the employment outcome and, if the recommendation is accepted, raise the "
                       + "separation through Offboarding (separation type 'Termination', or 'Article80' "
                       + "where there is cause). This entry clears once a separation exists.",
            })
            .ToList();
        return Ok(new { items, total = items.Count });
    }

    /// <summary>
    /// Close a PIP. <c>TerminationRecommended</c> and <c>Failed</c> now have to be supported by the
    /// monitoring record, and <c>TerminationRecommended</c> lands on <see cref="TerminationQueue"/>
    /// rather than evaporating.
    /// </summary>
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

        // ── THE MONITORING RECORD IS NOW EVIDENCE, NOT DECORATION ────────────────────────────────────
        // PIPCheckIn.Outcome had no read site anywhere. An adverse close — the one that ends up in front
        // of a labour court — could therefore be recorded against a plan nobody ever reviewed, and the
        // product could not tell the difference. A PIP closed adversely must have at least one recorded
        // check-in. This is the conservative half of the rule: it does NOT second-guess the outcome
        // (a deteriorating employee who then improves is a real and common case), only the absence of
        // any monitoring at all. See scratchpad/performance-decisions.md Q4.
        if (req.Status is "TerminationRecommended" or "Failed")
        {
            var checkInCount = await _db.PIPCheckIns
                .CountAsync(c => c.TenantId == tenantId && c.PipId == id, ct);
            if (checkInCount == 0)
                return Conflict(new
                {
                    error   = "no_monitoring_evidence",
                    message = $"This plan has no recorded check-ins, so there is no evidence of the monitoring a "
                            + $"'{req.Status}' outcome rests on. Record at least one check-in before closing it "
                            + "adversely.",
                });
        }

        pip.Status    = req.Status;
        pip.HrNotes   = (pip.HrNotes + "\n" + req.Notes).Trim();
        pip.ClosedAtUtc = req.Status == "Extended" ? null : DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new
        {
            pip,
            // The consequence, stated. A recommendation that lands nowhere is the defect; this says
            // where it landed.
            routedTo = req.Status == "TerminationRecommended" ? "/api/performance/pip/termination-queue" : null,
            nextStep = req.Status == "TerminationRecommended"
                ? "This is a recommendation, not a termination. It now appears on the HR termination queue "
                + "and stays there until a separation is raised for this employee through Offboarding."
                : null,
        });
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

/// <summary>
/// The PIP list row. A named type rather than an anonymous one so callers (and the scope-contract test
/// in Security/SecurityAuditBatch2Tests) can still bind the payload.
/// <para><c>LatestCheckInOutcome</c>/<c>CheckInCount</c> are the read sites <c>PIPCheckIn.Outcome</c>
/// never had — the monitoring record a PIP exists to produce was write-only.</para>
/// </summary>
public record PipListItem(
    Guid Id, int EmployeeId, string EmployeeName, string DepartmentName, Guid? TriggerReviewId,
    string PerformanceGaps, string ImprovementGoals, string SupportPlan,
    DateOnly StartDate, DateOnly EndDate, string Status,
    string HrNotes, string ManagerNotes, string EmployeeComments, string InitiatedByName,
    DateTime CreatedAtUtc, DateTime? ClosedAtUtc,
    string? LatestCheckInOutcome, int CheckInCount);

public record CheckInRequest(DateOnly CheckInDate, string Notes, string Outcome);
