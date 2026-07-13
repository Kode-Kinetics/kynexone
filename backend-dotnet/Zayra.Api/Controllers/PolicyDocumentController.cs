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
        if (!CanReadPolicyDocuments()) return Forbid();
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
        if (!CanAskPolicyAi()) return Forbid();
        var response = await _svc.AskAsync(tid.Value, request.Question, ct);
        return Ok(response);
    }

    private bool CanReadPolicyDocuments() =>
        User.IsInRole("Admin") || User.IsInRole("HR Manager") || User.IsInRole("HR Officer") ||
        HasPermission("policy.documents.read") || HasPermission("ai.policy.ask");

    private bool CanAskPolicyAi() =>
        User.IsInRole("Admin") || User.IsInRole("HR Manager") || User.IsInRole("HR Officer") ||
        HasPermission("ai.query") || HasPermission("ai.policy.ask");

    private bool HasPermission(string permission) =>
        User.Claims.Any(c => c.Type == "permission" && string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    private Guid? GetTenantId()
    {
        var v = User.FindFirstValue("tenant_id");
        return Guid.TryParse(v, out var id) ? id : null;
    }
    private Guid? GetUserId()
    {
        var v = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(v, out var id) ? id : null;
    }
}
