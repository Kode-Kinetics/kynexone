using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Performance;

[ApiController]
[Route("api/performance/probation")]
[Authorize]
public class ProbationController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;
    private readonly IEmployeeManagementService? _employeeManagement;

    /// <param name="employeeManagement">
    /// The separation domain's only injectable entry point. Optional so the many tests that do
    /// <c>new ProbationController(db, scope)</c> keep compiling — but a "Terminated" decision REFUSES
    /// rather than silently recording a word when it is absent (see <see cref="HrDecision"/>). DI always
    /// supplies it (Program.cs registers IEmployeeManagementService).
    /// </param>
    public ProbationController(
        ZayraDbContext db,
        IDataScopeService scopeService,
        IEmployeeManagementService? employeeManagement = null)
    {
        _db = db;
        _scopeService = scopeService;
        _employeeManagement = employeeManagement;
    }

    /// <summary>The HR decisions this endpoint can actually honour. Any other word is refused.</summary>
    public static readonly string[] AllowedHrDecisions = ["Confirmed", "Extended", "Terminated"];

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] int? employeeId,
        CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        // Scope probation-review reads to the caller (own → team → org), like the other performance
        // controllers. Previously any authenticated user could enumerate every employee's probation
        // outcomes/notes (IDOR, CWE-639).
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var (singleId, setFilter) = scope.Constrain(employeeId);
        var query = _db.ProbationReviews.Where(p => p.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(p => p.Status == status);
        if (setFilter is not null) query = query.Where(p => setFilter.Contains(p.EmployeeId));
        else if (singleId.HasValue) query = query.Where(p => p.EmployeeId == singleId.Value);
        return Ok(await query.OrderByDescending(p => p.ProbationEndDate).ToListAsync(ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var r = await _db.ProbationReviews
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);
        if (r is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        return scope.CanAccessEmployee(r.EmployeeId) ? Ok(r) : Forbid();
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Create([FromBody] ProbationRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var rev = new ProbationReview
        {
            TenantId           = tenantId,
            EmployeeId         = req.EmployeeId,
            EmployeeName       = req.EmployeeName,
            DepartmentName     = req.DepartmentName,
            DesignationTitle   = req.DesignationTitle,
            ProbationStartDate = req.ProbationStartDate,
            ProbationEndDate   = req.ProbationEndDate,
            ReviewDueDate      = req.ReviewDueDate,
        };
        _db.ProbationReviews.Add(rev);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/performance/probation/{rev.Id}", rev);
    }

    [HttpPost("{id:guid}/manager-review")]
    [Authorize(Roles = "Admin,HR Manager,Manager")]
    public async Task<IActionResult> ManagerReview(Guid id, [FromBody] ProbationManagerReviewRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId   = this.GetUserId();
        var userName = HttpContext.User.FindFirst("FullName")?.Value ?? "Manager";
        var r = await _db.ProbationReviews
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);
        if (r is null) return NotFound();
        // The recommendation is the input to the HR decision below, which is now a real employment
        // action. An unrecognised word here would arrive at HR as an unreadable instruction.
        if (req.Recommendation is not ("Confirm" or "Extend" or "Terminate"))
            return BadRequest(new { error = "invalid_recommendation", message = "Recommendation must be Confirm, Extend, or Terminate." });
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(r.EmployeeId)) return Forbid();

        r.PerformanceSummary           = req.PerformanceSummary;
        r.OverallRating                = req.OverallRating;
        r.ManagerRecommendation        = req.Recommendation; // Confirm/Extend/Terminate
        r.ManagerNotes                 = req.Notes ?? string.Empty;
        r.ReviewedByManagerUserId      = userId;
        r.ReviewedByManagerName        = userName;
        r.ManagerReviewedAt            = DateTime.UtcNow;
        r.Status                       = "ManagerReviewed";
        await _db.SaveChangesAsync(ct);
        return Ok(r);
    }

    /// <summary>
    /// The HR decision that ends probation — and now actually ends it.
    ///
    /// <para>THE DEFECT. This action wrote <c>HrDecision</c> and nothing else. The file contained zero
    /// references to <c>Employees</c>, there was no reader of <c>HrDecision</c> anywhere in the
    /// repository, and the value was not even validated — so "Terminated", "Confirmed" and "Yes please"
    /// were all accepted and all produced the identical screen. A terminated probationer stayed Active,
    /// kept their login, kept their WPS payroll footprint and was never routed to a final settlement.</para>
    ///
    /// <para>Each decision now routes into machinery that already exists, rather than a parallel one:</para>
    /// <list type="bullet">
    /// <item><b>Terminated</b> → <c>IEmployeeManagementService.TerminateAsync</c> with
    /// <c>SeparationType = "ProbationFailure"</c>, the value already in the closed separation vocabulary
    /// (<c>EmployeeManagementService.AllowedSeparationTypes</c>). That one call transitions the employee,
    /// writes the status history, mints the authoritative <c>EmployeeOffboarding</c> that
    /// <c>/final-settlement</c> requires, and deactivates the WPS payroll footprint — all in one
    /// transaction. "ProbationFailure" pays the full Art. 84 award; a probation dismissal FOR CAUSE is
    /// <c>Article80</c> and is raised through the offboarding screen, not from here (Q3).</item>
    /// <item><b>Confirmed</b> → ends the probation window on the employee record. <c>ConfirmationDate</c>
    /// is the field that represents "made permanent", and <c>ProbationEndDate</c> is the one anything
    /// actually READS: <c>LeaveService</c> refuses leave under a policy with
    /// <c>AppliesOnProbation = false</c> while the request falls on or before it, and the probation
    /// headcount is <c>ProbationEndDate >= today</c>. Bringing it forward to the effective date is what
    /// makes confirmation observable instead of decorative.</item>
    /// <item><b>Extended</b> → requires the new end date and writes it to the same field, so the leave
    /// gate and the probation headcount extend with it. An extension with no new date is refused: it
    /// would be the original defect wearing a different word.</item>
    /// </list>
    /// </summary>
    [HttpPost("{id:guid}/hr-decision")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> HrDecision(Guid id, [FromBody] ProbationHrDecisionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId   = this.GetUserId();

        var decision = AllowedHrDecisions.FirstOrDefault(
            d => string.Equals(d, req.Decision?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (decision is null)
            return BadRequest(new
            {
                error   = "invalid_decision",
                message = "A probation decision changes this person's employment. An unrecognised word is refused rather than recorded and ignored.",
                allowed = AllowedHrDecisions,
            });

        var r = await _db.ProbationReviews
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);
        if (r is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(r.EmployeeId)) return Forbid();
        if (r.Status == "HRApproved" || r.Status == "Closed")
            return Conflict(new { error = "already_decided", message = $"This probation review is already '{r.Status}' with decision '{r.HrDecision}'." });

        var employee = await _db.Employees
            .FirstOrDefaultAsync(e => e.Id == r.EmployeeId && e.TenantId == tenantId && !e.IsDeleted, ct);
        if (employee is null)
            return Conflict(new { error = "employee_not_found", message = "The employee this probation review belongs to no longer exists, so the decision cannot be carried out." });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var effectiveDate = req.EffectiveDate ?? today;

        switch (decision)
        {
            case "Terminated":
            {
                // PRIVILEGE BOUNDARY, the same one EmployeesController draws at PATCH /status: the
                // separation type decides the end-of-service award, and minting one is employees.approve
                // — the canonical separation command's permission — not merely a performance role.
                if (!User.HasPermission("employees.approve"))
                    return Forbid();
                if (_employeeManagement is null)
                    return StatusCode(StatusCodes.Status501NotImplemented, new
                    {
                        code    = "probation_termination_unavailable",
                        message = "The separation service is not available on this request, and a probation termination "
                                + "will not be recorded as a word that terminates nothing. Retry, or raise the separation "
                                + "through the offboarding screen.",
                    });
                // Default the last working day to the end of the probation period — the date the
                // employment was always scheduled to be decided on — and let the caller state another.
                // TerminateAsync refuses a date before joining, which surfaces here as a 400.
                var lastWorkingDay = req.EffectiveDate ?? r.ProbationEndDate;
                var reason = string.IsNullOrWhiteSpace(req.Notes)
                    ? $"Probation not passed — probation review {r.Id}."
                    : $"Probation not passed — {req.Notes.Trim()}";
                try
                {
                    var result = await _employeeManagement.TerminateAsync(
                        tenantId, r.EmployeeId,
                        new EmployeeStatusChangeRequest(
                            Status: EmployeeStatuses.Terminated,
                            EffectiveDate: lastWorkingDay,
                            Reason: reason.Length > 500 ? reason[..500] : reason,
                            SeparationType: "ProbationFailure"),
                        new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(),
                            Request.Headers.UserAgent.ToString(), userId, tenantId),
                        ct);
                    if (result is null) return NotFound();
                }
                // The readiness gate (EmployeeActivationBlockedException) only fires on a transition INTO
                // an occupying status, so it cannot reach this branch and is deliberately not caught here.
                catch (InvalidOperationException ex)
                {
                    return BadRequest(new { error = "termination_refused", message = ex.Message });
                }
                catch (DbUpdateException)
                {
                    return Conflict(new
                    {
                        error   = "separation_already_open",
                        message = "This employee already has a live separation. Complete or cancel it before terminating probation.",
                    });
                }
                break;
            }

            case "Confirmed":
            {
                // Confirmation ENDS probation. EffectiveDate is the first PERMANENT day, so the last
                // probationary day is the one before it — both readers are inclusive of
                // ProbationEndDate (LeaveService refuses a request starting ON it; the probation
                // headcount counts `>= today`), so leaving it equal to the confirmation date would keep
                // the employee on probation for the day they were confirmed.
                var lastProbationDay = effectiveDate.AddDays(-1);
                employee.ConfirmationDate = effectiveDate;
                // Only ever brought FORWARD: a confirmation must not silently lengthen a probation.
                if (employee.ProbationEndDate is null || employee.ProbationEndDate > lastProbationDay)
                    employee.ProbationEndDate = lastProbationDay;
                employee.UpdatedAtUtc = DateTime.UtcNow;
                employee.UpdatedBy = userId;
                r.ProbationEndDate = employee.ProbationEndDate!.Value;
                break;
            }

            case "Extended":
            {
                if (req.NewProbationEndDate is not { } newEnd)
                    return BadRequest(new
                    {
                        error   = "new_probation_end_date_required",
                        message = "An extension must state the new probation end date. Without one the decision would change "
                                + "nothing the employee or the leave rules can observe.",
                    });
                if (newEnd <= r.ProbationEndDate)
                    return BadRequest(new
                    {
                        error   = "new_probation_end_date_not_later",
                        message = $"The new probation end date ({newEnd:yyyy-MM-dd}) must be after the current one ({r.ProbationEndDate:yyyy-MM-dd}).",
                    });
                employee.ProbationEndDate = newEnd;
                employee.UpdatedAtUtc = DateTime.UtcNow;
                employee.UpdatedBy = userId;
                r.ProbationEndDate = newEnd;
                break;
            }
        }

        r.HrDecision         = decision; // Confirmed/Extended/Terminated
        r.HrNotes            = req.Notes ?? string.Empty;
        r.ApprovedByHrUserId = userId;
        r.HrApprovedAt       = DateTime.UtcNow;
        // An extension is not the end of the story: a further manager review is due against the new
        // date, so the review reopens rather than closing on a decision that defers the real one.
        r.Status             = decision == "Extended" ? "Pending" : "Closed";
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            probationReview = r,
            // The consequences, stated. Each one is a fact some other part of the product now reads.
            employeeStatus      = employee.Status,
            confirmationDate    = employee.ConfirmationDate,
            probationEndDate    = employee.ProbationEndDate,
            separationRaised    = decision == "Terminated",
            separationType      = decision == "Terminated" ? "ProbationFailure" : null,
        });
    }
}

public record ProbationRequest(
    int EmployeeId, string EmployeeName, string DepartmentName, string DesignationTitle,
    DateOnly ProbationStartDate, DateOnly ProbationEndDate, DateOnly? ReviewDueDate);

public record ProbationManagerReviewRequest(
    string PerformanceSummary, decimal OverallRating, string Recommendation, string? Notes);

/// <param name="EffectiveDate">
/// Confirmed: the date probation ends. Terminated: the LAST WORKING DAY, which decides the
/// end-of-service award. Omitted → today for a confirmation, the probation end date for a termination.
/// </param>
/// <param name="NewProbationEndDate">Required for, and only used by, an Extended decision.</param>
public record ProbationHrDecisionRequest(
    string Decision, string? Notes, DateOnly? EffectiveDate = null, DateOnly? NewProbationEndDate = null);
