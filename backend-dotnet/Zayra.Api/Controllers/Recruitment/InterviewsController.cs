using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Recruitment;

[Authorize]
[ApiController]
[Route("api/recruitment/interviews")]
public class InterviewsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public InterviewsController(ZayraDbContext db) => _db = db;

    private Guid GetTenantId() =>
        Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;

    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    private string GetUserName() => User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "System";

    // GET /api/recruitment/interviews?applicationId=...&status=...
    [HttpGet]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter")]
    public async Task<IActionResult> List(
        [FromQuery] Guid? applicationId = null,
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var q = _db.InterviewSchedules.Where(x => x.TenantId == tid);

        if (applicationId.HasValue) q = q.Where(x => x.ApplicationId == applicationId.Value);
        if (!string.IsNullOrEmpty(status)) q = q.Where(x => x.Status == status);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.ScheduledAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    // GET /api/recruitment/interviews/{id}
    [HttpGet("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter,Manager")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var interview = await _db.InterviewSchedules
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (interview == null) return NotFound();

        var feedbacks = await _db.InterviewFeedbacks
            .Where(x => x.TenantId == tid && x.InterviewScheduleId == id)
            .ToListAsync(ct);

        return Ok(new { interview, feedbacks });
    }

    // POST /api/recruitment/interviews
    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager,Recruiter")]
    public async Task<IActionResult> Schedule([FromBody] ScheduleInterviewRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();

        var app = await _db.JobApplications.FirstOrDefaultAsync(x => x.Id == req.ApplicationId && x.TenantId == tid, ct);
        if (app == null) return BadRequest("Application not found.");

        var interview = new InterviewSchedule
        {
            TenantId = tid,
            ApplicationId = req.ApplicationId,
            InterviewType = req.InterviewType,
            InterviewerNames = req.InterviewerNames,
            ScheduledAt = req.ScheduledAt,
            DurationMinutes = req.DurationMinutes,
            Mode = req.Mode,
            MeetingLink = req.MeetingLink ?? string.Empty,
            Location = req.Location ?? string.Empty,
        };

        _db.InterviewSchedules.Add(interview);

        _db.ApplicationEvents.Add(new ApplicationEvent
        {
            TenantId = tid, ApplicationId = req.ApplicationId,
            EventType = "InterviewScheduled", Stage = app.Stage,
            Notes = $"{req.InterviewType} interview scheduled for {req.ScheduledAt:yyyy-MM-dd HH:mm}",
            PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
        });

        _db.RecruitmentAuditLogs.Add(new RecruitmentAuditLog
        {
            TenantId = tid, EntityType = "Interview", EntityId = interview.Id.ToString(),
            Action = "Scheduled", PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { req.InterviewType, req.ScheduledAt }),
        });

        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = interview.Id }, interview);
    }

    // PATCH /api/recruitment/interviews/{id}/complete
    [HttpPatch("{id:guid}/complete")]
    [Authorize(Roles = "Admin,HR Manager,Recruiter")]
    public async Task<IActionResult> Complete(Guid id, [FromBody] CompleteInterviewRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var interview = await _db.InterviewSchedules
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (interview == null) return NotFound();

        interview.Status = "Completed";
        interview.OverallRating = req.OverallRating;
        interview.Recommendation = req.Recommendation;
        interview.FeedbackNotes = req.FeedbackNotes ?? string.Empty;
        interview.CompletedAt = DateTime.UtcNow;

        _db.ApplicationEvents.Add(new ApplicationEvent
        {
            TenantId = tid, ApplicationId = interview.ApplicationId,
            EventType = "FeedbackRecorded", Stage = string.Empty,
            Notes = $"Interview completed — {req.Recommendation}",
            PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
        });

        await _db.SaveChangesAsync(ct);
        return Ok(interview);
    }

    // PATCH /api/recruitment/interviews/{id}/cancel
    [HttpPatch("{id:guid}/cancel")]
    [Authorize(Roles = "Admin,HR Manager,Recruiter")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var interview = await _db.InterviewSchedules
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (interview == null) return NotFound();

        interview.Status = "Cancelled";
        await _db.SaveChangesAsync(ct);
        return Ok(interview);
    }

    // POST /api/recruitment/interviews/{id}/feedback
    [HttpPost("{id:guid}/feedback")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter,Manager")]
    public async Task<IActionResult> SubmitFeedback(Guid id, [FromBody] SubmitFeedbackRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var interview = await _db.InterviewSchedules
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (interview == null) return NotFound();

        // Assigned-interviewer guard: a panel-only interviewer (Manager, not an HR coordinator)
        // may submit feedback ONLY for an interview they are assigned to — prevents tenant-wide
        // feedback injection into arbitrary interview ids. HR coordinators (Admin/HR Manager/
        // HR Officer/Recruiter) run panels and may record on behalf of any interviewer.
        var isHrCoordinator = User.IsInRole("Admin") || User.IsInRole("HR Manager")
            || User.IsInRole("HR Officer") || User.IsInRole("Recruiter");
        if (!isHrCoordinator)
        {
            var me = GetUserName();
            var assigned = !string.IsNullOrWhiteSpace(me)
                && interview.InterviewerNames.Contains(me, StringComparison.OrdinalIgnoreCase);
            if (!assigned)
                return Forbid();
        }

        var feedback = new InterviewFeedback
        {
            TenantId = tid,
            InterviewScheduleId = id,
            ApplicationId = interview.ApplicationId,
            InterviewerUserId = GetUserId(),
            InterviewerName = req.InterviewerName ?? GetUserName(),
            InterviewerRole = req.InterviewerRole ?? string.Empty,
            CommunicationScore = req.CommunicationScore,
            TechnicalScore = req.TechnicalScore,
            CultureFitScore = req.CultureFitScore,
            ProblemSolvingScore = req.ProblemSolvingScore,
            LeadershipScore = req.LeadershipScore,
            OverallScore = req.OverallScore,
            Strengths = req.Strengths ?? string.Empty,
            Concerns = req.Concerns ?? string.Empty,
            Notes = req.Notes ?? string.Empty,
            Recommendation = req.Recommendation,
        };

        _db.InterviewFeedbacks.Add(feedback);
        await _db.SaveChangesAsync(ct);
        return Ok(feedback);
    }

    // GET /api/recruitment/interviews/{id}/feedback
    [HttpGet("{id:guid}/feedback")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter")]
    public async Task<IActionResult> GetFeedback(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var feedbacks = await _db.InterviewFeedbacks
            .Where(x => x.TenantId == tid && x.InterviewScheduleId == id)
            .ToListAsync(ct);
        return Ok(feedbacks);
    }
}

public record ScheduleInterviewRequest(
    Guid ApplicationId, string InterviewType, string InterviewerNames,
    DateTime ScheduledAt, int DurationMinutes, string Mode,
    string? MeetingLink, string? Location);

public record CompleteInterviewRequest(
    int OverallRating, string Recommendation, string? FeedbackNotes);

public record SubmitFeedbackRequest(
    string? InterviewerName, string? InterviewerRole,
    int CommunicationScore, int TechnicalScore, int CultureFitScore,
    int ProblemSolvingScore, int LeadershipScore, int OverallScore,
    string? Strengths, string? Concerns, string? Notes, string Recommendation);
