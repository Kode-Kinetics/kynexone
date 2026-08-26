using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Leave;

[ApiController]
[Route("api/leave/delegation")]
[Authorize]
public class LeaveDelegationController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;

    public LeaveDelegationController(ZayraDbContext db, IDataScopeService scopeService)
    {
        _db = db;
        _scopeService = scopeService;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? employeeId,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);

        var query = _db.LeaveDelegations.Where(d => d.TenantId == tenantId);
        if (!scope.IsUnrestricted)
            query = query.Where(d => scope.AllowedEmployeeIds!.Contains(d.EmployeeId));
        if (employeeId.HasValue) query = query.Where(d => d.EmployeeId == employeeId.Value || d.DelegateEmployeeId == employeeId.Value);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(d => d.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(d => d.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PagedResult<LeaveDelegation>(items, total, page, pageSize));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDelegationRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        var canAdminister = User.IsInRole("Admin") || User.IsInRole("HR Manager");
        if (!canAdminister && scope.CallerEmployeeId != req.EmployeeId) return Forbid();
        if (!scope.CanAccessEmployee(req.EmployeeId) || !scope.CanAccessEmployee(req.DelegateEmployeeId)) return Forbid();
        if (req.EndDate < req.StartDate) return BadRequest(new { message = "End date must be after start date." });

        var employee = await _db.Employees
            .FirstOrDefaultAsync(e => e.Id == req.EmployeeId && e.TenantId == tenantId, ct);
        if (employee is null)
            return BadRequest(new { message = "Employee not found." });

        var delegateEmployee = await _db.Employees
            .FirstOrDefaultAsync(e => e.Id == req.DelegateEmployeeId && e.TenantId == tenantId, ct);
        if (delegateEmployee is null)
            return BadRequest(new { message = "Delegate employee not found." });

        if (req.EmployeeId == req.DelegateEmployeeId)
            return BadRequest(new { message = "Employee cannot delegate to themselves." });

        var delegation = new LeaveDelegation
        {
            TenantId = tenantId.Value,
            EmployeeId = req.EmployeeId,
            EmployeeName = employee.FullName,
            DelegateEmployeeId = req.DelegateEmployeeId,
            DelegateEmployeeName = delegateEmployee.FullName,
            LeaveRequestId = req.LeaveRequestId,
            StartDate = req.StartDate,
            EndDate = req.EndDate,
            DelegationType = req.DelegationType ?? "ApprovalOnly",
            Notes = req.Notes ?? string.Empty,
            Status = "Active"
        };

        _db.LeaveDelegations.Add(delegation);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/leave/delegation/{delegation.Id}", delegation);
    }

    [HttpPost("{id:guid}/end")]
    public async Task<IActionResult> End(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var delegation = await _db.LeaveDelegations
            .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
        if (delegation is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!(User.IsInRole("Admin") || User.IsInRole("HR Manager"))
            && scope.CallerEmployeeId != delegation.EmployeeId) return Forbid();

        if (delegation.Status != "Active")
            return BadRequest(new { message = "Only active delegations can be ended." });

        delegation.Status = "Ended";
        await _db.SaveChangesAsync(ct);
        return Ok(delegation);
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var delegation = await _db.LeaveDelegations
            .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
        if (delegation is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!(User.IsInRole("Admin") || User.IsInRole("HR Manager"))
            && scope.CallerEmployeeId != delegation.EmployeeId) return Forbid();

        if (delegation.Status != "Active")
            return BadRequest(new { message = "Only active delegations can be cancelled." });

        delegation.Status = "Cancelled";
        await _db.SaveChangesAsync(ct);
        return Ok(delegation);
    }
}

public record CreateDelegationRequest(
    int EmployeeId,
    int DelegateEmployeeId,
    DateOnly StartDate,
    DateOnly EndDate,
    string? DelegationType,
    string? Notes,
    Guid? LeaveRequestId);
