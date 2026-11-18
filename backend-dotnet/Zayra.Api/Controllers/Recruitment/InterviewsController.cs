using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Recruitment;
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
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var tid = GetTenantId();
        var q = _db.InterviewSchedules.Where(x => x.TenantId == tid);

        if (applicationId.HasValue) q = q.Where(x => x.ApplicationId == applicationId.Value);
        if (!string.IsNullOrEmpty(status)) q = q.Where(x => x.Status == status);

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.ScheduledAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        // The list names the candidate and the job itself, so the screen needs no second lookup of every application.
        var applicationIds = rows.Select(r => r.ApplicationId).Distinct().ToList();
        var applications = await _db.JobApplications.AsNoTracking()
            .Where(a => a.TenantId == tid && applicationIds.Contains(a.Id))
            .Select(a => new { a.Id, a.CandidateName, a.JobTitle })
            .ToDictionaryAsync(a => a.Id, ct);
        var items = rows.Select(r => new InterviewListItem(r,
            applications.TryGetValue(r.ApplicationId, out var a) ? a.CandidateName : string.Empty,
            applications.TryGetValue(r.ApplicationId, out var b) ? b.JobTitle : string.Empty)).ToList();

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

        // A line manager (narrow key only) reads an interview they are named on, and only their own scorecard on it: never a peer's.
        var narrow = !InterviewPanel.IsCoordinator(User) && !User.HasClaim("permission", "recruitment.read");
        if (narrow && await InterviewPanel.StandingAsync(_db, tid, interview, GetUserId(), GetUserName(), ct) != PanelStanding.Assigned) return NotFound();

        var feedbackQuery = _db.InterviewFeedbacks.Where(x => x.TenantId == tid && x.InterviewScheduleId == id);
        if (narrow) { var me = GetUserId(); feedbackQuery = feedbackQuery.Where(x => x.InterviewerUserId == me); }
        var feedbacks = await feedbackQuery.ToListAsync(ct);

        // A panellist sees the interview's logistics and their own scorecard only: the schedule's shared verdict (overall rating, recommendation and
        // notes) was written by HR or a peer, so it is not part of what they are shown.
        if (narrow)
            return Ok(new
            {
                interview = new
                {
                    interview.Id, interview.ApplicationId, interview.InterviewType, interview.ScheduledAt, interview.DurationMinutes, interview.Mode,
                    interview.MeetingLink, interview.Location, interview.Status,
                },
                feedbacks,
            });
        return Ok(new { interview, feedbacks });
    }

    // GET /api/recruitment/interviews/mine : the interviews the caller is named on, for a line manager on a panel. Names the candidate and the
    // job only; no contact details, resume or peer feedback.
    [HttpGet("mine")]
    [HasPermission("recruitment.read", "recruitment.interview.feedback")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var tid = GetTenantId();
        var name = GetUserName();
        if (string.IsNullOrWhiteSpace(name)) return Ok(new { total = 0, items = Array.Empty<object>() });
        var me = GetUserId();
        var meText = (me ?? Guid.Empty).ToString();
        var normalized = InterviewPanel.Normalize(name);
        // Stored panel text may space or case a name differently, so SQL only narrows by the name's first and last words; each candidate is then decided exactly.
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var firstWord = words.FirstOrDefault() ?? normalized; var lastWord = words.LastOrDefault() ?? normalized;
        // Candidates are narrowed in SQL (their stored ids hold this login, or an interview with no ids names it somewhere); each is then decided exactly.
        var candidates = await _db.InterviewSchedules.AsNoTracking()
            .Where(x => x.TenantId == tid && x.Status != "Cancelled"
                && ((x.InterviewerUserIds != null && x.InterviewerUserIds.Contains(meText))
                    || ((x.InterviewerUserIds == null || x.InterviewerUserIds == "") && x.InterviewerNames.ToLower().Contains(firstWord) && x.InterviewerNames.ToLower().Contains(lastWord))))
            .OrderByDescending(x => x.ScheduledAt).Take(300).ToListAsync(ct);
        var rows = new List<InterviewSchedule>();
        foreach (var candidate in candidates)
            if (await InterviewPanel.StandingAsync(_db, tid, candidate, me, name, ct) == PanelStanding.Assigned) rows.Add(candidate);
        rows = rows.Take(100).ToList();
        var applicationIds = rows.Select(r => r.ApplicationId).Distinct().ToList();
        var applications = await _db.JobApplications.AsNoTracking()
            .Where(a => a.TenantId == tid && applicationIds.Contains(a.Id))
            .Select(a => new { a.Id, a.CandidateName, a.JobTitle }).ToDictionaryAsync(a => a.Id, ct);
        var items = rows.Select(r => new
        {
            r.Id, r.ApplicationId, r.InterviewType, r.ScheduledAt, r.DurationMinutes, r.Mode, r.Location, r.MeetingLink, r.Status,
            candidateName = applications.TryGetValue(r.ApplicationId, out var a) ? a.CandidateName : string.Empty,
            jobTitle = applications.TryGetValue(r.ApplicationId, out var b) ? b.JobTitle : string.Empty,
        }).ToList();
        return Ok(new { total = items.Count, items });
    }

    // POST /api/recruitment/interviews
    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager,Recruiter")]
    public async Task<IActionResult> Schedule([FromBody] ScheduleInterviewRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();

        var app = await _db.JobApplications.FirstOrDefaultAsync(x => x.Id == req.ApplicationId && x.TenantId == tid, ct);
        if (app == null) return BadRequest("Application not found.");
        if (await CandidateStanding.IsBlacklistedAsync(_db, tid, app.CandidateId, ct))
            return Conflict(new { error = CandidateStanding.ErrorCode, message = CandidateStanding.Message, messageAr = CandidateStanding.MessageAr });

        var interview = new InterviewSchedule
        {
            TenantId = tid,
            CompanyId = app.CompanyId,
            ApplicationId = req.ApplicationId,
            InterviewType = req.InterviewType,
            InterviewerNames = req.InterviewerNames,
            InterviewerUserIds = await InterviewPanel.ResolveIdsAsync(_db, tid, req.InterviewerNames, req.InterviewerUserIds, ct, app.CompanyId),
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
        if (!InterviewPanel.IsCoordinator(User))
        {
            switch (await InterviewPanel.StandingAsync(_db, tid, interview, GetUserId(), GetUserName(), ct))
            {
                case PanelStanding.AmbiguousName:
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        error = "interviewer_name_ambiguous",
                        message = "More than one person in your company has your name, so this interview cannot tell which of you is on the panel. Ask HR to add you to the panel by your login.",
                        messageAr = "أكثر من شخص في شركتك يحمل اسمك، لذلك لا يمكن للمقابلة التمييز بينكم في لجنتها. اطلب من الموارد البشرية إضافتك إلى اللجنة بحسابك.",
                    });
                case PanelStanding.NotAssigned: return Forbid();
            }
        }

        var feedback = new InterviewFeedback
        {
            TenantId = tid,
            CompanyId = interview.CompanyId,
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
    string? MeetingLink, string? Location, Guid[]? InterviewerUserIds = null);

public record CompleteInterviewRequest(
    int OverallRating, string Recommendation, string? FeedbackNotes);

public record SubmitFeedbackRequest(
    string? InterviewerName, string? InterviewerRole,
    int CommunicationScore, int TechnicalScore, int CultureFitScore,
    int ProblemSolvingScore, int LeadershipScore, int OverallScore,
    string? Strengths, string? Concerns, string? Notes, string Recommendation);

/// <summary>An interview as the list returns it: the schedule's own fields (as before), plus who is being interviewed and for which job.</summary>
public sealed record InterviewListItem([property: System.Text.Json.Serialization.JsonIgnore] InterviewSchedule Schedule, string CandidateName, string JobTitle)
{
    // Flattened onto the schedule's own JSON so existing readers of the list keep every field they had.
    public Guid Id => Schedule.Id;
    public Guid ApplicationId => Schedule.ApplicationId;
    public string InterviewType => Schedule.InterviewType;
    public string InterviewerNames => Schedule.InterviewerNames;
    public DateTime ScheduledAt => Schedule.ScheduledAt;
    public int DurationMinutes => Schedule.DurationMinutes;
    public string Mode => Schedule.Mode;
    public string MeetingLink => Schedule.MeetingLink;
    public string Location => Schedule.Location;
    public string Status => Schedule.Status;
    public int? OverallRating => Schedule.OverallRating;
    public string Recommendation => Schedule.Recommendation;
    public string FeedbackNotes => Schedule.FeedbackNotes;
    public DateTime? CompletedAt => Schedule.CompletedAt;
    public DateTime CreatedAtUtc => Schedule.CreatedAtUtc;
}
