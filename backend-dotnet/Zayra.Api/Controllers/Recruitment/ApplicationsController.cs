using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Recruitment;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Recruitment;

[ApiController]
[Route("api/recruitment/applications")]
[Authorize]
public class ApplicationsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IRecruitmentService _svc;
    private readonly INotificationService _notify;
    private readonly OfferLifecycleService _lifecycle;

    public ApplicationsController(ZayraDbContext db, IRecruitmentService svc, INotificationService notify, OfferLifecycleService? lifecycle = null)
    {
        _db = db; _svc = svc; _notify = notify; _lifecycle = lifecycle ?? new(db, new ConfigurationBuilder().Build());
    }

    private OfferActor Actor(Guid tenant, Guid? company) => new(tenant, company, this.GetUserId() ?? Guid.Empty, UserName());
    private IActionResult LifecycleFailure(OfferLifecycleException e)
    {
        var body = new { error = e.Code, message = e.Message };
        return e.Status switch { 409 => Conflict(body), 404 => NotFound(body), 422 => UnprocessableEntity(body), 400 => BadRequest(body), _ => StatusCode(e.Status, body) };
    }

    // ── Pipeline list ──────────────────────────────────────────────────────────

    [HttpGet]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter")]
    public async Task<IActionResult> List(
        [FromQuery] Guid? jobOpeningId,
        [FromQuery] string? stage,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var tenantId = this.GetTenantId()!.Value;
        var query = _db.JobApplications.Where(a => a.TenantId == tenantId);
        if (jobOpeningId.HasValue) query = query.Where(a => a.JobOpeningId == jobOpeningId.Value);
        if (!string.IsNullOrWhiteSpace(stage)) query = query.Where(a => a.Stage == stage);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(a => a.Status == status);
        else query = query.Where(a => a.Status == "Active");

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(a => a.StageOrder).ThenBy(a => a.AppliedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return Ok(new { items, total, page, pageSize });
    }

    // Kanban — all active apps for an opening grouped by stage
    [HttpGet("kanban/{jobOpeningId:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter")]
    public async Task<IActionResult> Kanban(Guid jobOpeningId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var apps = await _db.JobApplications
            .Where(a => a.TenantId == tenantId && a.JobOpeningId == jobOpeningId)
            .OrderBy(a => a.StageOrder).ThenBy(a => a.AppliedAtUtc)
            .ToListAsync(ct);

        var grouped = RecruitmentStages.Pipeline.Select(s => new
        {
            stage = s.Name,
            order = s.Order,
            applications = apps.Where(a => a.Stage == s.Name).ToList(),
        });

        var rejected = apps.Where(a => a.Status is "Rejected" or "Withdrawn").ToList();
        return Ok(new { stages = grouped, rejected });
    }

    [HttpGet("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var app = await _db.JobApplications.FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, ct);
        if (app is null) return NotFound();

        var events = await _db.ApplicationEvents
            .Where(e => e.TenantId == tenantId && e.ApplicationId == id)
            .OrderBy(e => e.CreatedAtUtc).ToListAsync(ct);

        var interviews = await _db.InterviewSchedules
            .Where(i => i.TenantId == tenantId && i.ApplicationId == id)
            .OrderBy(i => i.ScheduledAt).ToListAsync(ct);

        var offer = await _db.OfferLetters
            .Where(o => o.TenantId == tenantId && o.ApplicationId == id)
            .OrderByDescending(o => o.GeneratedAtUtc).FirstOrDefaultAsync(ct);

        var candidate = await _db.Candidates
            .FirstOrDefaultAsync(c => c.Id == app.CandidateId && c.TenantId == tenantId, ct);

        return Ok(new { application = app, candidate, events, interviews, offer });
    }

    // ── Create application ─────────────────────────────────────────────────────

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Apply([FromBody] ApplyRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;

        var opening = await _db.JobOpenings
            .FirstOrDefaultAsync(j => j.Id == req.JobOpeningId && j.TenantId == tenantId, ct);
        if (opening is null) return BadRequest(new { message = "Job opening not found." });
        if (opening.Status is "Closed" or "Cancelled") return BadRequest(new { message = "This opening is no longer accepting applications." });

        var candidate = await _db.Candidates
            .FirstOrDefaultAsync(c => c.Id == req.CandidateId && c.TenantId == tenantId, ct);
        if (candidate is null) return BadRequest(new { message = "Candidate not found." });

        var exists = await _db.JobApplications
            .AnyAsync(a => a.TenantId == tenantId && a.JobOpeningId == req.JobOpeningId && a.CandidateId == req.CandidateId, ct);
        if (exists) return Conflict(new { message = "This candidate has already applied for this opening." });

        var app = new JobApplication
        {
            TenantId = tenantId,
            CompanyId = candidate.CompanyId, // inherit legal entity from parent candidate
            JobOpeningId = req.JobOpeningId,
            JobTitle = opening.Title,
            CandidateId = req.CandidateId,
            CandidateName = $"{candidate.FirstName} {candidate.LastName}".Trim(),
            CandidateEmail = candidate.Email,
            Stage = "Applied",
            StageOrder = 1,
            Status = "Active",
        };
        _db.JobApplications.Add(app);

        await LogEventAsync(tenantId, app.Id, "ApplicationCreated", "Applied",
            req.Notes ?? "Application created.", null, "HR", ct);

        if (opening.Status == "Open") { opening.Status = "InProgress"; }

        await _db.SaveChangesAsync(ct);
        return Created($"/api/recruitment/applications/{app.Id}", app);
    }

    // ── Advance stage ──────────────────────────────────────────────────────────

    [HttpPost("{id:guid}/advance")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Advance(Guid id, [FromBody] AdvanceRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId = this.GetUserId();
        var app = await _db.JobApplications.FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, ct);
        if (app is null) return NotFound();
        try { app = await _lifecycle.ChangeApplicationAsync(Actor(tenantId, app.CompanyId), id, req.ExpectedVersion, "Advance", req.Notes ?? "Advanced application stage.", ct); }
        catch (OfferLifecycleException e) { return LifecycleFailure(e); }

        await _notify.NotifyAsync(tenantId, null,
            "Application Advanced",
            $"{app.CandidateName} moved to {app.Stage} stage for {app.JobTitle}.",
            "JobApplication", app.Id.ToString(), ct);

        return Ok(app);
    }

    // ── Reject application ─────────────────────────────────────────────────────

    [HttpPost("{id:guid}/reject")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectApplicationRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId = this.GetUserId();
        var app = await _db.JobApplications.FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, ct);
        if (app is null) return NotFound();
        try { app = await _lifecycle.ChangeApplicationAsync(Actor(tenantId, app.CompanyId), id, req.ExpectedVersion, "Reject", req.Reason, ct); }
        catch (OfferLifecycleException e) { return LifecycleFailure(e); }
        return Ok(app);
    }

    // ── Add note ───────────────────────────────────────────────────────────────

    [HttpPost("{id:guid}/notes")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> AddNote(Guid id, [FromBody] NoteRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId = this.GetUserId();
        var app = await _db.JobApplications.FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, ct);
        if (app is null) return NotFound();

        await LogEventAsync(tenantId, app.Id, "NoteAdded", app.Stage, req.Notes, userId, req.PerformedByName ?? "HR", ct);
        await _db.SaveChangesAsync(ct);
        return Ok();
    }

    // ── Schedule interview ─────────────────────────────────────────────────────

    [HttpPost("{id:guid}/interviews")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> ScheduleInterview(Guid id, [FromBody] ScheduleInterviewRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId = this.GetUserId();
        var app = await _db.JobApplications.FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, ct);
        if (app is null) return NotFound();

        var interview = new InterviewSchedule
        {
            TenantId = tenantId,
            ApplicationId = id,
            InterviewType = req.InterviewType,
            InterviewerNames = req.InterviewerNames,
            ScheduledAt = req.ScheduledAt,
            DurationMinutes = req.DurationMinutes,
            Mode = req.Mode,
            MeetingLink = req.MeetingLink ?? string.Empty,
            Location = req.Location ?? string.Empty,
        };
        _db.InterviewSchedules.Add(interview);

        await LogEventAsync(tenantId, id, "InterviewScheduled", app.Stage,
            $"{req.InterviewType} interview scheduled for {req.ScheduledAt:dd MMM yyyy HH:mm} via {req.Mode}.",
            userId, "HR", ct);

        await _db.SaveChangesAsync(ct);

        await _notify.NotifyAsync(tenantId, null,
            "Interview Scheduled",
            $"{app.CandidateName} — {req.InterviewType} scheduled on {req.ScheduledAt:dd MMM yyyy}.",
            "InterviewSchedule", interview.Id.ToString(), ct);

        return Created($"/api/recruitment/interviews/{interview.Id}", interview);
    }

    [HttpPost("interviews/{interviewId:guid}/feedback")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager")]
    public async Task<IActionResult> RecordFeedback(Guid interviewId, [FromBody] InterviewFeedbackRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId = this.GetUserId();
        var iv = await _db.InterviewSchedules
            .FirstOrDefaultAsync(i => i.Id == interviewId && i.TenantId == tenantId, ct);
        if (iv is null) return NotFound();

        iv.Status = "Completed";
        iv.OverallRating = req.OverallRating;
        iv.Recommendation = req.Recommendation;
        iv.FeedbackNotes = req.FeedbackNotes;
        iv.CompletedAt = DateTime.UtcNow;

        await LogEventAsync(tenantId, iv.ApplicationId, "FeedbackRecorded", string.Empty,
            $"{iv.InterviewType} feedback: Rating {req.OverallRating}/5 — {req.Recommendation}. {req.FeedbackNotes}",
            userId, "HR", ct);

        await _db.SaveChangesAsync(ct);
        return Ok(iv);
    }

    // ── Offer letter ───────────────────────────────────────────────────────────

    [HttpPost("{id:guid}/offer")]
    [Authorize(Roles = "Admin,HR Manager,Recruiter")]
    [OfferAudience(OfferAudienceRequirement.Author)]
    public async Task<IActionResult> GenerateOffer(Guid id, [FromBody] GenerateOfferRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId = this.GetUserId();
        var app = await _db.JobApplications.FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, ct);
        if (app is null) return NotFound();
        if (app.Stage != "Offer") return BadRequest(new { message = "Application must be in Offer stage to generate an offer letter." });
        if (req.Components is not null && !_lifecycle.Enabled) return Conflict(new { error = "managed_offer_lifecycle_disabled", message = "Managed component offers are not enabled for this deployment." });

        try
        {
            var offer = await _lifecycle.CreateAsync(Actor(tenantId, app.CompanyId), id,
                new(req.StartDate, req.ProbationMonths, req.Designation, req.Department, req.DepartmentId, req.DesignationId,
                    req.ResponseDeadline, req.TemplateId, req.Components, req.BasicSalary, req.HousingAllowance,
                    req.TransportAllowance, req.OtherAllowances, ExpectedApplicationVersion: req.ExpectedApplicationVersion), ct);
            return Created($"/api/recruitment/offers/{offer.Id}", offer);
        }
        catch (OfferLifecycleException e) { return LifecycleFailure(e); }
    }

    [HttpPost("offers/{offerId:guid}/send")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> SendOffer(Guid offerId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId = this.GetUserId();
        var offer = await _db.OfferLetters.FirstOrDefaultAsync(o => o.Id == offerId && o.TenantId == tenantId, ct);
        if (offer is null) return NotFound();
        return Conflict(new { error = "explicit_delivery_required", message = "This endpoint no longer records delivery. Review a frozen managed revision and use /api/recruitment/offers/{id}/email or /external-sends. Existing sent offers remain readable." });
    }

    [HttpPost("offers/{offerId:guid}/accept")]
    [Authorize(Roles = "Admin,HR Manager")]
    [OfferAudience(OfferAudienceRequirement.Manager)]
    public async Task<IActionResult> AcceptOffer(Guid offerId, CancellationToken ct,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] OfferResponseRequest? request = null)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId = this.GetUserId()!.Value;
        var offer = await _db.OfferLetters.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == offerId && o.TenantId == tenantId, ct);
        if (offer is null) return NotFound();
        if (request is null || request.Decision != "Accepted") return Conflict(new { error = "explicit_response_required", message = "Submit expectedVersion, decision Accepted, source StaffRecorded, occurredAtUtc and reason, or use /api/recruitment/offers/{id}/responses." });
        OfferResponseDto acceptance;
        try { acceptance = await _lifecycle.RespondAsync(Actor(tenantId, offer.CompanyId), offerId, request, ct); }
        catch (OfferLifecycleException e) { return LifecycleFailure(e); }
        offer = await _db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == offerId && o.TenantId == tenantId, ct);
        if (!acceptance.AlreadyRecorded)
        {
            await _notify.NotifyAsync(tenantId, null,
                "Offer Accepted — Onboarding Started",
                $"{offer.CandidateName} accepted the offer for {offer.OfferedJobTitle}. Employee draft created.",
                "OfferLetter", offer.Id.ToString(), ct);
        }

        return Ok(new
        {
            offer,
            onboardingDraftId = acceptance.OnboardingDraftId,
            alreadyAccepted = acceptance.AlreadyRecorded,
        });
    }

    [HttpPost("offers/{offerId:guid}/decline")]
    [Authorize(Roles = "Admin,HR Manager")]
    [OfferAudience(OfferAudienceRequirement.Manager)]
    public async Task<IActionResult> DeclineOffer(Guid offerId, [FromBody] DeclineOfferRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var userId = this.GetUserId();
        var offer = await _db.OfferLetters.FirstOrDefaultAsync(o => o.Id == offerId && o.TenantId == tenantId, ct);
        if (offer is null) return NotFound();
        if (req.Response() is not { } response) return Conflict(new { error = "explicit_response_required", message = "Submit expectedVersion, decision Declined, source StaffRecorded, occurredAtUtc and reason, or use /api/recruitment/offers/{id}/responses." });
        try { await _lifecycle.RespondAsync(Actor(tenantId, offer.CompanyId), offerId, response, ct); }
        catch (OfferLifecycleException e) { return LifecycleFailure(e); }
        return Ok(await _db.OfferLetters.AsNoTracking().SingleAsync(o => o.Id == offerId && o.TenantId == tenantId, ct));
    }

    [HttpGet("offers/{offerId:guid}/html")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter")]
    public async Task<IActionResult> GetOfferHtml(Guid offerId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var offer = await _db.OfferLetters.FirstOrDefaultAsync(o => o.Id == offerId && o.TenantId == tenantId, ct);
        if (offer is null) return NotFound();
        // Defense-in-depth: never render stored offer HTML inline on the SPA origin (where
        // JWTs live in localStorage). Force a download; the API-origin CSP (script-src 'self')
        // additionally neutralises any injected inline script in stored ContentHtml. The
        // frontend uses the authenticated PDF download path, not this endpoint.
        Response.Headers["Content-Disposition"] = $"attachment; filename=\"offer-{offerId}.html\"";
        return Content(offer.ContentHtml, "text/html");
    }

    // ── Shared helpers ─────────────────────────────────────────────────────────

    private string UserName() => User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "HR";

    private Task LogEventAsync(Guid tenantId, Guid applicationId, string eventType, string stage,
        string notes, Guid? userId, string performedByName, CancellationToken ct)
    {
        _db.ApplicationEvents.Add(new ApplicationEvent
        {
            TenantId = tenantId,
            ApplicationId = applicationId,
            EventType = eventType,
            Stage = stage,
            Notes = notes,
            PerformedByUserId = userId,
            PerformedByName = performedByName,
        });
        return Task.CompletedTask;
    }
}

// ── Request DTOs ───────────────────────────────────────────────────────────────

public record ApplyRequest(Guid JobOpeningId, Guid CandidateId, string? Notes);

public record AdvanceRequest(string? Notes, string? PerformedByName, long? ExpectedVersion = null);

public record RejectApplicationRequest(string Reason, string? PerformedByName, long? ExpectedVersion = null);

public record NoteRequest(string Notes, string? PerformedByName);

public record InterviewFeedbackRequest(int OverallRating, string Recommendation, string FeedbackNotes);

/// <summary>Department and designation are resolved against the organisation's records when the offer
/// is generated (<see cref="Zayra.Api.Infrastructure.Recruitment.OfferPlacement"/>). An id wins over a
/// name. Left empty, they default to the job opening's department and designation.</summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public record GenerateOfferRequest(
    string? Department, DateOnly StartDate,
    decimal? BasicSalary, decimal? HousingAllowance, decimal? TransportAllowance, decimal? OtherAllowances,
    int ProbationMonths,
    Guid? DepartmentId = null, Guid? DesignationId = null, string? Designation = null, Guid? TemplateId = null,
    IReadOnlyList<Zayra.Api.Application.Recruitment.OfferComponentInput>? Components = null,
    long? ExpectedApplicationVersion = null, DateTime? ResponseDeadline = null);
