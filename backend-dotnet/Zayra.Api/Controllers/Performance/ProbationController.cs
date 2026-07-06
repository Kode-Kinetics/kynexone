using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Performance;

[ApiController]
[Route("api/performance/probation")]
[Authorize]
public class ProbationController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;
    public ProbationController(ZayraDbContext db, IDataScopeService scopeService)
    {
        _db = db;
        _scopeService = scopeService;
    }

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

    [HttpPost("{id:guid}/hr-decision")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> HrDecision(Guid id, [FromBody] ProbationHrDecisionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId   = this.GetUserId();
        var r = await _db.ProbationReviews
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);
        if (r is null) return NotFound();

        r.HrDecision       = req.Decision; // Confirmed/Extended/Terminated
        r.HrNotes          = req.Notes ?? string.Empty;
        r.ApprovedByHrUserId = userId;
        r.HrApprovedAt     = DateTime.UtcNow;
        r.Status           = "HRApproved";
        await _db.SaveChangesAsync(ct);
        return Ok(r);
    }
}

public record ProbationRequest(
    int EmployeeId, string EmployeeName, string DepartmentName, string DesignationTitle,
    DateOnly ProbationStartDate, DateOnly ProbationEndDate, DateOnly? ReviewDueDate);

public record ProbationManagerReviewRequest(
    string PerformanceSummary, decimal OverallRating, string Recommendation, string? Notes);

public record ProbationHrDecisionRequest(string Decision, string? Notes);
