using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Attendance;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Attendance;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// The attendance rule sets (Setup -> Attendance rules). Attendance processing reads these rows to decide lateness,
/// early exit, half days and the working day; until this API existed the only way to create one was the old setup
/// studio, and nothing could change one afterwards.
///
/// <para><b>What an edit changes.</b> Days already processed are stored (late / early-exit minutes, status, deductions)
/// and are never rewritten by an edit here. The new rules apply to days processed from now on, and to a day that is
/// recomputed. Before a payroll run consumes a period, the attendance check compares each unpaid day with today's
/// rules and lists the days that differ under "Recompute these days" (ERR_ATTENDANCE_IMPACT_STALE, see
/// <see cref="AttendanceImpactReconciliation"/>); paid and payroll-locked days are never judged or changed.</para>
///
/// <para><b>Why the scope rules.</b> The processor picks an employee's policy by explicit assignment, then by the most
/// specific branch / department / grade match, then by lowest code. Two active policies for the same scope would be
/// picked by code alone, and with no company-wide policy a restricted one would fall to employees it was not written
/// for, so both are refused here. Every change runs under the tenant's default-policy lock, the same one the
/// processor takes to create the DEFAULT policy, so two concurrent edits cannot both pass those checks.</para>
/// </summary>
[ApiController]
[Route("api/attendance/policies")]
[Authorize(Roles = "Admin,HR Manager")]
public class AttendancePoliciesController : ControllerBase
{
    private static readonly Regex CodePattern = new("^[A-Z0-9][A-Z0-9_-]{0,39}$", RegexOptions.Compiled);
    private readonly ZayraDbContext _db;

    public AttendancePoliciesController(ZayraDbContext db) => _db = db;

    [HttpGet]
    [HasPermission("attendance.policy_manage")]
    public async Task<ActionResult<IReadOnlyCollection<AttendancePolicyDto>>> List(CancellationToken ct)
    {
        var tenantId = RequireTenant();
        // A tenant that has never processed attendance has no row yet, although the processor would use the built-in
        // rules. Showing and editing a real DEFAULT policy is truer than an empty list.
        if (!await _db.AttendancePolicies.AnyAsync(x => x.TenantId == tenantId, ct))
        {
            await AttendanceDailyRecordLock.RunForDefaultPolicyAsync(_db, tenantId, async () =>
            {
                if (await _db.AttendancePolicies.AnyAsync(x => x.TenantId == tenantId, ct)) return true;
                _db.AttendancePolicies.Add(AttendanceService.DefaultPolicy(tenantId));
                await _db.SaveChangesAsync(ct);
                return true;
            }, ct);
        }
        var rows = await _db.AttendancePolicies.AsNoTracking().Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.IsActive).ThenBy(x => x.Code).ToListAsync(ct);
        return Ok(rows.Select(Project).ToList());
    }

    [HttpGet("{id:guid}")]
    [HasPermission("attendance.policy_manage")]
    public async Task<ActionResult<AttendancePolicyDto>> Get(Guid id, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var row = await _db.AttendancePolicies.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        return row is null ? NotFound() : Ok(Project(row));
    }

    [HttpPost]
    [HasPermission("attendance.policy_manage")]
    public async Task<ActionResult<AttendancePolicyDto>> Create(AttendancePolicyRequest req, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var invalid = await ValidateAsync(tenantId, req, ct);
        if (invalid is not null) return BadRequest(new { message = invalid });
        var code = req.Code.Trim().ToUpperInvariant();

        return await AttendanceDailyRecordLock.RunForDefaultPolicyAsync<ActionResult<AttendancePolicyDto>>(_db, tenantId, async () =>
        {
            if (await _db.AttendancePolicies.AnyAsync(x => x.TenantId == tenantId && x.Code.ToUpper() == code, ct))
                return Conflict(new { message = $"A policy with the code {code} already exists." });
            var policy = new AttendancePolicy { TenantId = tenantId, Code = code, IsActive = true };
            Apply(policy, req);
            var refusal = await ScopeRefusalAsync(tenantId, policy, ct);
            if (refusal is not null) return Conflict(new { message = refusal });
            _db.AttendancePolicies.Add(policy);
            Audit("attendance.policy_created", policy, new { after = Snapshot(policy) });
            await _db.SaveChangesAsync(ct);
            return Created($"/api/attendance/policies/{policy.Id}", Project(policy));
        }, ct);
    }

    [HttpPut("{id:guid}")]
    [HasPermission("attendance.policy_manage")]
    public async Task<ActionResult<AttendancePolicyDto>> Update(Guid id, AttendancePolicyRequest req, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var invalid = await ValidateAsync(tenantId, req, ct);
        if (invalid is not null) return BadRequest(new { message = invalid });

        return await AttendanceDailyRecordLock.RunForDefaultPolicyAsync<ActionResult<AttendancePolicyDto>>(_db, tenantId, async () =>
        {
            var policy = await _db.AttendancePolicies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (policy is null) return NotFound();
            // Employees are assigned to a policy by its code (Employee.AttendancePolicyCode), so a rename would orphan them.
            if (!string.Equals(policy.Code, req.Code.Trim(), StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "A policy's code cannot be changed: employees are assigned to it by code. Create a new policy instead." });
            var before = Snapshot(policy);
            var wasCompanyWide = IsCompanyWide(policy);
            Apply(policy, req);
            if (policy.IsActive)
            {
                var refusal = await ScopeRefusalAsync(tenantId, policy, ct);
                if (refusal is not null) return Conflict(new { message = refusal });
                // Narrowing the only company-wide policy would leave nothing to fall back on.
                if (wasCompanyWide && !IsCompanyWide(policy)
                    && !await _db.AttendancePolicies.AnyAsync(x => x.TenantId == tenantId && x.IsActive && x.Id != id
                        && x.BranchId == null && x.DepartmentId == null && x.GradeId == null, ct))
                    return Conflict(new { message = "This is the only company-wide policy, so it cannot be restricted to a branch, department or grade. Add another company-wide policy first." });
            }
            Audit("attendance.policy_updated", policy, new { before, after = Snapshot(policy) });
            await _db.SaveChangesAsync(ct);
            return Ok(Project(policy));
        }, ct);
    }

    [HttpPost("{id:guid}/deactivate")]
    [HasPermission("attendance.policy_manage")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        return await AttendanceDailyRecordLock.RunForDefaultPolicyAsync<IActionResult>(_db, tenantId, async () =>
        {
            var policy = await _db.AttendancePolicies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (policy is null) return NotFound();
            if (!policy.IsActive) return Ok(Project(policy));
            var others = await _db.AttendancePolicies.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive && x.Id != id).ToListAsync(ct);
            if (others.Count == 0)
                return Conflict(new { message = "This is the only active policy, so it cannot be turned off. Attendance needs at least one." });
            if (IsCompanyWide(policy) && !others.Any(IsCompanyWide))
                return Conflict(new { message = "This is the only company-wide policy, so it cannot be turned off. Employees outside the other policies' branch, department or grade would have no rules." });
            policy.IsActive = false;
            Audit("attendance.policy_deactivated", policy, new { code = policy.Code });
            await _db.SaveChangesAsync(ct);
            return Ok(Project(policy));
        }, ct);
    }

    [HttpPost("{id:guid}/reactivate")]
    [HasPermission("attendance.policy_manage")]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        return await AttendanceDailyRecordLock.RunForDefaultPolicyAsync<IActionResult>(_db, tenantId, async () =>
        {
            var policy = await _db.AttendancePolicies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (policy is null) return NotFound();
            if (policy.IsActive) return Ok(Project(policy));
            policy.IsActive = true;
            var refusal = await ScopeRefusalAsync(tenantId, policy, ct);
            if (refusal is not null)
            {
                policy.IsActive = false;
                return Conflict(new { message = refusal });
            }
            Audit("attendance.policy_reactivated", policy, new { code = policy.Code });
            await _db.SaveChangesAsync(ct);
            return Ok(Project(policy));
        }, ct);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private async Task<string?> ValidateAsync(Guid tenantId, AttendancePolicyRequest req, CancellationToken ct)
    {
        var code = (req.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern.IsMatch(code)) return "The code must be 1 to 40 letters, digits, hyphens or underscores, with no spaces.";
        var name = (req.Name ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > 120) return "Give the policy a name of up to 120 characters.";
        if (req.GraceMinutes is < 0 or > 120) return "The grace period must be between 0 and 120 minutes.";
        if (req.EarlyExitThresholdMinutes is < 0 or > 240) return "The early-leaving allowance must be between 0 and 240 minutes.";
        if (req.StandardWorkMinutes is < 60 or > 960) return "The standard working day must be between 60 and 960 minutes.";
        if (req.BreakMinutes < 0 || req.BreakMinutes >= req.StandardWorkMinutes) return "The break must be at least 0 minutes and shorter than the working day.";
        if (req.HalfDayThresholdMinutes < 1 || req.HalfDayThresholdMinutes > req.StandardWorkMinutes) return "The half-day threshold must be above 0 and no more than the working day.";
        if (req.BranchId is { } branch && !await _db.Branches.AnyAsync(x => x.TenantId == tenantId && x.Id == branch && !x.IsDeleted, ct)) return "That branch does not exist.";
        if (req.DepartmentId is { } dept && !await _db.Departments.AnyAsync(x => x.TenantId == tenantId && x.Id == dept && !x.IsDeleted, ct)) return "That department does not exist.";
        if (req.GradeId is { } grade && !await _db.Grades.AnyAsync(x => x.TenantId == tenantId && x.Id == grade && !x.IsDeleted, ct)) return "That grade does not exist.";
        return null;
    }

    /// <summary>Why an ACTIVE <paramref name="policy"/> may not hold its scope, or null. Excludes the policy itself.</summary>
    private async Task<string?> ScopeRefusalAsync(Guid tenantId, AttendancePolicy policy, CancellationToken ct)
    {
        if (!policy.IsActive) return null;
        var rivals = await _db.AttendancePolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsActive && x.Id != policy.Id).ToListAsync(ct);
        var same = rivals.FirstOrDefault(x => x.BranchId == policy.BranchId && x.DepartmentId == policy.DepartmentId && x.GradeId == policy.GradeId);
        if (same is not null)
            return $"The policy {same.Code} already covers the same employees. Edit that one, or choose a different branch, department or grade.";
        if (!IsCompanyWide(policy) && !rivals.Any(IsCompanyWide))
            return "Set up a company-wide policy first. Employees outside this branch, department or grade need one to fall back on.";
        return null;
    }

    private static bool IsCompanyWide(AttendancePolicy p) => p.BranchId is null && p.DepartmentId is null && p.GradeId is null;

    private static void Apply(AttendancePolicy policy, AttendancePolicyRequest req)
    {
        policy.Name = req.Name.Trim();
        policy.BranchId = req.BranchId;
        policy.DepartmentId = req.DepartmentId;
        policy.GradeId = req.GradeId;
        policy.GraceMinutes = req.GraceMinutes;
        policy.EarlyExitThresholdMinutes = req.EarlyExitThresholdMinutes;
        policy.HalfDayThresholdMinutes = req.HalfDayThresholdMinutes;
        policy.StandardWorkMinutes = req.StandardWorkMinutes;
        policy.BreakMinutes = req.BreakMinutes;
    }

    private static AttendancePolicyDto Project(AttendancePolicy p) => new(p.Id, p.Code, p.Name, p.BranchId, p.DepartmentId, p.GradeId,
        p.GraceMinutes, p.EarlyExitThresholdMinutes, p.HalfDayThresholdMinutes, p.StandardWorkMinutes, p.BreakMinutes, p.IsActive);

    private static AttendancePolicyDto Snapshot(AttendancePolicy p) => Project(p);

    private void Audit(string action, AttendancePolicy policy, object detail)
    {
        var context = new RequestContext(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), GetUserId(), policy.TenantId);
        _db.AuditLogs.Add(AuthAuditEntry.Create(Guid.NewGuid(), DateTime.UtcNow, action, nameof(AttendancePolicy), policy.Id.ToString(), context,
            JsonSerializer.Serialize(new { source = "attendance_rules", code = policy.Code, detail })));
    }

    private Guid RequireTenant() => Guid.Parse(User.FindFirstValue("tenant_id")!);
    private Guid? GetUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;
}
