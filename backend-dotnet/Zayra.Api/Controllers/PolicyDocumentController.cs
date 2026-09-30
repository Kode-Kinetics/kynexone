using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Zayra.Api.Application.AI;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/ai/policy")]
[Authorize]
public class PolicyDocumentController : ControllerBase
{
    private readonly IPolicyDocumentService _svc;
    public PolicyDocumentController(IPolicyDocumentService svc) => _svc = svc;

    [HttpGet("documents")]
    public async Task<ActionResult<IReadOnlyList<PolicyDocumentDto>>> List(CancellationToken ct)
    {
        var tid = GetTenantId();
        if (tid is null) return Unauthorized();
        if (!CanUsePolicyAssistant()) return Forbid();
        return Ok(await _svc.ListAsync(tid.Value, ct));
    }

    [HttpPost("documents/upload")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<ActionResult<PolicyDocumentDto>> Upload(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "No file provided." });
        if (file.Length > 20 * 1024 * 1024)
            return BadRequest(new { message = "File size exceeds 20 MB limit." });
        var allowed = new[] { ".pdf", ".docx", ".doc", ".txt" };
        if (!allowed.Contains(Path.GetExtension(file.FileName).ToLowerInvariant()))
            return BadRequest(new { message = "Unsupported file type. Upload PDF, DOCX, or TXT." });
        var tid = GetTenantId();
        if (tid is null) return Unauthorized();
        using var stream = file.OpenReadStream();
        var doc = await _svc.UploadAsync(tid.Value, GetUserId(), stream, file.FileName, file.ContentType, ct);
        return Ok(doc);
    }

    [HttpDelete("documents/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        if (tid is null) return Unauthorized();
        return await _svc.DeleteAsync(tid.Value, id, ct) ? NoContent() : NotFound();
    }

    [HttpPost("ask")]
    public async Task<ActionResult<PolicyAskResponse>> Ask([FromBody] PolicyAskRequest request, CancellationToken ct)
    {
        var tid = GetTenantId();
        if (tid is null) return Unauthorized();
        if (!CanUsePolicyAssistant()) return Forbid();
        // Identity is threaded so the AI usage record names who asked, not "System".
        var response = await _svc.AskAsync(tid.Value, GetUserId(), GetUserRole(), request.Question, ct);
        return Ok(response);
    }

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
    /// <para>The role-name disjuncts are unchanged, deliberately: HR Manager and HR Officer reach this
    /// today by role name and their seeded bundles do not hold <c>ai.query</c>.</para>
    /// </summary>
    private bool CanUsePolicyAssistant() =>
        User.IsInRole("Admin") || User.IsInRole("HR Manager") || User.IsInRole("HR Officer") ||
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
