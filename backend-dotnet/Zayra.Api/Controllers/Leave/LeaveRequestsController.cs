using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers.Leave;

[ApiController]
[Route("api/leave/requests")]
[Authorize]
public class LeaveRequestsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly ILeaveService _leaveService;
    private readonly IDataScopeService _scopeService;
    private readonly INotificationService _notifications;

    public LeaveRequestsController(ZayraDbContext db, ILeaveService leaveService, IDataScopeService scopeService, INotificationService notifications)
    {
        _db = db;
        _leaveService = leaveService;
        _scopeService = scopeService;
        _notifications = notifications;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] int? employeeId,
        [FromQuery] Guid? leaveTypeId,
        [FromQuery] DateOnly? fromDate,
        [FromQuery] DateOnly? toDate,
        [FromQuery] string? departmentName,
        [FromQuery] Guid? companyId,
        [FromQuery] Guid? branchId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        var (singleId, setFilter) = scope.Constrain(employeeId);

        var query = _db.LeaveRequests.Where(r => r.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
        if (setFilter is not null) query = query.Where(r => setFilter.Contains(r.EmployeeId));
        else if (singleId.HasValue) query = query.Where(r => r.EmployeeId == singleId.Value);
        if (leaveTypeId.HasValue) query = query.Where(r => r.LeaveTypeId == leaveTypeId.Value);
        if (fromDate.HasValue) query = query.Where(r => r.EndDate >= fromDate.Value);
        if (toDate.HasValue) query = query.Where(r => r.StartDate <= toDate.Value);
        if (!string.IsNullOrWhiteSpace(departmentName)) query = query.Where(r => r.DepartmentName == departmentName);
        if (companyId.HasValue || branchId.HasValue)
        {
            var empQ = _db.Employees.Where(e => e.TenantId == tenantId && !e.IsDeleted);
            if (companyId.HasValue) empQ = empQ.Where(e => e.CompanyId == companyId);
            if (branchId.HasValue)  empQ = empQ.Where(e => e.BranchId  == branchId);
            var ids = await empQ.Select(e => e.Id).ToListAsync(ct);
            query = query.Where(r => ids.Contains(r.EmployeeId));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PagedResult<LeaveRequest>(items, total, page, pageSize));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var request = await _db.LeaveRequests
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (request is null) return NotFound();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.CanAccessEmployee(request.EmployeeId))
            return Forbid();

        var approvals = await _db.LeaveApprovals
            .Where(a => a.TenantId == tenantId && a.LeaveRequestId == id)
            .OrderBy(a => a.StepNumber)
            .ToListAsync(ct);

        return Ok(new { request, approvals });
    }

    [HttpPost]
    public async Task<IActionResult> Submit([FromBody] SubmitLeaveRequestRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        // GCC compliance: employees may only submit for themselves unless they hold employees.write or approvals.decide
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.IsUnrestricted && scope.CallerEmployeeId.HasValue && req.EmployeeId != scope.CallerEmployeeId.Value)
        {
            var hasWritePermission = User.Claims.Any(c => c.Type == "permission" &&
                (c.Value == "employees.write" || c.Value == "approvals.decide"));
            if (!hasWritePermission)
                return Forbid();
        }

        var leaveType = await _db.LeaveTypes
            .FirstOrDefaultAsync(t => t.Id == req.LeaveTypeId && t.TenantId == tenantId, ct);
        if (leaveType is null)
            return BadRequest(new { message = "Leave type not found." });

        var employee = await _db.Employees
            .FirstOrDefaultAsync(e => e.Id == req.EmployeeId && e.TenantId == tenantId, ct);
        if (employee is null)
            return BadRequest(new { message = "Employee not found." });

        if (req.EndDate < req.StartDate)
            return BadRequest(new { message = "End date must be after start date." });

        var request = new LeaveRequest
        {
            TenantId = tenantId.Value,
            EmployeeId = req.EmployeeId,
            EmployeeName = employee.FullName,
            DepartmentName = employee.Department ?? string.Empty,
            DesignationTitle = employee.Designation ?? string.Empty,
            LeaveTypeId = req.LeaveTypeId,
            LeaveTypeName = leaveType.NameEn,
            PolicyId = req.PolicyId,
            StartDate = req.StartDate,
            EndDate = req.EndDate,
            DayType = req.DayType ?? "Full",
            HoursRequested = req.HoursRequested ?? 0,
            Reason = req.Reason ?? string.Empty,
            IsEmergency = req.IsEmergency,
            AttachmentPath = req.AttachmentPath ?? string.Empty,
            PayrollImpact = leaveType.IsPaid ? "Full" : "None"
        };

        try
        {
            var submitted = await _leaveService.SubmitRequestAsync(tenantId.Value, request, ct);
            await _notifications.NotifyAsync(tenantId.Value, null,
                "New Leave Request",
                $"{submitted.EmployeeName} submitted a {submitted.LeaveTypeName} request for {submitted.TotalDays} day(s).",
                "LeaveRequest", submitted.Id.ToString(), ct);
            return Created($"/api/leave/requests/{submitted.Id}", submitted);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Roles = "Manager,HR Manager,Admin")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] ApproveLeaveRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        // Load request now so we can scope-check before the service call.
        var leaveRequest = await _db.LeaveRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (leaveRequest is null) return NotFound();

        // Managers are scoped to their team/direct-reports; Admin and HR roles are unrestricted.
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.IsUnrestricted && !scope.AllowedEmployeeIds!.Contains(leaveRequest.EmployeeId))
            return Forbid();

        var approverId = this.GetUserId() ?? Guid.Empty;
        var approverName = User.Identity?.Name ?? approverId.ToString();

        try
        {
            var result = await _leaveService.ApproveRequestAsync(tenantId.Value, id, approverId, approverName, req.Notes, ct);
            await _notifications.NotifyAsync(tenantId.Value, null,
                "Leave Approved",
                $"{result.EmployeeName}'s {result.LeaveTypeName} request has been approved.",
                "LeaveRequest", result.Id.ToString(), ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Roles = "Manager,HR Manager,Admin")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectLeaveRequestBody req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new { message = "A rejection reason is required." });

        // Same scope guard as Approve — managers can only reject requests for their own team.
        var leaveRequest = await _db.LeaveRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (leaveRequest is null) return NotFound();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.IsUnrestricted && !scope.AllowedEmployeeIds!.Contains(leaveRequest.EmployeeId))
            return Forbid();

        var approverId = this.GetUserId() ?? Guid.Empty;
        var approverName = User.Identity?.Name ?? approverId.ToString();

        try
        {
            var result = await _leaveService.RejectRequestAsync(tenantId.Value, id, approverId, approverName, req.Reason, ct);
            await _notifications.NotifyAsync(tenantId.Value, null,
                "Leave Rejected",
                $"{result.EmployeeName}'s {result.LeaveTypeName} request has been rejected.",
                "LeaveRequest", result.Id.ToString(), ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] CancelLeaveRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        // Load request first so we can check ownership before actioning.
        var leaveRequest = await _db.LeaveRequests
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (leaveRequest is null) return NotFound();

        // Authorization: Admin and HR Manager can cancel any request in the tenant.
        // All others must be cancelling their own request.
        var isAdminOrHr = User.IsInRole("Admin") || User.IsInRole("HR Manager");
        if (!isAdminOrHr)
        {
            var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
            if (scope.CallerEmployeeId != leaveRequest.EmployeeId)
                return Forbid();
        }

        var cancelledByName = User.Identity?.Name ?? this.GetUserId()?.ToString() ?? "Employee";

        try
        {
            var result = await _leaveService.CancelRequestAsync(tenantId.Value, id, cancelledByName, req.Reason ?? string.Empty, ct);
            await _notifications.NotifyAsync(tenantId.Value, null,
                "Leave Cancelled",
                $"{result.EmployeeName}'s {result.LeaveTypeName} request has been cancelled.",
                "LeaveRequest", result.Id.ToString(), ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/withdraw")]
    public async Task<IActionResult> Withdraw(Guid id, [FromBody] WithdrawLeaveRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var leaveRequest = await _db.LeaveRequests
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (leaveRequest is null) return NotFound();

        if (leaveRequest.Status != "Draft" && leaveRequest.Status != "Submitted")
            return BadRequest(new { message = "Only draft or submitted requests can be withdrawn." });

        var employeeName = User.Identity?.Name ?? this.GetUserId()?.ToString() ?? "Employee";

        var previousStatus = leaveRequest.Status;
        leaveRequest.Status = "Withdrawn";
        leaveRequest.CancellationReason = req.Reason ?? string.Empty;
        leaveRequest.CancelledAtUtc = DateTime.UtcNow;

        var balance = await _db.EmployeeLeaveBalances
            .FirstOrDefaultAsync(b => b.TenantId == tenantId && b.EmployeeId == leaveRequest.EmployeeId
                && b.LeaveTypeId == leaveRequest.LeaveTypeId && b.Year == leaveRequest.StartDate.Year, ct);

        if (balance is not null && balance.Pending > 0)
        {
            balance.Pending = Math.Max(0, balance.Pending - leaveRequest.TotalDays);
            balance.UpdatedAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(leaveRequest);
    }

    [HttpPost("{id:guid}/delegate")]
    public async Task<IActionResult> Delegate(Guid id, [FromBody] DelegateLeaveRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var leaveRequest = await _db.LeaveRequests
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (leaveRequest is null) return NotFound();

        var delegateEmployee = await _db.Employees
            .FirstOrDefaultAsync(e => e.Id == req.DelegateEmployeeId && e.TenantId == tenantId, ct);
        if (delegateEmployee is null)
            return BadRequest(new { message = "Delegate employee not found." });

        leaveRequest.DelegateEmployeeId = req.DelegateEmployeeId;
        leaveRequest.DelegateEmployeeName = delegateEmployee.FullName;

        var delegation = new LeaveDelegation
        {
            TenantId = tenantId.Value,
            EmployeeId = leaveRequest.EmployeeId,
            EmployeeName = leaveRequest.EmployeeName,
            DelegateEmployeeId = req.DelegateEmployeeId,
            DelegateEmployeeName = delegateEmployee.FullName,
            LeaveRequestId = id,
            StartDate = leaveRequest.StartDate,
            EndDate = leaveRequest.EndDate,
            DelegationType = req.DelegationType ?? "ApprovalOnly",
            Notes = req.Notes ?? string.Empty,
            Status = "Active"
        };
        _db.LeaveDelegations.Add(delegation);

        await _db.SaveChangesAsync(ct);
        return Ok(new { leaveRequest, delegation });
    }

    // ── Export ───────────────────────────────────────────────────────────────
    private static readonly string[] LeaveRequestCsvHeaders =
        { "EmployeeCode", "EmployeeName", "LeaveType", "StartDate", "EndDate", "Days", "Status", "Reason" };

    [HttpGet("export")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var requests = await _db.LeaveRequests
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToListAsync(ct);

        var rows = requests.Select(r => (IReadOnlyList<object?>)new object?[]
        {
            r.EmployeeId.ToString(), r.EmployeeName, r.LeaveTypeName,
            r.StartDate.ToString("yyyy-MM-dd"), r.EndDate.ToString("yyyy-MM-dd"),
            r.TotalDays, r.Status, r.Reason
        });
        var csv = Csv.Build(LeaveRequestCsvHeaders, rows);
        Response.Headers["Content-Disposition"] = "attachment; filename=leave_requests_export.csv";
        return Content(csv, "text/csv");
    }
}

public record SubmitLeaveRequestRequest(
    int EmployeeId,
    Guid LeaveTypeId,
    Guid? PolicyId,
    DateOnly StartDate,
    DateOnly EndDate,
    string? DayType,
    decimal? HoursRequested,
    string? Reason,
    bool IsEmergency,
    string? AttachmentPath);

public record ApproveLeaveRequest(string? Notes);
public record RejectLeaveRequestBody(string Reason);
public record CancelLeaveRequest(string? Reason);
public record WithdrawLeaveRequest(string? Reason);
public record DelegateLeaveRequest(int DelegateEmployeeId, string? DelegationType, string? Notes);
