using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Recruitment;
using Zayra.Api.Data;

namespace Zayra.Api.Controllers.Recruitment;

[ApiController]
[Route("api/recruitment/ai")]
[Authorize(Roles = "Admin,HR Manager,HR Officer")]
public class RecruitmentAiController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IRecruitmentAiService _ai;

    public RecruitmentAiController(ZayraDbContext db, IRecruitmentAiService ai)
    {
        _db = db;
        _ai = ai;
    }

    /// <summary>Generate a job description (summary/responsibilities/requirements). Returns text only —
    /// the caller decides whether to save it onto an opening.</summary>
    [HttpPost("job-description")]
    public async Task<IActionResult> JobDescription([FromBody] JobDescriptionRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title)) return BadRequest(new { message = "Job title is required." });
        var caller = ResolveCaller();
        if (caller is null) return Unauthorized();
        return Ok(await _ai.GenerateJobDescriptionAsync(caller, req, ct));
    }

    /// <summary>Score & rank the active candidates of an opening against its requirements. Advisory only.</summary>
    [HttpPost("screen")]
    public async Task<IActionResult> Screen([FromBody] ScreenRequest req, CancellationToken ct)
    {
        var caller = ResolveCaller();
        if (caller is null) return Unauthorized();
        var tenantId = caller.TenantId;
        var opening = await _db.JobOpenings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == req.OpeningId && x.TenantId == tenantId, ct);
        if (opening is null) return NotFound(new { message = "Job opening not found." });

        var candidateIds = await _db.JobApplications.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.JobOpeningId == req.OpeningId && a.Status == "Active")
            .Select(a => a.CandidateId).Distinct().ToListAsync(ct);

        var candidates = await _db.Candidates.AsNoTracking()
            .Where(c => c.TenantId == tenantId && candidateIds.Contains(c.Id))
            .Select(c => new CandidateForScreening(
                c.Id, (c.FirstName + " " + c.LastName).Trim(), c.CurrentJobTitle,
                c.TotalExperienceYears, c.EducationLevel, c.Tags))
            .ToListAsync(ct);

        var input = new ScreeningInput(opening.Title, opening.Description, opening.Requirements, candidates);
        return Ok(await _ai.ScreenAsync(caller, input, ct));
    }

    /// <summary>Generate interview questions for an opening (by id) or a free-text title.</summary>
    [HttpPost("interview-questions")]
    public async Task<IActionResult> InterviewQuestions([FromBody] InterviewQuestionsApiRequest req, CancellationToken ct)
    {
        var caller = ResolveCaller();
        if (caller is null) return Unauthorized();

        var title = req.Title;
        var seniority = req.SeniorityLevel;
        if (req.OpeningId is { } oid)
        {
            var opening = await _db.JobOpenings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == oid && x.TenantId == caller.TenantId, ct);
            if (opening is null) return NotFound(new { message = "Job opening not found." });
            title = opening.Title;
        }
        if (string.IsNullOrWhiteSpace(title)) return BadRequest(new { message = "A job title or opening id is required." });
        return Ok(await _ai.GenerateInterviewQuestionsAsync(caller, new InterviewQuestionsRequest(title!, seniority, req.Notes), ct));
    }

    /// <summary>
    /// The tenant/user/role the AI call is billed and audited against. Every model call must
    /// produce a usage/cost record, and the service has no other source for the identity, so a
    /// token without a tenant claim cannot be allowed to spend model budget anonymously.
    /// </summary>
    private RecruitmentAiCaller? ResolveCaller()
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return null;
        var roles = string.Join(",", User.Claims
            .Where(c => c.Type == System.Security.Claims.ClaimTypes.Role)
            .Select(c => c.Value));
        return new RecruitmentAiCaller(tenantId.Value, this.GetUserId(), roles);
    }
}

public record ScreenRequest(Guid OpeningId);
public record InterviewQuestionsApiRequest(Guid? OpeningId, string? Title, string? SeniorityLevel, string? Notes);
