using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Documents;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public sealed class EmployeePhotoController : ControllerBase
{
    private const long MaxPhotoBytes = 3 * 1024 * 1024;
    private readonly ZayraDbContext _db;
    private readonly IDocumentStorage _storage;
    private readonly IDataScopeService _scope;
    private readonly IAuditService _audit;

    public EmployeePhotoController(
        ZayraDbContext db,
        IDocumentStorage storage,
        IDataScopeService scope,
        IAuditService audit)
    {        _db = db;
        _storage = storage;
        _scope = scope;
        _audit = audit;
    }

    [HttpPost("ess/profile/photo")]
    [RequestSizeLimit(MaxPhotoBytes + 512 * 1024)]
    public async Task<IActionResult> UploadOwnPhoto(IFormFile file, CancellationToken ct)
    {
        if (!User.HasClaim("permission", "ess.write"))
            return Forbid();

        var tenantId = RequireTenant();
        var dataScope = await _scope.ResolveAsync(User, tenantId, ct);
        if (dataScope.CallerEmployeeId is not int employeeId)
            return BadRequest(new { message = "No employee profile is linked to this login." });

        var validation = await ValidatePhoto(file, ct);
        if (validation is not null) return BadRequest(new { message = validation });

        var employee = await _db.Employees.FirstOrDefaultAsync(
            e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted,
            ct);
        if (employee is null) return NotFound();        var stored = await _storage.SaveAsync(tenantId, file, ct);
        employee.ProfilePhotoUrl = stored.StorageUrl;
        employee.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(
            "ess.profile_photo.updated",
            "Employee",
            employeeId.ToString(),
            Context(tenantId),
            $"{{\"contentType\":\"{stored.ContentType}\",\"bytes\":{file.Length}}}",
            ct);

        return Ok(new
        {
            photoUrl = PhotoRoute(employeeId, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            employeeId
        });
    }

    [HttpGet("employees/{employeeId:int}/photo")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetPhoto(int employeeId, CancellationToken ct)
    {        var tenantId = RequireTenant();
        var dataScope = await _scope.ResolveAsync(User, tenantId, ct);
        if (!dataScope.CanAccessEmployee(employeeId))
            return Forbid();

        var storageUrl = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted)
            .Select(e => e.ProfilePhotoUrl)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(storageUrl))
            return NotFound();

        try
        {
            var bytes = await _storage.GetBytesAsync(tenantId, storageUrl, ct);
            return File(bytes, DetectContentType(bytes), enableRangeProcessing: false);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException)
        {
            return Forbid();
        }
    }

    internal static string PhotoRoute(int employeeId, long version = 0) =>        version > 0
            ? $"/api/employees/{employeeId}/photo?v={version}"
            : $"/api/employees/{employeeId}/photo";

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

    private static async Task<string?> ValidatePhoto(IFormFile file, CancellationToken ct)
    {        if (file is null || file.Length <= 0)
            return "A profile photo is required.";
        if (file.Length > MaxPhotoBytes)
            return "The profile photo must be 3 MB or smaller.";

        var contentType = (file.ContentType ?? string.Empty).ToLowerInvariant();
        if (contentType is not ("image/jpeg" or "image/png"))
            return "Profile photos must be JPEG or PNG.";

        await using var stream = file.OpenReadStream();
        var header = new byte[8];
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length), ct);
        var valid = contentType == "image/jpeg"
            ? read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF
            : read >= 8
              && header[0] == 0x89 && header[1] == 0x50
              && header[2] == 0x4E && header[3] == 0x47
              && header[4] == 0x0D && header[5] == 0x0A
              && header[6] == 0x1A && header[7] == 0x0A;

        return valid ? null : "The uploaded file is not a valid image.";
    }

    private static string DetectContentType(byte[] bytes)
    {        if (bytes.Length >= 3
            && bytes[0] == 0xFF
            && bytes[1] == 0xD8
            && bytes[2] == 0xFF)
            return "image/jpeg";

        if (bytes.Length >= 8
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4E
            && bytes[3] == 0x47)
            return "image/png";

        return "application/octet-stream";
    }
}
