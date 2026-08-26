using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Shifts;
using Zayra.Api.Application.WorkWeek;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.WorkWeek;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/shifts")]
[Authorize]
public class ShiftsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;
    private readonly IRosterPlannerService _planner;
    private readonly IWorkWeekService _workWeek;

    public ShiftsController(ZayraDbContext db, IDataScopeService scopeService, IRosterPlannerService planner, IWorkWeekService? workWeek = null)
    {
        _db = db;
        _scopeService = scopeService;
        _planner = planner;
        // Optional so existing callers/tests keep working; DI always supplies the real one.
        _workWeek = workWeek ?? new WorkWeekService(db);
    }

    // ── Shift Definitions ─────────────────────────────────────────────

    [HttpGet("definitions")]
    public async Task<IActionResult> ListDefinitions(CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var defs = await _db.ShiftDefinitions
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.StartTime)
            .ToListAsync(ct);
        return Ok(defs);
    }

    [HttpPost("definitions")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> CreateDefinition([FromBody] ShiftDefinitionRequest req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (!TimeOnly.TryParse(req.StartTime, out var startTime) || !TimeOnly.TryParse(req.EndTime, out var endTime))
            return BadRequest(new { message = "Start and end times must use a valid time format." });
        if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name) || req.BreakMinutes < 0)
            return BadRequest(new { message = "Code, name, and a non-negative break are required." });
        if (await _db.ShiftDefinitions.AnyAsync(x => x.TenantId == tenantId && x.Code == req.Code.ToUpper(), ct))
            return Conflict(new { message = "Shift code already exists." });
        var def = new ShiftDefinition
        {
            TenantId = tenantId,
            Code = req.Code.ToUpperInvariant(),
            Name = req.Name,
            StartTime = startTime,
            EndTime = endTime,
            BreakMinutes = req.BreakMinutes,
            Color = req.Color,
            IsActive = true,
        };
        _db.ShiftDefinitions.Add(def);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/shifts/definitions/{def.Id}", def);
    }

    [HttpPut("definitions/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> UpdateDefinition(Guid id, [FromBody] ShiftDefinitionRequest req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var def = await _db.ShiftDefinitions.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (def is null) return NotFound();
        if (!TimeOnly.TryParse(req.StartTime, out var startTime) || !TimeOnly.TryParse(req.EndTime, out var endTime))
            return BadRequest(new { message = "Start and end times must use a valid time format." });
        if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name) || req.BreakMinutes < 0)
            return BadRequest(new { message = "Code, name, and a non-negative break are required." });
        if (await _db.ShiftDefinitions.AnyAsync(x => x.TenantId == tenantId && x.Id != id && x.Code == req.Code.ToUpper(), ct))
            return Conflict(new { message = "Shift code already exists." });

        def.Code = req.Code.ToUpperInvariant();
        def.Name = req.Name;
        def.StartTime = startTime;
        def.EndTime = endTime;
        def.BreakMinutes = req.BreakMinutes;
        def.Color = req.Color;
        def.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(def);
    }

    [HttpDelete("definitions/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> DeleteDefinition(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var def = await _db.ShiftDefinitions.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (def is null) return NotFound();
        if (await _db.ShiftAssignments.AnyAsync(x => x.TenantId == tenantId && x.ShiftDefinitionId == id, ct))
            return Conflict(new { message = "Shift definition is in use. Reassign employees before deleting it." });
        _db.ShiftDefinitions.Remove(def);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Roster ────────────────────────────────────────────────────────

    [HttpGet("roster")]
    public async Task<IActionResult> GetRoster([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var start = from ?? today.AddDays(-daysSinceMonday); // Monday
        var end = to ?? start.AddDays(6); // Sunday

        var employeeQuery = _db.Employees
            .Where(e => e.TenantId == tenantId && e.Status == "Active");
        if (!scope.IsUnrestricted)
            employeeQuery = employeeQuery.Where(e => scope.AllowedEmployeeIds!.Contains(e.Id));

        var employees = await employeeQuery
            .OrderBy(e => e.Department).ThenBy(e => e.FullName)
            .Select(e => new RosterEmployee(e.Id, e.FullName, e.Department, e.EmployeeCode))
            .ToListAsync(ct);

        var assignmentQuery = _db.ShiftAssignments
            .Where(a => a.TenantId == tenantId && a.AssignedDate >= start && a.AssignedDate <= end);
        if (!scope.IsUnrestricted)
            assignmentQuery = assignmentQuery.Where(a => scope.AllowedEmployeeIds!.Contains(a.EmployeeId));

        var assignments = await assignmentQuery
            .OrderBy(a => a.AssignedDate)
            .Select(a => new RosterAssignment(a.Id, a.EmployeeId, a.AssignedDate, a.ShiftDefinitionId, a.ShiftName, a.ShiftCode, a.ShiftColor))
            .ToListAsync(ct);

        return Ok(new { from = start, to = end, employees, assignments });
    }

    [HttpPost("roster/assign")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager,Supervisor")]
    public async Task<IActionResult> Assign([FromBody] AssignShiftRequest req, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == req.EmployeeId && e.TenantId == tenantId, ct);
        if (emp is null) return BadRequest(new { message = "Employee not found." });
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(req.EmployeeId)) return Forbid();

        var def = await _db.ShiftDefinitions.FirstOrDefaultAsync(d => d.Id == req.ShiftDefinitionId && d.TenantId == tenantId, ct);
        if (def is null) return BadRequest(new { message = "Shift definition not found." });
        var constraintError = await ValidateAssignmentAsync(tenantId, emp.Id, req.Date, def, ct);
        if (constraintError is not null) return Conflict(new { message = constraintError });

        var existing = await _db.ShiftAssignments
            .FirstOrDefaultAsync(a => a.EmployeeId == req.EmployeeId && a.AssignedDate == req.Date && a.TenantId == tenantId, ct);

        if (existing is not null)
        {
            existing.ShiftDefinitionId = def.Id;
            existing.ShiftName = def.Name;
            existing.ShiftCode = def.Code;
            existing.ShiftColor = def.Color;
            existing.Notes = req.Notes ?? string.Empty;
        }
        else
        {
            _db.ShiftAssignments.Add(new ShiftAssignment
            {
                TenantId = tenantId,
                EmployeeId = req.EmployeeId,
                EmployeeName = emp.FullName,
                ShiftDefinitionId = def.Id,
                ShiftName = def.Name,
                ShiftCode = def.Code,
                ShiftColor = def.Color,
                AssignedDate = req.Date,
                Notes = req.Notes ?? string.Empty,
            });
        }

        await _db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpPost("roster/auto-plan")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> AutoPlan([FromBody] AutoPlanRequest req, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        if (req.DateFrom > req.DateTo)
            return BadRequest(new { message = "From date must be before To date." });
        if (req.DateTo.DayNumber - req.DateFrom.DayNumber > 366)
            return BadRequest(new { message = "Auto-plan is limited to 367 days." });
        if (req.ShiftIds == null || req.ShiftIds.Count == 0)
            return BadRequest(new { message = "At least one shift must be selected." });

        var shifts = await _db.ShiftDefinitions
            .Where(d => req.ShiftIds.Contains(d.Id) && d.TenantId == tenantId && d.IsActive)
            .ToListAsync(ct);
        if (shifts.Count == 0)
            return BadRequest(new { message = "No valid active shifts found." });

        var employeeQuery = _db.Employees.Where(e => e.TenantId == tenantId && e.Status == "Active");
        if (req.EmployeeIds != null && req.EmployeeIds.Count > 0)
            employeeQuery = employeeQuery.Where(e => req.EmployeeIds.Contains(e.Id));
        var employees = await employeeQuery.Select(e => new { e.Id, e.FullName }).ToListAsync(ct);

        // Weekend skip honours the configured working week (WorkWeekService), not a hard-coded Sat/Sun.
        var autoAssignWeekend = await _workWeek.ResolveAsync(tenantId, null, null, ct);
        var days = new List<DateOnly>();
        for (var d = req.DateFrom; d <= req.DateTo; d = d.AddDays(1))
        {
            if (req.SkipWeekend && autoAssignWeekend.IsWeekend(d.DayOfWeek))
                continue;
            days.Add(d);
        }

        var existingKeys = (await _db.ShiftAssignments
            .Where(a => a.TenantId == tenantId && a.AssignedDate >= req.DateFrom && a.AssignedDate <= req.DateTo)
            .Select(a => new { a.EmployeeId, a.AssignedDate })
            .ToListAsync(ct))
            .Select(a => (a.EmployeeId, a.AssignedDate))
            .ToHashSet(new AssignmentKeyComparer());

        int created = 0, skipped = 0;
        for (int ei = 0; ei < employees.Count; ei++)
        {
            var emp = employees[ei];
            for (int di = 0; di < days.Count; di++)
            {
                var day = days[di];
                if (req.OverwriteExisting == false && existingKeys.Contains((emp.Id, day))) { skipped++; continue; }

                var shift = req.Pattern switch
                {
                    "rotating" => shifts[(ei + di) % shifts.Count],
                    "alternating" => shifts[di % shifts.Count],
                    _ => shifts[0],
                };

                var constraintError = await ValidateAssignmentAsync(tenantId, emp.Id, day, shift, ct);
                if (constraintError is not null) { skipped++; continue; }

                var existing = await _db.ShiftAssignments
                    .FirstOrDefaultAsync(a => a.EmployeeId == emp.Id && a.AssignedDate == day && a.TenantId == tenantId, ct);
                if (existing != null)
                {
                    existing.ShiftDefinitionId = shift.Id; existing.ShiftName = shift.Name;
                    existing.ShiftCode = shift.Code; existing.ShiftColor = shift.Color;
                }
                else
                {
                    _db.ShiftAssignments.Add(new ShiftAssignment
                    {
                        TenantId = tenantId, EmployeeId = emp.Id, EmployeeName = emp.FullName,
                        ShiftDefinitionId = shift.Id, ShiftName = shift.Name,
                        ShiftCode = shift.Code, ShiftColor = shift.Color,
                        AssignedDate = day,
                    });
                }
                created++;
            }
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { created, skipped, employees = employees.Count, days = days.Count });
    }

    // ── Rostering Policy ──────────────────────────────────────────────
    // Per-tenant rules that drive the intelligent planner. One row per tenant.

    [HttpGet("policy")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> GetPolicy(CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var policy = await _db.ShiftPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
        if (policy is null)
        {
            // No policy yet — infer sensible defaults from the tenant's shift names so the
            // planner works out of the box (morning/evening/night/afternoon keyword matching).
            var shifts = await _db.ShiftDefinitions.AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.IsActive).ToListAsync(ct);
            return Ok(DefaultPolicyDto(shifts));
        }
        return Ok(ToDto(policy));
    }

    [HttpPut("policy")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> UpsertPolicy([FromBody] ShiftPolicyDto dto, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var policy = await _db.ShiftPolicies.FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
        if (policy is null)
        {
            policy = new ShiftPolicy { TenantId = tenantId };
            _db.ShiftPolicies.Add(policy);
        }
        policy.GenderShiftRulesJson = JsonSerializer.Serialize(dto.GenderRules ?? new());
        policy.VoluntaryShiftCodesJson = JsonSerializer.Serialize(dto.VoluntaryShiftCodes ?? new());
        policy.WeekendDemandJson = JsonSerializer.Serialize(dto.WeekendDemand ?? new());
        policy.HolidayDemandJson = JsonSerializer.Serialize(dto.HolidayDemand ?? new());
        policy.MinRestHours = dto.MinRestHours > 0 ? dto.MinRestHours : 8;
        policy.MaxConsecutiveDays = dto.MaxConsecutiveDays > 0 ? dto.MaxConsecutiveDays : 6;
        policy.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(policy));
    }

    // ── Intelligent (AI) roster planning ──────────────────────────────
    // Preview returns a proposed plan WITHOUT saving; commit persists an approved plan.

    [HttpPost("roster/ai-plan")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> AiPlan([FromBody] AiPlanRequest req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (req.DateFrom > req.DateTo)
            return BadRequest(new { message = "From date must be before To date." });

        var shifts = await _db.ShiftDefinitions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.IsActive).ToListAsync(ct);
        if (shifts.Count == 0)
            return BadRequest(new { message = "Define at least one active shift before planning." });

        var employeeQuery = _db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && e.Status == "Active");
        if (req.EmployeeIds is { Count: > 0 })
            employeeQuery = employeeQuery.Where(e => req.EmployeeIds.Contains(e.Id));
        var employees = await employeeQuery
            .Select(e => new RosterPlanEmployee(e.Id, e.FullName, e.Gender, e.Department))
            .ToListAsync(ct);
        if (employees.Count == 0)
            return BadRequest(new { message = "No active employees match the selection." });

        var policyEntity = await _db.ShiftPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
        var policyDto = policyEntity is null ? DefaultPolicyDto(shifts) : ToDto(policyEntity);

        var holidays = (await _db.PublicHolidays.AsNoTracking()
            .Where(h => h.TenantId == tenantId && h.Date >= req.DateFrom && h.Date <= req.DateTo)
            .Select(h => h.Date).ToListAsync(ct)).ToHashSet();

        // Per-run override kept: an explicit client-supplied weekend-day list wins; otherwise
        // fall back to the configured working week (WorkWeekService), never a hard-coded Sat/Sun.
        var weekendDow = WorkWeekParser.ParseWeekendDays(req.WeekendDays)
            ?? (await _workWeek.ResolveAsync(tenantId, null, null, ct)).WeekendDays;
        var weekendDays = new HashSet<DateOnly>();
        for (var d = req.DateFrom; d <= req.DateTo; d = d.AddDays(1))
            if (weekendDow.Contains(d.DayOfWeek)) weekendDays.Add(d);

        var input = new RosterPlanInput(
            req.DateFrom, req.DateTo,
            employees,
            shifts.Select(s => new RosterPlanShift(s.Id, s.Code, s.Name, s.StartTime, s.EndTime, s.Color)).ToList(),
            new RosterPlanPolicy(
                (policyDto.GenderRules ?? new()).Select(r => new GenderShiftRule(r.Gender, r.ShiftCodes ?? new(), r.Mode)).ToList(),
                policyDto.VoluntaryShiftCodes ?? new(),
                (policyDto.WeekendDemand ?? new()).Select(d => new DemandTarget(d.ShiftCode, d.Headcount)).ToList(),
                (policyDto.HolidayDemand ?? new()).Select(d => new DemandTarget(d.ShiftCode, d.Headcount)).ToList(),
                policyDto.MinRestHours, policyDto.MaxConsecutiveDays),
            holidays, weekendDays);

        var result = await _planner.PlanAsync(input, ct);
        return Ok(result);
    }

    [HttpPost("roster/ai-plan/commit")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> CommitPlan([FromBody] CommitPlanRequest req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (req.Assignments is null || req.Assignments.Count == 0)
            return BadRequest(new { message = "No assignments to commit." });

        var shifts = await _db.ShiftDefinitions
            .Where(d => d.TenantId == tenantId && d.IsActive).ToDictionaryAsync(d => d.Id, ct);
        var employeeNames = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .ToDictionaryAsync(e => e.Id, e => e.FullName, ct);

        int created = 0, updated = 0, skipped = 0;
        foreach (var a in req.Assignments)
        {
            if (!shifts.TryGetValue(a.ShiftDefinitionId, out var shift)) { skipped++; continue; }
            if (!employeeNames.TryGetValue(a.EmployeeId, out var empName)) { skipped++; continue; }
            if (await ValidateAssignmentAsync(tenantId, a.EmployeeId, a.Date, shift, ct) is not null)
            {
                skipped++;
                continue;
            }

            var existing = await _db.ShiftAssignments
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.EmployeeId == a.EmployeeId && x.AssignedDate == a.Date, ct);
            if (existing is not null)
            {
                if (!req.OverwriteExisting) { skipped++; continue; }
                existing.ShiftDefinitionId = shift.Id; existing.ShiftName = shift.Name;
                existing.ShiftCode = shift.Code; existing.ShiftColor = shift.Color;
                updated++;
            }
            else
            {
                _db.ShiftAssignments.Add(new ShiftAssignment
                {
                    TenantId = tenantId, EmployeeId = a.EmployeeId, EmployeeName = empName,
                    ShiftDefinitionId = shift.Id, ShiftName = shift.Name,
                    ShiftCode = shift.Code, ShiftColor = shift.Color, AssignedDate = a.Date,
                });
                created++;
            }
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { created, updated, skipped });
    }

    [HttpDelete("roster/{id:guid}")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Manager,Supervisor")]
    public async Task<IActionResult> RemoveAssignment(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var a = await _db.ShiftAssignments.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (a is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(a.EmployeeId)) return Forbid();
        _db.ShiftAssignments.Remove(a);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private Guid GetTenantId() => Guid.Parse(User.FindFirstValue("tenant_id")!);

    private async Task<string?> ValidateAssignmentAsync(Guid tenantId, int employeeId, DateOnly date, ShiftDefinition shift, CancellationToken ct)
    {
        if (await _db.LeaveRequests.AsNoTracking().AnyAsync(x => x.TenantId == tenantId
                && x.EmployeeId == employeeId && x.Status == "Approved" && x.StartDate <= date && x.EndDate >= date, ct))
            return "Employee has approved leave on this date.";

        var policy = await _db.ShiftPolicies.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
        var minRest = policy?.MinRestHours > 0 ? policy.MinRestHours : 8;
        var maxConsecutive = policy?.MaxConsecutiveDays > 0 ? policy.MaxConsecutiveDays : 6;
        var nearby = await _db.ShiftAssignments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId
                && x.AssignedDate >= date.AddDays(-maxConsecutive - 1) && x.AssignedDate <= date.AddDays(maxConsecutive + 1)
                && x.AssignedDate != date)
            .ToListAsync(ct);
        // Batch planners stage assignments before their final SaveChanges. Include those rows so
        // a single auto-plan cannot create a prohibited streak or short-rest sequence internally.
        var staged = _db.ShiftAssignments.Local.Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId
            && x.AssignedDate >= date.AddDays(-maxConsecutive - 1) && x.AssignedDate <= date.AddDays(maxConsecutive + 1)
            && x.AssignedDate != date).ToList();
        foreach (var assignment in staged)
            if (!nearby.Any(x => x.Id == assignment.Id)) nearby.Add(assignment);
        var dates = nearby.Select(x => x.AssignedDate).ToHashSet();
        var streak = 1;
        for (var d = date.AddDays(-1); dates.Contains(d); d = d.AddDays(-1)) streak++;
        for (var d = date.AddDays(1); dates.Contains(d); d = d.AddDays(1)) streak++;
        if (streak > maxConsecutive) return $"Assignment exceeds the {maxConsecutive}-day consecutive-work limit.";

        var adjacent = nearby.Where(x => x.AssignedDate == date.AddDays(-1) || x.AssignedDate == date.AddDays(1)).ToList();
        if (adjacent.Count == 0) return null;
        var definitions = await _db.ShiftDefinitions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && adjacent.Select(a => a.ShiftDefinitionId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var currentStart = date.ToDateTime(shift.StartTime);
        var currentEnd = date.ToDateTime(shift.EndTime);
        if (shift.EndTime <= shift.StartTime) currentEnd = currentEnd.AddDays(1);
        foreach (var assignment in adjacent)
        {
            if (!definitions.TryGetValue(assignment.ShiftDefinitionId, out var other)) continue;
            var otherStart = assignment.AssignedDate.ToDateTime(other.StartTime);
            var otherEnd = assignment.AssignedDate.ToDateTime(other.EndTime);
            if (other.EndTime <= other.StartTime) otherEnd = otherEnd.AddDays(1);
            var rest = assignment.AssignedDate < date ? currentStart - otherEnd : otherStart - currentEnd;
            if (rest.TotalHours < minRest) return $"Assignment violates the {minRest}-hour minimum rest rule.";
        }
        return null;
    }

    // ── Policy helpers ────────────────────────────────────────────────

    private static ShiftPolicyDto ToDto(ShiftPolicy p) => new(
        Deserialize<List<GenderShiftRuleDto>>(p.GenderShiftRulesJson) ?? new(),
        Deserialize<List<string>>(p.VoluntaryShiftCodesJson) ?? new(),
        Deserialize<List<DemandTargetDto>>(p.WeekendDemandJson) ?? new(),
        Deserialize<List<DemandTargetDto>>(p.HolidayDemandJson) ?? new(),
        p.MinRestHours, p.MaxConsecutiveDays);

    private static T? Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json); } catch { return default; }
    }

    /// <summary>Infers a starter policy from shift names so the planner is useful before HR configures it:
    /// females→morning, males→evening/night, afternoon voluntary, weekend/holiday by demand.</summary>
    private static ShiftPolicyDto DefaultPolicyDto(List<ShiftDefinition> shifts)
    {
        bool Has(ShiftDefinition s, string kw) =>
            s.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) || s.Code.Contains(kw, StringComparison.OrdinalIgnoreCase);

        var morning   = shifts.Where(s => Has(s, "morning")).Select(s => s.Code).ToList();
        var evening   = shifts.Where(s => Has(s, "evening") || Has(s, "night")).Select(s => s.Code).ToList();
        var afternoon = shifts.Where(s => Has(s, "afternoon")).Select(s => s.Code).ToList();

        var genderRules = new List<GenderShiftRuleDto>();
        if (morning.Count > 0) genderRules.Add(new GenderShiftRuleDto("Female", morning, "required"));
        if (evening.Count > 0) genderRules.Add(new GenderShiftRuleDto("Male", evening, "required"));

        return new ShiftPolicyDto(genderRules, afternoon, new(), new(), 8, 6);
    }

}

public record ShiftDefinitionRequest(string Code, string Name, string StartTime, string EndTime, int BreakMinutes, string Color);
public record GenderShiftRuleDto(string Gender, List<string> ShiftCodes, string Mode);
public record DemandTargetDto(string ShiftCode, int Headcount);
public record ShiftPolicyDto(
    List<GenderShiftRuleDto> GenderRules,
    List<string> VoluntaryShiftCodes,
    List<DemandTargetDto> WeekendDemand,
    List<DemandTargetDto> HolidayDemand,
    int MinRestHours,
    int MaxConsecutiveDays);
public record AiPlanRequest(DateOnly DateFrom, DateOnly DateTo, List<int>? EmployeeIds, List<string>? WeekendDays);
public record CommitAssignment(int EmployeeId, DateOnly Date, Guid ShiftDefinitionId);
public record CommitPlanRequest(DateOnly DateFrom, DateOnly DateTo, bool OverwriteExisting, List<CommitAssignment> Assignments);
public record AssignShiftRequest(int EmployeeId, Guid ShiftDefinitionId, DateOnly Date, string? Notes);
public record RosterEmployee(int Id, string FullName, string Department, string EmployeeCode);
public record RosterAssignment(Guid Id, int EmployeeId, DateOnly Date, Guid ShiftDefinitionId, string ShiftName, string ShiftCode, string ShiftColor);
public record AutoPlanRequest(DateOnly DateFrom, DateOnly DateTo, List<Guid> ShiftIds, string Pattern, bool SkipWeekend, bool OverwriteExisting, List<int>? EmployeeIds);

internal class AssignmentKeyComparer : IEqualityComparer<(int EmployeeId, DateOnly Date)>
{
    public bool Equals((int, DateOnly) x, (int, DateOnly) y) => x == y;
    public int GetHashCode((int, DateOnly) obj) => HashCode.Combine(obj.Item1, obj.Item2);
}
