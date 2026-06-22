using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Recruitment;

[ApiController]
[Route("api/recruitment/candidates")]
[Authorize]
public class CandidatesController : ControllerBase
{
    private readonly ZayraDbContext _db;
    public CandidatesController(ZayraDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] string? source,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId()!.Value;
        var query = _db.Candidates.Where(c => c.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(c => c.Status == status);
        if (!string.IsNullOrWhiteSpace(source)) query = query.Where(c => c.Source == source);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(c =>
                c.FirstName.ToLower().Contains(s) ||
                c.LastName.ToLower().Contains(s) ||
                c.Email.ToLower().Contains(s) ||
                c.CurrentJobTitle.ToLower().Contains(s) ||
                c.CurrentCompany.ToLower().Contains(s));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(c => c.CreatedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        // Enrich with application count
        var ids = items.Select(c => c.Id).ToList();
        var appCounts = await _db.JobApplications
            .Where(a => a.TenantId == tenantId && ids.Contains(a.CandidateId))
            .GroupBy(a => a.CandidateId)
            .Select(g => new { CandidateId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var countMap = appCounts.ToDictionary(x => x.CandidateId, x => x.Count);

        var enriched = items.Select(c => new
        {
            c.Id, c.FirstName, c.LastName, FullName = $"{c.FirstName} {c.LastName}".Trim(),
            c.Email, c.Phone, c.CurrentJobTitle, c.CurrentCompany,
            c.TotalExperienceYears, c.EducationLevel, c.Nationality,
            c.LinkedInUrl, c.Source, c.Status, c.Tags, c.CreatedAtUtc,
            ApplicationCount = countMap.GetValueOrDefault(c.Id, 0),
        });

        return Ok(new { items = enriched, total, page, pageSize });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var c = await _db.Candidates.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (c is null) return NotFound();

        var applications = await _db.JobApplications
            .Where(a => a.TenantId == tenantId && a.CandidateId == id)
            .OrderByDescending(a => a.AppliedAtUtc)
            .ToListAsync(ct);

        return Ok(new { candidate = c, applications });
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Create([FromBody] CandidateRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;

        var normalizedCandidateEmail = req.Email?.Trim().ToLowerInvariant() ?? "";
        var exists = await _db.Candidates.AnyAsync(c => c.TenantId == tenantId && c.Email == normalizedCandidateEmail, ct);
        if (exists) return Conflict(new { message = "A candidate with this email already exists." });

        var c = new Candidate
        {
            TenantId = tenantId,
            FirstName = req.FirstName,
            LastName = req.LastName,
            Email = normalizedCandidateEmail,
            Phone = req.Phone,
            CurrentJobTitle = req.CurrentJobTitle,
            CurrentCompany = req.CurrentCompany,
            TotalExperienceYears = req.TotalExperienceYears,
            EducationLevel = req.EducationLevel,
            Nationality = req.Nationality,
            LinkedInUrl = req.LinkedInUrl,
            ResumeUrl = req.ResumeUrl ?? "",
            Source = req.Source,
            Tags = req.Tags,
        };
        _db.Candidates.Add(c);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/recruitment/candidates/{c.Id}", c);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CandidateRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var c = await _db.Candidates.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (c is null) return NotFound();

        c.FirstName = req.FirstName;
        c.LastName = req.LastName;
        c.Phone = req.Phone;
        c.CurrentJobTitle = req.CurrentJobTitle;
        c.CurrentCompany = req.CurrentCompany;
        c.TotalExperienceYears = req.TotalExperienceYears;
        c.EducationLevel = req.EducationLevel;
        c.Nationality = req.Nationality;
        c.LinkedInUrl = req.LinkedInUrl;
        c.Source = req.Source;
        c.Tags = req.Tags;
        c.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(c);
    }

    [HttpPost("{id:guid}/blacklist")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Blacklist(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId()!.Value;
        var c = await _db.Candidates.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (c is null) return NotFound();
        c.Status = "Blacklisted";
        await _db.SaveChangesAsync(ct);
        return Ok(c);
    }
}

public record CandidateRequest(
    string FirstName, string LastName, string Email, string Phone,
    string CurrentJobTitle, string CurrentCompany, decimal TotalExperienceYears,
    string EducationLevel, string Nationality, string LinkedInUrl, string Source, string Tags,
    string? ResumeUrl = null);
