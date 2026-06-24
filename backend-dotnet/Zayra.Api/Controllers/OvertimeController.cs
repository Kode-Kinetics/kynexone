using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

[ApiController]
[Route("api/overtime")]
[Authorize]
public class OvertimeController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;

    public OvertimeController(ZayraDbContext db, IDataScopeService scopeService)
    {
        _db = db;
        _scopeService = scopeService;
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
        _db.OvertimeMultipliers.Add(new OvertimeMultiplier { TenantId = tenantId, OvertimePolicyId = policy.Id, DayCategory = "RegularDay", Multiplier = req.RegularDayMultiplier <= 0 ? 1.25m : req.RegularDayMultiplier });
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
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == req.EmployeeId && !x.IsDeleted, ct);
        if (employee is null) return BadRequest(new { message = "Employee not found." });
        var minutes = (int)Math.Max(0, Math.Round((req.EndTimeUtc - req.StartTimeUtc).TotalMinutes));
        var request = new OvertimeRequest
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            EmployeeName = employee.FullName,
            OvertimePolicyId = req.OvertimePolicyId,
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

    [HttpPost("detect-from-attendance")]
    [Authorize(Roles = "Admin,HR Manager,Payroll Officer")]
    [AllowEntityReturn("Flat entity — no navigation properties. Fields: EmployeeId, EmployeeName, WorkDate, start/end times, requested/approved minutes, Reason, Status. No salary, bank/IBAN, passport, national-ID, medical, or disciplinary data.")]
    public async Task<ActionResult<IReadOnlyCollection<OvertimeRequest>>> DetectFromAttendance(DetectOvertimeRequest req, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var daily = await _db.AttendanceDailyRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.WorkDate >= req.FromDate && x.WorkDate <= req.ToDate && x.OvertimeMinutes > 0)
            .ToListAsync(ct);
        var created = new List<OvertimeRequest>();
        foreach (var record in daily)
        {
            var exists = await _db.OvertimeRequests.AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == record.EmployeeId && x.WorkDate == record.WorkDate && x.Source == "Attendance", ct);
            if (exists) continue;
            var request = new OvertimeRequest
            {
                TenantId = tenantId,
                EmployeeId = record.EmployeeId,
                EmployeeName = record.EmployeeName,
                OvertimePolicyId = req.OvertimePolicyId,
                WorkDate = record.WorkDate,
                StartTimeUtc = record.LastOutUtc?.AddMinutes(-record.OvertimeMinutes) ?? DateTime.UtcNow,
                EndTimeUtc = record.LastOutUtc ?? DateTime.UtcNow,
                RequestedMinutes = record.OvertimeMinutes,
                Source = "Attendance",
                Reason = "Auto-detected from processed attendance",
                Status = "PendingManager",
                AttendanceDailyRecordId = record.Id,
                CreatedBy = GetUserId()
            };
            _db.OvertimeRequests.Add(request);
            created.Add(request);
        }
        await _db.SaveChangesAsync(ct);
        return Ok(created);
    }

    [HttpPost("requests/{id:guid}/approve")]
    [Authorize(Roles = "Admin,HR Manager,Manager,Supervisor")]
    public async Task<ActionResult> Approve(Guid id, OvertimeDecisionRequest req, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var request = await _db.OvertimeRequests.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (request is null) return NotFound();
        if (!request.Status.StartsWith("Pending")) return BadRequest(new { message = "Only pending overtime can be approved." });

        var isAdmin = User.IsInRole("Admin");
        var isHR = User.IsInRole("HR Manager");
        var isManager = User.IsInRole("Manager") || User.IsInRole("Supervisor");

        _db.OvertimeApprovals.Add(new OvertimeApproval
        {
            TenantId = tenantId,
            OvertimeRequestId = id,
            Decision = "Approved",
            Notes = req.Notes ?? string.Empty,
            DecidedByUserId = GetUserId(),
            DecidedAtUtc = DateTime.UtcNow
        });

        // Admin bypasses all steps; HR Manager finalises from PendingHR
        if (isAdmin || (isHR && request.Status == "PendingHR"))
        {
            request.Status = "Approved";
            request.ApprovedMinutes = req.ApprovedMinutes > 0 ? req.ApprovedMinutes : request.RequestedMinutes;
            request.DecidedAtUtc = DateTime.UtcNow;
            var calc = await Calculate(request, ct);
            _db.OvertimeCalculations.Add(calc);
            _db.OvertimePayrollImpacts.Add(new OvertimePayrollImpact
            {
                TenantId = tenantId,
                OvertimeRequestId = request.Id,
                EmployeeId = request.EmployeeId,
                Hours = calc.ApprovedHours,
                Amount = calc.Amount,
                // Persist the multiplier used at approval time so payroll can apply
                // the correct rate (e.g. 2× for holiday/rest-day) without re-resolving the policy.
                ApprovedMultiplier = calc.Multiplier,
            });
            await SaveAudit("overtime.request.approved", "OvertimeRequest", request.Id.ToString(), ct);
            await _db.SaveChangesAsync(ct);
            return Ok(calc);
        }

        // Manager/Supervisor advances PendingManager → PendingHR (no calculation yet)
        if (isManager && request.Status == "PendingManager")
        {
            request.Status = "PendingHR";
            await SaveAudit("overtime.request.manager_approved", "OvertimeRequest", request.Id.ToString(), ct);
            await _db.SaveChangesAsync(ct);
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
        request.Status = "Rejected";
        request.DecidedAtUtc = DateTime.UtcNow;
        _db.OvertimeApprovals.Add(new OvertimeApproval { TenantId = tenantId, OvertimeRequestId = id, Decision = "Rejected", Notes = req.Notes ?? string.Empty, DecidedByUserId = GetUserId(), DecidedAtUtc = DateTime.UtcNow });
        await SaveAudit("overtime.request.rejected", "OvertimeRequest", request.Id.ToString(), ct);
        await _db.SaveChangesAsync(ct);
        return Ok(request);
    }

    [HttpGet("payroll-review")]
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
        var query = _db.OvertimeCompOffConversions.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (employeeId.HasValue) query = query.Where(x => x.EmployeeId == employeeId.Value);
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
        if (policy is not null && !policy.AllowCompOffConversion) return BadRequest(new { message = "Comp-off conversion is not allowed by this policy." });
        var compOff = new OvertimeCompOffConversion { TenantId = tenantId, OvertimeRequestId = req.OvertimeRequestId, EmployeeId = request.EmployeeId, OvertimeHours = Math.Round(request.ApprovedMinutes / 60m, 2), CompOffDays = req.CompOffDays, Status = "Pending" };
        _db.OvertimeCompOffConversions.Add(compOff);
        await SaveAudit("overtime.compoff.created", "OvertimeCompOffConversion", compOff.Id.ToString(), ct);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/overtime/comp-off-conversions/{compOff.Id}", compOff);
    }

    [HttpGet("reports/summary")]
    public async Task<ActionResult<object>> Summary([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var tenantId = RequireTenant();
        var start = from ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var requests = await _db.OvertimeRequests.AsNoTracking().Where(x => x.TenantId == tenantId && x.WorkDate >= start && x.WorkDate <= end).ToListAsync(ct);
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
        var salary = await _db.EmployeeSalaryStructures.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == request.EmployeeId && x.IsActive).OrderByDescending(x => x.EffectiveDate).FirstOrDefaultAsync(ct);
        var employee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == request.EmployeeId, ct);
        var basic = salary?.BasicSalary ?? employee?.Salary ?? 0m;
        var gross = salary is null ? basic : salary.BasicSalary + salary.HousingAllowance + salary.TransportAllowance + salary.FoodAllowance + salary.MobileAllowance + salary.OtherAllowance;
        var rateBase = policy.HourlyRateBasis switch
        {
            "GrossSalary" => gross,
            "FixedHourlyRate" => policy.FixedHourlyRate * policy.StandardMonthlyHours,
            _ => basic
        };
        var hourlyRate = policy.HourlyRateBasis == "FixedHourlyRate" ? policy.FixedHourlyRate : Math.Round(rateBase / Math.Max(1, policy.StandardMonthlyHours), 2);
        var dayCategory = await IsPublicHoliday(tenantId, request.WorkDate, ct) ? "PublicHoliday" : request.WorkDate.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday ? "Weekend" : "RegularDay";
        var multiplier = await _db.OvertimeMultipliers.AsNoTracking().Where(x => x.TenantId == tenantId && x.OvertimePolicyId == policy.Id && x.DayCategory == dayCategory && x.IsActive).Select(x => x.Multiplier).FirstOrDefaultAsync(ct);
        if (multiplier <= 0) multiplier = dayCategory == "PublicHoliday" ? 2m : dayCategory == "Weekend" ? 1.5m : 1.25m;
        var approvedHours = Math.Round(request.ApprovedMinutes / 60m, 2);
        var amount = Math.Round(approvedHours * hourlyRate * multiplier, 2);
        var currency = !string.IsNullOrWhiteSpace(salary?.Currency) ? salary.Currency : await _db.ResolveTenantCurrencyAsync(tenantId, ct);
        return new OvertimeCalculation { TenantId = tenantId, OvertimeRequestId = request.Id, EmployeeId = request.EmployeeId, ApprovedHours = approvedHours, HourlyRate = hourlyRate, Multiplier = multiplier, Amount = amount, Currency = currency, CalculationJson = $"{{\"dayCategory\":\"{dayCategory}\",\"basis\":\"{policy.HourlyRateBasis}\"}}" };
    }

    private Task<bool> IsPublicHoliday(Guid tenantId, DateOnly date, CancellationToken ct) =>
        _db.PublicHolidays.AnyAsync(x => x.TenantId == tenantId && x.Date == date && !x.IsOptional, ct);

    private async Task SaveAudit(string action, string entity, string entityId, CancellationToken ct)
    {
        _db.OvertimeAuditLogs.Add(new OvertimeAuditLog { TenantId = RequireTenant(), Action = action, EntityName = entity, EntityId = entityId, UserId = GetUserId() });
        await Task.CompletedTask;
    }

    private Guid RequireTenant() => Guid.Parse(User.FindFirstValue("tenant_id")!);
    private Guid? GetUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;
}

public record OvertimePolicyRequest(string Code, string Name, string? HourlyRateBasis, decimal FixedHourlyRate, int StandardMonthlyHours, int MinimumMinutes, int MaximumMinutesPerDay, int MonthlyCapMinutes, string? RoundingRule, bool RequiresApproval, bool AllowCompOffConversion, decimal RegularDayMultiplier, decimal WeekendMultiplier, decimal HolidayMultiplier);
public record OvertimeTypeRequest(string Code, string Name, string? Category);
public record OvertimeRequestCreate(int EmployeeId, Guid? OvertimePolicyId, Guid? OvertimeTypeId, DateOnly WorkDate, DateTime StartTimeUtc, DateTime EndTimeUtc, string? Source, string? Reason);
public record OvertimeDecisionRequest(int ApprovedMinutes, string? Notes);
public record DetectOvertimeRequest(DateOnly FromDate, DateOnly ToDate, Guid? OvertimePolicyId);
public record CompOffConversionRequest(Guid OvertimeRequestId, decimal CompOffDays);
