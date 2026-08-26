using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Leave;

[ApiController]
[Route("api/leave/encashment")]
[Authorize]
public class EncashmentController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;
    private readonly IStatutoryRuleReader _rules;

    public EncashmentController(ZayraDbContext db, IDataScopeService scopeService, IStatutoryRuleReader rules)
    {
        _db = db;
        _scopeService = scopeService;
        _rules = rules;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] int? employeeId,
        [FromQuery] int? year,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);

        var query = _db.LeaveEncashmentRequests.Where(e => e.TenantId == tenantId);
        if (!scope.IsUnrestricted)
            query = query.Where(e => scope.AllowedEmployeeIds!.Contains(e.EmployeeId));
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(e => e.Status == status);
        if (employeeId.HasValue) query = query.Where(e => e.EmployeeId == employeeId.Value);
        if (year.HasValue) query = query.Where(e => e.Year == year.Value);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(e => e.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PagedResult<LeaveEncashmentRequest>(items, total, page, pageSize));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateEncashmentRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!(User.IsInRole("Admin") || User.IsInRole("HR Manager")) && scope.CallerEmployeeId != req.EmployeeId)
            return Forbid();
        if (!scope.CanAccessEmployee(req.EmployeeId)) return Forbid();
        if (req.DaysToEncash <= 0) return BadRequest(new { message = "Days to encash must be positive." });

        var employee = await _db.Employees
            .FirstOrDefaultAsync(e => e.Id == req.EmployeeId && e.TenantId == tenantId, ct);
        if (employee is null)
            return BadRequest(new { message = "Employee not found." });
        if (!employee.CompanyId.HasValue)
            return BadRequest(new { message = "The employee must belong to a legal entity before leave can be encashed." });
        var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c =>
            c.TenantId == tenantId && c.Id == employee.CompanyId && c.IsActive && !c.IsDeleted, ct);
        if (company is null || string.IsNullOrWhiteSpace(company.DefaultCurrency))
            return BadRequest(new { message = "The employee's legal entity must have an active payroll currency." });

        var leaveType = await _db.LeaveTypes
            .FirstOrDefaultAsync(t => t.Id == req.LeaveTypeId && t.TenantId == tenantId, ct);
        if (leaveType is null)
            return BadRequest(new { message = "Leave type not found." });

        var policy = await _db.LeavePolicies
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.LeaveTypeId == req.LeaveTypeId && p.Status == "Active", ct);
        if (policy is null || !policy.EncashmentAllowed)
            return BadRequest(new { message = "Encashment is not allowed for this leave type." });

        var year = req.Year ?? DateTime.UtcNow.Year;
        var balance = await _db.EmployeeLeaveBalances
            .FirstOrDefaultAsync(b => b.TenantId == tenantId && b.EmployeeId == req.EmployeeId
                && b.LeaveTypeId == req.LeaveTypeId && b.Year == year, ct);

        if (balance is null || balance.Available < req.DaysToEncash)
            return BadRequest(new { message = "Insufficient leave balance for encashment." });

        if (req.DaysToEncash > policy.EncashmentMaxDays)
            return BadRequest(new { message = $"Cannot encash more than {policy.EncashmentMaxDays} days per request." });

        var salary = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == req.EmployeeId && x.IsActive)
            .OrderByDescending(x => x.EffectiveDate).FirstOrDefaultAsync(ct);
        var divisor = await ResolveDayDivisorAsync(tenantId.Value, employee, ct);
        var amountPerDay = Math.Round((salary?.BasicSalary ?? employee.Salary ?? 0m) / divisor, 2);
        if (amountPerDay <= 0) return BadRequest(new { message = "An active basic salary is required to calculate encashment." });
        var currency = string.IsNullOrWhiteSpace(salary?.Currency)
            ? company.DefaultCurrency.Trim().ToUpperInvariant()
            : salary.Currency.Trim().ToUpperInvariant();
        if (!string.Equals(currency, company.DefaultCurrency.Trim(), StringComparison.OrdinalIgnoreCase))
            return Conflict(new { message = "The employee salary currency does not match the legal entity payroll currency." });

        var encashmentRequest = new LeaveEncashmentRequest
        {
            TenantId = tenantId.Value,
            CompanyId = employee.CompanyId,
            EmployeeId = req.EmployeeId,
            EmployeeName = employee.FullName,
            LeaveTypeId = req.LeaveTypeId,
            LeaveTypeName = leaveType.NameEn,
            Year = year,
            DaysToEncash = req.DaysToEncash,
            AmountPerDay = amountPerDay,
            TotalAmount = req.DaysToEncash * amountPerDay,
            Currency = currency,
            Reason = req.Reason ?? string.Empty,
            Status = LeaveEncashmentStatuses.Pending
        };

        balance.Pending += req.DaysToEncash;
        balance.UpdatedAtUtc = DateTime.UtcNow;

        _db.LeaveEncashmentRequests.Add(encashmentRequest);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/leave/encashment/{encashmentRequest.Id}", encashmentRequest);
    }

    [HttpPost("{id:guid}/hr-approve")]
    [Authorize(Roles = "HR Manager,Admin")]
    public async Task<IActionResult> HRApprove(Guid id, [FromBody] EncashmentDecisionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var encashment = await _db.LeaveEncashmentRequests
            .FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId, ct);
        if (encashment is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.CanAccessEmployee(encashment.EmployeeId)) return Forbid();

        if (encashment.Status != LeaveEncashmentStatuses.Pending)
            return BadRequest(new { message = "Only pending requests can be HR-approved." });

        encashment.Status = LeaveEncashmentStatuses.HRApproved;
        encashment.DecisionVersion++;
        encashment.HRNotes = req.Notes ?? string.Empty;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "This encashment request was decided concurrently." });
        }
        return Ok(encashment);
    }

    [HttpPost("{id:guid}/payroll-approve")]
    [Authorize(Roles = "Admin,Payroll Officer,Payroll Manager")]
    public async Task<IActionResult> PayrollApprove(Guid id, [FromBody] EncashmentDecisionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var encashment = await _db.LeaveEncashmentRequests
            .FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId, ct);
        if (encashment is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.CanAccessEmployee(encashment.EmployeeId)) return Forbid();

        if (encashment.Status != LeaveEncashmentStatuses.HRApproved)
            return BadRequest(new { message = "Only HR-approved requests can be payroll-approved." });
        if (!req.PayrollRunId.HasValue || req.PayrollRunId == Guid.Empty)
            return BadRequest(new { message = "A target payrollRunId must be selected by the payroll approver." });

        var targetRun = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r =>
            r.TenantId == tenantId && r.Id == req.PayrollRunId.Value, ct);
        if (targetRun is null) return BadRequest(new { message = "Target payroll run was not found in this tenant." });
        var targetError = await ValidateTargetRunAsync(tenantId.Value, encashment, targetRun, ct);
        if (targetError is not null) return targetError;

        encashment.Status = LeaveEncashmentStatuses.PayrollApproved;
        encashment.DecisionVersion++;
        encashment.PayrollNotes = req.Notes ?? string.Empty;
        encashment.PayrollRunId = targetRun.Id;

        var balance = await _db.EmployeeLeaveBalances
            .FirstOrDefaultAsync(b => b.TenantId == tenantId && b.EmployeeId == encashment.EmployeeId
                && b.LeaveTypeId == encashment.LeaveTypeId && b.Year == encashment.Year, ct);

        if (balance is not null)
        {
            if (balance.Pending < encashment.DaysToEncash || balance.Available < 0)
                return Conflict(new { message = "Reserved leave balance is no longer sufficient." });
            var balanceBefore = balance.Available;
            balance.Pending = Math.Max(0, balance.Pending - encashment.DaysToEncash);
            balance.Encashed += encashment.DaysToEncash;
            balance.UpdatedAtUtc = DateTime.UtcNow;

            var txn = new LeaveBalanceTransaction
            {
                TenantId = tenantId.Value,
                CompanyId = encashment.CompanyId,
                EmployeeId = encashment.EmployeeId,
                LeaveTypeId = encashment.LeaveTypeId,
                Year = encashment.Year,
                TransactionType = "Encashed",
                Amount = encashment.DaysToEncash,
                BalanceBefore = balanceBefore,
                BalanceAfter = balance.Available,
                Reference = encashment.Id.ToString(),
                Reason = "Encashment approved",
                PerformedByName = User.Identity?.Name ?? "Payroll"
            };
            _db.LeaveBalanceTransactions.Add(txn);
        }
        else
        {
            return Conflict(new { message = "The reserved leave balance no longer exists." });
        }

        var adjustment = new PayrollAdjustment
        {
            TenantId = tenantId.Value,
            PayrollRunId = targetRun.Id,
            EmployeeId = encashment.EmployeeId,
            AdjustmentType = "Leave Encashment",
            Amount = encashment.TotalAmount,
            Reason = $"Leave encashment {encashment.Id}: {encashment.DaysToEncash:N2} {encashment.LeaveTypeName} day(s)",
            Status = "Approved",
            SourceType = PayrollAdjustmentSources.LeaveEncashment,
            SourceId = encashment.Id
        };
        _db.PayrollAdjustments.Add(adjustment);
        encashment.PayrollAdjustmentId = adjustment.Id;

        try
        {
            // Request CAS, leave ledger, balance and normal payroll artifact commit together.
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "This encashment request was payroll-approved concurrently." });
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is Npgsql.PostgresException
                { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return Conflict(new { message = "This encashment request already has a payroll artifact." });
        }
        return Ok(encashment);
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Roles = "HR Manager,Admin")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] EncashmentDecisionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var encashment = await _db.LeaveEncashmentRequests
            .FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId, ct);
        if (encashment is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.CanAccessEmployee(encashment.EmployeeId)) return Forbid();

        if (encashment.Status is not (LeaveEncashmentStatuses.Pending or LeaveEncashmentStatuses.HRApproved))
            return BadRequest(new { message = "Only pending or HR-approved requests can be rejected. Use void after payroll approval." });

        var balance = await _db.EmployeeLeaveBalances.FirstOrDefaultAsync(b => b.TenantId == tenantId
            && b.EmployeeId == encashment.EmployeeId && b.LeaveTypeId == encashment.LeaveTypeId
            && b.Year == encashment.Year, ct);
        if (balance is not null)
        {
            balance.Pending = Math.Max(0, balance.Pending - encashment.DaysToEncash);
            balance.UpdatedAtUtc = DateTime.UtcNow;
        }
        encashment.Status = LeaveEncashmentStatuses.Rejected;
        encashment.DecisionVersion++;
        encashment.HRNotes = req.Notes ?? string.Empty;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "This encashment request was decided concurrently." });
        }
        return Ok(encashment);
    }

    [HttpPost("{id:guid}/void")]
    [Authorize(Roles = "Admin,Payroll Manager")]
    public async Task<IActionResult> Void(Guid id, [FromBody] EncashmentVoidRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Trim().Length < 5)
            return BadRequest(new { message = "A substantive void reason is required." });

        var encashment = await _db.LeaveEncashmentRequests.FirstOrDefaultAsync(e =>
            e.TenantId == tenantId && e.Id == id, ct);
        if (encashment is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.CanAccessEmployee(encashment.EmployeeId)) return Forbid();
        if (encashment.Status != LeaveEncashmentStatuses.PayrollApproved)
            return Conflict(new { message = "Only an unprocessed payroll-approved encashment can be voided. Void/reopen its payroll run first if already processed." });
        if (!encashment.PayrollAdjustmentId.HasValue || !encashment.PayrollRunId.HasValue)
            return Conflict(new { message = "The encashment has no linked payroll artifact." });

        var run = await _db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r =>
            r.TenantId == tenantId && r.Id == encashment.PayrollRunId, ct);
        if (run is null || run.Status != "Draft")
            return Conflict(new { message = "The linked payroll run is no longer open for an encashment void." });
        var adjustment = await _db.PayrollAdjustments.FirstOrDefaultAsync(a =>
            a.TenantId == tenantId && a.Id == encashment.PayrollAdjustmentId
            && a.SourceType == PayrollAdjustmentSources.LeaveEncashment && a.SourceId == encashment.Id, ct);
        if (adjustment is null || adjustment.Status != "Approved")
            return Conflict(new { message = "The linked payroll artifact is not reversible in its current state." });

        var balance = await _db.EmployeeLeaveBalances.FirstOrDefaultAsync(b => b.TenantId == tenantId
            && b.EmployeeId == encashment.EmployeeId && b.LeaveTypeId == encashment.LeaveTypeId
            && b.Year == encashment.Year, ct);
        if (balance is null || balance.Encashed < encashment.DaysToEncash)
            return Conflict(new { message = "The encashed leave balance cannot be reversed safely." });
        var before = balance.Available;
        balance.Encashed -= encashment.DaysToEncash;
        balance.UpdatedAtUtc = DateTime.UtcNow;
        _db.LeaveBalanceTransactions.Add(new LeaveBalanceTransaction
        {
            TenantId = tenantId.Value,
            CompanyId = encashment.CompanyId,
            EmployeeId = encashment.EmployeeId,
            LeaveTypeId = encashment.LeaveTypeId,
            Year = encashment.Year,
            TransactionType = "EncashmentVoided",
            Amount = encashment.DaysToEncash,
            BalanceBefore = before,
            BalanceAfter = balance.Available,
            Reference = encashment.Id.ToString(),
            Reason = $"Encashment voided: {req.Reason.Trim()}",
            PerformedByName = User.Identity?.Name ?? "Payroll"
        });
        adjustment.Status = "Voided";
        encashment.Status = LeaveEncashmentStatuses.Voided;
        encashment.DecisionVersion++;
        encashment.VoidedAtUtc = DateTime.UtcNow;
        encashment.VoidedByUserId = this.GetUserId();
        encashment.VoidReason = req.Reason.Trim();
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "This encashment request was changed concurrently." });
        }
        return Ok(encashment);
    }

    private async Task<IActionResult?> ValidateTargetRunAsync(
        Guid tenantId, LeaveEncashmentRequest encashment, PayrollRun run, CancellationToken ct)
    {
        if (run.Status != "Draft" || run.LockedAtUtc.HasValue)
            return Conflict(new { message = $"Payroll run {run.Id} is not open and processable (status: {run.Status})." });
        if (!encashment.CompanyId.HasValue || run.CompanyId != encashment.CompanyId)
            return BadRequest(new { message = "The payroll run and employee must belong to the same legal entity." });

        var companyCurrency = await _db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Id == encashment.CompanyId && c.IsActive && !c.IsDeleted)
            .Select(c => c.DefaultCurrency).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(companyCurrency)
            || !string.Equals(companyCurrency.Trim(), encashment.Currency.Trim(), StringComparison.OrdinalIgnoreCase))
            return Conflict(new { message = "The encashment currency does not match the target run's legal entity currency." });

        var employeeEligible = await _db.Employees.AsNoTracking().AnyAsync(e =>
            e.TenantId == tenantId && e.Id == encashment.EmployeeId && !e.IsDeleted
            && e.CompanyId == run.CompanyId && e.Status == "Active"
            && e.JoiningDate <= new DateTime(run.Year, run.Month, DateTime.DaysInMonth(run.Year, run.Month), 0, 0, 0, DateTimeKind.Utc), ct);
        if (!employeeEligible)
            return BadRequest(new { message = "The employee is not eligible for the target payroll run." });

        var selections = await _db.PayrollRunEmployeeSelections.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.PayrollRunId == run.Id).ToListAsync(ct);
        if (selections.Any(s => s.EmployeeId == encashment.EmployeeId && s.Mode == PayrollRunSelectionModes.Exclude))
            return BadRequest(new { message = "The employee is explicitly excluded from the target payroll run." });
        if (PayrollRunTypes.RequiresExplicitPopulation(run.RunType)
            && !selections.Any(s => s.EmployeeId == encashment.EmployeeId && s.Mode == PayrollRunSelectionModes.Include))
            return BadRequest(new { message = "The employee is not explicitly included in this supplemental payroll run." });
        return null;
    }

    private async Task<decimal> ResolveDayDivisorAsync(Guid tenantId, Employee employee, CancellationToken ct)
    {
        const decimal fallback = 30m;
        var companyPack = employee.CompanyId.HasValue
            ? await _db.Companies.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Id == employee.CompanyId)
                .Select(x => new { x.CountryCode, x.Jurisdiction }).FirstOrDefaultAsync(ct)
            : null;
        var countryCode = companyPack?.CountryCode ?? employee.CountryCode;
        if (string.IsNullOrWhiteSpace(countryCode)) return fallback;
        var jurisdiction = !string.IsNullOrWhiteSpace(companyPack?.Jurisdiction)
            ? companyPack.Jurisdiction
            : countryCode switch
            {
                CountryCodes.Saudi => Jurisdictions.KsaMainland,
                CountryCodes.UAE => Jurisdictions.UAEMainland,
                CountryCodes.Qatar => Jurisdictions.QatarMainland,
                _ => countryCode
            };
        var value = await _rules.GetDecimalAsync(countryCode, jurisdiction,
            "lop.monthly_day_divisor", DateOnly.FromDateTime(DateTime.UtcNow), tenantId, ct);
        return value is > 0 ? value.Value : fallback;
    }
}

public record CreateEncashmentRequest(
    int EmployeeId,
    Guid LeaveTypeId,
    decimal DaysToEncash,
    decimal AmountPerDay,
    string? Reason,
    int? Year);

public record EncashmentDecisionRequest(string? Notes, Guid? PayrollRunId = null);
public record EncashmentVoidRequest(string Reason);
