using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Recruitment;

[Authorize]
[ApiController]
[Route("api/recruitment/assessments")]
public class AssessmentsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public AssessmentsController(ZayraDbContext db) => _db = db;

    private Guid GetTenantId() =>
        Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;

    private Guid? GetUserId() =>
        Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    private string GetUserName() => User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "System";

    // ── Templates ──────────────────────────────────────────────────────────────

    [HttpGet("templates")]
    public async Task<IActionResult> ListTemplates([FromQuery] bool activeOnly = true, CancellationToken ct = default)
    {
        var tid = GetTenantId();
        var q = _db.AssessmentTemplates.Where(x => x.TenantId == tid && !x.IsDeleted);
        if (activeOnly) q = q.Where(x => x.IsActive);

        var items = await q.OrderBy(x => x.Title).ToListAsync(ct);
        return Ok(items);
    }

    [HttpGet("templates/{id:guid}")]
    public async Task<IActionResult> GetTemplate(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var template = await _db.AssessmentTemplates
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
        if (template == null) return NotFound();

        var questions = await _db.AssessmentQuestions
            .Where(x => x.TenantId == tid && x.TemplateId == id)
            .OrderBy(x => x.OrderIndex).ToListAsync(ct);

        return Ok(new { template, questions });
    }

    [HttpPost("templates")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> CreateTemplate([FromBody] CreateTemplateRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();

        var template = new AssessmentTemplate
        {
            TenantId = tid,
            Code = req.Code,
            Title = req.Title,
            Description = req.Description ?? string.Empty,
            AssessmentType = req.AssessmentType,
            DurationMinutes = req.DurationMinutes,
            PassingScore = req.PassingScore,
            TotalMarks = req.Questions?.Sum(q => q.Marks) ?? 0,
            IsRandomized = req.IsRandomized,
            Audience = req.Audience ?? string.Empty,
            CreatedByUserId = GetUserId(),
        };

        _db.AssessmentTemplates.Add(template);

        if (req.Questions != null)
        {
            for (int i = 0; i < req.Questions.Count; i++)
            {
                var qr = req.Questions[i];
                _db.AssessmentQuestions.Add(new AssessmentQuestion
                {
                    TenantId = tid, TemplateId = template.Id,
                    OrderIndex = i + 1, QuestionType = qr.QuestionType,
                    QuestionText = qr.QuestionText, OptionsJson = qr.OptionsJson ?? "[]",
                    CorrectAnswer = qr.CorrectAnswer ?? string.Empty, Marks = qr.Marks,
                    Difficulty = qr.Difficulty ?? "Medium", SkillTag = qr.SkillTag ?? string.Empty,
                });
            }
        }

        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetTemplate), new { id = template.Id }, template);
    }

    // ── Candidate Assessments ──────────────────────────────────────────────────

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
        var q = _db.CandidateAssessments.Where(x => x.TenantId == tid);

        if (applicationId.HasValue) q = q.Where(x => x.ApplicationId == applicationId.Value);
        if (!string.IsNullOrEmpty(status)) q = q.Where(x => x.Status == status);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    // POST /api/recruitment/assessments/send — Send assessment to candidate
    [HttpPost("send")]
    [Authorize(Roles = "Admin,HR Manager,Recruiter")]
    public async Task<IActionResult> Send([FromBody] SendAssessmentRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();

        var app = await _db.JobApplications.FirstOrDefaultAsync(x => x.Id == req.ApplicationId && x.TenantId == tid, ct);
        if (app == null) return BadRequest("Application not found.");

        var template = await _db.AssessmentTemplates
            .FirstOrDefaultAsync(x => x.Id == req.TemplateId && x.TenantId == tid && !x.IsDeleted, ct);
        if (template == null) return BadRequest("Assessment template not found.");

        var existing = await _db.CandidateAssessments
            .AnyAsync(x => x.TenantId == tid && x.ApplicationId == req.ApplicationId && x.TemplateId == req.TemplateId
                && x.Status != "Expired", ct);
        if (existing) return BadRequest("An assessment for this template is already active for this application.");

        var assessment = new CandidateAssessment
        {
            TenantId = tid,
            ApplicationId = req.ApplicationId,
            CandidateId = app.CandidateId,
            TemplateId = req.TemplateId,
            TemplateName = template.Title,
            Status = "Sent",
            SentAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(req.ExpiryDays > 0 ? req.ExpiryDays : 7),
            TotalMarks = template.TotalMarks,
            AssignedByUserId = GetUserId(),
        };

        _db.CandidateAssessments.Add(assessment);

        _db.ApplicationEvents.Add(new ApplicationEvent
        {
            TenantId = tid, ApplicationId = req.ApplicationId, EventType = "AssessmentSent",
            Stage = app.Stage, Notes = $"Assessment '{template.Title}' sent to candidate",
            PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
        });

        _db.RecruitmentAuditLogs.Add(new RecruitmentAuditLog
        {
            TenantId = tid, EntityType = "Assessment", EntityId = assessment.Id.ToString(),
            Action = "Sent", PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { template.Title, assessment.ExpiresAtUtc }),
        });

        await _db.SaveChangesAsync(ct);
        return Ok(assessment);
    }

    // PATCH /api/recruitment/assessments/{id}/result — Record result (HR submits on behalf)
    [HttpPatch("{id:guid}/result")]
    [Authorize(Roles = "Admin,HR Manager,Recruiter")]
    public async Task<IActionResult> RecordResult(Guid id, [FromBody] RecordResultRequest req, CancellationToken ct)
    {
        var tid = GetTenantId();
        var assessment = await _db.CandidateAssessments
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (assessment == null) return NotFound();

        assessment.Status = "Completed";
        assessment.CompletedAtUtc = DateTime.UtcNow;
        assessment.ScoreObtained = req.ScoreObtained;
        assessment.ScorePercentage = assessment.TotalMarks > 0
            ? (decimal)req.ScoreObtained / assessment.TotalMarks * 100
            : 0;
        assessment.Passed = assessment.ScorePercentage >= (await _db.AssessmentTemplates
            .Where(t => t.Id == assessment.TemplateId).Select(t => (decimal)t.PassingScore).FirstOrDefaultAsync(ct));

        _db.RecruitmentAuditLogs.Add(new RecruitmentAuditLog
        {
            TenantId = tid, EntityType = "Assessment", EntityId = id.ToString(),
            Action = "ResultRecorded", PerformedByUserId = GetUserId(), PerformedByName = GetUserName(),
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { assessment.ScoreObtained, assessment.ScorePercentage, assessment.Passed }),
        });

        await _db.SaveChangesAsync(ct);
        return Ok(assessment);
    }
}

public record CreateTemplateRequest(
    string Code, string Title, string? Description, string AssessmentType,
    int DurationMinutes, int PassingScore, bool IsRandomized, string? Audience,
    List<QuestionRequest>? Questions);

public record QuestionRequest(
    string QuestionType, string QuestionText, string? OptionsJson,
    string? CorrectAnswer, int Marks, string? Difficulty, string? SkillTag);

public record SendAssessmentRequest(Guid ApplicationId, Guid TemplateId, int ExpiryDays);
public record RecordResultRequest(int ScoreObtained);
