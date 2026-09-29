using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Controllers;

/// <summary>
/// Employee self-service view of the caller's OWN benefit enrolments ("My benefits"). Read-only.
/// Kept out of EmployeeSelfServiceController deliberately; resolves the caller's employee id the same
/// way ESS does (employee_id claim, then an unambiguous work/personal-email match) and never accepts an
/// employee id from the request, so there is no IDOR surface.
/// </summary>
[ApiController]
[Route("api/ess/benefits")]
[Authorize]
public class EssBenefitsController : ControllerBase
{
    private readonly ZayraDbContext _db;

    public EssBenefitsController(ZayraDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> MyBenefits(CancellationToken ct)
    {
        var accessMode = User.FindFirstValue("access_mode") ?? string.Empty;
        if (accessMode is "NoLogin" or "KioskOnly")
            return BadRequest(new { message = "This access mode cannot use ESS." });
        if (!HasPermission("ess.read") && !HasPermission("ess.write"))
            return Forbid();
        if (!Guid.TryParse(User.FindFirstValue("tenant_id"), out var tenantId))
            return Unauthorized();

        var (employeeId, error) = await ResolveCallerEmployeeIdAsync(tenantId, ct);
        if (employeeId is null)
            return NotFound(new { message = error });

        var enrollments = await _db.BenefitEnrollments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId)
            .OrderByDescending(x => x.EffectiveFrom)
            .ToListAsync(ct);
        var planIds = enrollments.Select(e => e.BenefitPlanId).Distinct().ToList();
        var plans = await _db.BenefitPlans.AsNoTracking()
            .Where(p => p.TenantId == tenantId && planIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        var enrollmentIds = enrollments.Select(e => e.Id).ToList();
        var contributions = await _db.BenefitContributions.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.EmployeeId == employeeId && enrollmentIds.Contains(c.BenefitEnrollmentId))
            .OrderByDescending(c => c.EffectiveFrom)
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var items = enrollments.Select(e =>
        {
            plans.TryGetValue(e.BenefitPlanId, out var plan);
            var current = contributions
                .Where(c => c.BenefitEnrollmentId == e.Id && c.IsActive && c.EffectiveFrom <= today && (!c.EffectiveTo.HasValue || c.EffectiveTo >= today))
                .OrderByDescending(c => c.EffectiveFrom)
                .FirstOrDefault();
            return new EssBenefitEnrollmentDto(
                e.Id,
                e.BenefitPlanId,
                plan?.Code ?? string.Empty,
                plan?.Name ?? "Benefit plan",
                plan?.PlanType ?? string.Empty,
                plan?.Currency ?? string.Empty,
                e.CoverageTier,
                e.EffectiveFrom,
                e.EffectiveTo,
                e.Status,
                current?.EmployeeAmount,
                current?.EmployerAmount,
                current?.Frequency);
        }).ToList();

        return Ok(new EssBenefitsDto(employeeId.Value, items));
    }

    private async Task<(int? EmployeeId, string Error)> ResolveCallerEmployeeIdAsync(Guid tenantId, CancellationToken ct)
    {
        if (int.TryParse(User.FindFirstValue("employee_id"), out var empId))
            return (empId, string.Empty);

        var email = User.FindFirstValue("email") ?? User.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(email))
        {
            var normalizedEmail = email.Trim().ToUpperInvariant();
            var matches = await _db.Employees.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted &&
                    (x.WorkEmail.ToUpper() == normalizedEmail || x.PersonalEmail.ToUpper() == normalizedEmail))
                .Select(x => x.Id).Take(2).ToListAsync(ct);
            if (matches.Count == 1) return (matches[0], string.Empty);
            if (matches.Count > 1) return (null, "Multiple employee records match this email. Ask HR to link your account explicitly.");
        }
        return (null, "Your user account is not linked to an employee record. Ask HR to link your account via User Management → Invite Employee.");
    }

    private bool HasPermission(string permission) =>
        User.Claims.Any(x => x.Type == "permission" && x.Value == permission);
}

public record EssBenefitEnrollmentDto(
    Guid Id, Guid BenefitPlanId, string PlanCode, string PlanName, string PlanType, string Currency,
    string CoverageTier, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string Status,
    decimal? CurrentEmployeeAmount, decimal? CurrentEmployerAmount, string? ContributionFrequency);

public record EssBenefitsDto(int EmployeeId, IReadOnlyList<EssBenefitEnrollmentDto> Enrollments);
