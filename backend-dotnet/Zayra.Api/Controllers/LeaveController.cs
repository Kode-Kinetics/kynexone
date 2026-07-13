using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
        await Task.CompletedTask;
        return StatusCode(StatusCodes.Status410Gone, new { message = "Legacy leave creation is disabled. Use POST /api/leave/requests so policy eligibility and resolved approvers are enforced." });
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Roles = "Admin,HR Manager,Manager")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken cancellationToken)
    {
        var existsInTenant = await _db.LeaveRequests.AnyAsync(l => l.Id == id && l.TenantId == GetTenantId(), cancellationToken);
        if (!existsInTenant) return NotFound();
        return StatusCode(StatusCodes.Status410Gone, new { message = "Legacy leave approval is disabled. Use POST /api/leave/requests/{id}/approve so the resolved pending approver is enforced." });
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Roles = "Admin,HR Manager,Manager")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] LegacyRejectLeaveRequest req, CancellationToken cancellationToken)
    {
        var existsInTenant = await _db.LeaveRequests.AnyAsync(l => l.Id == id && l.TenantId == GetTenantId(), cancellationToken);
        if (!existsInTenant) return NotFound();
        return StatusCode(StatusCodes.Status410Gone, new { message = "Legacy leave rejection is disabled. Use POST /api/leave/requests/{id}/reject so the resolved pending approver is enforced." });
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
