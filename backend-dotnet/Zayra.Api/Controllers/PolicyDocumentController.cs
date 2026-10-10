using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Application.AI;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/ai/policy")]
[Authorize]
public class PolicyDocumentController : ControllerBase
{
    private readonly IPolicyDocumentService _svc;
    private readonly ZayraDbContext? _db;
    private readonly IAuditService? _audit;
    public PolicyDocumentController(IPolicyDocumentService svc, ZayraDbContext? db = null, IAuditService? audit = null)
    { _svc = svc; _db = db; _audit = audit; }

    [HttpGet("documents")]
    public async Task<ActionResult<IReadOnlyList<PolicyDocumentDto>>> List(CancellationToken ct)
    {
        var tid = GetTenantId();
        if (tid is null) return Unauthorized();
        if (!CanUsePolicyAssistant()) return Forbid();
        return Ok(await _svc.ListAsync(tid.Value, ReadScope(), ct));
    }

    [HttpPost("documents/upload")]
    [HasPermission("organization.write")]
    public async Task<ActionResult<PolicyDocumentDto>> Upload(IFormFile file, CancellationToken ct)
    {
        if (!HasPermission("organization.write")) return Forbid();
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "No file provided." });
        if (file.Length > 20 * 1024 * 1024)
            return BadRequest(new { message = "File size exceeds 20 MB limit." });
        var allowed = new[] { ".pdf", ".docx", ".txt" };
        if (!allowed.Contains(Path.GetExtension(file.FileName).ToLowerInvariant()))
            return BadRequest(new { message = "Unsupported file type. Upload PDF, DOCX, or TXT." });
        var tid = GetTenantId();
        if (tid is null) return Unauthorized();
        using var stream = file.OpenReadStream();
        var doc = await _svc.UploadAsync(tid.Value, GetUserId(), stream, file.FileName, file.ContentType, ct);
        return Ok(doc);
    }

    [HttpDelete("documents/{id:guid}")]
    [HasPermission("organization.write")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        if (tid is null) return Unauthorized();
        if (!HasPermission("organization.write")) return Forbid();
        if (await _svc.FindAsync(tid.Value, id, ReadScope(), ct) is null) return NotFound();
        if (_db is not null && await _db.PolicyDocuments.AnyAsync(d => d.TenantId == tid && d.Id == id && (d.PublishedAtUtc != null || d.PublicationStatus == "Published"), ct))
            return Conflict(new { message = "Published policy versions are retained for audit. Withdraw the document to remove employee access." });
        try { return await _svc.DeleteAsync(tid.Value, id, ct) ? NoContent() : NotFound(); }
        catch (DbUpdateConcurrencyException) { return ChangedPolicy(); }
    }

    [HttpPost("ask")]
    public async Task<ActionResult<PolicyAskResponse>> Ask([FromBody] PolicyAskRequest request, CancellationToken ct)
    {
        var tid = GetTenantId();
        if (tid is null) return Unauthorized();
        if (!CanUsePolicyAssistant()) return Forbid();
        // Identity is threaded so the AI usage record names who asked, not "System".
        var response = await _svc.AskAsync(tid.Value, GetUserId(), GetUserRole(), request.Question, ReadScope(), ct);
        return Ok(response);
    }

    [HttpGet("documents/{id:guid}/text")]
    public async Task<IActionResult> Text(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        if (tid is null) return Unauthorized();
        if (!IsHrReader()) return Forbid();
        var result = await _svc.TextAsync(tid.Value, id, ReadScope(), ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("documents/{id:guid}/publish")]
    [HasPermission("organization.write")]
    public async Task<IActionResult> Publish(Guid id, [FromBody] PublishPolicyRequest request, CancellationToken ct)
    {
        var tid = GetTenantId();
        if (tid is null) return Unauthorized();
        if (!HasPermission("organization.write")) return Forbid();
        if (_db is null || _audit is null) return StatusCode(503);
        if (request.CompanyId == Guid.Empty || string.IsNullOrWhiteSpace(request.ContentSha256))
            return BadRequest(new { message = "Choose a company and confirm the reviewed document version." });
        if (!this.GetRequestScope().CanAccessCompany(request.CompanyId)) return Forbid();
        if (!await _db.Companies.AnyAsync(c => c.Id == request.CompanyId && c.TenantId == tid && !c.IsDeleted && c.IsActive, ct))
            return NotFound();
        var text = await _svc.TextAsync(tid.Value, id, ReadScope(), ct);
        if (text is null) return NotFound();
        if (text.ContentSha256.Length != 64 || !string.Equals(text.ContentSha256, request.ContentSha256, StringComparison.OrdinalIgnoreCase))
            return Conflict(new { message = "The reviewed policy version does not match. Reload and review the document before publishing." });
        var from = request.EffectiveFromUtc ?? DateTime.UtcNow;
        if (from.Kind != DateTimeKind.Utc || request.EffectiveToUtc is { Kind: not DateTimeKind.Utc })
            return BadRequest(new { message = "Effective dates must include the UTC timezone (Z)." });
        if (request.EffectiveToUtc.HasValue && request.EffectiveToUtc <= from)
            return BadRequest(new { message = "The end date must be after the start date." });
        var doc = await _db.PolicyDocuments.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tid && !d.IsDeleted, ct);
        if (doc is null || doc.Status != "Ready") return NotFound();
        if (doc.CompanyId.HasValue && doc.CompanyId.Value != request.CompanyId)
            return Conflict(new { message = "A document cannot be moved between companies. Upload a separate company copy." });
        doc.CompanyId = request.CompanyId;
        doc.PublicationStatus = "Published";
        doc.EffectiveFromUtc = from;
        doc.EffectiveToUtc = request.EffectiveToUtc;
        doc.PublishedByUserId = GetUserId();
        doc.PublishedAtUtc = DateTime.UtcNow;
        doc.UpdatedAtUtc = DateTime.UtcNow;
        // AuditService saves the pending publication and audit row together in one EF save.
        try
        {
            await _audit.WriteAsync("policy.document_published", "PolicyDocument", id.ToString(), Context(tid.Value),
                JsonSerializer.Serialize(new { doc.CompanyId, doc.ContentSha256, doc.EffectiveFromUtc, doc.EffectiveToUtc }), ct);
        }
        catch (DbUpdateConcurrencyException) { return ChangedPolicy(); }
        return Ok(ToDto(doc));
    }

    [HttpPost("documents/{id:guid}/withdraw")]
    [HasPermission("organization.write")]
    public async Task<IActionResult> Withdraw(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        if (tid is null) return Unauthorized();
        if (!HasPermission("organization.write")) return Forbid();
        if (_db is null || _audit is null) return StatusCode(503);
        if (await _svc.TextAsync(tid.Value, id, ReadScope(), ct) is null) return NotFound();
        var doc = await _db.PolicyDocuments.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tid && !d.IsDeleted, ct);
        if (doc is null) return NotFound();
        doc.PublicationStatus = "Withdrawn";
        doc.UpdatedAtUtc = DateTime.UtcNow;
        try
        {
            await _audit.WriteAsync("policy.document_withdrawn", "PolicyDocument", id.ToString(), Context(tid.Value),
                JsonSerializer.Serialize(new { doc.CompanyId, doc.ContentSha256 }), ct);
        }
        catch (DbUpdateConcurrencyException) { return ChangedPolicy(); }
        return Ok(ToDto(doc));
    }

    [HttpGet("employee/documents")]
    public async Task<IActionResult> EmployeeDocuments(CancellationToken ct)
    {
        var tid = GetTenantId();
        if (tid is null) return Unauthorized();
        var scope = await EmployeeScope(tid.Value, ct);
        if (scope is null) return Forbid();
        return Ok(await _svc.ListAsync(tid.Value, scope, ct));
    }

    [HttpPost("employee/ask")]
    public async Task<IActionResult> EmployeeAsk([FromBody] PolicyAskRequest request, CancellationToken ct)
    {
        var tid = GetTenantId();
        if (tid is null) return Unauthorized();
        var scope = await EmployeeScope(tid.Value, ct);
        if (scope is null) return Forbid();
        return Ok(await _svc.AskAsync(tid.Value, GetUserId(), GetUserRole(), request.Question, scope, ct));
    }

    [HttpGet("employee/documents/{id:guid}/text")]
    public async Task<IActionResult> EmployeeText(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        if (tid is null) return Unauthorized();
        var scope = await EmployeeScope(tid.Value, ct);
        if (scope is null) return Forbid();
        var result = await _svc.TextAsync(tid.Value, id, scope, ct);
        return result is null ? NotFound() : Ok(result);
    }

    private async Task<PolicyReadScope?> EmployeeScope(Guid tenantId, CancellationToken ct)
    {
        if (_db is null || !HasPermission("ess.read") || User.FindFirstValue("access_mode") is "NoLogin" or "KioskOnly") return null;
        var employeeId = await CallerEmployeeResolver.ResolveAsync(_db, User, tenantId, ct, requireActive: true);
        if (employeeId is null) return null;
        var companyId = await _db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted)
            .Select(e => e.CompanyId).FirstOrDefaultAsync(ct);
        return companyId.HasValue ? new(false, false, [companyId.Value], GetUserId()) : null;
    }

    private IActionResult ChangedPolicy() => Conflict(new { message = "The policy changed while you were reviewing it. Reload and review the current version." });

    private bool IsHrReader() => User.IsInRole("Admin") || User.IsInRole("HR Director") || User.IsInRole("HR Manager") || User.IsInRole("HR Officer")
        || HasPermission("organization.write");
    private PolicyReadScope ReadScope()
    {
        var scope = this.GetRequestScope();
        return new(IsHrReader(), scope.IsGroupLevel, scope.AuthorizedCompanyIds, GetUserId(), User.IsInRole("Admin"));
    }
    private RequestContext Context(Guid tenantId) => new(HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString(), GetUserId(), tenantId);
    private static PolicyDocumentDto ToDto(Zayra.Api.Domain.Entities.PolicyDocument d) =>
        new(d.Id, d.OriginalName, d.MimeType, d.FileSizeBytes, d.Status, d.ChunkCount, d.ErrorMessage, d.CreatedAtUtc)
        { CompanyId = d.CompanyId, PublicationStatus = d.PublicationStatus, EffectiveFromUtc = d.EffectiveFromUtc,
            EffectiveToUtc = d.EffectiveToUtc, ContentSha256 = d.ContentSha256 };

    /// <summary>
    /// Listing the policy documents and asking the policy assistant are one capability: the list IS the
    /// corpus the assistant answers from, and the product shows both on the Assistant page's Policy
    /// Documents tab. It is gated on <see cref="AssistantPermission"/> — the key
    /// <c>LegacyRolePermissionResolver</c> already assigns to this controller for read and write.
    ///
    /// <para>The list used to accept <c>policy.documents.read</c> / <c>ai.policy.ask</c> and the ask
    /// <c>ai.policy.ask</c>. Neither key was ever in the permission catalog, so a tenant could not grant
    /// policy-document access to anyone outside the three role names below: an Allow override for either
    /// key is refused, and a custom role holding <c>ai.query</c> could ask the assistant but not see
    /// which documents it was answering from.</para>
    ///
    /// <para>Policy authors also need to inspect their own draft corpus before publication, so an
    /// explicit <c>organization.write</c> grant carries read access without depending on a role name.</para>
    /// </summary>
    private bool CanUsePolicyAssistant() =>
        IsHrReader() ||
        HasPermission(AssistantPermission);

    /// <summary>"Query the AI HR assistant".</summary>
    public const string AssistantPermission = "ai.query";

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    private Guid? GetTenantId()
    {
        var v = User.FindFirstValue("tenant_id");
        return Guid.TryParse(v, out var id) ? id : null;
    }
    // Comma-joined, matching AiAdvisoryService's AiAuditEntry.UserRole so one audit query reads
    // the same across AI modules.
    private string GetUserRole() =>
        string.Join(",", User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).Distinct());

    private Guid? GetUserId()
    {
        var v = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(v, out var id) ? id : null;
    }
}
