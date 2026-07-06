using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

// Legacy leave-requests endpoint — kept for backwards compatibility.
// New endpoints are under api/leave/* (Controllers/Leave/).
[ApiController]
[Route("api/leave-requests")]
[Authorize]
public class LeaveController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;

    public LeaveController(ZayraDbContext db, IDataScopeService scopeService)
    {
        _db = db;
        _scopeService = scopeService;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] int? employeeId,
        [FromQuery] string? leaveType,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId();
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
        var (singleId, setFilter) = scope.Constrain(employeeId);
        var query = _db.LeaveRequests.Where(l => l.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(l => l.Status == status);
        if (setFilter is not null) query = query.Where(l => setFilter.Contains(l.EmployeeId));
        else if (singleId.HasValue) query = query.Where(l => l.EmployeeId == singleId.Value);
        if (!string.IsNullOrWhiteSpace(leaveType)) query = query.Where(l => l.LeaveTypeName == leaveType);

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(l => l.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return Ok(new PagedResult<LeaveRequest>(items, total, page, pageSize));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] LegacyCreateLeaveRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == req.EmployeeId && e.TenantId == tenantId, cancellationToken);
        if (employee is null) return BadRequest(new { message = "Employee not found." });
        if (req.EndDate < req.StartDate) return BadRequest(new { message = "End date must be after start date." });

        var days = (decimal)(req.EndDate.DayNumber - req.StartDate.DayNumber) + 1;

        var leaveType = await _db.LeaveTypes
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.NameEn == req.LeaveType, cancellationToken);

        var balance = leaveType is not null
            ? await GetOrCreateBalance(tenantId, req.EmployeeId, DateTime.UtcNow.Year, leaveType.Id, leaveType.NameEn, cancellationToken)
            : null;

        if (balance is not null)
        {
            if (balance.Available < days)
                return BadRequest(new { message = "Insufficient leave balance." });
            balance.Pending += days;
            balance.UpdatedAtUtc = DateTime.UtcNow;
        }

        var leave = new LeaveRequest
        {
            TenantId = tenantId,
            EmployeeId = req.EmployeeId,
            EmployeeName = employee.FullName,
            DepartmentName = employee.Department ?? string.Empty,
            DesignationTitle = employee.Designation ?? string.Empty,
            LeaveTypeId = leaveType?.Id ?? Guid.Empty,
            LeaveTypeName = req.LeaveType,
            StartDate = req.StartDate,
            EndDate = req.EndDate,
            TotalDays = days,
            Reason = req.Reason,
            Status = "Submitted",
            SubmittedAtUtc = DateTime.UtcNow
        };
        _db.LeaveRequests.Add(leave);
        await _db.SaveChangesAsync(cancellationToken);
        return Created($"/api/leave-requests/{leave.Id}", leave);
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Roles = "Admin,HR Manager,Manager")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var leave = await _db.LeaveRequests.FirstOrDefaultAsync(l => l.Id == id && l.TenantId == tenantId, cancellationToken);
        if (leave is null) return NotFound();
        if (leave.Status != "Submitted" && leave.Status != "Pending")
            return BadRequest(new { message = "Only pending requests can be approved." });

        if (leave.LeaveTypeId != Guid.Empty)
        {
            var balance = await _db.EmployeeLeaveBalances
                .FirstOrDefaultAsync(b => b.TenantId == tenantId && b.EmployeeId == leave.EmployeeId
                    && b.LeaveTypeId == leave.LeaveTypeId && b.Year == leave.StartDate.Year, cancellationToken);
            if (balance is not null)
            {
                balance.Pending = Math.Max(0, balance.Pending - leave.TotalDays);
                balance.Used += leave.TotalDays;
                balance.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        leave.Status = "Approved";
        leave.DecidedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(leave);
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Roles = "Admin,HR Manager,Manager")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] LegacyRejectLeaveRequest req, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        var leave = await _db.LeaveRequests.FirstOrDefaultAsync(l => l.Id == id && l.TenantId == tenantId, cancellationToken);
        if (leave is null) return NotFound();
        if (leave.Status != "Submitted" && leave.Status != "Pending")
            return BadRequest(new { message = "Only pending requests can be rejected." });

        if (leave.LeaveTypeId != Guid.Empty)
        {
            var balance = await _db.EmployeeLeaveBalances
                .FirstOrDefaultAsync(b => b.TenantId == tenantId && b.EmployeeId == leave.EmployeeId
                    && b.LeaveTypeId == leave.LeaveTypeId && b.Year == leave.StartDate.Year, cancellationToken);
            if (balance is not null)
            {
                balance.Pending = Math.Max(0, balance.Pending - leave.TotalDays);
                balance.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        leave.Status = "Rejected";
        leave.RejectionReason = req.Reason ?? string.Empty;
        leave.DecidedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(leave);
    }

    [HttpGet("balances")]
    public async Task<IActionResult> Balances([FromQuery] int? employeeId, [FromQuery] int? year, CancellationToken cancellationToken)
    {
        var tenantId = GetTenantId();
        year ??= DateTime.UtcNow.Year;
        // Apply data-scope BEFORE honouring the client's employeeId (mirrors List above and the newer
        // LeaveBalancesController). Without this, any employee could call GET /api/leave-requests/balances
        // with no params and receive EVERY employee's leave balances in the tenant (IDOR, CWE-639).
        var scope = await _scopeService.ResolveAsync(User, tenantId, cancellationToken);
        var (singleId, setFilter) = scope.Constrain(employeeId);
        var query = _db.EmployeeLeaveBalances.Where(b => b.TenantId == tenantId && b.Year == year);
        if (setFilter is not null) query = query.Where(b => setFilter.Contains(b.EmployeeId));
        else if (singleId.HasValue) query = query.Where(b => b.EmployeeId == singleId.Value);
        return Ok(await query.ToListAsync(cancellationToken));
    }

    private async Task<EmployeeLeaveBalance> GetOrCreateBalance(Guid tenantId, int employeeId, int year, Guid leaveTypeId, string leaveTypeName, CancellationToken ct)
    {
        var balance = await _db.EmployeeLeaveBalances
            .FirstOrDefaultAsync(b => b.TenantId == tenantId && b.EmployeeId == employeeId
                && b.LeaveTypeId == leaveTypeId && b.Year == year, ct);
        if (balance is null)
        {
            balance = new EmployeeLeaveBalance
            {
                TenantId = tenantId,
                EmployeeId = employeeId,
                LeaveTypeId = leaveTypeId,
                LeaveTypeName = leaveTypeName,
                Year = year,
                Entitled = 30
            };
            _db.EmployeeLeaveBalances.Add(balance);
        }
        return balance;
    }

    private Guid GetTenantId() => Guid.Parse(User.FindFirstValue("tenant_id")!);
    private Guid? GetUserId() => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var id) ? id : null;
}

public record LegacyCreateLeaveRequest(int EmployeeId, string LeaveType, DateOnly StartDate, DateOnly EndDate, string Reason);
public record LegacyRejectLeaveRequest(string? Reason);
