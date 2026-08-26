using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Performance;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Performance;

[ApiController]
[Route("api/performance/recommendations")]
[Authorize]
public class RecommendationsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IPerformanceService _svc;
    private readonly IDataScopeService _scopeService;

    public RecommendationsController(ZayraDbContext db, IPerformanceService svc, IDataScopeService scopeService)
    { _db = db; _svc = svc; _scopeService = scopeService; }

    [HttpGet("implementation-queue")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Manager")]
    public async Task<IActionResult> ImplementationQueue(CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var increments = await _db.IncrementRecommendations.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Status == "PendingImplementation")
            .Select(r => new { r.Id, Type = "SalaryIncrement", r.EmployeeId, r.EmployeeName, r.EffectiveDate, Amount = r.NewSalary, Action = "Create an effective-dated employee salary assignment, then record implementation evidence." })
            .ToListAsync(ct);
        var promotions = await _db.PromotionRecommendations.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Status == "PendingImplementation")
            .Select(r => new { r.Id, Type = "Promotion", r.EmployeeId, r.EmployeeName, r.EffectiveDate, Amount = (decimal?)null, Action = "Apply the approved designation/position change through employee position management." })
            .ToListAsync(ct);
        var bonuses = await _db.BonusRecommendations.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Status == "PendingImplementation")
            .Select(r => new { r.Id, Type = "PerformanceBonus", r.EmployeeId, r.EmployeeName, EffectiveDate = (DateOnly?)null, Amount = (decimal?)r.BonusAmount, Action = "Create the approved bonus in Finance Bonuses so it follows maker-checker and payroll/GL controls." })
            .ToListAsync(ct);
        return Ok(new { increments, promotions, bonuses, total = increments.Count + promotions.Count + bonuses.Count });
    }

    // ── Increment ──────────────────────────────────────────────────────────────

    [HttpGet("increments")]
    public async Task<IActionResult> ListIncrements([FromQuery] string? status, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var query = _db.IncrementRecommendations.Where(r => r.TenantId == tenantId);
        if (!scope.IsUnrestricted) query = query.Where(r => scope.AllowedEmployeeIds!.Contains(r.EmployeeId));
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
        return Ok(await query.OrderByDescending(r => r.CreatedAtUtc).ToListAsync(ct));
    }

    [HttpPost("increments")]
    [Authorize(Roles = "Admin,HR Manager,Manager")]
    public async Task<IActionResult> CreateIncrement([FromBody] IncrementRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId   = this.GetUserId();
        var userName = HttpContext.User.FindFirst("FullName")?.Value ?? "HR";
        var subject = await ResolveSubjectAsync(tenantId, req.EmployeeId, req.ReviewId, ct);
        if (subject.Error is not null) return subject.Error;
        if (req.IncrementPct <= 0 || req.IncrementPct > 100)
            return BadRequest(new { error = "invalid_increment", message = "Increment percentage must be greater than 0 and no more than 100." });
        var employee = subject.Employee!;
        var salary = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.EmployeeId == employee.Id && s.IsActive && s.EffectiveDate <= DateOnly.FromDateTime(DateTime.UtcNow))
            .OrderByDescending(s => s.EffectiveDate)
            .Select(s => (decimal?)s.BasicSalary)
            .FirstOrDefaultAsync(ct) ?? employee.Salary ?? 0m;

        var rec = new IncrementRecommendation
        {
            TenantId                   = tenantId,
            ReviewId                   = req.ReviewId,
            EmployeeId                 = employee.Id,
            EmployeeName               = employee.FullName,
            DepartmentName             = employee.Department ?? string.Empty,
            DesignationTitle           = employee.Designation ?? string.Empty,
            CurrentSalary              = salary,
            RecommendedIncrementPct    = req.IncrementPct,
            RecommendedIncrementAmount = Math.Round(salary * req.IncrementPct / 100, 2),
            NewSalary                  = Math.Round(salary * (1 + req.IncrementPct / 100), 2),
            EffectiveDate              = req.EffectiveDate,
            Reason                     = req.Reason,
            RecommendedByUserId        = userId,
            RecommendedByName          = userName,
        };
        _db.IncrementRecommendations.Add(rec);
        await _svc.LogAuditAsync(tenantId, "IncrementRecommendation", rec.Id.ToString(),
            "Created", string.Empty, $"Pct:{req.IncrementPct}%,NewSalary:{rec.NewSalary}", req.Reason, userId, userName, ct);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/performance/recommendations/increments/{rec.Id}", rec);
    }

    [HttpPost("increments/{id:guid}/approve")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> ApproveIncrement(Guid id, [FromBody] SimpleDecisionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId   = this.GetUserId();
        var rec = await _db.IncrementRecommendations
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (rec is null) return NotFound();
        var decisionError = ValidateDecision(rec.Status, rec.RecommendedByUserId, userId, req.Decision);
        if (decisionError is not null) return decisionError;

        rec.Status          = req.Decision == "Approved" ? "PendingImplementation" : "Rejected";
        rec.ApprovedByUserId = userId;
        rec.ApprovedAtUtc   = DateTime.UtcNow;
        await _svc.LogAuditAsync(tenantId, "IncrementRecommendation", id.ToString(),
            req.Decision, "Pending", rec.Status, req.Notes ?? string.Empty, userId, "HR", ct);
        await _db.SaveChangesAsync(ct);
        return Ok(rec);
    }

    // ── Promotion ──────────────────────────────────────────────────────────────

    [HttpGet("promotions")]
    public async Task<IActionResult> ListPromotions([FromQuery] string? status, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var query = _db.PromotionRecommendations.Where(r => r.TenantId == tenantId);
        if (!scope.IsUnrestricted) query = query.Where(r => scope.AllowedEmployeeIds!.Contains(r.EmployeeId));
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
        return Ok(await query.OrderByDescending(r => r.CreatedAtUtc).ToListAsync(ct));
    }

    [HttpPost("promotions")]
    [Authorize(Roles = "Admin,HR Manager,Manager")]
    public async Task<IActionResult> CreatePromotion([FromBody] PromotionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId   = this.GetUserId();
        var userName = HttpContext.User.FindFirst("FullName")?.Value ?? "HR";
        var subject = await ResolveSubjectAsync(tenantId, req.EmployeeId, req.ReviewId, ct);
        if (subject.Error is not null) return subject.Error;
        if (string.IsNullOrWhiteSpace(req.ProposedDesignation))
            return BadRequest(new { error = "designation_required", message = "Proposed designation is required." });
        var employee = subject.Employee!;

        var rec = new PromotionRecommendation
        {
            TenantId             = tenantId,
            ReviewId             = req.ReviewId,
            EmployeeId           = employee.Id,
            EmployeeName         = employee.FullName,
            DepartmentName       = employee.Department ?? string.Empty,
            CurrentDesignation   = employee.Designation ?? string.Empty,
            ProposedDesignation  = req.ProposedDesignation,
            EffectiveDate        = req.EffectiveDate,
            Reason               = req.Reason,
            RecommendedByUserId  = userId,
            RecommendedByName    = userName,
        };
        _db.PromotionRecommendations.Add(rec);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/performance/recommendations/promotions/{rec.Id}", rec);
    }

    [HttpPost("promotions/{id:guid}/approve")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> ApprovePromotion(Guid id, [FromBody] SimpleDecisionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId   = this.GetUserId();
        var rec = await _db.PromotionRecommendations
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (rec is null) return NotFound();
        var decisionError = ValidateDecision(rec.Status, rec.RecommendedByUserId, userId, req.Decision);
        if (decisionError is not null) return decisionError;
        rec.Status = req.Decision == "Approved" ? "PendingImplementation" : "Rejected";
        rec.ApprovedByUserId = userId; rec.ApprovedAtUtc = DateTime.UtcNow;
        await _svc.LogAuditAsync(tenantId, "PromotionRecommendation", id.ToString(),
            req.Decision, "Pending", rec.Status, req.Notes ?? string.Empty, userId, "HR", ct);
        await _db.SaveChangesAsync(ct);
        return Ok(rec);
    }

    // ── Bonus ──────────────────────────────────────────────────────────────────

    [HttpGet("bonuses")]
    public async Task<IActionResult> ListBonuses([FromQuery] string? status, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var query = _db.BonusRecommendations.Where(r => r.TenantId == tenantId);
        if (!scope.IsUnrestricted) query = query.Where(r => scope.AllowedEmployeeIds!.Contains(r.EmployeeId));
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
        return Ok(await query.OrderByDescending(r => r.CreatedAtUtc).ToListAsync(ct));
    }

    [HttpPost("bonuses")]
    [Authorize(Roles = "Admin,HR Manager,Manager")]
    public async Task<IActionResult> CreateBonus([FromBody] BonusRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId   = this.GetUserId();
        var userName = HttpContext.User.FindFirst("FullName")?.Value ?? "HR";
        var subject = await ResolveSubjectAsync(tenantId, req.EmployeeId, req.ReviewId, ct);
        if (subject.Error is not null) return subject.Error;
        if (req.BonusAmount <= 0)
            return BadRequest(new { error = "invalid_bonus", message = "Bonus amount must be greater than zero." });
        var employee = subject.Employee!;

        var rec = new BonusRecommendation
        {
            TenantId = tenantId, ReviewId = req.ReviewId,
            EmployeeId = employee.Id, EmployeeName = employee.FullName,
            DepartmentName = employee.Department ?? string.Empty,
            BonusAmount = req.BonusAmount, BonusType = req.BonusType,
            Reason = req.Reason, RecommendedByUserId = userId, RecommendedByName = userName,
        };
        _db.BonusRecommendations.Add(rec);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/performance/recommendations/bonuses/{rec.Id}", rec);
    }

    [HttpPost("bonuses/{id:guid}/approve")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> ApproveBonus(Guid id, [FromBody] SimpleDecisionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId   = this.GetUserId();
        var rec = await _db.BonusRecommendations
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (rec is null) return NotFound();
        var decisionError = ValidateDecision(rec.Status, rec.RecommendedByUserId, userId, req.Decision);
        if (decisionError is not null) return decisionError;
        rec.Status = req.Decision == "Approved" ? "PendingImplementation" : "Rejected";
        rec.ApprovedByUserId = userId; rec.ApprovedAtUtc = DateTime.UtcNow;
        await _svc.LogAuditAsync(tenantId, "BonusRecommendation", id.ToString(),
            req.Decision, "Pending", rec.Status, req.Notes ?? string.Empty, userId, "HR", ct);
        await _db.SaveChangesAsync(ct);
        return Ok(rec);
    }

    private async Task<(Employee? Employee, AppraisalReview? Review, IActionResult? Error)> ResolveSubjectAsync(
        Guid tenantId, int employeeId, Guid reviewId, CancellationToken ct)
    {
        var employee = await _db.Employees.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == employeeId && e.TenantId == tenantId && !e.IsDeleted, ct);
        if (employee is null)
            return (null, null, BadRequest(new { error = "employee_not_found", message = "Employee was not found in this tenant." }));
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(employee.Id)) return (null, null, Forbid());
        var review = await _db.AppraisalReviews.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reviewId && r.TenantId == tenantId && r.EmployeeId == employee.Id, ct);
        if (review is null)
            return (null, null, BadRequest(new { error = "review_employee_mismatch", message = "Review does not belong to this employee and tenant." }));
        if (review.Status is not ("Published" or "Acknowledged"))
            return (null, null, Conflict(new { error = "review_not_final", message = $"Recommendations require a published review (current: {review.Status})." }));
        return (employee, review, null);
    }

    private IActionResult? ValidateDecision(string status, Guid? recommendedBy, Guid? actor, string decision)
    {
        if (decision is not ("Approved" or "Rejected"))
            return BadRequest(new { error = "invalid_decision", message = "Decision must be Approved or Rejected." });
        if (status != "Pending")
            return Conflict(new { error = "invalid_state", message = $"Only a Pending recommendation can be decided (current: {status})." });
        if (actor.HasValue && recommendedBy == actor)
            return Conflict(new { error = "maker_checker_violation", message = "The recommender cannot approve or reject their own recommendation." });
        return null;
    }
}

public record IncrementRequest(
    Guid ReviewId, int EmployeeId, string EmployeeName,
    string DepartmentName, string DesignationTitle,
    decimal CurrentSalary, decimal IncrementPct,
    DateOnly EffectiveDate, string Reason);

public record PromotionRequest(
    Guid ReviewId, int EmployeeId, string EmployeeName, string DepartmentName,
    string CurrentDesignation, string ProposedDesignation,
    DateOnly EffectiveDate, string Reason);

public record BonusRequest(
    Guid ReviewId, int EmployeeId, string EmployeeName, string DepartmentName,
    decimal BonusAmount, string BonusType, string Reason);

public record SimpleDecisionRequest(string Decision, string? Notes);
