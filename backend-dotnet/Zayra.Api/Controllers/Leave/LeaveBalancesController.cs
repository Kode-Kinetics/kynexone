using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Leave;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Leave;

[ApiController]
[Route("api/leave/balances")]
[Authorize]
public class LeaveBalancesController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly ILeaveService _leaveService;
    private readonly IDataScopeService _scopeService;

    public LeaveBalancesController(ZayraDbContext db, ILeaveService leaveService, IDataScopeService scopeService)
    {
        _db = db;
        _leaveService = leaveService;
        _scopeService = scopeService;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? employeeId,
        [FromQuery] Guid? leaveTypeId,
        [FromQuery] int? year,
        [FromQuery] Guid? companyId,
        [FromQuery] Guid? branchId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);

        year ??= DateTime.UtcNow.Year;
        var query = _db.EmployeeLeaveBalances
            .Where(b => b.TenantId == tenantId && b.Year == year);

        if (!scope.IsUnrestricted)
            query = query.Where(b => scope.AllowedEmployeeIds!.Contains(b.EmployeeId));
        if (employeeId.HasValue) query = query.Where(b => b.EmployeeId == employeeId.Value);
        if (leaveTypeId.HasValue) query = query.Where(b => b.LeaveTypeId == leaveTypeId.Value);
        var group = await LeaveGroupFilter.EmployeeIdsAsync(_db, tenantId.Value, companyId, branchId, ct);
        if (group is not null) query = query.Where(b => group.Contains(b.EmployeeId));

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(b => b.EmployeeName)
            .ThenBy(b => b.LeaveTypeName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        await MarkStatutoryEntitlementsAsync(tenantId.Value, items, ct);

        return Ok(new PagedResult<EmployeeLeaveBalance>(items, total, page, pageSize));
    }

    [HttpGet("employee/{employeeId:int}")]
    public async Task<IActionResult> GetByEmployee(int employeeId, [FromQuery] int? year, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.IsUnrestricted && !scope.AllowedEmployeeIds!.Contains(employeeId))
            return Forbid();

        year ??= DateTime.UtcNow.Year;
        var balances = await _db.EmployeeLeaveBalances
            .Where(b => b.TenantId == tenantId && b.EmployeeId == employeeId && b.Year == year)
            .OrderBy(b => b.LeaveTypeName)
            .ToListAsync(ct);
        await MarkStatutoryEntitlementsAsync(tenantId.Value, balances, ct);

        return Ok(balances);
    }

    /// <summary>
    /// KSA statutory event leave (maternity, Hajj, marriage…) is granted by the statute per event, not
    /// drawn from this balance, so its Available can read negative while a request is pending. Stamp
    /// the statutory figure on those rows so the screen says "Statutory entitlement" instead.
    /// </summary>
    private async Task MarkStatutoryEntitlementsAsync(Guid tenantId, IReadOnlyCollection<EmployeeLeaveBalance> balances, CancellationToken ct)
    {
        foreach (var byEmployee in balances.GroupBy(b => b.EmployeeId))
        {
            var figures = await _leaveService.GetKsaStatutoryEntitlementsAsync(
                tenantId, byEmployee.Key, byEmployee.Select(b => b.LeaveTypeId).ToList(), ct);
            foreach (var b in byEmployee)
                if (figures.TryGetValue(b.LeaveTypeId, out var days)) b.StatutoryEntitlementDays = days;
        }
    }

    [HttpPost("adjust")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> Adjust([FromBody] BalanceAdjustmentRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var userId = this.GetUserId();
        var performedByName = User.Identity?.Name ?? userId?.ToString() ?? "HR";

        var leaveTypeExists = await _db.LeaveTypes
            .AnyAsync(t => t.Id == req.LeaveTypeId && t.TenantId == tenantId, ct);
        if (!leaveTypeExists)
            return BadRequest(new { message = "Leave type not found." });

        var year = req.Year ?? DateTime.UtcNow.Year;

        await _leaveService.ApplyLeaveBalanceAsync(
            tenantId.Value,
            req.EmployeeId,
            req.LeaveTypeId,
            req.Amount,
            year,
            "Adjustment",
            $"MANUAL-ADJ-{Guid.NewGuid():N}",
            performedByName,
            ct);

        await _leaveService.LogAuditAsync(tenantId.Value, "EmployeeLeaveBalance",
            $"{req.EmployeeId}/{req.LeaveTypeId}/{year}", "Adjusted",
            string.Empty, req.Amount.ToString("F2"),
            req.Reason ?? string.Empty, performedByName, ct);

        var balance = await _db.EmployeeLeaveBalances
            .FirstOrDefaultAsync(b => b.TenantId == tenantId && b.EmployeeId == req.EmployeeId
                && b.LeaveTypeId == req.LeaveTypeId && b.Year == year, ct);

        return Ok(new { message = "Balance adjusted successfully.", balance });
    }

    [HttpGet("transactions")]
    public async Task<IActionResult> Transactions(
        [FromQuery] int employeeId,
        [FromQuery] Guid? leaveTypeId,
        [FromQuery] int? year,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.IsUnrestricted && !scope.AllowedEmployeeIds!.Contains(employeeId))
            return Forbid();

        var query = _db.LeaveBalanceTransactions
            .Where(t => t.TenantId == tenantId && t.EmployeeId == employeeId);

        if (leaveTypeId.HasValue) query = query.Where(t => t.LeaveTypeId == leaveTypeId.Value);
        if (year.HasValue) query = query.Where(t => t.Year == year.Value);

        var items = await query
            .OrderByDescending(t => t.CreatedAtUtc)
            .Take(100)
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost("accrue")]
    [Authorize(Roles = "Admin,HR Manager")]
    public async Task<IActionResult> TriggerAccrual(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        await _leaveService.AccrueMonthlyAsync(tenantId.Value, ct);
        return Ok(new { message = "Monthly accrual completed." });
    }
}

public record BalanceAdjustmentRequest(
    int EmployeeId,
    Guid LeaveTypeId,
    decimal Amount,
    string Reason,
    int? Year);
