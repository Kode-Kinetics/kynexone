using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Documents;

namespace Zayra.Api.Controllers;

/// <summary>
/// Serves another employee's profile photo to a caller whose data scope covers that employee
/// (a manager's team view). Uploading is NOT here: the one upload path is the ESS endpoint
/// <c>POST /api/ess/profile/photo</c>, which re-encodes to a ≤512px JPEG, strips EXIF, and keeps
/// the storage key in <see cref="Models.Employee.ProfilePhotoStorageKey"/> — never in
/// <c>ProfilePhotoUrl</c>, which is serialised to clients.
///
/// Two gates, both required: the caller must hold <c>employees.read</c> (the same permission the
/// employee list carries), and the employee must be inside the caller's data scope. The lookup is
/// also tenant-filtered, so another tenant's employee id answers 404 without touching storage.
/// </summary>
[ApiController]
[Route("api/employees")]
[Authorize]
public sealed class EmployeePhotoController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDocumentStorage _storage;
    private readonly IDataScopeService _scope;

    public EmployeePhotoController(
        ZayraDbContext db,
        IDocumentStorage storage,
        IDataScopeService scope)
    {
        _db = db;
        _storage = storage;
        _scope = scope;
    }

    public const string ReadPermission = "employees.read";

    [HttpGet("{employeeId:int}/photo")]
    [HasPermission(ReadPermission)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetPhoto(int employeeId, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var dataScope = await _scope.ResolveAsync(User, tenantId, ct);
        if (!dataScope.CanAccessEmployee(employeeId))
            return Forbid();

        var storageKey = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == employeeId && !e.IsDeleted)
            .Select(e => e.ProfilePhotoStorageKey)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(storageKey))
            return NotFound();

        try
        {
            var bytes = await _storage.GetBytesAsync(tenantId, storageKey, ct);
            return File(bytes, DetectContentType(bytes), enableRangeProcessing: false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or DirectoryNotFoundException)
        {
            return NotFound();
        }
    }

    private Guid RequireTenant() =>
        Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId)
            ? tenantId
            : throw new UnauthorizedAccessException("Tenant claim is missing.");

    private static string DetectContentType(byte[] bytes)
    {
        if (bytes.Length >= 3
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
