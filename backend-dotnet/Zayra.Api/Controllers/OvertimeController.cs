using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Organization;
using Zayra.Api.Application.WorkWeek;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Setup;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.WorkWeek;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/overtime")]
[Authorize]
public class OvertimeController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;
    private readonly IHrmHierarchyService _hierarchyService;
    private readonly IWorkWeekService _workWeek;
    private readonly IStatutoryRuleReader _ruleReader;

    public OvertimeController(ZayraDbContext db, IDataScopeService scopeService, IHrmHierarchyService hierarchyService, IWorkWeekService? workWeek = null, IStatutoryRuleReader? ruleReader = null)
    {
        _db = db;
        _scopeService = scopeService;
        _hierarchyService = hierarchyService;
        // Optional so existing callers/tests keep working; DI always supplies the real one.
        _workWeek = workWeek ?? new WorkWeekService(db);
        // Same statutory rule source the payroll run reads. Without it this controller computed
        // overtime on the pre-S1 basis and disagreed with the money actually paid.
        _ruleReader = ruleReader ?? new StatutoryRuleReader(db);
    }

    [HttpGet("policies")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: Code, Name, HourlyRateBasis, FixedHourlyRate, scheduling caps, RoundingRule, flags. No employee salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<IReadOnlyCollection<OvertimePolicy>>> Policies(CancellationToken ct)
    {
        var tenantId = RequireTenant();
        return Ok(await _db.OvertimePolicies.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted).OrderBy(x => x.Name).ToListAsync(ct));
    }

    [HttpPost("policies")]
    [HasPermission("overtime.policy_manage")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: Code, Name, HourlyRateBasis, FixedHourlyRate, scheduling caps, RoundingRule, flags. No employee salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<OvertimePolicy>> CreatePolicy(OvertimePolicyRequest req, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tenantId = RequireTenant();
        if (await _db.OvertimePolicies.AnyAsync(x => x.TenantId == tenantId && x.Code == req.Code && !x.IsDeleted, ct))
            return Conflict(new { message = "Overtime policy code already exists." });
        var policy = new OvertimePolicy
        {
            TenantId = tenantId,
            Code = req.Code.Trim(),
            Name = req.Name.Trim(),
            HourlyRateBasis = req.HourlyRateBasis ?? "BasicSalary",
            FixedHourlyRate = req.FixedHourlyRate,
            StandardMonthlyHours = req.StandardMonthlyHours <= 0 ? 240 : req.StandardMonthlyHours,
            MinimumMinutes = req.MinimumMinutes <= 0 ? 30 : req.MinimumMinutes,
            MaximumMinutesPerDay = req.MaximumMinutesPerDay <= 0 ? 240 : req.MaximumMinutesPerDay,
            MonthlyCapMinutes = req.MonthlyCapMinutes <= 0 ? 3600 : req.MonthlyCapMinutes,
            RoundingRule = req.RoundingRule ?? "Nearest15",
            RequiresApproval = req.RequiresApproval,
            AllowCompOffConversion = req.AllowCompOffConversion,
            CreatedBy = GetUserId()
        };
        _db.OvertimePolicies.Add(policy);
        var regularDayDefault = await ResolveRegularDayDefaultMultiplierAsync(tenantId, ct);
        _db.OvertimeMultipliers.Add(new OvertimeMultiplier { TenantId = tenantId, OvertimePolicyId = policy.Id, DayCategory = "RegularDay", Multiplier = req.RegularDayMultiplier <= 0 ? regularDayDefault : req.RegularDayMultiplier });
        _db.OvertimeMultipliers.Add(new OvertimeMultiplier { TenantId = tenantId, OvertimePolicyId = policy.Id, DayCategory = "Weekend", Multiplier = req.WeekendMultiplier <= 0 ? 1.5m : req.WeekendMultiplier });
        _db.OvertimeMultipliers.Add(new OvertimeMultiplier { TenantId = tenantId, OvertimePolicyId = policy.Id, DayCategory = "PublicHoliday", Multiplier = req.HolidayMultiplier <= 0 ? 2.0m : req.HolidayMultiplier });
        await SaveAudit("overtime.policy.created", "OvertimePolicy", policy.Id.ToString(), ct);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/overtime/policies/{policy.Id}", policy);
    }

    [HttpGet("policies/{id:guid}")]
    public async Task<IActionResult> GetPolicy(Guid id, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var p = await _db.OvertimePolicies.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, ct);
        if (p is null) return NotFound();
        var m = await _db.OvertimeMultipliers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OvertimePolicyId == id && x.OvertimeTypeId == null).ToListAsync(ct);
        decimal Of(string c) => m.FirstOrDefault(x => x.DayCategory == c)?.Multiplier ?? 0m;
        return Ok(new
        {
            p.Id, p.Code, p.Name, p.HourlyRateBasis, p.FixedHourlyRate, p.StandardMonthlyHours, p.MinimumMinutes,
            p.MaximumMinutesPerDay, p.MonthlyCapMinutes, p.RoundingRule, p.RequiresApproval, p.AllowCompOffConversion, p.IsActive,
            regularDayMultiplier = Of("RegularDay"), weekendMultiplier = Of("Weekend"), holidayMultiplier = Of("PublicHoliday"),
        });
    }

    [HttpPut("policies/{id:guid}")]
    [HasPermission("overtime.policy_manage")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: Code, Name, HourlyRateBasis, FixedHourlyRate, scheduling caps, RoundingRule, flags. No employee salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public Task<ActionResult<OvertimePolicy>> UpdatePolicy(Guid id, OvertimePolicyRequest req, CancellationToken ct)
        => SetupTenantLock.InLockedTransactionAsync(_db, RequireTenant(), () => UpdatePolicyCore(id, req, ct), ct);

    private Task<bool> OtherActivePolicyExistsAsync(Guid tenantId, Guid id, CancellationToken ct) =>
        _db.OvertimePolicies.AnyAsync(x => x.TenantId == tenantId && x.Id != id && x.IsActive && !x.IsDeleted, ct);

    private static readonly string[] RateBases = { "BasicSalary", "GrossSalary", "FixedHourlyRate" };
    private static readonly string[] RoundingRules = { "None", "Nearest15", "Up15", "Down15", "Nearest30" };

    private static string? ValidatePolicyFigures(OvertimePolicyRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "A name is required.";
        foreach (var m in new[] { r.RegularDayMultiplier, r.WeekendMultiplier, r.HolidayMultiplier })
            if (m <= 0 || m > 5) return "Every overtime multiplier must be greater than 0 and at most 5.";
        if (r.FixedHourlyRate < 0 || r.FixedHourlyRate > 10000) return "The fixed hourly rate must be between 0 and 10,000.";
        if (r.StandardMonthlyHours < 160 || r.StandardMonthlyHours > 260) return "Standard monthly hours must be between 160 and 260.";
        if (r.HourlyRateBasis is not null && !RateBases.Contains(r.HourlyRateBasis)) return "Hourly rate basis must be BasicSalary, GrossSalary or FixedHourlyRate.";
        if (r.RoundingRule is not null && !RoundingRules.Contains(r.RoundingRule)) return "Unknown rounding rule.";
        if (r.MinimumMinutes <= 0 || r.MaximumMinutesPerDay <= 0 || r.MonthlyCapMinutes <= 0) return "Minimum, daily maximum and monthly cap must be positive.";
        return null;
    }

    private async Task<ActionResult<OvertimePolicy>> UpdatePolicyCore(Guid id, OvertimePolicyRequest req, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tenantId = RequireTenant();
        var policy = await _db.OvertimePolicies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, ct);
        if (policy is null) return NotFound();
        if (ValidatePolicyFigures(req) is { } invalid) return BadRequest(new { message = invalid });
        var multipliers = await _db.OvertimeMultipliers.Where(x => x.TenantId == tenantId && x.OvertimePolicyId == id && x.OvertimeTypeId == null).ToListAsync(ct);
        decimal Current(string c) => multipliers.FirstOrDefault(m => m.DayCategory == c)?.Multiplier ?? 0m;
        var rateChanged = (req.HourlyRateBasis ?? policy.HourlyRateBasis) != policy.HourlyRateBasis
            || req.FixedHourlyRate != policy.FixedHourlyRate || req.StandardMonthlyHours != policy.StandardMonthlyHours
            || req.RegularDayMultiplier != Current("RegularDay") || req.WeekendMultiplier != Current("Weekend") || req.HolidayMultiplier != Current("PublicHoliday");
        if (rateChanged)
        {
            var requestIds = await ScopedBypass.TenantWide(_db.OvertimeRequests, tenantId, "In-use check for a removal: must see every company's rows in this tenant, not only the caller's.")
                .Where(x => x.OvertimePolicyId == id).Select(x => x.Id).ToListAsync(ct);
            if (requestIds.Count > 0 && await _db.OvertimeCalculations.AnyAsync(c => c.TenantId == tenantId && requestIds.Contains(c.OvertimeRequestId), ct))
                return Conflict(new { code = "OVERTIME_POLICY_USED_BY_PAYROLL", message = "Overtime has already been calculated under this policy, so its rate basis, hours and multipliers are fixed. Turn it off and add a new policy for the new rates; the limits and name can still be edited." });
        }
        object View() => new
        {
            policy.Name, policy.HourlyRateBasis, policy.FixedHourlyRate, policy.StandardMonthlyHours, policy.MinimumMinutes,
            policy.MaximumMinutesPerDay, policy.MonthlyCapMinutes, policy.RoundingRule, policy.RequiresApproval, policy.AllowCompOffConversion,
            multipliers = multipliers.ToDictionary(m => m.DayCategory, m => m.Multiplier),
        };
        var before = View();
        // The code is the policy's identity (other records and reports name it): it is not editable.
        policy.Name = req.Name.Trim();
        policy.HourlyRateBasis = req.HourlyRateBasis ?? policy.HourlyRateBasis;
        policy.FixedHourlyRate = req.FixedHourlyRate;
        if (req.StandardMonthlyHours > 0) policy.StandardMonthlyHours = req.StandardMonthlyHours;
        if (req.MinimumMinutes > 0) policy.MinimumMinutes = req.MinimumMinutes;
        if (req.MaximumMinutesPerDay > 0) policy.MaximumMinutesPerDay = req.MaximumMinutesPerDay;
        if (req.MonthlyCapMinutes > 0) policy.MonthlyCapMinutes = req.MonthlyCapMinutes;
        policy.RoundingRule = req.RoundingRule ?? policy.RoundingRule;
        policy.RequiresApproval = req.RequiresApproval;
        policy.AllowCompOffConversion = req.AllowCompOffConversion;
        void SetMultiplier(string category, decimal value)
        {
            var row = multipliers.FirstOrDefault(m => m.DayCategory == category);
            if (row is null)
            {
                row = new OvertimeMultiplier { TenantId = tenantId, OvertimePolicyId = id, DayCategory = category, Multiplier = value };
                _db.OvertimeMultipliers.Add(row); multipliers.Add(row);
            }
            else row.Multiplier = value;
        }
        SetMultiplier("RegularDay", req.RegularDayMultiplier);
        SetMultiplier("Weekend", req.WeekendMultiplier);
        SetMultiplier("PublicHoliday", req.HolidayMultiplier);
        await SaveAudit("overtime.policy.updated", "OvertimePolicy", policy.Id.ToString(), ct, new { policy.Code, before, after = View() });
        await _db.SaveChangesAsync(ct);
        return Ok(policy);
    }

    [HttpPost("policies/{id:guid}/active")]
    [HasPermission("overtime.policy_manage")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: Code, Name, HourlyRateBasis, FixedHourlyRate, scheduling caps, RoundingRule, flags. No employee salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public Task<ActionResult<OvertimePolicy>> SetPolicyActive(Guid id, [FromQuery] bool active, CancellationToken ct)
        => SetupTenantLock.InLockedTransactionAsync(_db, RequireTenant(), () => SetPolicyActiveCore(id, active, ct), ct);

    private async Task<ActionResult<OvertimePolicy>> SetPolicyActiveCore(Guid id, [FromQuery] bool active, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tenantId = RequireTenant();
        var policy = await _db.OvertimePolicies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, ct);
        if (policy is null) return NotFound();
        if (policy.IsActive == active) return Ok(policy);
        // With no active policy left, payroll falls back to built-in defaults (basic salary, 240 h) and new
        // requests fail, so the last active policy can never be turned off.
        if (!active && !await OtherActivePolicyExistsAsync(tenantId, id, ct))
            return Conflict(new { code = "OVERTIME_POLICY_LAST_ACTIVE", message = "This is the only active overtime policy. Payroll would fall back to built-in defaults without it. Add and turn on another policy first." });
        policy.IsActive = active;
        await SaveAudit(active ? "overtime.policy.activated" : "overtime.policy.deactivated", "OvertimePolicy", policy.Id.ToString(), ct, new { policy.Code, policy.IsActive });
        await _db.SaveChangesAsync(ct);
        return Ok(policy);
    }

    // ManagedLifecycle: soft delete (IsDeleted/DeletedAtUtc/DeletedBy), refused while a request uses the policy or it is the last active one, audited.
    [HttpDelete("policies/{id:guid}")]
    [HasPermission("overtime.policy_manage")]
    public Task<IActionResult> DeletePolicy(Guid id, CancellationToken ct)
        => SetupTenantLock.InLockedTransactionAsync(_db, RequireTenant(), () => DeletePolicyCore(id, ct), ct);

    private async Task<IActionResult> DeletePolicyCore(Guid id, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tenantId = RequireTenant();
        var policy = await _db.OvertimePolicies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.IsDeleted, ct);
        if (policy is null) return NotFound();
        if (policy.IsActive && !await OtherActivePolicyExistsAsync(tenantId, id, ct))
            return Conflict(new { code = "OVERTIME_POLICY_LAST_ACTIVE", message = "This is the only active overtime policy. Payroll would fall back to built-in defaults without it. Add and turn on another policy first." });
        var used = await ScopedBypass.TenantWide(_db.OvertimeRequests, tenantId, "In-use check for a removal: must see every company's rows in this tenant, not only the caller's.").CountAsync(x => x.TenantId == tenantId && x.OvertimePolicyId == id, ct);
        if (used > 0)
            return Conflict(new { code = "OVERTIME_POLICY_IN_USE", message = $"{used} overtime request(s) were filed under this policy, so it cannot be deleted. Turn it off instead; it can be turned back on later." });
        policy.IsDeleted = true; policy.IsActive = false; policy.DeletedAtUtc = DateTime.UtcNow; policy.DeletedBy = GetUserId();
        await SaveAudit("overtime.policy.deleted", "OvertimePolicy", policy.Id.ToString(), ct, new { policy.Code, policy.Name });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("types")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: Code, Name, Category, IsActive. No employee data, salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<IReadOnlyCollection<OvertimeType>>> Types([FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        return Ok(await _db.OvertimeTypes.AsNoTracking().Where(x => x.TenantId == tenantId && (includeInactive || x.IsActive)).OrderBy(x => x.Name).ToListAsync(ct));
    }

    [HttpPut("types/{id:guid}")]
    [HasPermission("overtime.policy_manage")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: Code, Name, Category, IsActive. No employee data, salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<OvertimeType>> UpdateType(Guid id, OvertimeTypeRequest req, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tenantId = RequireTenant();
        var type = await _db.OvertimeTypes.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (type is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "A name is required." });
        var before = new { type.Name, type.Category };
        type.Name = req.Name.Trim(); type.Category = req.Category ?? type.Category;
        await SaveAudit("overtime.type.updated", "OvertimeType", type.Id.ToString(), ct, new { before, after = new { type.Name, type.Category } });
        await _db.SaveChangesAsync(ct);
        return Ok(type);
    }

    [HttpPost("types/{id:guid}/active")]
    [HasPermission("overtime.policy_manage")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: Code, Name, Category, IsActive. No employee data, salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<OvertimeType>> SetTypeActive(Guid id, [FromQuery] bool active, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tenantId = RequireTenant();
        var type = await _db.OvertimeTypes.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (type is null) return NotFound();
        if (type.IsActive == active) return Ok(type);
        type.IsActive = active;
        await SaveAudit(active ? "overtime.type.activated" : "overtime.type.deactivated", "OvertimeType", type.Id.ToString(), ct, new { type.Code, type.IsActive });
        await _db.SaveChangesAsync(ct);
        return Ok(type);
    }

    [HttpPost("types")]
    [HasPermission("overtime.policy_manage")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: Code, Name, Category, IsActive. No employee data, salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<OvertimeType>> CreateType(OvertimeTypeRequest req, CancellationToken ct)
    {
        if (!this.HasGroupEntityScope()) return Forbid();
        var tenantId = RequireTenant();
        var type = new OvertimeType { TenantId = tenantId, Code = req.Code.Trim(), Name = req.Name.Trim(), Category = req.Category ?? "Regular" };
        _db.OvertimeTypes.Add(type);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/overtime/types/{type.Id}", type);
    }

    [HttpGet("requests")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: EmployeeId, EmployeeName, WorkDate, start/end times, requested/approved minutes, Reason, Status. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<PagedResult<OvertimeRequest>>> Requests([FromQuery] string? status, [FromQuery] int? employeeId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var tenantId = RequireTenant();
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var (singleId, setFilter) = scope.Constrain(employeeId);
        var query = _db.OvertimeRequests.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        if (setFilter is not null) query = query.Where(x => setFilter.Contains(x.EmployeeId));
        else if (singleId.HasValue) query = query.Where(x => x.EmployeeId == singleId.Value);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.WorkDate).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new PagedResult<OvertimeRequest>(items, total, page, pageSize));
    }

    [HttpPost("requests")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: EmployeeId, EmployeeName, WorkDate, start/end times, requested/approved minutes, Reason, Status. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<OvertimeRequest>> CreateRequest(OvertimeRequestCreate req, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(req.EmployeeId)) return Forbid();
        // Filing for yourself is self-service (ess.write); filing for anyone else is overtime administration
        // (overtime.write). Data scope alone used to decide, so an ess.read-only login (an HR Assistant, an
        // Auditor) could file overtime for itself, and an org-scoped login for anyone, with no write key.
        var forSelf = scope.CallerEmployeeId == req.EmployeeId;
        if (!(User.HasPermission("overtime.write") || (forSelf && User.HasPermission("ess.write")))) return Forbid();
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == req.EmployeeId && !x.IsDeleted, ct);
        if (employee is null) return BadRequest(new { message = "Employee not found." });
        if (req.EndTimeUtc <= req.StartTimeUtc) return BadRequest(new { message = "End time must be after start time." });
        if (string.IsNullOrWhiteSpace(req.Reason)) return BadRequest(new { message = "A reason is required." });
        var policy = req.OvertimePolicyId.HasValue
            ? await _db.OvertimePolicies.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == req.OvertimePolicyId && x.IsActive && !x.IsDeleted, ct)
            : await OvertimePolicyDefault.Order(_db.OvertimePolicies.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted)).FirstOrDefaultAsync(ct);
        if (policy is null) return BadRequest(new { message = "An active overtime policy is required." });
        if (req.OvertimeTypeId.HasValue && !await _db.OvertimeTypes.AnyAsync(x => x.TenantId == tenantId && x.Id == req.OvertimeTypeId && x.IsActive, ct))
            return BadRequest(new { message = "Overtime type not found or inactive." });
        var rawMinutes = (int)Math.Round((req.EndTimeUtc - req.StartTimeUtc).TotalMinutes);
        // The policy's rounding rule and all three quantity limits, from the SHARED resolver the
        // attendance door now calls too. Both doors used to spell this out separately and only one
        // of them actually did it — see OvertimePolicyLimits for what that cost.
        var monthToDate = await MonthToDateMinutesAsync(tenantId, req.EmployeeId, req.WorkDate, ct);
        var limits = OvertimePolicyLimits.Apply(rawMinutes, policy, monthToDate);
        // Refusal order is the policy's own order, and the overlap conflict sits where it always did
        // (after the daily maximum, before the monthly cap) so no existing client sees a new message.
        if (limits.Limit is OvertimeLimitKind.BelowMinimum or OvertimeLimitKind.AboveDailyMaximum)
            return BadRequest(new { message = OvertimePolicyLimits.RefusalMessage(limits.Limit, policy) });
        if (await _db.OvertimeRequests.AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == req.EmployeeId
                && x.Status != "Rejected" && req.StartTimeUtc < x.EndTimeUtc && req.EndTimeUtc > x.StartTimeUtc, ct))
            return Conflict(new { message = "This overtime request overlaps an existing request." });
        if (limits.Limit == OvertimeLimitKind.AboveMonthlyCap)
            return BadRequest(new { message = OvertimePolicyLimits.RefusalMessage(limits.Limit, policy) });
        var minutes = limits.PayableMinutes;
        var request = new OvertimeRequest
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            OvertimePolicyId = policy.Id,
            OvertimeTypeId = req.OvertimeTypeId,
            WorkDate = req.WorkDate,
            StartTimeUtc = req.StartTimeUtc,
            EndTimeUtc = req.EndTimeUtc,
            RequestedMinutes = minutes,
            Source = req.Source ?? "Manual",
            Reason = req.Reason ?? string.Empty,
            Status = "PendingManager",
            CreatedBy = GetUserId()
        };
        _db.OvertimeRequests.Add(request);
        await SaveAudit("overtime.request.created", "OvertimeRequest", request.Id.ToString(), ct);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/overtime/requests/{request.Id}", request);
    }

    /// <summary>
    /// Raises overtime requests from processed attendance days — the OTHER door into overtime pay.
    ///
    /// <para>It used to write <c>RequestedMinutes = record.OvertimeMinutes</c> raw: no rounding rule,
    /// no minimum, no daily maximum, no monthly cap, and no policy lookup at all (the caller's policy
    /// id was stamped on the request unvalidated, including one belonging to another tenant). The
    /// same hours keyed by hand were capped; keyed by the attendance device they were paid in full,
    /// without limit. Both doors now go through <see cref="OvertimePolicyLimits"/>.</para>
    ///
    /// <para>What the policy will not pay is NOT truncated away: each affected day raises an
    /// <see cref="AttendanceException"/> against the attendance record it came from, and the response
    /// carries the same rows so the operator who pressed the button sees them immediately.</para>
    /// </summary>
    [HttpPost("detect-from-attendance")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Officer")]
    [AllowEntityReturn("Flat entity pair — no navigation properties on either. OvertimeRequest: EmployeeId, EmployeeName, WorkDate, start/end times, requested/approved minutes, Reason, Status. AttendanceException: EmployeeId, WorkDate, ExceptionType, Severity, Details — the overtime this tenant's policy will not pay, which the operator running detection has to see. Neither carries salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<DetectOvertimeResult>> DetectFromAttendance(DetectOvertimeRequest req, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        if (req.FromDate > req.ToDate || req.ToDate.DayNumber - req.FromDate.DayNumber > 366)
            return BadRequest(new { message = "Attendance detection range must be between 1 and 367 days." });
        // The policy is RESOLVED, exactly as the manual door resolves it: by id within this tenant,
        // active and not deleted, else the tenant's active default. Without this the limits below
        // would have nothing to apply, and the id on the request was never checked at all.
        var policy = req.OvertimePolicyId.HasValue
            ? await _db.OvertimePolicies.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == req.OvertimePolicyId && x.IsActive && !x.IsDeleted, ct)
            : await OvertimePolicyDefault.Order(_db.OvertimePolicies.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted)).FirstOrDefaultAsync(ct);
        if (policy is null) return BadRequest(new { message = "An active overtime policy is required." });
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var dailyQuery = _db.AttendanceDailyRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.WorkDate >= req.FromDate && x.WorkDate <= req.ToDate && x.OvertimeMinutes > 0);
        if (!scope.IsUnrestricted) dailyQuery = dailyQuery.Where(x => scope.AllowedEmployeeIds!.Contains(x.EmployeeId));
        var daily = await dailyQuery
            // Deterministic order: the monthly cap is consumed in date order, so an earlier day is
            // never displaced by a later one depending on how the database felt about returning rows.
            .OrderBy(x => x.EmployeeId).ThenBy(x => x.WorkDate)
            .ToListAsync(ct);
        var created = new List<OvertimeRequest>();
        var exceptions = new List<AttendanceException>();
        // Minutes already committed against each employee's monthly cap, including the requests this
        // very batch is adding — otherwise a month's worth of days each see an empty cap and the
        // batch walks straight past it one day at a time.
        var monthToDate = new Dictionary<(int EmployeeId, int Year, int Month), int>();
        foreach (var record in daily)
        {
            var exists = await _db.OvertimeRequests.AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == record.EmployeeId && x.WorkDate == record.WorkDate && x.Source == "Attendance", ct);
            if (exists) continue;

            var monthKey = (record.EmployeeId, record.WorkDate.Year, record.WorkDate.Month);
            if (!monthToDate.TryGetValue(monthKey, out var consumed))
            {
                consumed = await MonthToDateMinutesAsync(tenantId, record.EmployeeId, record.WorkDate, ct);
                monthToDate[monthKey] = consumed;
            }

            var limits = OvertimePolicyLimits.Apply(record.OvertimeMinutes, policy, consumed);

            if (limits.HasExcess)
            {
                var raised = await RaiseOvertimeLimitExceptionAsync(tenantId, record, policy, limits, ct);
                if (raised is not null) exceptions.Add(raised);
            }
            if (!limits.IsPayable) continue;

            monthToDate[monthKey] = consumed + limits.PayableMinutes;
            var request = new OvertimeRequest
            {
                TenantId = tenantId,
                EmployeeId = record.EmployeeId,
                EmployeeName = record.EmployeeName,
                OvertimePolicyId = policy.Id,
                WorkDate = record.WorkDate,
                // The window is the PAYABLE minutes ending at the last punch out, not the measured
                // ones: a request whose times span more than it claims to pay is its own defect.
                StartTimeUtc = record.LastOutUtc?.AddMinutes(-limits.PayableMinutes) ?? DateTime.UtcNow,
                EndTimeUtc = record.LastOutUtc ?? DateTime.UtcNow,
                RequestedMinutes = limits.PayableMinutes,
                Source = "Attendance",
                Reason = limits.HasExcess
                    ? $"Auto-detected from processed attendance; capped by policy '{policy.Name}' "
                      + $"({limits.RoundedMinutes} min detected, {limits.ExcessMinutes} min not payable)"
                    : "Auto-detected from processed attendance",
                Status = "PendingManager",
                AttendanceDailyRecordId = record.Id,
                CreatedBy = GetUserId()
            };
            _db.OvertimeRequests.Add(request);
            created.Add(request);
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new DetectOvertimeResult(created, exceptions));
    }

    /// <summary>
    /// Minutes already requested for this employee in the work date's calendar month, excluding
    /// rejected requests. The monthly cap basis for BOTH doors.
    /// </summary>
    private async Task<int> MonthToDateMinutesAsync(Guid tenantId, int employeeId, DateOnly workDate, CancellationToken ct)
    {
        var monthStart = new DateOnly(workDate.Year, workDate.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        return await _db.OvertimeRequests.Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId
                && x.Status != "Rejected" && x.WorkDate >= monthStart && x.WorkDate <= monthEnd)
            .SumAsync(x => (int?)x.RequestedMinutes, ct) ?? 0;
    }

    /// <summary>
    /// Records, against the attendance day itself, the overtime the configured policy will not pay.
    /// This is the difference between capping and silently truncating: the excess minutes stay on a
    /// row a human resolves, beside the missing-punch and late-arrival exceptions the attendance
    /// processor already raises. Idempotent — re-running detection over the same range does not
    /// stack duplicate unresolved rows.
    /// </summary>
    private async Task<AttendanceException?> RaiseOvertimeLimitExceptionAsync(
        Guid tenantId, AttendanceDailyRecord record, OvertimePolicy policy,
        OvertimeLimitOutcome limits, CancellationToken ct)
    {
        var type = limits.Limit == OvertimeLimitKind.BelowMinimum
            ? OvertimePolicyLimits.OvertimeBelowMinimumExceptionType
            : OvertimePolicyLimits.OvertimeAbovePolicyExceptionType;
        if (await _db.AttendanceExceptions.AnyAsync(x => x.TenantId == tenantId
                && x.EmployeeId == record.EmployeeId && x.WorkDate == record.WorkDate
                && x.ExceptionType == type && !x.IsResolved, ct))
            return null;
        var exception = new AttendanceException
        {
            TenantId = tenantId,
            EmployeeId = record.EmployeeId,
            DailyRecordId = record.Id,
            WorkDate = record.WorkDate,
            ExceptionType = type,
            // Unpaid time an employee actually worked is not an informational notice. Below-minimum
            // is the policy working as configured, so it is logged rather than escalated.
            Severity = limits.Limit == OvertimeLimitKind.BelowMinimum ? "Low" : "High",
            Details = OvertimePolicyLimits.ExplainForHuman(limits, policy),
        };
        _db.AttendanceExceptions.Add(exception);
        await SaveAudit("overtime.detect.capped_by_policy", "AttendanceDailyRecord", record.Id.ToString(), ct);
        return exception;
    }

    [HttpPost("requests/{id:guid}/approve")]
    [Authorize(Roles = "Admin,HR Manager,Manager,Supervisor")]
    public async Task<ActionResult> Approve(Guid id, OvertimeDecisionRequest req, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var request = await _db.OvertimeRequests.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (request is null) return NotFound();
        if (!request.Status.StartsWith("Pending")) return BadRequest(new { message = "Only pending overtime can be approved." });
        if (request.CreatedBy.HasValue && request.CreatedBy == GetUserId())
            return BadRequest(new { message = "Maker-checker violation: requester cannot approve their own overtime." });
        // The requester rule above bars whoever FILED it; this bars whoever it is ABOUT. HR or a manager
        // whose overtime a colleague keyed in must not approve it, whatever their role.
        if (await SubjectDecisionBar.CallerIsSubjectAsync(_db, tenantId, GetUserId(), request.EmployeeId, ct))
            return BadRequest(SubjectDecisionBar.Refusal("This overtime is yours. Someone else has to approve it."));

        var isAdmin = User.IsInRole("Admin");
        var isHR = User.IsInRole("HR Manager");
        var isManager = User.IsInRole("Manager") || User.IsInRole("Supervisor");
        if (request.Status == "PendingManager" && isManager && !isAdmin && !isHR
            && !await IsCurrentUserResolvedApproverAsync(tenantId, request.EmployeeId, "Overtime", ct))
            return Forbid();

        // Admin bypasses all steps; HR Manager finalises from PendingHR
        if (isAdmin || (isHR && request.Status == "PendingHR"))
        {
            _db.OvertimeApprovals.Add(NewOvertimeApproval(
                tenantId, request.Id, "Final", "Approved", req.Notes));
            request.Status = "Approved";
            request.DecisionVersion++;
            request.ApprovedMinutes = req.ApprovedMinutes > 0 ? req.ApprovedMinutes : request.RequestedMinutes;
            if (request.ApprovedMinutes <= 0 || request.ApprovedMinutes > request.RequestedMinutes)
                return BadRequest(new { message = "Approved minutes must be positive and cannot exceed requested minutes." });
            request.DecidedAtUtc = DateTime.UtcNow;
            var calc = await Calculate(request, ct);
            _db.OvertimeCalculations.Add(calc);
            _db.OvertimePayrollImpacts.Add(new OvertimePayrollImpact
            {
                TenantId = tenantId,
                OvertimeRequestId = request.Id,
                EmployeeId = request.EmployeeId,
                // Minutes, not hours: the payroll run multiplies this quantity by the hourly rate,
                // so it must reach it unrounded. See OvertimePayrollImpact.Minutes.
                Minutes = calc.ApprovedMinutes,
                Amount = calc.Amount,
                // Persist the multiplier used at approval time so payroll can apply
                // the correct rate (e.g. 2× for holiday/rest-day) without re-resolving the policy.
                ApprovedMultiplier = calc.Multiplier,
            });
            await SaveAudit("overtime.request.approved", "OvertimeRequest", request.Id.ToString(), ct);
            if (!await TrySaveDecisionAsync(ct))
                return Conflict(new { message = "This overtime request was decided concurrently." });
            return Ok(calc);
        }

        // Manager/Supervisor advances PendingManager → PendingHR (no calculation yet)
        if (isManager && request.Status == "PendingManager")
        {
            _db.OvertimeApprovals.Add(NewOvertimeApproval(
                tenantId, request.Id, "Manager", "Approved", req.Notes));
            request.Status = "PendingHR";
            request.DecisionVersion++;
            await SaveAudit("overtime.request.manager_approved", "OvertimeRequest", request.Id.ToString(), ct);
            if (!await TrySaveDecisionAsync(ct))
                return Conflict(new { message = "This overtime request was decided concurrently." });
            return Ok(request);
        }

        return BadRequest(new { message = "You cannot approve this request at its current stage." });
    }

    [HttpPost("requests/{id:guid}/reject")]
    [Authorize(Roles = "Admin,HR Manager,Manager,Supervisor")]
    public async Task<IActionResult> Reject(Guid id, OvertimeDecisionRequest req, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var request = await _db.OvertimeRequests.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (request is null) return NotFound();
        if (!request.Status.StartsWith("Pending")) return BadRequest(new { message = "Only pending overtime can be rejected." });
        if (request.CreatedBy.HasValue && request.CreatedBy == GetUserId())
            return BadRequest(new { message = "Maker-checker violation: requester cannot reject their own overtime." });
        if (await SubjectDecisionBar.CallerIsSubjectAsync(_db, tenantId, GetUserId(), request.EmployeeId, ct))
            return BadRequest(SubjectDecisionBar.Refusal("This overtime is yours. Someone else has to reject it."));
        var isAdmin = User.IsInRole("Admin");
        var isHR = User.IsInRole("HR Manager");
        var isManager = User.IsInRole("Manager") || User.IsInRole("Supervisor");
        if (request.Status == "PendingManager" && isManager && !isAdmin && !isHR
            && !await IsCurrentUserResolvedApproverAsync(tenantId, request.EmployeeId, "Overtime", ct))
            return Forbid();
        if (request.Status == "PendingHR" && !isAdmin && !isHR)
            return Forbid();
        var approvalLevel = request.Status == "PendingManager" ? "Manager" : "Final";
        request.Status = "Rejected";
        request.DecisionVersion++;
        request.DecidedAtUtc = DateTime.UtcNow;
        _db.OvertimeApprovals.Add(NewOvertimeApproval(
            tenantId, request.Id, approvalLevel, "Rejected", req.Notes));
        await SaveAudit("overtime.request.rejected", "OvertimeRequest", request.Id.ToString(), ct);
        if (!await TrySaveDecisionAsync(ct))
            return Conflict(new { message = "This overtime request was decided concurrently." });
        return Ok(request);
    }

    // Role-gate bypass sweep (LegacyRoleGateBypassSweepTests): per-employee overtime pay, unscoped. Resolved to overtime.read (line Managers, Supervisors); every named role holds payroll.read.
    [HttpGet("payroll-review")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Officer,Payroll Manager,Auditor")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: OvertimeRequestId, EmployeeId, PayrollRunId, Hours, Amount, Status. Payroll-role consumers require this data to process overtime pay. No bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    [HasPermission("payroll.read")]
    public async Task<ActionResult<IReadOnlyCollection<OvertimePayrollImpact>>> PayrollReview(CancellationToken ct)
    {
        var tenantId = RequireTenant();
        return Ok(await _db.OvertimePayrollImpacts.AsNoTracking().Where(x => x.TenantId == tenantId && x.Status == "PendingPayroll").OrderBy(x => x.EmployeeId).ToListAsync(ct));
    }

    [HttpGet("calculations")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: OvertimeRequestId, EmployeeId, ApprovedHours, HourlyRate (derived from salary for computation), Multiplier, Amount, CalculationJson. Scope-filtered to caller's accessible employees. No bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<IReadOnlyCollection<OvertimeCalculation>>> Calculations([FromQuery] int? employeeId, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        var tenantId = RequireTenant();
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var (singleId, setFilter) = scope.Constrain(employeeId);
        var query = _db.OvertimeCalculations.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (setFilter is not null) query = query.Where(x => setFilter.Contains(x.EmployeeId));
        else if (singleId.HasValue) query = query.Where(x => x.EmployeeId == singleId.Value);
        return Ok(await query.OrderByDescending(x => x.CreatedAtUtc).Take(pageSize).ToListAsync(ct));
    }

    [HttpGet("budgets")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Officer,Payroll Manager,Finance Approver,Auditor")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: DepartmentId, ProjectId, Year, Month, BudgetAmount, ConsumedAmount, Currency. Department-level aggregates; no individual salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<IReadOnlyCollection<OvertimeBudget>>> Budgets([FromQuery] int? year, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        var query = _db.OvertimeBudgets.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (year.HasValue) query = query.Where(x => x.Year == year.Value);
        return Ok(await query.OrderByDescending(x => x.Year).ThenByDescending(x => x.Month).ToListAsync(ct));
    }

    [HttpGet("comp-off-conversions")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: OvertimeRequestId, EmployeeId, OvertimeHours, CompOffDays, Status, CreatedAtUtc. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<IReadOnlyCollection<OvertimeCompOffConversion>>> CompOffConversions([FromQuery] int? employeeId, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        // Apply caller data-scope like the sibling Requests/Calculations endpoints; without it any
        // employee could read every colleague's comp-off (overtime→leave) conversions (IDOR, CWE-639).
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var (singleId, setFilter) = scope.Constrain(employeeId);
        var query = _db.OvertimeCompOffConversions.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (setFilter is not null) query = query.Where(x => setFilter.Contains(x.EmployeeId));
        else if (singleId.HasValue) query = query.Where(x => x.EmployeeId == singleId.Value);
        return Ok(await query.ToListAsync(ct));
    }

    [HttpPost("comp-off-conversions")]
    [Authorize(Roles = "Admin,HR Manager")]
    // Converting approved overtime into time off is overtime administration for the HR-manager tier, not
    // filing. Without an explicit key the gate resolved to overtime.write, which HR Officer now holds to file
    // overtime on an employee's behalf.
    [HasPermission("overtime.policy_manage")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: OvertimeRequestId, EmployeeId, OvertimeHours, CompOffDays, Status, CreatedAtUtc. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<OvertimeCompOffConversion>> CreateCompOffConversion(CompOffConversionRequest req, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var request = await _db.OvertimeRequests.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == req.OvertimeRequestId && x.Status == "Approved", ct);
        if (request is null) return BadRequest(new { message = "Approved overtime request not found." });
        // Turning overtime into paid time off is a decision about that employee's entitlement.
        if (await SubjectDecisionBar.CallerIsSubjectAsync(_db, tenantId, GetUserId(), request.EmployeeId, ct))
            return BadRequest(SubjectDecisionBar.Refusal("This overtime is yours. Someone else has to convert it to time off."));
        var policy = request.OvertimePolicyId.HasValue ? await _db.OvertimePolicies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.OvertimePolicyId && x.TenantId == tenantId, ct) : null;
        if (policy is null || !policy.AllowCompOffConversion) return BadRequest(new { message = "Comp-off conversion is not allowed by this policy." });
        if (req.CompOffDays <= 0) return BadRequest(new { message = "Comp-off days must be positive." });
        if (await _db.OvertimeCompOffConversions.AnyAsync(x => x.TenantId == tenantId && x.OvertimeRequestId == req.OvertimeRequestId && x.Status != "Rejected", ct))
            return Conflict(new { message = "This overtime request has already been converted to comp-off." });
        var compOff = new OvertimeCompOffConversion { TenantId = tenantId, OvertimeRequestId = req.OvertimeRequestId, EmployeeId = request.EmployeeId, OvertimeHours = Math.Round(request.ApprovedMinutes / 60m, 2), CompOffDays = req.CompOffDays, Status = "Approved" };
        _db.OvertimeCompOffConversions.Add(compOff);
        _db.CompOffCredits.Add(new CompOffCredit
        {
            TenantId = tenantId, EmployeeId = request.EmployeeId, EmployeeName = request.EmployeeName,
            OvertimeCompOffConversionId = compOff.Id,
            WorkedDate = request.WorkDate, WorkType = "Overtime", HoursWorked = compOff.OvertimeHours,
            DaysEarned = req.CompOffDays, Status = "Approved", ApprovedByName = User.Identity?.Name ?? "HR",
            ApprovedAtUtc = DateTime.UtcNow
        });
        await SaveAudit("overtime.compoff.created", "OvertimeCompOffConversion", compOff.Id.ToString(), ct);
        try
        {
            // Conversion, leave credit and audit are one implicit transaction. The request and
            // conversion unique keys make the initial Any check advisory rather than authoritative.
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is Npgsql.PostgresException
                { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return Conflict(new { message = "This overtime request has already been converted to comp-off." });
        }
        return Created($"/api/overtime/comp-off-conversions/{compOff.Id}", compOff);
    }

    [HttpGet("reports/summary")]
    public async Task<ActionResult<object>> Summary([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var start = from ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var requestQuery = _db.OvertimeRequests.AsNoTracking().Where(x => x.TenantId == tenantId && x.WorkDate >= start && x.WorkDate <= end);
        if (!scope.IsUnrestricted) requestQuery = requestQuery.Where(x => scope.AllowedEmployeeIds!.Contains(x.EmployeeId));
        var requests = await requestQuery.ToListAsync(ct);
        var impacts = await _db.OvertimePayrollImpacts.AsNoTracking().Where(x => x.TenantId == tenantId && requests.Select(r => r.Id).Contains(x.OvertimeRequestId)).ToListAsync(ct);
        return Ok(new
        {
            totalRequests = requests.Count,
            approvedRequests = requests.Count(x => x.Status == "Approved"),
            pendingRequests = requests.Count(x => x.Status.StartsWith("Pending")),
            approvedHours = impacts.Sum(x => x.Hours),
            payrollAmount = impacts.Sum(x => x.Amount)
        });
    }

    private async Task<OvertimeCalculation> Calculate(OvertimeRequest request, CancellationToken ct)
    {
        var tenantId = request.TenantId;
        var policy = request.OvertimePolicyId.HasValue
            ? await _db.OvertimePolicies.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == request.OvertimePolicyId && !x.IsDeleted, ct)
            : await OvertimePolicyDefault.Order(_db.OvertimePolicies.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted)).FirstOrDefaultAsync(ct);
        policy ??= new OvertimePolicy { TenantId = tenantId, HourlyRateBasis = "BasicSalary", StandardMonthlyHours = 240 };
        var salary = await _db.EmployeeSalaryStructures.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == request.EmployeeId && x.IsActive && x.EffectiveDate <= request.WorkDate).OrderByDescending(x => x.EffectiveDate).FirstOrDefaultAsync(ct);
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == request.EmployeeId, ct);
        var basic = salary?.BasicSalary ?? employee?.Salary ?? 0m;
        var gross = salary is null ? basic : salary.BasicSalary + salary.HousingAllowance + salary.TransportAllowance + salary.FoodAllowance + salary.MobileAllowance + salary.OtherAllowance;

        // Weekend categorisation is config-driven (WorkWeekService): the employee's company
        // resolves the rest days, not a hard-coded Fri/Sat — which was wrong for UAE (Sat/Sun)
        // and any non-default tenant.
        var workWeek = await _workWeek.ResolveAsync(tenantId, employee?.CompanyId, string.IsNullOrWhiteSpace(employee?.CountryCode) ? null : employee!.CountryCode, ct);
        var dayCategory = OvertimeStatutoryCalculator.DayCategory(
            request.WorkDate,
            await IsPublicHoliday(tenantId, request.WorkDate, ct),
            workWeek.IsWeekend(request.WorkDate.DayOfWeek));

        // ── The statutory context: the SAME rule keys, fallbacks and arithmetic the payroll run
        //    uses (PayrollController → OvertimeStatutoryContext / OvertimeStatutoryCalculator).
        //    Before this, the controller computed basic ÷ StandardMonthlyHours × policy-multiplier
        //    and never read a statutory rule, so it reported 112.50 for the canonical 60/40 Saudi
        //    overtime hour that payroll paid at 162.50 (Art. 107: hourly WAGE + 50% of BASIC).
        var (packCc, packJur) = await ResolveCountryPackAsync(tenantId, employee?.CompanyId, employee?.CountryCode, ct);
        var otContext = await _ruleReader.ResolveAsync(packCc, packJur, request.WorkDate, tenantId, ct, companyId: employee?.CompanyId);

        // The divisor: the same rule payroll applies — statutory hours unless the policy is more generous.
        var standardMonthlyHours = OvertimeStatutoryContextResolver.EffectiveMonthlyHours(policy.StandardMonthlyHours, otContext.StandardMonthlyHoursOverride);

        // The multiplier the tenant configured for this day category. It is a CANDIDATE only:
        // EffectiveMultiplier honours it at or above the statutory floor for the day and never
        // below it — an approval workflow cannot authorise an unlawful rate. That is exactly the
        // check payroll applies to the ApprovedMultiplier this method goes on to persist, so the
        // rate stamped at approval time is already the rate payroll will pay.
        var configuredMultiplier = await _db.OvertimeMultipliers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OvertimePolicyId == policy.Id && x.DayCategory == dayCategory && x.IsActive)
            .Select(x => x.Multiplier).FirstOrDefaultAsync(ct);
        var multiplier = otContext.EffectiveMultiplier(configuredMultiplier, dayCategory);

        // Art. 107's 50% is expressly "of his BASIC wage", so basic hourly is always the statutory
        // uplift base; only the FIRST term follows the jurisdiction's ot.hourly_base.
        //
        // The tenant's own HourlyRateBasis (GrossSalary, or FixedHourlyRate + FixedHourlyRate) is a
        // CONTRACTUAL rate and is honoured only where it is worth at least the Art. 107 hour —
        // resolved by the SHARED OvertimeStatutoryCalculator.ResolveHourRate, which the payroll run
        // now calls with the same policy. Before that, FixedHourlyRate was honoured here and read by
        // nothing in payroll: the module displayed a number the payroll engine would never pay.
        var basicHourly = basic / standardMonthlyHours;
        var wageHourly = gross / standardMonthlyHours;
        var hourRate = OvertimeStatutoryCalculator.ResolveHourRate(
            policy.HourlyRateBasis, policy.FixedHourlyRate,
            otContext.BaseIsFullWage ? wageHourly : basicHourly,
            wageHourly, basicHourly, multiplier);
        var baseHourly = hourRate.BaseHourly;

        // Rounded once at the end, from the unrounded hourly rates AND the unrounded quantity — the
        // same shape as the payroll run, which rounds only the summed overtime line. Rounding the
        // hourly rate first (as this method used to) put the controller a cent away from payroll on
        // any salary that does not divide evenly by the monthly hours; rounding the QUANTITY first
        // (Math.Round(ApprovedMinutes / 60m, 2), which this line used to do) quantised every
        // overtime request to 0.6-minute steps and underpaid 50 approved minutes as 0.83 h.
        // Minutes are the input; the division by 60 happens once, here, inside the money expression.
        var amount = Math.Round(request.ApprovedMinutes / 60m * hourRate.HourPay, 2);
        var currency = !string.IsNullOrWhiteSpace(salary?.Currency) ? salary.Currency : await _db.ResolveTenantCurrencyAsync(tenantId, ct);
        var calculationJson =
            $"{{\"dayCategory\":\"{dayCategory}\",\"basis\":\"{policy.HourlyRateBasis}\"," +
            $"\"otHourlyBase\":\"{(otContext.BaseIsFullWage ? "wage" : "basic")}\"," +
            $"\"standardMonthlyHours\":{standardMonthlyHours}," +
            $"\"basicHourly\":{Math.Round(hourRate.UpliftBasisHourly, 4).ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
            $"\"baseHourly\":{Math.Round(baseHourly, 4).ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
            // Whether the tenant's configured base (GrossSalary / FixedHourlyRate) beat the Art. 107
            // hour and is therefore what payroll will pay, or whether the statutory floor applied.
            $"\"policyRateHonoured\":{(hourRate.PolicyHonoured ? "true" : "false")}," +
            $"\"statutoryFloor\":{otContext.FloorFor(dayCategory).ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
            $"\"configuredMultiplier\":{configuredMultiplier.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
            $"\"effectiveMultiplier\":{multiplier.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
        return new OvertimeCalculation
        {
            TenantId = tenantId,
            OvertimeRequestId = request.Id,
            EmployeeId = request.EmployeeId,
            ApprovedMinutes = request.ApprovedMinutes,
            // The Art. 107 base hourly rate — the first term of the hour's pay. The full
            // arithmetic (including the basic-hourly uplift base) is in CalculationJson, because
            // hours × HourlyRate × Multiplier is not the shape of an Art. 107 overtime hour.
            HourlyRate = Math.Round(baseHourly, 2),
            // The DAY multiplier, not an effective rate ratio: payroll re-floors this value via
            // OvertimeStatutoryCalculator.EffectiveMultiplier, so it must stay a day multiplier.
            Multiplier = multiplier,
            Amount = amount,
            Currency = currency,
            CalculationJson = calculationJson
        };
    }

    /// <summary>
    /// The country pack to read statutory overtime rules from: the employee's company, falling
    /// back to the employee's own country code and then to the tenant's first active company —
    /// the same company row whose CountryCode/Jurisdiction the payroll run uses.
    /// </summary>
    private async Task<(string CountryCode, string Jurisdiction)> ResolveCountryPackAsync(
        Guid tenantId, Guid? companyId, string? employeeCountryCode, CancellationToken ct)
    {
        var company = companyId.HasValue
            ? await _db.Companies.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.Id == companyId.Value)
                .Select(c => new { c.CountryCode, c.Jurisdiction })
                .FirstOrDefaultAsync(ct)
            : null;
        company ??= await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive && !c.IsDeleted)
            .OrderBy(c => c.CreatedAtUtc)
            .Select(c => new { c.CountryCode, c.Jurisdiction })
            .FirstOrDefaultAsync(ct);

        var cc = company?.CountryCode;
        if (string.IsNullOrWhiteSpace(cc)) cc = employeeCountryCode;
        if (string.IsNullOrWhiteSpace(cc))
            cc = await _db.TenantLocalizationSettings.AsNoTracking()
                .Where(x => x.TenantId == tenantId)
                .Select(x => x.CountryCode)
                .FirstOrDefaultAsync(ct);

        return (cc ?? string.Empty, company?.Jurisdiction ?? string.Empty);
    }

    private Task<bool> IsPublicHoliday(Guid tenantId, DateOnly date, CancellationToken ct) =>
        _db.PublicHolidays.AnyAsync(x => x.TenantId == tenantId && x.Date == date && !x.IsOptional, ct);

    private async Task<decimal> ResolveRegularDayDefaultMultiplierAsync(Guid tenantId, CancellationToken ct)
    {
        var countryCode = await _db.TenantLocalizationSettings.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => x.CountryCode)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(countryCode))
            countryCode = await _db.Companies.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted)
                .OrderBy(x => x.CreatedAtUtc)
                .Select(x => x.CountryCode)
                .FirstOrDefaultAsync(ct);

        return DefaultRegularDayMultiplier(countryCode);
    }

    /// <summary>
    /// The regular-day multiplier a NEW policy is seeded with when the caller supplies none.
    /// This is a policy-authoring convenience, not the statutory rate: what actually gets paid is
    /// the configured multiplier floored at the jurisdiction's statutory rate, resolved in
    /// <see cref="OvertimeStatutoryContext"/> from <c>ot.standard_multiplier</c>.
    /// </summary>
    internal static decimal DefaultRegularDayMultiplier(string? countryCode) =>
        string.Equals(countryCode, CountryCodes.Saudi, StringComparison.OrdinalIgnoreCase)
        || string.Equals(countryCode, "SA", StringComparison.OrdinalIgnoreCase)
            ? 1.5m
            : 1.25m;

    private async Task SaveAudit(string action, string entity, string entityId, CancellationToken ct, object? metadata = null)
    {
        _db.OvertimeAuditLogs.Add(new OvertimeAuditLog
        {
            TenantId = RequireTenant(), Action = action, EntityName = entity, EntityId = entityId, UserId = GetUserId(),
            MetadataJson = metadata is null ? "{}" : System.Text.Json.JsonSerializer.Serialize(metadata),
        });
        await Task.CompletedTask;
    }

    private OvertimeApproval NewOvertimeApproval(
        Guid tenantId, Guid requestId, string level, string decision, string? notes) => new()
    {
        TenantId = tenantId,
        OvertimeRequestId = requestId,
        ApprovalLevel = level,
        Decision = decision,
        Notes = notes ?? string.Empty,
        DecidedByUserId = GetUserId(),
        DecidedAtUtc = DateTime.UtcNow
    };

    private async Task<bool> TrySaveDecisionAsync(CancellationToken ct)
    {
        try
        {
            // SaveChanges is an implicit relational transaction. DecisionVersion is a concurrency
            // token (CAS); unique outcome rows ensure calculation/payroll/approval can exist once.
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is Npgsql.PostgresException
                { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
    }

    private Guid RequireTenant() => Guid.Parse(User.FindFirstValue("tenant_id")!);
    private Guid? GetUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;
    private async Task<bool> IsCurrentUserResolvedApproverAsync(Guid tenantId, int employeeId, string workflowType, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return false;
        var callerEmployeeId = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsDeleted && e.UserAccountId == userId)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (callerEmployeeId is null) return false;
        var resolved = await _hierarchyService.ResolveWorkflowApproversAsync(tenantId, employeeId, workflowType, ct);
        return resolved.Approvers.Any(a => a.EmployeeId == callerEmployeeId.Value);
    }
}

public record OvertimePolicyRequest(string Code, string Name, string? HourlyRateBasis, decimal FixedHourlyRate, int StandardMonthlyHours, int MinimumMinutes, int MaximumMinutesPerDay, int MonthlyCapMinutes, string? RoundingRule, bool RequiresApproval, bool AllowCompOffConversion, decimal RegularDayMultiplier, decimal WeekendMultiplier, decimal HolidayMultiplier);
public record OvertimeTypeRequest(string Code, string Name, string? Category);
public record OvertimeRequestCreate(int EmployeeId, Guid? OvertimePolicyId, Guid? OvertimeTypeId, DateOnly WorkDate, DateTime StartTimeUtc, DateTime EndTimeUtc, string? Source, string? Reason);
public record OvertimeDecisionRequest(int ApprovedMinutes, string? Notes);
public record DetectOvertimeRequest(DateOnly FromDate, DateOnly ToDate, Guid? OvertimePolicyId);
/// <summary>
/// What a detection run did. <paramref name="Capped"/> is not decoration: it is the overtime the
/// tenant's policy will not pay, and it is the only reason capping the attendance door is a fix
/// rather than a quieter version of the same bug.
/// </summary>
public record DetectOvertimeResult(
    IReadOnlyCollection<OvertimeRequest> Created,
    IReadOnlyCollection<AttendanceException> Capped);
public record CompOffConversionRequest(Guid OvertimeRequestId, decimal CompOffDays);
