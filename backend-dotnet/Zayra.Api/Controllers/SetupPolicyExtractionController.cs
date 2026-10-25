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
        if (request.ChunkIndex < 0) return BadRequest(new { message = "The policy section index must be zero or greater." });
        if (request.Profile is null || request.Profile.Sections is null) return BadRequest(new { message = "Company context is required." });
        var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : (Guid?)null;
        var scope = this.GetRequestScope();
        var source = await documents.TextAsync(tenantId, request.DocumentId,
            new(true, scope.IsGroupLevel, scope.AuthorizedCompanyIds, userId, User.IsInRole("Admin")), ct);
        if (source is null) return NotFound();
        if (source.Text.Length == 0) return UnprocessableEntity(new { message = "This document has no readable text. Upload a text-based PDF, DOCX or TXT document." });
        // Native document parsing already happened at upload. Local mapping is read-only,
        // uses the same source access controls, and never consults AI quotas or providers.
        if (!request.UseAi)
        {
            if (request.ChunkIndex != 0)
                return BadRequest(new { message = "Local preparation reads the complete document; use section zero." });
            ct.ThrowIfCancellationRequested();
            return Ok(LocalOnboardingPolicyMapper.Prepare(request.DocumentId, source.ContentSha256, source.Text));
        }
        if (!extraction.IsValidChunkIndex(source.Text, request.ChunkIndex))
            return BadRequest(new { message = "This policy section no longer exists. Restart extraction from the first section." });
        var allowance = Zayra.Api.Infrastructure.Licensing.AiAllowance.For(
            Zayra.Api.Infrastructure.Licensing.LicenceControllerExtensions.Licence(this));
        if (!allowance.Licensed)
            return StatusCode(403, new
            {
                error = Zayra.Api.Infrastructure.Licensing.AiAllowance.NotLicensedCode,
                message = Zayra.Api.Infrastructure.Licensing.AiAllowance.NotLicensedMessage,
            });
        var limit = allowance.MonthlyTokenLimit;
        var yearMonth = DateTime.UtcNow.Year * 100 + DateTime.UtcNow.Month;
        if (await db.TenantAiUsages.AnyAsync(u => u.TenantId == tenantId && u.YearMonth == yearMonth && u.TokensUsed >= limit, ct))
            return StatusCode(429, new { message = "Your workspace AI usage limit has been reached." });
        return Ok(await extraction.ExtractAsync(new(tenantId, userId,
            string.Join(",", User.FindAll(ClaimTypes.Role).Select(c => c.Value))), request.DocumentId,
            source.ContentSha256, source.Text, request.Profile, ct, request.ChunkIndex));
    }
}
