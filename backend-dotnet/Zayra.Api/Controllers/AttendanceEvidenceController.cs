using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Infrastructure.Documents;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/attendance/evidence")]
[Authorize]
public sealed class AttendanceEvidenceController : ControllerBase
{
    private const long MaxSelfieBytes = 3 * 1024 * 1024;
    private readonly IDocumentStorage _storage;
    private readonly IAuditService _audit;

    public AttendanceEvidenceController(IDocumentStorage storage, IAuditService audit)
    {
        _storage = storage;
        _audit = audit;
    }
    [HttpPost("selfie")]
    [RequestSizeLimit(MaxSelfieBytes + 512 * 1024)]
    public async Task<IActionResult> UploadSelfie(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length <= 0)
            return BadRequest(new { message = "A selfie image is required." });
        if (file.Length > MaxSelfieBytes)
            return BadRequest(new { message = "The selfie image must be 3 MB or smaller." });

        var contentType = (file.ContentType ?? string.Empty).ToLowerInvariant();
        if (contentType is not ("image/jpeg" or "image/png"))
            return BadRequest(new { message = "Attendance selfies must be JPEG or PNG." });
        if (!await HasExpectedImageSignature(file, contentType, ct))
            return BadRequest(new { message = "The uploaded file is not a valid image." });

        var tenantId = RequireTenant();
        var stored = await _storage.SaveAsync(tenantId, file, ct);
        await _audit.WriteAsync(
            "attendance.selfie_uploaded",
            "AttendanceEvidence",
            null,
            Context(tenantId),
            $"{{\"contentType\":\"{contentType}\",\"bytes\":{file.Length}}}",
            ct);
        return Ok(new
        {
            photoReference = stored.StorageUrl,
            contentType = stored.ContentType,
            size = file.Length
        });
    }

    private Guid RequireTenant() =>
        Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId)
            ? tenantId
            : throw new UnauthorizedAccessException("Tenant claim is missing.");

    private Guid? GetUserId() =>
        Guid.TryParse(
            User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"),
            out var id)
            ? id
            : null;

    private RequestContext Context(Guid tenantId) =>
        new(
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            GetUserId(),
            tenantId);
    private static async Task<bool> HasExpectedImageSignature(
        IFormFile file,
        string contentType,
        CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        var header = new byte[8];
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length), ct);

        if (contentType == "image/jpeg")
            return read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;

        return read >= 8
            && header[0] == 0x89 && header[1] == 0x50
            && header[2] == 0x4E && header[3] == 0x47
            && header[4] == 0x0D && header[5] == 0x0A
            && header[6] == 0x1A && header[7] == 0x0A;
    }
}
