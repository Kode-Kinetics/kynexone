using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.AI;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Setup;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.AI;
using Zayra.Api.Infrastructure.Setup;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/setup-assistant/policy")]
[Authorize]
[HasPermission("organization.write")]
public sealed class SetupPolicyExtractionController(IPolicyDocumentService documents,
    PolicyExtractionService extraction, ZayraDbContext db) : ControllerBase
{
    [HttpPost("extract")]
    public async Task<IActionResult> Extract([FromBody] PolicyExtractionRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId)) return Unauthorized();
        if (!User.HasPermission("organization.write")) return Forbid();
        if (!request.UseAi) return BadRequest(new { message = "Choose AI assistance before sending policy content to the configured provider." });
        if (request.Profile is null || request.Profile.Sections is null) return BadRequest(new { message = "Company context is required." });
        var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : (Guid?)null;
        var scope = this.GetRequestScope();
        var source = await documents.TextAsync(tenantId, request.DocumentId,
            new(true, scope.IsGroupLevel, scope.AuthorizedCompanyIds, userId, User.IsInRole("Admin")), ct);
        if (source is null) return NotFound();
        if (source.Text.Length == 0) return UnprocessableEntity(new { message = "This document has no readable text. Upload a text-based PDF, DOCX or TXT document." });
        var subscription = await db.TenantSubscriptions.AsNoTracking().Where(s => s.TenantId == tenantId && s.Status == "Active")
            .OrderByDescending(s => s.StartedAtUtc).FirstOrDefaultAsync(ct);
        var limit = AiPlanLimits.GetMonthlyTokenLimit(subscription?.Plan ?? "Starter");
        var yearMonth = DateTime.UtcNow.Year * 100 + DateTime.UtcNow.Month;
        if (limit > 0 && await db.TenantAiUsages.AnyAsync(u => u.TenantId == tenantId && u.YearMonth == yearMonth && u.TokensUsed >= limit, ct))
            return StatusCode(429, new { message = "Your workspace AI usage limit has been reached." });
        return Ok(await extraction.ExtractAsync(new(tenantId, userId,
            string.Join(",", User.FindAll(ClaimTypes.Role).Select(c => c.Value))), request.DocumentId,
            source.ContentSha256, source.Text, request.Profile, ct));
    }
}
