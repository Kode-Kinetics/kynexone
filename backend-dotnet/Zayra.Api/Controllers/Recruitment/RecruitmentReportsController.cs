using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Controllers.Recruitment;

[Authorize]
[ApiController]
[Route("api/recruitment/reports")]
public class RecruitmentReportsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public RecruitmentReportsController(ZayraDbContext db) => _db = db;

    private Guid GetTenantId() =>
        Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;

    // GET /api/recruitment/reports/pipeline-summary
    [HttpGet("pipeline-summary")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter")]
    public async Task<IActionResult> PipelineSummary(CancellationToken ct)
    {
        var tid = GetTenantId();

        var byStage = await _db.JobApplications
            .Where(x => x.TenantId == tid && x.Status == "Active")
            .GroupBy(x => x.Stage)
            .Select(g => new { stage = g.Key, count = g.Count() })
            .ToListAsync(ct);

        var byOpening = await _db.JobApplications
            .Where(x => x.TenantId == tid && x.Status == "Active")
            .GroupBy(x => new { x.JobOpeningId, x.JobTitle })
            .Select(g => new { openingId = g.Key.JobOpeningId, title = g.Key.JobTitle, total = g.Count() })
            .OrderByDescending(x => x.total).Take(10)
            .ToListAsync(ct);

        var hiredThisMonth = await _db.JobApplications
            .CountAsync(x => x.TenantId == tid && x.Stage == "Hired"
                && x.HiredAtUtc.HasValue
                && x.HiredAtUtc.Value.Year == DateTime.UtcNow.Year
                && x.HiredAtUtc.Value.Month == DateTime.UtcNow.Month, ct);

        return Ok(new { byStage, byOpening, hiredThisMonth });
    }

    // GET /api/recruitment/reports/time-to-hire
    [HttpGet("time-to-hire")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter")]
    public async Task<IActionResult> TimeToHire(
        [FromQuery] int year = 0,
        [FromQuery] int month = 0,
        CancellationToken ct = default)
    {
        var tid = GetTenantId();
        if (year == 0) year = DateTime.UtcNow.Year;

        var hiredApps = await _db.JobApplications
            .Where(x => x.TenantId == tid && x.Stage == "Hired"
                && x.HiredAtUtc.HasValue
                && x.HiredAtUtc.Value.Year == year)
            .ToListAsync(ct);

        if (month > 0) hiredApps = hiredApps.Where(a => a.HiredAtUtc!.Value.Month == month).ToList();

        var avgDaysToHire = hiredApps.Count > 0
            ? hiredApps.Average(a => (a.HiredAtUtc!.Value - a.AppliedAtUtc).TotalDays)
            : 0;

        return Ok(new
        {
            year, month,
            hiredCount = hiredApps.Count,
            avgDaysToHire = Math.Round(avgDaysToHire, 1),
        });
    }

    // GET /api/recruitment/reports/source-effectiveness
    [HttpGet("source-effectiveness")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter")]
    public async Task<IActionResult> SourceEffectiveness(CancellationToken ct)
    {
        var tid = GetTenantId();

        var candidatesBySource = await _db.Candidates
            .Where(x => x.TenantId == tid)
            .GroupBy(x => x.Source)
            .Select(g => new { source = g.Key, total = g.Count() })
            .ToListAsync(ct);

        // Hired by joining candidate source through applications
        var hiredBySource = await _db.JobApplications
            .Where(x => x.TenantId == tid && x.Stage == "Hired")
            .Join(_db.Candidates.Where(c => c.TenantId == tid),
                a => a.CandidateId, c => c.Id,
                (a, c) => c.Source)
            .GroupBy(s => s)
            .Select(g => new { source = g.Key, hired = g.Count() })
            .ToListAsync(ct);

        return Ok(new { candidatesBySource, hiredBySource });
    }

    // GET /api/recruitment/reports/open-positions
    [HttpGet("open-positions")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter")]
    public async Task<IActionResult> OpenPositions(CancellationToken ct)
    {
        var tid = GetTenantId();

        var rows = await _db.JobOpenings
            .Where(x => x.TenantId == tid && x.Status == "Open")
            .Select(o => new
            {
                o.Id, o.JobCode, o.Title, o.DepartmentName, o.HeadCount, o.FilledCount,
                remaining = o.HeadCount - o.FilledCount,
                o.CreatedAtUtc,
            })
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        var openings = rows.Select(o => new
        {
            o.Id, o.JobCode, o.Title, o.DepartmentName, o.HeadCount, o.FilledCount, o.remaining,
            daysSincePosted = (DateTime.UtcNow - o.CreatedAtUtc).TotalDays,
        }).ToList();

        return Ok(new { total = openings.Count, openings });
    }

    // GET /api/recruitment/reports/ai-insights
    [HttpGet("ai-insights")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> AIInsights(CancellationToken ct)
    {
        var tid = GetTenantId();

        // Compute bottleneck detection — stage with the most applications stuck > 7 days
        var now = DateTime.UtcNow;
        var stuckCutoff = now.AddDays(-7);
        var stageAge = await _db.JobApplications
            .Where(x => x.TenantId == tid && x.Status == "Active"
                && x.StageChangedAtUtc.HasValue
                && x.StageChangedAtUtc.Value < stuckCutoff)
            .GroupBy(x => x.Stage)
            .Select(g => new { stage = g.Key, stuckCount = g.Count() })
            .OrderByDescending(g => g.stuckCount)
            .ToListAsync(ct);

        // Drop-off prediction: applications in Screening that are > 14 days old
        var dropOffCutoff = now.AddDays(-14);
        var dropOffRisk = await _db.JobApplications
            .CountAsync(x => x.TenantId == tid && x.Stage == "Screening" && x.Status == "Active"
                && x.StageChangedAtUtc.HasValue
                && x.StageChangedAtUtc.Value < dropOffCutoff, ct);

        // Demand forecast: approved requisitions without openings
        var unresolvedReqs = await _db.ManpowerRequisitions
            .CountAsync(x => x.TenantId == tid && x.Status == "Approved", ct);

        var insights = new List<object>
        {
            new { type = "Bottleneck", severity = stageAge.FirstOrDefault()?.stuckCount > 5 ? "High" : "Medium",
                title = "Pipeline Bottleneck Detected",
                description = stageAge.Count > 0
                    ? $"[ADVISORY] {stageAge.First().stuckCount} applications stuck in '{stageAge.First().stage}' stage for >7 days. Review and advance or reject."
                    : "[ADVISORY] No significant bottlenecks detected in the current pipeline.",
                isAdvisory = true },
            new { type = "DropOffRisk", severity = dropOffRisk > 10 ? "High" : "Low",
                title = "Candidate Drop-Off Risk",
                description = $"[ADVISORY] {dropOffRisk} candidates in Screening stage with no movement for >14 days. Risk of candidate withdrawal if not engaged.",
                isAdvisory = true },
            new { type = "DemandForecast", severity = unresolvedReqs > 5 ? "Medium" : "Low",
                title = "Unresolved Approved Requisitions",
                description = $"[ADVISORY] {unresolvedReqs} approved manpower requisitions do not yet have active job openings. Consider posting or escalating.",
                isAdvisory = true },
        };

        return Ok(new { generatedAt = DateTime.UtcNow, isAdvisory = true, insights });
    }
}
