using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Common;
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
    [HasPermission(PerformancePermissions.Read, PerformancePermissions.Write, PerformancePermissions.Approve, PerformancePermissions.Setup)]
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
        // A private note is its author's alone — for every caller, HR and Admin included. includePrivate is
        // still accepted (old clients send it) but does not widen anything. A caller with no user id sees no private notes at all, so a note
        // whose author was never recorded (null) cannot match a null caller.
        _ = includePrivate;
        query = userId is Guid me
            ? query.Where(f => !f.IsPrivate || f.GivenByUserId == me)
            : query.Where(f => !f.IsPrivate);

        return Ok(await query.OrderByDescending(f => f.CreatedAtUtc).ToListAsync(ct));
    }

    [HttpPost("continuous")]
    [HasPermission(PerformancePermissions.Read, PerformancePermissions.Write, PerformancePermissions.Approve, PerformancePermissions.Setup)]
    public async Task<IActionResult> CreateContinuous([FromBody] ContinuousFeedbackRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId   = this.GetUserId();
        var userName = HttpContext.User.FindFirst("FullName")?.Value ?? string.Empty;

        if (string.IsNullOrWhiteSpace(req.Content))
            return BadRequest(new { error = "feedback_content_required", message = "Write the feedback before saving it." });

        // The subject must be someone the caller may see (their data scope) or an active colleague in the caller's own company (peer feedback); a
        // group-level login may give feedback across the group. Anyone else is a 404, the same as a wrong id.
        var subject = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == req.EmployeeId)
            .Select(e => new { e.Id, e.FullName, e.CompanyId })
            .FirstOrDefaultAsync(ct);
        if (subject is null) return NotFound(new { message = "Employee not found." });

        var allowed = this.GetRequestScope().IsGroupLevel
            || await _scopeService.InScopeAsync(User, tenantId, subject.Id, ct);
        if (!allowed)
        {
            var callerEmployeeId = await CallerEmployeeResolver.ResolveAsync(_db, User, tenantId, ct, requireActive: true);
            if (callerEmployeeId is int me && me != subject.Id && subject.CompanyId is Guid subjectCompany)
                allowed = await _db.Employees.AsNoTracking()
                    .AnyAsync(e => e.TenantId == tenantId && e.Id == me && e.CompanyId == subjectCompany, ct);
        }
        if (!allowed) return NotFound(new { message = "Employee not found." });

        if (req.LinkedReviewId is Guid linkedReviewId
            && !await _db.AppraisalReviews.AsNoTracking().AnyAsync(
                r => r.Id == linkedReviewId && r.TenantId == tenantId && r.EmployeeId == subject.Id, ct))
            return BadRequest(new { error = "linked_review_mismatch", message = "The linked review is not this employee's review." });

        _db.ContinuousFeedback.Add(new ContinuousFeedback
        {
            TenantId       = tenantId,
            EmployeeId     = subject.Id,
            EmployeeName   = subject.FullName,
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
    [HasPermission(PerformancePermissions.Read, PerformancePermissions.Write, PerformancePermissions.Approve, PerformancePermissions.Setup)]
    public async Task<IActionResult> List360(Guid reviewId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;

        // The review's subject must be in the caller's scope (the subject, their in-scope manager, or HR of
        // their company). Out of scope is a 404, the same as a wrong id.
        var review = await _scopeService.FindInScopeAsync(User, tenantId,
            _db.AppraisalReviews.AsNoTracking().Where(r => r.Id == reviewId && r.TenantId == tenantId),
            r => r.EmployeeId, ct);
        if (review is null) return NotFound();

        var items = await _db.Feedback360.AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.ReviewId == reviewId)
            .ToListAsync(ct);

        // Same projection, same visibility rule as GET /reviews/{id}: see Feedback360View.
        return Ok(Feedback360View.Project(items, User.HasPermission(PerformancePermissions.Approve)));
    }

    /// <summary>Review statuses that still accept 360 feedback (before the manager review is complete).</summary>
    private static readonly HashSet<string> OpenFor360 = new(StringComparer.Ordinal)
    {
        "Pending", "SelfAssessmentDue", "SelfAssessmentSubmitted", "ManagerReview",
    };

    /// <summary>Roles a reviewer may state for themselves. Manager and Subordinate are facts about the
    /// reporting line, so they are derived, never accepted from the body.</summary>
    private static readonly HashSet<string> SelfDeclaredRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Peer", "CrossFunc", "Customer",
    };

    /// <summary>
    /// Submit 360 feedback AS THE CALLER. There is no nomination model yet, so the rule is: the reviewer is the caller's own linked, active employee
    /// record; the body's ReviewerEmployeeId, if sent, must be that employee; the name is read from the
    /// employee record; and the role is derived from the reporting line where it can be.
    /// </summary>
    [HttpPost("360")]
    [HasPermission(PerformancePermissions.Read, PerformancePermissions.Write, PerformancePermissions.Approve, PerformancePermissions.Setup)]
    public async Task<IActionResult> Submit360([FromBody] Feedback360Request req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;

        var reviewerId = await CallerEmployeeResolver.ResolveAsync(_db, User, tenantId, ct, requireActive: true);
        if (reviewerId is null)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "reviewer_not_linked",
                message = "360 feedback is given as yourself. " + EssLinkGuidance.En,
            });
        if (req.ReviewerEmployeeId != 0 && req.ReviewerEmployeeId != reviewerId.Value)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "reviewer_mismatch",
                message = "You can only submit 360 feedback as yourself.",
            });

        // Ensure the target review belongs to this tenant before accepting feedback.
        var review = await _db.AppraisalReviews.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == req.ReviewId && r.TenantId == tenantId, ct);
        if (review is null) return NotFound(new { message = "Review not found." });
        if (review.EmployeeId == reviewerId.Value)
            return BadRequest(new
            {
                error = "self_feedback",
                message = "You cannot give 360 feedback on your own review. Use the self-assessment instead.",
            });

        // 360 is open only where the cycle asked for it, and only while the review is still being written:
        // feedback arriving after the manager review is complete cannot inform it, but could still be read
        // as if it had.
        var cycle = await _db.PerformanceCycles.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == review.CycleId && c.TenantId == tenantId, ct);
        if (cycle is null || !cycle.Enable360Feedback)
            return Conflict(new { error = "feedback360_not_enabled", message = "This performance cycle does not collect 360 feedback." });
        if (cycle.Status is not ("Active" or "InReview") || !OpenFor360.Contains(review.Status))
            return Conflict(new
            {
                error = "feedback360_closed",
                message = $"360 feedback is closed for this review (review: {review.Status}, cycle: {cycle.Status}).",
            });

        var people = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && (e.Id == reviewerId.Value || e.Id == review.EmployeeId))
            .Select(e => new { e.Id, e.FullName, e.CompanyId, e.ManagerEmployeeId, e.SecondLevelManagerEmployeeId })
            .ToListAsync(ct);
        var reviewer = people.FirstOrDefault(e => e.Id == reviewerId.Value);
        var subject = people.FirstOrDefault(e => e.Id == review.EmployeeId);
        if (reviewer is null) return StatusCode(StatusCodes.Status403Forbidden, new { error = "reviewer_not_linked", message = EssLinkGuidance.En });

        // Without a nomination model the company is the boundary: a reviewer gives 360 feedback inside their
        // own company. A group-level login may review across the group. An employee with no company is in
        // nobody's company, so the check fails closed for them.
        var sameCompany = subject?.CompanyId is Guid subjectCompany && reviewer.CompanyId == subjectCompany;
        if (!sameCompany && !this.GetRequestScope().IsGroupLevel)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "reviewer_outside_company",
                message = "You can give 360 feedback only to colleagues in your own company.",
            });

        var isManager = subject is not null
            && (subject.ManagerEmployeeId == reviewer.Id || subject.SecondLevelManagerEmployeeId == reviewer.Id);
        var isSubordinate = reviewer.ManagerEmployeeId == review.EmployeeId;
        var requested = (req.ReviewerRole ?? string.Empty).Trim();
        string role;
        if (isManager) role = "Manager";
        else if (isSubordinate) role = "Subordinate";
        else if (requested.Length == 0) role = "Peer";
        else if (SelfDeclaredRoles.TryGetValue(requested, out var declared)) role = declared;
        else
            return BadRequest(new
            {
                error = "reviewer_role_mismatch",
                message = $"You are not the reviewee's {requested}. Choose Peer, CrossFunc or Customer.",
            });

        var existing = await _db.Feedback360
            .AnyAsync(f => f.TenantId == tenantId && f.ReviewId == req.ReviewId && f.ReviewerEmployeeId == reviewer.Id, ct);
        if (existing) return Conflict(new { message = "Feedback already submitted for this review." });

        _db.Feedback360.Add(new Feedback360
        {
            TenantId           = tenantId,
            ReviewId           = req.ReviewId,
            ReviewerEmployeeId = reviewer.Id,
            ReviewerName       = reviewer.FullName,
            ReviewerRole       = role,
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

/// <summary>ReviewerEmployeeId and ReviewerName are kept for wire compatibility only: the reviewer is the
/// authenticated caller. A non-zero ReviewerEmployeeId that is not the caller is refused.</summary>
public record Feedback360Request(
    Guid ReviewId, int ReviewerEmployeeId, string? ReviewerName,
    string? ReviewerRole, bool IsAnonymous,
    decimal Score, string? Strengths, string? Improvements, string? Comments);
