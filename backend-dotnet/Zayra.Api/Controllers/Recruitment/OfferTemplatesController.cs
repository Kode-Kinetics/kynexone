using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Drawing.Exceptions;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Scope;
using Zayra.Api.Models;
using Zayra.Api.Infrastructure.Recruitment;

namespace Zayra.Api.Controllers.Recruitment;

[ApiController]
[Route("api/recruitment/offer-templates")]
[Authorize(Roles = "Admin,HR Manager,HR Officer,Recruiter")]
[RequestSizeLimit(8_000_000)]
public class OfferTemplatesController(ZayraDbContext db, IRequestEntityScopeResolver scopeResolver) : ControllerBase
{
    private Guid TenantId => Guid.TryParse(User.FindFirst("tenant_id")?.Value, out var id) ? id : Guid.Empty;
    private Guid? UserId => Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
    private object Dto(OfferLetterTemplate t) => new { t.Id, t.Name, t.CompanyId, t.Version, t.Archived, t.UpdatedAtUtc, design = JsonSerializer.Deserialize<OfferDesign>(t.DesignJson, OfferLetterDesign.Json) };
    private void Audit(OfferLetterTemplate t, string action) => db.RecruitmentAuditLogs.Add(new RecruitmentAuditLog
    {
        TenantId = TenantId, EntityType = "OfferTemplate", EntityId = t.Id.ToString(), Action = action,
        PerformedByUserId = UserId, PerformedByName = User.FindFirst("name")?.Value ?? "HR",
        NewValuesJson = JsonSerializer.Serialize(new { t.Name, t.Version, t.CompanyId, t.Archived })
    });

    [HttpGet("defaults")]
    public IActionResult Defaults() => Ok(new { design = OfferLetterDesign.DynamicDefault(), tokens = OfferLetterDesign.Tokens });

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await db.OfferLetterTemplates.AsNoTracking()
        .Where(t => t.TenantId == TenantId && !t.Archived).OrderBy(t => t.Name)
        .Select(t => new { t.Id, t.Name, t.CompanyId, t.Version, t.UpdatedAtUtc }).Take(250).ToListAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var t = await db.OfferLetterTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.TenantId == TenantId, ct);
        return t is null ? NotFound() : Ok(Dto(t));
    }

    [HttpPost("preview")]
    public async Task<IActionResult> Preview(OfferPreviewRequest request, CancellationToken ct)
    {
        try
        {
            OfferDesignSnapshot snapshot;
            if (request.OfferId is { } offerId)
            {
                if (!OfferAccess.CanRead(User)) return Forbid();
                var offer = await db.OfferLetters.AsNoTracking().FirstOrDefaultAsync(o => o.Id == offerId && o.TenantId == TenantId, ct);
                if (offer is null || !scopeResolver.Resolve().CanAccessCompany(offer.CompanyId)) return NotFound();
                if (request.ExpectedVersion is null || request.ExpectedVersion != offer.Version)
                    return Conflict(new { error = "offer_version_conflict", message = "Reload the current offer before previewing." });
                if (offer.Status != "Draft" || offer.FrozenAtUtc is not null || offer.ArtifactId is not null
                    || await db.OfferApprovals.AnyAsync(a => a.TenantId == TenantId && a.OfferLetterId == offerId, ct))
                    return Conflict(new { error = "offer_original_required", message = "Read the frozen original. Create a revision to change its design." });
                snapshot = await OfferLetterDesign.SnapshotAsync(db, offer, request.Design, null, null, ct);
            }
            else
            {
                OfferLetterDesign.Validate(request.Design);
                var values = OfferLetterDesign.SampleValues();
                foreach (var field in request.Design.CustomFields) values.Add(field.Key, field.Value);
                snapshot = new(request.Design, values, null, null, OfferLetterDesign.SampleCompensation());
            }
            return File(OfferLetterDesign.Render(snapshot), "application/pdf");
        }
        catch (ArgumentException e) { return BadRequest(new { message = e.Message }); }
        catch (DocumentLayoutException) { return BadRequest(new { message = "This layout does not fit the page. Reduce text size, image sizes or margins." }); }
    }

    [HttpPost]
    [Authorize(Roles = "Admin,HR Manager,Recruiter")]
    public async Task<IActionResult> Create(OfferTemplateRequest request, CancellationToken ct)
    {
        var scope = scopeResolver.Resolve();
        var companyId = scope.SelectedCompanyId;
        if (!scope.CanAccessCompany(companyId) || TenantId == Guid.Empty) return Forbid();
        var failure = ValidateRequest(request);
        if (failure is not null) return BadRequest(new { message = failure });
        if (await db.OfferLetterTemplates.CountAsync(t => t.TenantId == TenantId && !t.Archived, ct) >= 250)
            return BadRequest(new { message = "Archive an unused template before creating another (250 active templates maximum)." });
        var t = new OfferLetterTemplate { TenantId = TenantId, CompanyId = companyId, Name = request.Name.Trim(), DesignJson = JsonSerializer.Serialize(request.Design, OfferLetterDesign.Json), UpdatedByUserId = UserId };
        db.OfferLetterTemplates.Add(t); Audit(t, "Created"); await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = t.Id }, Dto(t));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,Recruiter")]
    public async Task<IActionResult> Update(Guid id, OfferTemplateRequest request, CancellationToken ct)
    {
        var t = await db.OfferLetterTemplates.FirstOrDefaultAsync(t => t.Id == id && t.TenantId == TenantId, ct);
        if (t is null) return NotFound();
        // Shared templates are readable by company users, but only group users may change them.
        if (!scopeResolver.Resolve().CanAccessCompany(t.CompanyId)) return Forbid();
        if (t.Archived || request.ExpectedVersion != t.Version) return Conflict(new { message = "This template changed or was archived. Reload it before saving." });
        var failure = ValidateRequest(request);
        if (failure is not null) return BadRequest(new { message = failure });
        t.Name = request.Name.Trim(); t.DesignJson = JsonSerializer.Serialize(request.Design, OfferLetterDesign.Json);
        t.Version++; t.UpdatedAtUtc = DateTime.UtcNow; t.UpdatedByUserId = UserId; Audit(t, "Updated");
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Conflict(new { message = "Another user updated this template. Reload before saving." }); }
        return Ok(Dto(t));
    }

    [HttpPost("{id:guid}/archive")]
    [Authorize(Roles = "Admin,HR Manager,Recruiter")]
    public async Task<IActionResult> Archive(Guid id, OfferTemplateArchiveRequest request, CancellationToken ct)
    {
        var t = await db.OfferLetterTemplates.FirstOrDefaultAsync(t => t.Id == id && t.TenantId == TenantId, ct);
        if (t is null) return NotFound();
        if (!scopeResolver.Resolve().CanAccessCompany(t.CompanyId)) return Forbid();
        if (request.ExpectedVersion != t.Version) return Conflict(new { message = "Reload the template before archiving." });
        t.Archived = true; t.Version++; t.UpdatedAtUtc = DateTime.UtcNow; Audit(t, "Archived");
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return Conflict(new { message = "This template changed. Reload it." }); }
        return NoContent();
    }

    private static string? ValidateRequest(OfferTemplateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120) return "Enter a template name (up to 120 characters).";
        try
        {
            OfferLetterDesign.Validate(request.Design);
            var values = OfferLetterDesign.SampleValues();
            foreach (var field in request.Design.CustomFields) values.Add(field.Key, field.Value);
            OfferLetterDesign.Render(new(request.Design, values, null, null, OfferLetterDesign.SampleCompensation()));
            return null;
        }
        catch (ArgumentException e) { return e.Message; }
        catch (DocumentLayoutException) { return "This layout does not fit the page. Reduce text size, images or margins."; }
    }
}

public sealed record OfferTemplateRequest(string Name, OfferDesign Design, int? ExpectedVersion = null);
public sealed record OfferTemplateArchiveRequest(int ExpectedVersion);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record OfferPreviewRequest(OfferDesign Design, Guid? OfferId = null, long? ExpectedVersion = null);
