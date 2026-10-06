using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Entitlements;

/// <summary>
/// The employee's dependants (Release A slice R2, CTO decision in review round 1): HR records the spouse and children a
/// medical, ticket or education entitlement covers. Uses the existing <c>employee_dependents</c> table — no migration.
/// Under /api/entitlements, so closed unless the tenant has release_a on. Every change is audited; the ID number is never
/// written to the audit log in full, and is shown in full only to callers with employees.write or employees.sensitive.
/// Removing a dependant is a soft delete (expand-only migration 20261008000200).
/// </summary>
[Authorize]
[ApiController]
[Route("api/entitlements/employees/{employeeId:int}/dependants")]
public sealed class EmployeeDependantsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly ITenantClock _clock;

    public EmployeeDependantsController(ZayraDbContext db, ITenantClock clock)
    {
        _db = db;
        _clock = clock;
    }

    [HttpGet]
    [HasPermission("employees.read")]
    public async Task<IActionResult> List(int employeeId, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        if (await VisibleEmployeeAsync(tid, employeeId, ct) is null) return EmployeeNotFound();
        var rows = await _db.EmployeeDependents.AsNoTracking().Where(x => x.TenantId == tid && x.EmployeeId == employeeId && !x.IsDeleted)
            .OrderBy(x => x.DateOfBirth).ToListAsync(ct);
        var reveal = CanSeeIdNumbers();
        return Ok(rows.Select(r => Dto(r, reveal)));
    }

    [HttpPost]
    [HasPermission("employees.write")]
    public async Task<IActionResult> Add(int employeeId, [FromBody] DependantInput input, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var employee = await VisibleEmployeeAsync(tid, employeeId, ct);
        if (employee is null) return EmployeeNotFound();
        if (await ValidateAsync(tid, input, ct) is { } problem) return BadRequest(problem);
        var row = new EmployeeDependent { TenantId = tid, EmployeeId = employeeId };
        Apply(row, input);
        _db.EmployeeDependents.Add(row);
        Audit(tid, employee, row, "DependantAdded");
        await _db.SaveChangesAsync(ct);
        return Ok(Dto(row, true));
    }

    [HttpPut("{dependantId:guid}")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> Update(int employeeId, Guid dependantId, [FromBody] DependantInput input, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var employee = await VisibleEmployeeAsync(tid, employeeId, ct);
        if (employee is null) return EmployeeNotFound();
        var row = await _db.EmployeeDependents.FirstOrDefaultAsync(x => x.TenantId == tid && x.EmployeeId == employeeId && x.Id == dependantId && !x.IsDeleted, ct);
        if (row is null) return NotFound(new { error = "dependant_not_found", message = "That dependant isn't on this employee's file." });
        if (await ValidateAsync(tid, input, ct) is { } problem) return BadRequest(problem);
        Apply(row, input);
        Audit(tid, employee, row, "DependantChanged");
        await _db.SaveChangesAsync(ct);
        return Ok(Dto(row, true));
    }

    [HttpDelete("{dependantId:guid}")]
    [HasPermission("employees.write")]
    public async Task<IActionResult> Remove(int employeeId, Guid dependantId, CancellationToken ct)
    {
        if (this.GetTenantId() is not Guid tid) return Unauthorized();
        var employee = await VisibleEmployeeAsync(tid, employeeId, ct);
        if (employee is null) return EmployeeNotFound();
        var row = await _db.EmployeeDependents.FirstOrDefaultAsync(x => x.TenantId == tid && x.EmployeeId == employeeId && x.Id == dependantId && !x.IsDeleted, ct);
        if (row is null) return NotFound(new { error = "dependant_not_found", message = "That dependant isn't on this employee's file." });
        // Soft delete: the package history that counted this dependant stays explainable.
        row.IsDeleted = true;
        row.DeletedAtUtc = DateTime.UtcNow;
        row.DeletedBy = this.GetUserId();
        Audit(tid, employee, row, "DependantRemoved");
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    /// <param name="revealIdNumber">False: the Iqama / ID number is masked to its last 4 (SensitiveValueMask.MaskId).</param>
    public static DependantDto Dto(EmployeeDependent x, bool revealIdNumber) =>
        new(x.Id, x.FullName, Normalise(x.Relationship), x.DateOfBirth,
            revealIdNumber ? x.NationalId : SensitiveValueMask.MaskId(x.NationalId));

    /// <summary>The full ID number only for those who may change it (employees.write) or see identity fields (employees.sensitive).</summary>
    private bool CanSeeIdNumbers() =>
        User.HasClaim("permission", "employees.write") || User.HasClaim("permission", "employees.sensitive");

    /// <summary>Stored values are the enum; older free-text rows are shown in the enum's terms.</summary>
    public static string Normalise(string? relationship) =>
        PackageRules.IsSpouse(relationship) ? DependantRelationships.Spouse
        : PackageRules.IsChild(relationship) ? DependantRelationships.Child
        : DependantRelationships.All.FirstOrDefault(r => string.Equals(r, relationship?.Trim(), StringComparison.OrdinalIgnoreCase))
          ?? (relationship is not null && relationship.Trim().Equals("father", StringComparison.OrdinalIgnoreCase)
              || relationship?.Trim().Equals("mother", StringComparison.OrdinalIgnoreCase) == true ? DependantRelationships.Parent : DependantRelationships.Other);

    private async Task<object?> ValidateAsync(Guid tid, DependantInput input, CancellationToken ct)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.FullName) || input.FullName.Trim().Length > 200) errors.Add("Enter the dependant's full name (up to 200 characters).");
        if (!DependantRelationships.All.Contains(input.Relationship)) errors.Add("Choose the relationship: spouse, child, parent or other.");
        var today = await _clock.TodayAsync(tid, ct);
        if (input.DateOfBirth is not DateOnly dob) errors.Add("Enter the date of birth.");
        else if (dob > today) errors.Add("The date of birth can't be in the future.");
        else if (dob < today.AddYears(-120)) errors.Add("Check the date of birth.");
        if (input.NationalId is { } id && (id.Trim().Length > 30 || !id.Trim().All(char.IsLetterOrDigit)))
            errors.Add("The Iqama or ID number can only have letters and digits (up to 30).");
        return errors.Count == 0 ? null : new { error = "invalid_dependant", message = string.Join(" ", errors), errors };
    }

    private static void Apply(EmployeeDependent row, DependantInput input)
    {
        row.FullName = input.FullName!.Trim();
        row.Relationship = input.Relationship!;
        row.DateOfBirth = input.DateOfBirth;
        row.NationalId = input.NationalId?.Trim() ?? string.Empty;
    }

    private void Audit(Guid tid, Employee employee, EmployeeDependent row, string action) =>
        _db.ComplianceAuditLogs.Add(new ComplianceAuditLog
        {
            TenantId = tid, EntityType = "EmployeeDependent", EntityId = row.Id.ToString(), EmployeeId = employee.PublicId, Action = action,
            PerformedByUserId = this.GetUserId(), PerformedByName = User.Identity?.Name ?? string.Empty,
            MetadataJson = JsonSerializer.Serialize(new
            {
                row.FullName, row.Relationship, row.DateOfBirth,
                nationalId = SensitiveValueMask.MaskId(row.NationalId),
            }),
        });

    private NotFoundObjectResult EmployeeNotFound() =>
        NotFound(new { error = "employee_not_found", message = "That employee doesn't exist or isn't in your companies." });

    private async Task<Employee?> VisibleEmployeeAsync(Guid tid, int employeeId, CancellationToken ct)
    {
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == employeeId && !x.IsDeleted, ct);
        return employee is not null && this.GetRequestScope().CanAccessCompany(employee.CompanyId) ? employee : null;
    }
}

public sealed record DependantInput(string? FullName, string? Relationship, DateOnly? DateOfBirth, string? NationalId);
public sealed record DependantDto(Guid Id, string FullName, string Relationship, DateOnly? DateOfBirth, string NationalId);
