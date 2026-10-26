using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees.Onboarding;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

public sealed record NationalityDto(string Iso2, string NameEn, string NameAr, string Class);
public sealed record NationalityReviewItemDto(int EmployeeId, string EmployeeCode, string FullName, string NationalityText,
    string State, string? ProposedCode, string? ProposedClass, string LegacyClass);
public sealed record ConfirmNationalityRequest(string? NationalityCode);

/// <summary>
/// The nationality catalog for the client pickers, and the HR review queue for employees whose free-text
/// nationality could not be mapped (or would change class). Confirming a CLASS CHANGE is a per-row act that is audited.
/// </summary>
[ApiController]
[Authorize]
public sealed class NationalityController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IAuditService _audit;
    private readonly IDataScopeService _scope;

    public NationalityController(ZayraDbContext db, IAuditService audit, IDataScopeService scope)
    {
        _db = db; _audit = audit; _scope = scope;
    }

    [HttpGet("api/reference/nationalities")]
    [HasPermission("employees.read")]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Client)]
    public ActionResult<IReadOnlyList<NationalityDto>> List() =>
        Ok(NationalityCatalog.Entries.Select(e => new NationalityDto(e.Iso2, e.NameEn, e.NameAr, e.Class.ToString())).ToList());

    [HttpGet("api/employees/nationality-review")]
    [HasPermission("employees.read")]
    public async Task<ActionResult<IReadOnlyList<NationalityReviewItemDto>>> Review(CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var scope = await _scope.ResolveAsync(User, tenantId, ct);
        var rows = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted
                && (e.NationalityMappingState == NationalityMappingStates.ProposedAuto || e.NationalityMappingState == NationalityMappingStates.Unresolved))
            .OrderBy(e => e.EmployeeCode).Take(500).ToListAsync(ct);
        return Ok(rows.Where(e => scope.CanAccessEmployee(e.Id)).Select(e => new NationalityReviewItemDto(
            e.Id, e.EmployeeCode, e.FullName, e.Nationality, e.NationalityMappingState, e.NationalityCode,
            NationalityCatalog.ClassOf(e.NationalityCode)?.ToString(), NationalityCatalog.LegacyClassOf(e.Nationality).ToString())).ToList());
    }

    [HttpPost("api/employees/{id:int}/nationality/confirm")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> Confirm(int id, [FromBody] ConfirmNationalityRequest body, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var scope = await _scope.ResolveAsync(User, tenantId, ct);
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == id && !e.IsDeleted, ct);
        if (employee is null || !scope.CanAccessEmployee(id)) return NotFound();
        var entry = NationalityCatalog.FindByIso2(body.NationalityCode ?? employee.NationalityCode);
        if (entry is null) return UnprocessableEntity(new { code = "nationality_unknown", error = "Choose a nationality from the list." });

        var before = NationalityCatalog.LegacyClassOf(employee.Nationality);
        employee.NationalityCode = entry.Iso2;
        employee.Nationality = entry.NameEn;
        employee.NationalityMappingState = NationalityMappingStates.Confirmed;
        if (before != entry.Class)
            await _audit.WriteAsync("employee.nationality_reclassified", "Employee", id.ToString(),
                new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), UserId(), tenantId),
                System.Text.Json.JsonSerializer.Serialize(new { beforeClass = before.ToString(), afterClass = entry.Class.ToString(), code = entry.Iso2 }), ct);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private Guid RequireTenant() =>
        Guid.TryParse(User.FindFirstValue("tenant_id"), out var t) ? t : throw new UnauthorizedAccessException("Tenant claim is missing.");

    private Guid? UserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var u) ? u : null;
}
