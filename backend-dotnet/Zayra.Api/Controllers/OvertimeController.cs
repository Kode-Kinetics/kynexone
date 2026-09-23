using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Organization;
using Zayra.Api.Application.WorkWeek;
using Zayra.Api.Data;
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
    [Authorize(Roles = "Admin,HR Manager,Payroll Officer")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: Code, Name, HourlyRateBasis, FixedHourlyRate, scheduling caps, RoundingRule, flags. No employee salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<OvertimePolicy>> CreatePolicy(OvertimePolicyRequest req, CancellationToken ct)
    {
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

    [HttpGet("types")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: Code, Name, Category, IsActive. No employee data, salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<IReadOnlyCollection<OvertimeType>>> Types(CancellationToken ct)
    {
        var tenantId = RequireTenant();
        return Ok(await _db.OvertimeTypes.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive).OrderBy(x => x.Name).ToListAsync(ct));
    }

    [HttpPost("types")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Officer")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: Code, Name, Category, IsActive. No employee data, salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<OvertimeType>> CreateType(OvertimeTypeRequest req, CancellationToken ct)
    {
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
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == req.EmployeeId && !x.IsDeleted, ct);
        if (employee is null) return BadRequest(new { message = "Employee not found." });
        if (req.EndTimeUtc <= req.StartTimeUtc) return BadRequest(new { message = "End time must be after start time." });
        if (string.IsNullOrWhiteSpace(req.Reason)) return BadRequest(new { message = "A reason is required." });
        var policy = req.OvertimePolicyId.HasValue
            ? await _db.OvertimePolicies.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == req.OvertimePolicyId && x.IsActive && !x.IsDeleted, ct)
            : await _db.OvertimePolicies.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted, ct);
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
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: EmployeeId, EmployeeName, WorkDate, start/end times, requested/approved minutes, Reason, Status. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
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
            : await _db.OvertimePolicies.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted, ct);
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

    [HttpGet("payroll-review")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Officer,Payroll Manager,Auditor")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: OvertimeRequestId, EmployeeId, PayrollRunId, Hours, Amount, Status. Payroll-role consumers require this data to process overtime pay. No bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<IReadOnlyCollection<OvertimePayrollImpact>>> PayrollReview(CancellationToken ct)
    {
        var tenantId = RequireTenant();
        return Ok(await _db.OvertimePayrollImpacts.AsNoTracking().Where(x => x.TenantId == tenantId && x.Status == "PendingPayroll").OrderBy(x => x.EmployeeId).ToListAsync(ct));
    }

    [HttpGet("calculations")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: OvertimeRequestId, EmployeeId, ApprovedHours, HourlyRate (derived from salary for computation), Multiplier, Amount, CalculationJson. Scope-filtered to caller's accessible employees. No bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<IReadOnlyCollection<OvertimeCalculation>>> Calculations([FromQuery] int? employeeId, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
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
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: OvertimeRequestId, EmployeeId, OvertimeHours, CompOffDays, Status, CreatedAtUtc. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<OvertimeCompOffConversion>> CreateCompOffConversion(CompOffConversionRequest req, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var request = await _db.OvertimeRequests.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == req.OvertimeRequestId && x.Status == "Approved", ct);
        if (request is null) return BadRequest(new { message = "Approved overtime request not found." });
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
            : await _db.OvertimePolicies.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted, ct);
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
        var otContext = await _ruleReader.ResolveAsync(packCc, packJur, request.WorkDate, tenantId, ct);

        // The divisor: the request's own policy, unless the pack states one (payroll applies the
        // same rule override on top of the active policy's StandardMonthlyHours).
        var standardMonthlyHours = otContext.StandardMonthlyHoursOverride is int h && h > 0
            ? h
            : Math.Max(1, policy.StandardMonthlyHours);

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

    private async Task SaveAudit(string action, string entity, string entityId, CancellationToken ct)
    {
        _db.OvertimeAuditLogs.Add(new OvertimeAuditLog { TenantId = RequireTenant(), Action = action, EntityName = entity, EntityId = entityId, UserId = GetUserId() });
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
