using Zayra.Api.Infrastructure.Common;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Infrastructure.Leave;
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
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
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
        var group = await LeaveGroupFilter.EmployeeIdsAsync(_db, tenantId.Value, companyId, branchId, ct);
        if (group is not null) query = query.Where(r => group.Contains(r.EmployeeId));

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PagedResult<LeaveRequest>(items, total, page, pageSize));
    }

    /// <summary>The most pending requests one queue read examines; <c>truncated</c> says when there were more.</summary>
    public const int QueueScanCap = 1000;

    private static readonly string[] QueueStatuses = Zayra.Api.Application.Leave.LeaveAwaitingDecisionStatuses.All;

    private sealed record QueueScan(List<LeaveRequest> Rows, int Total, bool Cut, DataScope Scope,
        Dictionary<Guid, LeaveCancellationRequest> Cancellations);

    /// <summary>
    /// Every pending request (and pending cancellation) in the caller's data scope, oldest first, up to
    /// <see cref="QueueScanCap"/>. <c>Total</c> is how many are pending in that scope, so a capped read says so.
    /// </summary>
    private async Task<QueueScan> ScanPendingAsync(Guid tenantId, Guid? companyId, Guid? branchId, CancellationToken ct)
    {
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var query = _db.LeaveRequests.AsNoTracking().Where(r => r.TenantId == tenantId && QueueStatuses.Contains(r.Status));
        if (!scope.IsUnrestricted)
        {
            var allowed = scope.AllowedEmployeeIds!.ToList();
            query = query.Where(r => allowed.Contains(r.EmployeeId));
        }
        // The Leave screen's company / branch picker narrows the queue exactly as it narrows the request list.
        var group = await LeaveGroupFilter.EmployeeIdsAsync(_db, tenantId, companyId, branchId, ct);
        if (group is not null) query = query.Where(r => group.Contains(r.EmployeeId));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(r => r.SubmittedAtUtc ?? r.CreatedAtUtc).Take(QueueScanCap).ToListAsync(ct);

        var cancelIds = rows.Where(r => r.Status == "CancellationRequested").Select(r => r.Id).ToList();
        var cancellations = cancelIds.Count > 0
            ? (await _db.LeaveCancellationRequests.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Status == "Pending" && cancelIds.Contains(x.LeaveRequestId))
                .ToListAsync(ct)).GroupBy(x => x.LeaveRequestId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.CreatedAtUtc).First())
            : new Dictionary<Guid, LeaveCancellationRequest>();
        return new QueueScan(rows, total, total > rows.Count, scope, cancellations);
    }

    /// <summary>The scanned rows the caller may decide now, by the decision endpoints' own tests.</summary>
    private async Task<List<LeaveAwaitingDecisionDto>> DecidableAsync(Guid tenantId, Guid userId, QueueScan scan, CancellationToken ct)
    {
        var items = new List<LeaveAwaitingDecisionDto>();
        // The decision endpoints are [Authorize(Roles = "Manager,HR Manager,Admin")]; a caller they would turn away
        // has nothing to decide.
        if (!LeaveNotifications.CancellationDeciderRoles.Any(User.IsInRole)) return items;
        foreach (var r in scan.Rows)
        {
            if (r.Status == "CancellationRequested")
            {
                // cancel-approve / cancel-reject: a pending cancellation row, scope (applied by the scan), never the employee.
                if (!scan.Cancellations.TryGetValue(r.Id, out var cancellation) || scan.Scope.CallerEmployeeId == r.EmployeeId) continue;
                items.Add(LeaveAwaitingDecisionDto.From(r, "Cancellation", cancellation.Reason, cancellation.CreatedAtUtc));
                continue;
            }
            if (!await CanDecideResolvedLeaveStepAsync(tenantId, r.Id, userId, ct)) continue;
            if (await LeaveService.MakerCheckerRefusalAsync(_db, r, userId, ct) is not null) continue;
            items.Add(LeaveAwaitingDecisionDto.From(r, "Decision", null, null));
        }
        return items;
    }

    /// <summary>
    /// The approvals queue: every pending leave request the caller may decide RIGHT NOW (whichever step it is at:
    /// manager, HR, or a named person), and every pending cancellation of approved leave the caller may decide.
    /// "May decide" is the same test the decision endpoints apply: the roles they admit, the caller's data scope,
    /// the routed step (<see cref="CanDecideResolvedLeaveStepAsync"/>) and separation of duties; nothing here
    /// widens or narrows who is allowed to decide. The old queue listed one status, so HR-step requests never
    /// appeared on the Leave screen. Reads at most <see cref="QueueScanCap"/> pending requests and says so
    /// (<c>truncated</c>, with the pending <c>total</c>) rather than silently dropping the rest.
    /// </summary>
    [HttpGet("awaiting-decision")]
    public async Task<IActionResult> AwaitingDecision(
        [FromQuery] Guid? companyId = null, [FromQuery] Guid? branchId = null, CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var userId = this.GetUserId();
        if (userId is null) return Unauthorized();
        var scan = await ScanPendingAsync(tenantId.Value, companyId, branchId, ct);
        var items = await DecidableAsync(tenantId.Value, userId.Value, scan, ct);
        return Ok(new LeaveQueueResponse<LeaveAwaitingDecisionDto>(items, scan.Total, scan.Cut));
    }

    /// <summary>
    /// Read-only: pending requests and cancellations in the caller's data scope that the caller may NOT decide, with
    /// the step each is at and who holds it. For people who see leave but do not decide it (HR Officer, Supervisor),
    /// and for deciders who want to see what is waiting on someone else. Needs <c>leave.read</c>; scope as everywhere.
    /// Names are the ones the leave detail already shows to anyone who can see that employee's leave.
    /// </summary>
    [HttpGet("waiting-on-others")]
    public async Task<IActionResult> WaitingOnOthers(
        [FromQuery] Guid? companyId = null, [FromQuery] Guid? branchId = null, CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var userId = this.GetUserId();
        if (userId is null) return Unauthorized();
        if (!User.HasPermission("leave.read")) return Ok(new LeaveQueueResponse<LeaveWaitingDto>([], 0, false));

        var scan = await ScanPendingAsync(tenantId.Value, companyId, branchId, ct);
        var mine = (await DecidableAsync(tenantId.Value, userId.Value, scan, ct)).Select(x => x.Id).ToHashSet();
        var others = scan.Rows.Where(r => !mine.Contains(r.Id) && scan.Scope.CallerEmployeeId != r.EmployeeId).ToList();

        var ids = others.Select(r => r.Id).ToList();
        var steps = (await _db.LeaveApprovals.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Decision == "Pending" && ids.Contains(a.LeaveRequestId))
            .ToListAsync(ct)).GroupBy(a => a.LeaveRequestId).ToDictionary(g => g.Key, g => g.OrderBy(a => a.StepNumber).First());

        var items = new List<LeaveWaitingDto>();
        foreach (var r in others)
        {
            if (r.Status == "CancellationRequested")
            {
                if (!scan.Cancellations.ContainsKey(r.Id)) continue;
                var names = await LeaveNotifications.NamesAsync(_db, tenantId.Value,
                    await LeaveNotifications.CancellationDecidersAsync(_db, tenantId.Value, r.EmployeeId, ct), ct);
                items.Add(LeaveWaitingDto.From(r, "Cancellation", null, "Manager / HR Manager / Admin", names));
                continue;
            }
            steps.TryGetValue(r.Id, out var step);
            var holderRole = string.IsNullOrWhiteSpace(step?.ApproverRole) ? "HR Manager" : step!.ApproverRole;
            var holderNames = !string.IsNullOrWhiteSpace(step?.ApproverName) ? new[] { step!.ApproverName } : Array.Empty<string>();
            items.Add(LeaveWaitingDto.From(r, "Decision", step?.StepNumber, holderRole, holderNames));
        }
        return Ok(new LeaveQueueResponse<LeaveWaitingDto>(items, scan.Total, scan.Cut));
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

        // The approver decides a Saudi statutory leave (maternity, Hajj, bereavement…) with the
        // employee's earlier leave of the same kind in view.
        var statutory = (await _leaveService.GetKsaStatutoryLeaveHistoryAsync(tenantId.Value, new[] { id }, ct)).GetValueOrDefault(id);
        var statutoryHistory = statutory?.History ?? Array.Empty<StatutoryLeaveHistoryItem>();

        // A cancellation of approved leave: where it stands and, while it is pending, WHO now holds it, so the
        // employee is never left looking at "Cancellation requested" with no idea whom it is waiting on.
        var cancellationRow = await _db.LeaveCancellationRequests.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.LeaveRequestId == id && x.Status != "Closed")
            .OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        object? cancellation = null;
        if (cancellationRow is not null)
        {
            var awaiting = cancellationRow.Status == "Pending"
                ? await LeaveNotifications.NamesAsync(_db, tenantId.Value,
                    await LeaveNotifications.CancellationDecidersAsync(_db, tenantId.Value, request.EmployeeId, ct), ct)
                : [];
            cancellation = new
            {
                id = cancellationRow.Id, status = cancellationRow.Status, reason = cancellationRow.Reason,
                requestedAtUtc = cancellationRow.CreatedAtUtc, reviewedByName = cancellationRow.ReviewedByName,
                reviewNotes = cancellationRow.ReviewNotes, reviewedAtUtc = cancellationRow.ReviewedAtUtc, awaiting,
            };
        }

        return Ok(new { request, approvals, statutoryHistory, statutory, cancellation });
    }

    /// <summary>
    /// The approver's view of KSA statutory leave history for several requests at once (the approvals
    /// queue): for each request that is statutory leave, the employee's other leave of that kind and
    /// whether it is the same statutory event. Requests the caller cannot see are omitted.
    /// </summary>
    [HttpGet("statutory-history")]
    public async Task<IActionResult> StatutoryHistory([FromQuery] string? ids, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var requested = (ids ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).Distinct().Take(200).ToList();
        if (requested.Count == 0) return Ok(new Dictionary<Guid, StatutoryLeaveContext>());

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        var visible = (await _db.LeaveRequests.AsNoTracking()
                .Where(r => r.TenantId == tenantId && requested.Contains(r.Id))
                .Select(r => new { r.Id, r.EmployeeId })
                .ToListAsync(ct))
            .Where(r => scope.CanAccessEmployee(r.EmployeeId))
            .Select(r => r.Id)
            .ToList();
        return Ok(await _leaveService.GetKsaStatutoryLeaveHistoryAsync(tenantId.Value, visible, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Submit([FromBody] SubmitLeaveRequestRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        // Employees submit for themselves (the every-employee baseline). Filing on someone else's behalf
        // is leave administration: it needs leave.write AND the employee in the caller's data scope; a
        // permission claim must never widen a team/company boundary. approvals.decide used to count as
        // that authority, which let Finance, Finance Approver and Payroll Manager file leave for anyone;
        // deciding approvals is not filing them.
        // "Self" is the caller's linked employee (the employee_id claim). The data scope cannot answer it: an
        // org-wide scope carries no CallerEmployeeId, which refused Payroll and Finance their own leave.
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        var self = await CallerEmployeeResolver.ResolveAsync(_db, User, tenantId.Value, ct);
        var isSelf = self is int callerEmployeeId && callerEmployeeId == req.EmployeeId;
        if (!isSelf && !(User.HasPermission("leave.write") && scope.CanAccessEmployee(req.EmployeeId)))
            return Forbid();

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

        // W2-D (S1): AttachmentPath holds a STORAGE KEY, never file bytes, and only the key of a
        // document the leave's employee owns. The app sends AttachmentDocumentId (the id returned by
        // POST /api/ess/documents) and the key is resolved here, server-side, so it never travels to
        // the client. A raw AttachmentPath is accepted only if it IS such a document's key.
        var attachmentPath = string.Empty;
        if (req.AttachmentDocumentId is { } attachmentDocumentId)
        {
            var key = await _db.EmployeeDocuments.AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.EmployeeId == req.EmployeeId && d.Id == attachmentDocumentId && !d.IsDeleted)
                .Select(d => d.StorageUrl).FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(key))
                return BadRequest(new { message = "The attachment was not found among the employee's documents." });
            attachmentPath = key;
        }
        else if (!string.IsNullOrWhiteSpace(req.AttachmentPath))
        {
            var candidate = req.AttachmentPath.Trim();
            var owned = candidate.Length <= 1024 && await _db.EmployeeDocuments.AsNoTracking()
                .AnyAsync(d => d.TenantId == tenantId && d.EmployeeId == req.EmployeeId && d.StorageUrl == candidate && !d.IsDeleted, ct);
            if (!owned)
                return BadRequest(new { message = "attachmentPath must reference an uploaded document. Upload the file first (POST /api/ess/documents) and send its id as attachmentDocumentId." });
            attachmentPath = candidate;
        }

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
            AttachmentPath = attachmentPath,
            DelegateEmployeeId = delegateEmployee?.Id,
            DelegateEmployeeName = delegateEmployee?.FullName ?? string.Empty,
            PayrollImpact = leaveType.IsPaid ? "Full" : "None",
            StatutoryEventDate = req.StatutoryEventDate,
            SeparateEventReason = req.SeparateEventReason,
        };

        try
        {
            var submitted = await _leaveService.SubmitRequestAsync(tenantId.Value, request, this.GetUserId(), ct);
            // The employee and whoever decides the first step; never the whole workspace. The same path Self-Service uses.
            await LeaveNotifications.NotifySubmittedAsync(_db, _notifications, tenantId.Value, submitted, ct);
            return Created($"/api/leave/requests/{submitted.Id}", submitted);
        }
        // F1 — approval CONFIGURATION errors (no applicable workflow / broken workflow) are 422 with a
        // stable code, distinct from ordinary validation failures.
        catch (Zayra.Api.Application.Approvals.ApprovalRoutingException ex) { return UnprocessableEntity(new { code = ex.Code, message = ex.Message }); }
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
            // "Leave Approved" is true only when the LAST step has decided. After an intermediate step of a
            // multi-step route the request is still pending the next approver, and the employee is told nothing
            // (these notices carry one string, no bilingual template, so there is no honest "now with Y" to send).
            if (result.Status == "Approved")
                await _notifications.NotifyEachAsync(tenantId.Value,
                    [.. await EmployeeLoginsAsync(tenantId.Value, result.EmployeeId, ct)],
                    "Leave Approved",
                    $"{result.EmployeeName}'s {result.LeaveTypeName} request has been approved.",
                    "LeaveRequest", result.Id.ToString(), ct);
            // A multi-step chain moved on: tell whoever decides the next step.
            await _notifications.NotifyEachAsync(tenantId.Value,
                [.. await PendingDecidersAsync(tenantId.Value, result.Id, result.EmployeeId, ct)],
                "Leave Request Awaiting Approval",
                $"{result.EmployeeName}'s {result.LeaveTypeName} request for {result.TotalDays} day(s) is waiting for your decision.",
                "LeaveRequest", result.Id.ToString(), ct);
            return Ok(result);
        }
        // F1 — approval CONFIGURATION errors (no applicable workflow / broken workflow) are 422 with a
        // stable code, distinct from ordinary validation failures.
        catch (Zayra.Api.Application.Approvals.ApprovalRoutingException ex) { return UnprocessableEntity(new { code = ex.Code, message = ex.Message }); }
        catch (Zayra.Api.Infrastructure.Approvals.ApprovalSeparationException ex)
        {
            // Same 400, plus who could act instead — a sole approver barred here must learn nobody else can.
            return BadRequest(new { message = ex.Message + await UnblockHintAsync(tenantId.Value, leaveRequest, approverId, ct) });
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
            await _notifications.NotifyEachAsync(tenantId.Value,
                [.. await EmployeeLoginsAsync(tenantId.Value, result.EmployeeId, ct)],
                "Leave Rejected",
                $"{result.EmployeeName}'s {result.LeaveTypeName} request has been rejected.",
                "LeaveRequest", result.Id.ToString(), ct);
            return Ok(result);
        }
        // F1 — approval CONFIGURATION errors (no applicable workflow / broken workflow) are 422 with a
        // stable code, distinct from ordinary validation failures.
        catch (Zayra.Api.Application.Approvals.ApprovalRoutingException ex) { return UnprocessableEntity(new { code = ex.Code, message = ex.Message }); }
        catch (Zayra.Api.Infrastructure.Approvals.ApprovalSeparationException ex)
        {
            // Same 400, plus who could act instead — a sole approver barred here must learn nobody else can.
            return BadRequest(new { message = ex.Message + await UnblockHintAsync(tenantId.Value, leaveRequest, approverId, ct) });
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
        var cancelScope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        var ownRequest = cancelScope.CallerEmployeeId == leaveRequest.EmployeeId;
        if (!isAdminOrHr && !ownRequest)
            return Forbid();
        // Cancelling your own request is self-service (ess.write); anyone else's is leave administration
        // (leave.write or leave.cancel). The role and ownership checks alone let an ess.read-only login
        // cancel its own leave.
        var canAdministerLeave = User.HasPermission("leave.write") || User.HasPermission("leave.cancel");
        if (!(canAdministerLeave || (ownRequest && User.HasPermission("ess.write"))))
            return Forbid();

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
            // Nobody decides what nobody knows about: tell everyone who may decide it.
            await LeaveNotifications.NotifyCancellationRequestedAsync(_db, _notifications, tenantId.Value, leaveRequest, req.Reason, ct);
            return Accepted(new { request = leaveRequest, cancellation });
        }

        // Whoever was deciding it is told it is gone; read before the cancellation closes the pending step.
        var deciders = await PendingDecidersAsync(tenantId.Value, id, leaveRequest.EmployeeId, ct);
        try
        {
            var result = await _leaveService.CancelRequestAsync(tenantId.Value, id, cancelledByName, req.Reason ?? string.Empty, ct);
            await _notifications.NotifyEachAsync(tenantId.Value,
                [.. await EmployeeLoginsAsync(tenantId.Value, result.EmployeeId, ct), .. deciders],
                "Leave Cancelled",
                $"{result.EmployeeName}'s {result.LeaveTypeName} request has been cancelled.",
                "LeaveRequest", result.Id.ToString(), ct);
            return Ok(result);
        }
        // F1 — approval CONFIGURATION errors (no applicable workflow / broken workflow) are 422 with a
        // stable code, distinct from ordinary validation failures.
        catch (Zayra.Api.Application.Approvals.ApprovalRoutingException ex) { return UnprocessableEntity(new { code = ex.Code, message = ex.Message }); }
        catch (LeaveDecisionConflictException ex) { return Conflict(new { error = "invalid_leave_state", message = ex.Message }); }
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
            // The leave's cancellation and this decision commit in ONE transaction, re-checked under the
            // employee's leave lock (see LeaveService.ApproveCancellationAsync). The decision used to be a
            // second save after the cancellation committed: a failure in between stranded it Pending forever.
            var leave = await _leaveService.ApproveCancellationAsync(tenantId.Value, id, reviewer, req.Notes ?? string.Empty, ct);
            await LeaveNotifications.NotifyCancellationDecidedAsync(_db, _notifications, tenantId.Value, leave, approved: true, req.Notes, ct);
            return Ok(leave);
        }
        catch (LeaveDecisionConflictException ex) { return Conflict(new { error = "invalid_leave_state", message = ex.Message }); }
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

        // Refusing a cancellation REVERTS the leave to the state it was in when the cancellation was
        // asked for, and the only state that can be is "CancellationRequested" — POST /{id}/cancel
        // moves an Approved leave into it and creates the Pending row this endpoint decides.
        //
        // This assignment used to be unconditional. A leave that had since been Withdrawn or
        // Cancelled — releasing its balance back to the employee — was resurrected as Approved by a
        // stale pending cancellation row, while the released balance stayed released. The employee
        // then held an approved absence AND the days back in their entitlement: the same days spent
        // twice. LeaveService.CancelRequestAsync already refuses to act on a Cancelled or Withdrawn
        // request (see its status check); this path was the way around that guard.
        //
        // Refuses the way the codebase's other decision endpoints refuse: a coded 409, as
        // ApprovalDecisionGuard's ParentStateForbidsDecision does for loans.
        if (leave.Status != "CancellationRequested")
            return Conflict(new
            {
                error = "invalid_leave_state",
                message = $"A cancellation can only be rejected while the leave is awaiting that "
                        + $"decision (current: {leave.Status}). This cancellation request is stale — "
                        + $"the leave has already moved on."
            });

        // The checks above are the fast path; the service repeats them under the employee's leave lock, so a
        // concurrent approval of the same cancellation and this refusal cannot both apply.
        try
        {
            leave = await _leaveService.RejectCancellationAsync(tenantId.Value, id, User.Identity?.Name ?? "Approver",
                req.Notes ?? string.Empty, ct);
        }
        catch (LeaveDecisionConflictException ex) { return Conflict(new { error = "invalid_leave_state", message = ex.Message }); }
        await LeaveNotifications.NotifyCancellationDecidedAsync(_db, _notifications, tenantId.Value, leave, approved: false, req.Notes, ct);
        return Ok(LeaveRequestStateDto.Project(leave));
    }

    [HttpPost("{id:guid}/withdraw")]
    // The web client posts no content at all (axios sends nothing when no data is given); the mobile app posts {}.
    // The default [FromBody] binding answers an empty body with 400/415, so an empty one is allowed and means "no reason".
    public async Task<IActionResult> Withdraw(Guid id, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] WithdrawLeaveRequest? req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var leaveRequest = await _db.LeaveRequests.AsNoTracking()
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
        // The fast path; LeaveService repeats this check under the employee's leave lock, so a final approval that
        // commits between this read and the write cannot be overwritten.
            return BadRequest(new { message = "Only unapproved requests can be withdrawn." });

        var employeeName = User.Identity?.Name ?? this.GetUserId()?.ToString() ?? "Employee";

        // Whoever was deciding it is told it is gone; read before the withdrawal closes the pending step.
        var deciders = await PendingDecidersAsync(tenantId.Value, id, leaveRequest.EmployeeId, ct);
        try
        {
            var result = await _leaveService.WithdrawRequestAsync(tenantId.Value, id, employeeName, req?.Reason ?? string.Empty, ct);
            await _notifications.NotifyEachAsync(tenantId.Value,
                [.. await EmployeeLoginsAsync(tenantId.Value, result.EmployeeId, ct), .. deciders],
                "Leave Withdrawn",
                $"{result.EmployeeName}'s {result.LeaveTypeName} request has been withdrawn.",
                "LeaveRequest", result.Id.ToString(), ct);
            return Ok(result);
        }
        catch (LeaveDecisionConflictException ex) { return Conflict(new { error = "invalid_leave_state", message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
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
        return CsvDownload.Result(csv);
    }

    [HttpGet("import-template")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer")]
    public IActionResult ImportTemplate()
    {
        Response.Headers["Content-Disposition"] = "attachment; filename=leave_requests_import_template.csv";
        return CsvDownload.Result(Csv.Template(LeaveRequestCsvHeaders, LeaveRequestCsvExampleRow));
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
        // Leave types are per company (2026-10-23): codes and names repeat across a group, so each row resolves within
        // its employee's company (an unassigned legacy type is the fallback).
        var activeTypes = await _db.LeaveTypes
            .Where(t => t.TenantId == tenantId && t.IsActive)
            .ToListAsync(ct);
        var typesByCompany = new Dictionary<Guid, (Dictionary<string, LeaveType> ByCode, Dictionary<string, LeaveType> ByName)>();
        (Dictionary<string, LeaveType> ByCode, Dictionary<string, LeaveType> ByName) TypesFor(Guid? company)
        {
            var key = company ?? Guid.Empty;
            if (typesByCompany.TryGetValue(key, out var found)) return found;
            var byCode = Infrastructure.Leave.LeaveCompanyScope.ByCodeFor(activeTypes, company);
            var byName = new Dictionary<string, LeaveType>(StringComparer.Ordinal);
            foreach (var t in byCode.Values.OrderBy(t => t.CompanyId == null).ThenBy(t => t.Id))
                byName.TryAdd(t.NameEn.ToUpperInvariant(), t);
            return typesByCompany[key] = (byCode, byName);
        }

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
            var (leaveTypesByCode, leaveTypesByName) = TypesFor(employee.CompanyId);
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

    /// <summary>
    /// The "nobody else can decide it yet" sentence for a leave decision refused for separation of duties,
    /// or empty when someone else can. Excludes the caller, the requester, the leave's own employee and
    /// whoever approved an earlier step; counts Admin (who may decide any step) and the roles
    /// <see cref="CanDecideResolvedLeaveStepAsync"/> accepts for the pending step. A hint only.
    /// </summary>
    private async Task<string> UnblockHintAsync(Guid tenantId, LeaveRequest leave, Guid callerId, CancellationToken ct)
    {
        var steps = await _db.LeaveApprovals.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.LeaveRequestId == leave.Id)
            .ToListAsync(ct);
        var pending = steps.Where(a => a.Decision == "Pending").OrderBy(a => a.StepNumber).FirstOrDefault();
        var excluded = new HashSet<Guid> { callerId };
        foreach (var approved in steps.Where(a => a.Decision == "Approved" && a.ApproverId is not null)) excluded.Add(approved.ApproverId!.Value);
        if (await _db.ApprovalRequests.AsNoTracking().Where(a => a.TenantId == tenantId && a.Id == leave.Id)
                .Select(a => a.RequestedByUserId).FirstOrDefaultAsync(ct) is Guid requester)
            excluded.Add(requester);
        foreach (var linked in await Zayra.Api.Infrastructure.Approvals.ApprovalUnblock.SubjectUserIdsAsync(_db, tenantId, leave.EmployeeId, ct))
            excluded.Add(linked);

        if (pending?.ApproverId is Guid named)
        {
            if (!excluded.Contains(named)) return string.Empty;
            return await Zayra.Api.Infrastructure.Approvals.ApprovalUnblock.AnyOtherUserInRolesAsync(_db, tenantId, new[] { "Admin" }, orOverride: false, excluded, ct)
                ? string.Empty
                : Zayra.Api.Infrastructure.Approvals.ApprovalUnblock.NobodyElseSentence("decide",
                    string.IsNullOrWhiteSpace(pending.ApproverName) ? "the named approver" : pending.ApproverName, null);
        }
        var routedRole = pending?.ApproverRole?.Trim() ?? string.Empty;
        var roles = routedRole.ToUpperInvariant() switch
        {
            "" => new[] { "HR Manager" },
            "HR" or "HRBUSINESSPARTNER" => new[] { "HR Manager", "HR Officer" },
            "MANAGER" or "DIRECTMANAGER" or "SUPERVISOR" or "DEPARTMENTHEAD" => new[] { "Manager" },
            _ => new[] { routedRole },
        };
        return await Zayra.Api.Infrastructure.Approvals.ApprovalUnblock.AnyOtherUserInRolesAsync(_db, tenantId, roles.Append("Admin").ToArray(), orOverride: false, excluded, ct)
            ? string.Empty
            : Zayra.Api.Infrastructure.Approvals.ApprovalUnblock.NobodyElseSentence("decide", null, roles[0]);
    }

    private Task<IReadOnlyList<Guid>> EmployeeLoginsAsync(Guid tenantId, int employeeId, CancellationToken ct)
        => NotificationAudience.EmployeeLoginIdsAsync(_db, tenantId, employeeId, ct);

    /// <summary>Who decides the request's pending step (see <see cref="LeaveNotifications.PendingDecidersAsync"/>).</summary>
    private Task<IReadOnlyList<Guid>> PendingDecidersAsync(Guid tenantId, Guid leaveRequestId, int employeeId, CancellationToken ct)
        => LeaveNotifications.PendingDecidersAsync(_db, tenantId, leaveRequestId, employeeId, ct);

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
    string? DelegateEmployeeName = null,
    // W2-D (S1): id of an EmployeeDocument owned by the leave's employee; resolved to AttachmentPath.
    Guid? AttachmentDocumentId = null,
    // KSA statutory leave: the date of the event (death, birth, marriage), and — to declare this a
    // separate event from earlier leave of the same kind — the reason. Ignored for other leave.
    DateOnly? StatutoryEventDate = null,
    string? SeparateEventReason = null);

/// <summary>A queue read: its rows, how many requests are pending in the caller's scope, and whether the read stopped short.</summary>
public record LeaveQueueResponse<T>(IReadOnlyList<T> Items, int Total, bool Truncated);

/// <summary>A pending request the viewer may see but not decide: where it is and who holds it.</summary>
public record LeaveWaitingDto(
    Guid Id,
    int EmployeeId,
    string EmployeeName,
    string LeaveTypeName,
    DateOnly StartDate,
    DateOnly EndDate,
    decimal TotalDays,
    string Status,
    /// <summary>"Decision" (a request at some step) or "Cancellation" (approved leave someone asked to cancel).</summary>
    string Kind,
    int? StepNumber,
    string HolderRole,
    IReadOnlyList<string> HolderNames)
{
    public static LeaveWaitingDto From(LeaveRequest r, string kind, int? stepNumber, string holderRole, IReadOnlyList<string> holderNames) => new(
        r.Id, r.EmployeeId, r.EmployeeName, r.LeaveTypeName, r.StartDate, r.EndDate, r.TotalDays, r.Status, kind,
        stepNumber, holderRole, holderNames);
}

/// <summary>One row of the leave approvals queue: a request to decide, or a cancellation of approved leave to decide.</summary>
public record LeaveAwaitingDecisionDto(
    Guid Id,
    int EmployeeId,
    string EmployeeName,
    string DepartmentName,
    string DesignationTitle,
    Guid LeaveTypeId,
    string LeaveTypeName,
    DateOnly StartDate,
    DateOnly EndDate,
    decimal TotalDays,
    string Status,
    string Reason,
    bool IsEmergency,
    DateTime? SubmittedAtUtc,
    /// <summary>"Decision" (approve or reject the request) or "Cancellation" (approve the cancellation or keep the leave).</summary>
    string Kind,
    string? CancellationReason,
    DateTime? CancellationRequestedAtUtc)
{
    public static LeaveAwaitingDecisionDto From(LeaveRequest r, string kind, string? cancellationReason, DateTime? cancellationRequestedAtUtc) => new(
        r.Id, r.EmployeeId, r.EmployeeName, r.DepartmentName, r.DesignationTitle, r.LeaveTypeId, r.LeaveTypeName,
        r.StartDate, r.EndDate, r.TotalDays, r.Status, r.Reason, r.IsEmergency, r.SubmittedAtUtc, kind,
        cancellationReason, cancellationRequestedAtUtc);
}

public record ApproveLeaveRequest(string? Notes);
public record RejectLeaveRequestBody(string Reason);
public record CancelLeaveRequest(string? Reason);
public record CancellationDecisionRequest(string? Notes);
public record WithdrawLeaveRequest(string? Reason);
public record DelegateLeaveRequest(int DelegateEmployeeId, string? DelegationType, string? Notes);
public record ImportLeaveRequestsRequest(string CsvContent);

/// <summary>
/// The leave-request state returned by a cancellation decision. Replaces returning the raw
/// <c>LeaveRequest</c> entity, which serialized TenantId, CompanyId and every field later added to
/// the model. Nothing consumed the entity shape — the endpoint has no frontend or test caller — so
/// this projects the decision-relevant subset and nothing else.
/// </summary>
public record LeaveRequestStateDto(
    Guid Id,
    int EmployeeId,
    string EmployeeName,
    Guid LeaveTypeId,
    string LeaveTypeName,
    DateOnly StartDate,
    DateOnly EndDate,
    decimal TotalDays,
    string DayType,
    string Status,
    string CancellationReason,
    DateTime? DecidedAtUtc,
    DateTime? CancelledAtUtc)
{
    public static LeaveRequestStateDto Project(Zayra.Api.Models.LeaveRequest r) => new(
        r.Id, r.EmployeeId, r.EmployeeName, r.LeaveTypeId, r.LeaveTypeName,
        r.StartDate, r.EndDate, r.TotalDays, r.DayType, r.Status,
        r.CancellationReason, r.DecidedAtUtc, r.CancelledAtUtc);
}
