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
    private readonly Zayra.Api.Infrastructure.Recruitment.OfferLifecycleService _lifecycle;
    public CandidatesController(ZayraDbContext db, Zayra.Api.Infrastructure.Recruitment.OfferLifecycleService? lifecycle = null)
    { _db = db; _lifecycle = lifecycle ?? new(db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()); }

    [HttpGet]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter")]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] string? source,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
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
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter")]
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
    [Zayra.Api.Infrastructure.Recruitment.RefuseHrOfficer]
    public async Task<IActionResult> Blacklist(Guid id, CancellationToken ct,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] BlacklistRequest? req = null)
    {
        var tenantId = this.GetTenantId()!.Value;
        // Blacklisting shuts a person out of every hiring step, so it needs a written reason and leaves an audit record (who, when, whom, why). The
        // reason lives on the audit record only; nothing that lists candidates returns it.
        var reason = req?.Reason?.Trim() ?? string.Empty;
        if (reason.Length < 10)
            return BadRequest(new
            {
                error = "blacklist_reason_required",
                message = "Say why this candidate is being blacklisted (at least 10 characters). The reason is kept on the audit record.",
                messageAr = "اذكر سبب إدراج هذا المرشح في القائمة السوداء (10 أحرف على الأقل). يُحفظ السبب في سجل التدقيق.",
            });
        if (reason.Length > 1000) return BadRequest(new { error = "reason_too_long", message = "Keep the reason under 1,000 characters.", messageAr = "اجعل السبب أقل من 1000 حرف." });
        var c = await _db.Candidates.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (c is null) return NotFound();
        if (c.Status == "Blacklisted") return Ok(c);
        var userId = this.GetUserId();
        var userName = User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "HR";
        var previous = c.Status;
        c.Status = "Blacklisted";
        _db.RecruitmentAuditLogs.Add(new RecruitmentAuditLog
        {
            TenantId = tenantId, EntityType = "Candidate", EntityId = c.Id.ToString(), Action = "Blacklisted",
            PerformedByUserId = userId, PerformedByName = userName,
            OldValuesJson = System.Text.Json.JsonSerializer.Serialize(new { status = previous }),
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(new { status = "Blacklisted", reason }),
        });
        await _db.SaveChangesAsync(ct);
        // Offers of theirs still waiting in the dispatch queue are held, not sent. The listing stands even if holding one of them fails.
        try { await _lifecycle.HoldQueuedDispatchesForBlacklistedCandidateAsync(tenantId, c.Id, userId ?? Guid.Empty, userName, ct); }
        catch (Zayra.Api.Infrastructure.Recruitment.OfferLifecycleException) { }
        return Ok(c);
    }
}

public record BlacklistRequest(string? Reason);

public record CandidateRequest(
    string FirstName, string LastName, string Email, string Phone,
    string CurrentJobTitle, string CurrentCompany, decimal TotalExperienceYears,
    string EducationLevel, string Nationality, string LinkedInUrl, string Source, string Tags,
    string? ResumeUrl = null);
