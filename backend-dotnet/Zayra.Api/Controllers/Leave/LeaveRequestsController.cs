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

        Employee? delegateEmployee = null;
        if (req.DelegateEmployeeId.HasValue)
        {
            if (req.DelegateEmployeeId.Value == req.EmployeeId)
                return BadRequest(new { message = "An employee cannot delegate leave duties to themselves." });
            delegateEmployee = await _db.Employees.AsNoTracking().FirstOrDefaultAsync(e =>
                e.Id == req.DelegateEmployeeId.Value && e.TenantId == tenantId && !e.IsDeleted && e.Status == "Active", ct);
            if (delegateEmployee is null || (employee.CompanyId.HasValue && delegateEmployee.CompanyId != employee.CompanyId))
                return BadRequest(new { message = "Delegate employee was not found in the employee's company." });
        }

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
            DelegateEmployeeId = delegateEmployee?.Id,
            DelegateEmployeeName = delegateEmployee?.FullName ?? string.Empty,
            PayrollImpact = leaveType.IsPaid ? "Full" : "None"
        };

        try
        {
            var submitted = await _leaveService.SubmitRequestAsync(tenantId.Value, request, this.GetUserId(), ct);
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
        if (!await CanDecideResolvedLeaveStepAsync(tenantId.Value, id, approverId, ct))
            return Forbid();
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
        if (!await CanDecideResolvedLeaveStepAsync(tenantId.Value, id, approverId, ct))
            return Forbid();
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

        if (leaveRequest.Status == "Approved")
        {
            if (await _db.LeaveCancellationRequests.AnyAsync(x => x.TenantId == tenantId
                    && x.LeaveRequestId == id && x.Status == "Pending", ct))
                return Conflict(new { message = "A cancellation request is already pending." });
            var cancellation = new LeaveCancellationRequest
            {
                TenantId = tenantId.Value, LeaveRequestId = id, EmployeeId = leaveRequest.EmployeeId,
                Reason = req.Reason ?? string.Empty, Status = "Pending"
            };
            leaveRequest.Status = "CancellationRequested";
            _db.LeaveCancellationRequests.Add(cancellation);
            await _db.SaveChangesAsync(ct);
            return Accepted(new { request = leaveRequest, cancellation });
        }

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

    [HttpPost("{id:guid}/cancel-approve")]
    [Authorize(Roles = "Admin,HR Manager,Manager")]
    public async Task<IActionResult> ApproveCancellation(Guid id, [FromBody] CancellationDecisionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var cancellation = await _db.LeaveCancellationRequests
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.LeaveRequestId == id && x.Status == "Pending", ct);
        if (cancellation is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.CanAccessEmployee(cancellation.EmployeeId)) return Forbid();
        if (scope.CallerEmployeeId == cancellation.EmployeeId) return Forbid();

        var reviewer = User.Identity?.Name ?? this.GetUserId()?.ToString() ?? "Approver";
        try
        {
            var leave = await _leaveService.CancelRequestAsync(tenantId.Value, id, reviewer, cancellation.Reason, ct);
            cancellation.Status = "Approved";
            cancellation.ReviewedByName = reviewer;
            cancellation.ReviewNotes = req.Notes ?? string.Empty;
            cancellation.ReviewedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return Ok(leave);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("{id:guid}/cancel-reject")]
    [Authorize(Roles = "Admin,HR Manager,Manager")]
    public async Task<IActionResult> RejectCancellation(Guid id, [FromBody] CancellationDecisionRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var cancellation = await _db.LeaveCancellationRequests
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.LeaveRequestId == id && x.Status == "Pending", ct);
        if (cancellation is null) return NotFound();
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        if (!scope.CanAccessEmployee(cancellation.EmployeeId)) return Forbid();
        if (scope.CallerEmployeeId == cancellation.EmployeeId) return Forbid();
        var leave = await _db.LeaveRequests.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (leave is null) return NotFound();
        cancellation.Status = "Rejected";
        cancellation.ReviewedByName = User.Identity?.Name ?? "Approver";
        cancellation.ReviewNotes = req.Notes ?? string.Empty;
        cancellation.ReviewedAtUtc = DateTime.UtcNow;
        leave.Status = "Approved";
        await _db.SaveChangesAsync(ct);
        return Ok(leave);
    }

    [HttpPost("{id:guid}/withdraw")]
    public async Task<IActionResult> Withdraw(Guid id, [FromBody] WithdrawLeaveRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var leaveRequest = await _db.LeaveRequests
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (leaveRequest is null) return NotFound();

        var canAdminister = User.IsInRole("Admin") || User.IsInRole("HR Manager");
        if (!canAdminister)
        {
            var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
            if (scope.CallerEmployeeId != leaveRequest.EmployeeId) return Forbid();
        }

        var withdrawable = new[] { "Draft", "Submitted", "PendingManagerApproval", "PendingHRApproval" };
        if (!withdrawable.Contains(leaveRequest.Status))
            return BadRequest(new { message = "Only unapproved requests can be withdrawn." });

        var employeeName = User.Identity?.Name ?? this.GetUserId()?.ToString() ?? "Employee";

        leaveRequest.Status = "Withdrawn";
        leaveRequest.CancellationReason = req.Reason ?? string.Empty;
        leaveRequest.CancelledAtUtc = DateTime.UtcNow;

        // A request may span calendar years. Release the exact reservations recorded during
        // submission rather than subtracting the aggregate from the start-year balance.
        var reservations = await _db.LeaveBalanceTransactions.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.EmployeeId == leaveRequest.EmployeeId
                && t.LeaveTypeId == leaveRequest.LeaveTypeId && t.Reference == leaveRequest.Id.ToString()
                && t.TransactionType == "Pending")
            .ToListAsync(ct);
        foreach (var reservation in reservations)
        {
            var balance = await _db.EmployeeLeaveBalances.FirstOrDefaultAsync(b =>
                b.TenantId == tenantId && b.EmployeeId == leaveRequest.EmployeeId
                && b.LeaveTypeId == leaveRequest.LeaveTypeId && b.Year == reservation.Year, ct);
            if (balance is null) continue;
            var before = balance.Available;
            balance.Pending = Math.Max(0, balance.Pending - reservation.Amount);
            balance.UpdatedAtUtc = DateTime.UtcNow;
            _db.LeaveBalanceTransactions.Add(new LeaveBalanceTransaction
            {
                TenantId = tenantId.Value,
                CompanyId = leaveRequest.CompanyId,
                EmployeeId = leaveRequest.EmployeeId,
                LeaveTypeId = leaveRequest.LeaveTypeId,
                Year = reservation.Year,
                TransactionType = "Reversed",
                Amount = reservation.Amount,
                BalanceBefore = before,
                BalanceAfter = balance.Available,
                Reference = leaveRequest.Id.ToString(),
                Reason = "Leave request withdrawn",
                PerformedByName = employeeName
            });
        }

        var projection = await _db.ApprovalRequests
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.EntityName == nameof(LeaveRequest)
                && a.EntityId == leaveRequest.Id.ToString() && a.Status == "Pending", ct);
        if (projection is not null)
        {
            projection.Status = "Cancelled";
            projection.CompletedAtUtc = DateTime.UtcNow;
        }
        var pendingApprovals = await _db.LeaveApprovals
            .Where(a => a.TenantId == tenantId && a.LeaveRequestId == id && a.Decision == "Pending")
            .ToListAsync(ct);
        foreach (var approval in pendingApprovals) approval.Decision = "Cancelled";

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

        var canAdminister = User.IsInRole("Admin") || User.IsInRole("HR Manager");
        if (!canAdminister)
        {
            var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
            if (scope.CallerEmployeeId != leaveRequest.EmployeeId) return Forbid();
        }

        var delegateEmployee = await _db.Employees
            .FirstOrDefaultAsync(e => e.Id == req.DelegateEmployeeId && e.TenantId == tenantId, ct);
        if (delegateEmployee is null)
            return BadRequest(new { message = "Delegate employee not found." });
        if (delegateEmployee.Id == leaveRequest.EmployeeId)
            return BadRequest(new { message = "Employee cannot delegate to themselves." });

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
        { "EmployeeCode", "EmployeeName", "LeaveTypeCode", "LeaveType", "StartDate", "EndDate", "DayType", "HoursRequested", "IsEmergency", "AttachmentPath", "Days", "Status", "Reason" };

    /// <summary>Neutral example row, kept adjacent to the header it is positionally paired with. The
    /// header is shared with the export, so EmployeeName, Days and Status are export-only columns the
    /// importer derives for itself and are left blank here.</summary>
    private static readonly string[] LeaveRequestCsvExampleRow =
        { "EMP-0001", "", "EXAMPLE", "", "2026-01-01", "2026-01-05", "Full", "0", "false", "", "", "", "Example reason" };

    [HttpGet("export")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Auditor")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        var employeeQuery = _db.Employees.Where(e => e.TenantId == tenantId && !e.IsDeleted);
        if (!scope.IsUnrestricted)
            employeeQuery = employeeQuery.Where(e => scope.AllowedEmployeeIds!.Contains(e.Id));

        var requests = await _db.LeaveRequests
            .Where(r => r.TenantId == tenantId)
            .Join(employeeQuery,
                r => r.EmployeeId, e => e.Id, (r, e) => new { r, e })
            .GroupJoin(_db.LeaveTypes.Where(t => t.TenantId == tenantId),
                x => x.r.LeaveTypeId, t => t.Id, (x, types) => new { x.r, x.e, leaveType = types.FirstOrDefault() })
            .OrderByDescending(x => x.r.CreatedAtUtc)
            .ToListAsync(ct);

        var rows = requests.Select(x => (IReadOnlyList<object?>)new object?[]
        {
            x.e.EmployeeCode, x.r.EmployeeName, x.leaveType?.Code ?? string.Empty, x.r.LeaveTypeName,
            x.r.StartDate.ToString("yyyy-MM-dd"), x.r.EndDate.ToString("yyyy-MM-dd"),
            x.r.DayType, x.r.HoursRequested, x.r.IsEmergency ? "true" : "false", x.r.AttachmentPath,
            x.r.TotalDays, x.r.Status, x.r.Reason
        });
        var csv = Csv.Build(LeaveRequestCsvHeaders, rows);
        Response.Headers["Content-Disposition"] = "attachment; filename=leave_requests_export.csv";
        return Content(csv, "text/csv");
    }

    [HttpGet("import-template")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public IActionResult ImportTemplate()
    {
        Response.Headers["Content-Disposition"] = "attachment; filename=leave_requests_import_template.csv";
        return Content(Csv.Template(LeaveRequestCsvHeaders, LeaveRequestCsvExampleRow), "text/csv");
    }

    [HttpPost("import")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public async Task<IActionResult> Import([FromBody] ImportLeaveRequestsRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var rows = Csv.Parse(req.CsvContent ?? string.Empty);
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        var employeeQuery = _db.Employees.Where(e => e.TenantId == tenantId && !e.IsDeleted);
        if (!scope.IsUnrestricted)
            employeeQuery = employeeQuery.Where(e => scope.AllowedEmployeeIds!.Contains(e.Id));

        var employees = await employeeQuery
            .ToDictionaryAsync(e => e.EmployeeCode.ToUpperInvariant(), ct);
        var leaveTypesByCode = await _db.LeaveTypes
            .Where(t => t.TenantId == tenantId && t.IsActive)
            .ToDictionaryAsync(t => t.Code.ToUpperInvariant(), ct);
        var leaveTypesByName = await _db.LeaveTypes
            .Where(t => t.TenantId == tenantId && t.IsActive)
            .ToDictionaryAsync(t => t.NameEn.ToUpperInvariant(), ct);

        int created = 0, skipped = 0;
        var errors = new List<string>();
        var rowNum = 1;
        foreach (var row in rows)
        {
            rowNum++;
            var employeeCode = row.GetValueOrDefault("EmployeeCode", string.Empty).Trim();
            var leaveTypeCode = row.GetValueOrDefault("LeaveTypeCode", string.Empty).Trim();
            var leaveTypeName = row.GetValueOrDefault("LeaveType", string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(employeeCode) || !employees.TryGetValue(employeeCode.ToUpperInvariant(), out var employee))
            { skipped++; errors.Add($"Row {rowNum}: EmployeeCode '{employeeCode}' not found or out of scope."); continue; }

            LeaveType? leaveType = null;
            if (!string.IsNullOrWhiteSpace(leaveTypeCode))
                leaveTypesByCode.TryGetValue(leaveTypeCode.ToUpperInvariant(), out leaveType);
            if (leaveType is null && !string.IsNullOrWhiteSpace(leaveTypeName))
                leaveTypesByName.TryGetValue(leaveTypeName.ToUpperInvariant(), out leaveType);
            if (leaveType is null)
            { skipped++; errors.Add($"Row {rowNum}: Leave type '{leaveTypeCode}{leaveTypeName}' not found."); continue; }

            if (!DateOnly.TryParse(row.GetValueOrDefault("StartDate", string.Empty), out var startDate))
            { skipped++; errors.Add($"Row {rowNum}: StartDate is required and must be yyyy-MM-dd."); continue; }
            if (!DateOnly.TryParse(row.GetValueOrDefault("EndDate", string.Empty), out var endDate))
            { skipped++; errors.Add($"Row {rowNum}: EndDate is required and must be yyyy-MM-dd."); continue; }
            decimal.TryParse(row.GetValueOrDefault("HoursRequested", "0"), out var hours);

            var request = new LeaveRequest
            {
                TenantId = tenantId.Value,
                EmployeeId = employee.Id,
                EmployeeName = employee.FullName,
                DepartmentName = employee.Department ?? string.Empty,
                DesignationTitle = employee.Designation ?? string.Empty,
                LeaveTypeId = leaveType.Id,
                LeaveTypeName = leaveType.NameEn,
                StartDate = startDate,
                EndDate = endDate,
                DayType = row.GetValueOrDefault("DayType", "Full").Trim(),
                HoursRequested = hours,
                Reason = row.GetValueOrDefault("Reason", string.Empty).Trim(),
                IsEmergency = row.TryGetValue("IsEmergency", out var emergencyRaw) && string.Equals(emergencyRaw, "true", StringComparison.OrdinalIgnoreCase),
                AttachmentPath = row.GetValueOrDefault("AttachmentPath", string.Empty).Trim(),
                PayrollImpact = leaveType.IsPaid ? "Full" : "None"
            };

            try
            {
                await _leaveService.SubmitRequestAsync(tenantId.Value, request, this.GetUserId(), ct);
                created++;
            }
            catch (InvalidOperationException ex)
            {
                skipped++;
                errors.Add($"Row {rowNum}: {ex.Message}");
            }
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { received = rows.Count, created, skipped, errors = errors.Take(30) });
    }

    private async Task<bool> CanDecideResolvedLeaveStepAsync(Guid tenantId, Guid leaveRequestId, Guid approverId, CancellationToken ct)
    {
        if (User.IsInRole("Admin")) return true;
        var pendingStep = await _db.LeaveApprovals.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.LeaveRequestId == leaveRequestId && a.Decision == "Pending")
            .OrderBy(a => a.StepNumber)
            .FirstOrDefaultAsync(ct);

        if (pendingStep?.ApproverId is not null) return pendingStep.ApproverId == approverId;

        // Role-routed steps are still owned work. A null concrete user does not mean that every
        // manager in the tenant may decide it; enforce the routed role (Admin remains an explicit
        // override above).
        var requiredRole = pendingStep?.ApproverRole?.Trim();
        if (string.IsNullOrWhiteSpace(requiredRole)) return User.IsInRole("HR Manager");
        return requiredRole.ToUpperInvariant() switch
        {
            "HR" or "HRBUSINESSPARTNER" => User.IsInRole("HR Manager") || User.IsInRole("HR Officer"),
            "MANAGER" or "DIRECTMANAGER" or "SUPERVISOR" or "DEPARTMENTHEAD" => User.IsInRole("Manager"),
            _ => User.IsInRole(requiredRole)
        };
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
    string? AttachmentPath,
    int? DelegateEmployeeId = null,
    string? DelegateEmployeeName = null);

public record ApproveLeaveRequest(string? Notes);
public record RejectLeaveRequestBody(string Reason);
public record CancelLeaveRequest(string? Reason);
public record CancellationDecisionRequest(string? Notes);
public record WithdrawLeaveRequest(string? Reason);
public record DelegateLeaveRequest(int DelegateEmployeeId, string? DelegationType, string? Notes);
public record ImportLeaveRequestsRequest(string CsvContent);
