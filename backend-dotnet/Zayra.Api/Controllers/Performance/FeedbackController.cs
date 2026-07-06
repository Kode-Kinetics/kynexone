using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Performance;

[ApiController]
[Route("api/performance/feedback")]
[Authorize]
public class FeedbackController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;

    public FeedbackController(ZayraDbContext db, IDataScopeService scopeService)
    {
        _db = db;
        _scopeService = scopeService;
    }

    // ── Continuous feedback ────────────────────────────────────────────────────

    [HttpGet("continuous")]
    public async Task<IActionResult> ListContinuous(
        [FromQuery] int? employeeId,
        [FromQuery] string? type,
        [FromQuery] bool? includePrivate,
        CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId   = this.GetUserId();
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var query = _db.ContinuousFeedback.Where(f => f.TenantId == tenantId);
        if (!scope.IsUnrestricted)
            query = query.Where(f => scope.AllowedEmployeeIds!.Contains(f.EmployeeId));
        if (employeeId.HasValue) query = query.Where(f => f.EmployeeId == employeeId.Value);
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(f => f.FeedbackType == type);
        // Only show private feedback to the author
        if (includePrivate != true)
            query = query.Where(f => !f.IsPrivate || f.GivenByUserId == userId);

        return Ok(await query.OrderByDescending(f => f.CreatedAtUtc).ToListAsync(ct));
    }

    [HttpPost("continuous")]
    public async Task<IActionResult> CreateContinuous([FromBody] ContinuousFeedbackRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId   = this.GetUserId();
        var userName = HttpContext.User.FindFirst("FullName")?.Value ?? string.Empty;

        _db.ContinuousFeedback.Add(new ContinuousFeedback
        {
            TenantId       = tenantId,
            EmployeeId     = req.EmployeeId,
            EmployeeName   = req.EmployeeName,
            GivenByUserId  = userId,
            GivenByName    = userName,
            FeedbackType   = req.FeedbackType,
            Content        = req.Content,
            IsPrivate      = req.IsPrivate,
            LinkedReviewId = req.LinkedReviewId,
        });
        await _db.SaveChangesAsync(ct);
        return Ok();
    }

    // ── 360 feedback ──────────────────────────────────────────────────────────

    [HttpGet("360/{reviewId:guid}")]
    public async Task<IActionResult> List360(Guid reviewId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;

        // Authorize by the review's subject employee: the caller may see 360 feedback
        // only if they are the subject, their in-scope manager, or an org-wide role.
        var review = await _db.AppraisalReviews
            .FirstOrDefaultAsync(r => r.Id == reviewId && r.TenantId == tenantId, ct);
        if (review is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(review.EmployeeId))
            return Forbid();

        var items = await _db.Feedback360
            .Where(f => f.TenantId == tenantId && f.ReviewId == reviewId)
            .ToListAsync(ct);

        // Mask reviewer names for anonymous entries
        var result = items.Select(f => new
        {
            f.Id, f.ReviewId, f.ReviewerRole,
            ReviewerName = f.IsAnonymous ? "Anonymous" : f.ReviewerName,
            f.Score, f.Strengths, f.Improvements, f.Comments, f.SubmittedAt,
            f.IsAnonymous, f.CreatedAtUtc,
        });
        return Ok(result);
    }

    [HttpPost("360")]
    public async Task<IActionResult> Submit360([FromBody] Feedback360Request req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;

        // Ensure the target review belongs to this tenant before accepting feedback.
        var reviewExists = await _db.AppraisalReviews
            .AnyAsync(r => r.Id == req.ReviewId && r.TenantId == tenantId, ct);
        if (!reviewExists) return NotFound(new { message = "Review not found." });

        var existing = await _db.Feedback360
            .AnyAsync(f => f.TenantId == tenantId && f.ReviewId == req.ReviewId && f.ReviewerEmployeeId == req.ReviewerEmployeeId, ct);
        if (existing) return Conflict(new { message = "Feedback already submitted for this review." });

        _db.Feedback360.Add(new Feedback360
        {
            TenantId           = tenantId,
            ReviewId           = req.ReviewId,
            ReviewerEmployeeId = req.ReviewerEmployeeId,
            ReviewerName       = req.ReviewerName,
            ReviewerRole       = req.ReviewerRole,
            IsAnonymous        = req.IsAnonymous,
            Score              = req.Score,
            Strengths          = req.Strengths ?? string.Empty,
            Improvements       = req.Improvements ?? string.Empty,
            Comments           = req.Comments ?? string.Empty,
            SubmittedAt        = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return Ok();
    }
}

public record ContinuousFeedbackRequest(
    int EmployeeId, string EmployeeName, string FeedbackType,
    string Content, bool IsPrivate, Guid? LinkedReviewId);

public record Feedback360Request(
    Guid ReviewId, int ReviewerEmployeeId, string ReviewerName,
    string ReviewerRole, bool IsAnonymous,
    decimal Score, string? Strengths, string? Improvements, string? Comments);
