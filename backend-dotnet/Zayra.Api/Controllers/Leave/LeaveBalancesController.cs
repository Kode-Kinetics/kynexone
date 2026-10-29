using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Leave;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Authorization;
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
        // One lookup for the whole page: countries and leave types are resolved once, not per employee.
        var figures = await _leaveService.GetKsaStatutoryEntitlementsAsync(
            tenantId, balances.Select(b => (b.EmployeeId, b.LeaveTypeId)).Distinct().ToList(), ct);
        foreach (var b in balances)
            if (figures.TryGetValue((b.EmployeeId, b.LeaveTypeId), out var days)) b.StatutoryEntitlementDays = days;
    }

    private const decimal MaxAdjustmentDays = 60m;
    private const decimal MaxCumulativeGrantDays = 60m;

    /// <summary>A manual adjustment of MORE than this many days (either way) needs a second HR approver. Tenant setting
    /// Leave / <see cref="ApprovalThresholdSettingKey"/>; <see cref="DefaultApprovalThresholdDays"/> when unset or unreadable.</summary>
    public const decimal DefaultApprovalThresholdDays = 5m;
    public const string ApprovalThresholdSettingKey = "manual_adjustment_approval_threshold_days";

    private async Task<decimal> ApprovalThresholdDaysAsync(Guid tenantId, CancellationToken ct)
    {
        var raw = await _db.SystemSettings.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Category == "Leave" && x.SettingKey == ApprovalThresholdSettingKey)
            .Select(x => x.SettingValue).FirstOrDefaultAsync(ct);
        return decimal.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var days) && days >= 0m && days <= MaxAdjustmentDays
            ? days : DefaultApprovalThresholdDays;
    }

    /// <summary>The year and cumulative-grant rules of a manual adjustment, shared by the proposal and the approval (the world moves in between).</summary>
    private async Task<IActionResult?> CheckAdjustmentLimitsAsync(Guid tenantId, int employeeId, int year, decimal amount, CancellationToken ct)
    {
        // Only the current or the previous leave year can be adjusted by hand, and the days one person may grant an employee in a
        // year are capped in total: leave converts to cash through encashment, so this is a money path.
        if (year < DateTime.UtcNow.Year - 1 || year > DateTime.UtcNow.Year)
            return BadRequest(new { error = "year_out_of_range", message = "A leave balance can only be adjusted for the current or the previous year." });
        if (amount > 0m)
        {
            var granted = await _db.LeaveBalanceTransactions.AsNoTracking()
                .Where(t => t.TenantId == tenantId && t.EmployeeId == employeeId && t.Year == year && t.TransactionType == "Adjustment" && t.Amount > 0m)
                .SumAsync(t => (decimal?)t.Amount, ct) ?? 0m;
            if (granted + amount > MaxCumulativeGrantDays)
                return BadRequest(new { error = "cumulative_adjustment_exceeded", message = $"Manual grants for one employee in one year are limited to {MaxCumulativeGrantDays} days in total ({granted} already granted). Use the leave policy or an approved encashment/entitlement change instead." });
        }
        return null;
    }

    [HttpPost("adjust")]
    [Authorize(Roles = "Admin,HR Manager")]
    [HasPermission("leave.write")]
    public async Task<IActionResult> Adjust([FromBody] BalanceAdjustmentRequest req, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var userId = this.GetUserId();
        var performedByName = User.Identity?.Name ?? userId?.ToString() ?? "HR";

        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new { error = "reason_required", message = "A reason is required to adjust a leave balance." });
        if (req.Amount == 0m || Math.Abs(req.Amount) > MaxAdjustmentDays)
            return BadRequest(new { error = "amount_out_of_range", message = $"An adjustment must be non-zero and at most {MaxAdjustmentDays} days either way." });

        // The employee must be inside the caller's company scope (the Employees set is company-filtered, so an
        // employee of another company simply does not resolve) and inside their data scope.
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        var targetExists = await _db.Employees.AsNoTracking()
            .AnyAsync(e => e.TenantId == tenantId && e.Id == req.EmployeeId && !e.IsDeleted, ct);
        if (!targetExists || !scope.CanAccessEmployee(req.EmployeeId))
            return NotFound(new { message = "Employee not found." });
        // The subject never decides: nobody adjusts their own balance, whatever their role.
        if (await Zayra.Api.Infrastructure.Approvals.SubjectDecisionBar.CallerIsSubjectAsync(_db, tenantId.Value, userId, req.EmployeeId, ct))
            return BadRequest(Zayra.Api.Infrastructure.Approvals.SubjectDecisionBar.Refusal("You cannot adjust your own leave balance. Ask another HR administrator."));

        var leaveType = await _db.LeaveTypes
            .FirstOrDefaultAsync(t => t.Id == req.LeaveTypeId && t.TenantId == tenantId, ct);
        if (leaveType is null)
            return BadRequest(new { message = "Leave type not found." });
        // A balance is only ever adjusted on a leave type of the employee's own company.
        var employeeCompany = await _db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id == req.EmployeeId).Select(e => e.CompanyId).FirstOrDefaultAsync(ct);
        if (!await Zayra.Api.Infrastructure.Leave.LeaveCompanyScope.IsUsableByAsync(_db, leaveType, employeeCompany, ct))
            return BadRequest(new { message = Zayra.Api.Infrastructure.Leave.LeaveCompanyScope.NotThisCompanysType });

        var year = req.Year ?? DateTime.UtcNow.Year;
        if (await CheckAdjustmentLimitsAsync(tenantId.Value, req.EmployeeId, year, req.Amount, ct) is { } limitRefusal) return limitRefusal;

        // A large adjustment is a proposal: it waits for a second HR person (leave.write; line managers hold leave.approve for team leave and do not decide balance adjustments) who is not the employee, and the
        // balance is untouched until then. Refused up front when nobody else could approve it (owner decision: add a second approver).
        var threshold = await ApprovalThresholdDaysAsync(tenantId.Value, ct);
        if (Math.Abs(req.Amount) > threshold)
        {
            if (await Zayra.Api.Infrastructure.Approvals.SecondApproverCheck.RefuseIfNoneAsync(_db, tenantId.Value, "leave.write", userId, [req.EmployeeId], employeeCompany,
                    $"adjusting a leave balance by more than {threshold:0.##} days", $"تعديل رصيد الإجازة بأكثر من {threshold:0.##} أيام", ct) is { } noApprover)
                return noApprover;
            var proposal = new LeaveBalanceAdjustmentRequest
            {
                TenantId = tenantId.Value, CompanyId = employeeCompany, EmployeeId = req.EmployeeId, LeaveTypeId = req.LeaveTypeId, Year = year,
                Amount = req.Amount, Reason = req.Reason.Trim(), RequestedByUserId = userId, RequestedByName = performedByName,
            };
            _db.LeaveBalanceAdjustmentRequests.Add(proposal);
            await _db.SaveChangesAsync(ct);
            await _leaveService.LogAuditAsync(tenantId.Value, "LeaveBalanceAdjustmentRequest", proposal.Id.ToString(), "AdjustmentRequested",
                string.Empty, req.Amount.ToString("F2"), req.Reason ?? string.Empty, performedByName, ct);
            return Accepted($"/api/leave/balances/adjustments/{proposal.Id}", new
            {
                requiresApproval = true, thresholdDays = threshold, request = AdjustmentDto.Project(proposal),
                message = $"Adjustments of more than {threshold:0.##} days need a second HR approver. The balance changes when they approve it.",
            });
        }

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

    internal static class AdjustmentDto
    {
        public static object Project(LeaveBalanceAdjustmentRequest r) => new
        {
            r.Id, r.EmployeeId, r.LeaveTypeId, r.Year, r.Amount, r.Reason, r.Status,
            r.RequestedByName, r.RequestedAtUtc, r.DecidedByName, r.DecidedAtUtc, r.DecisionNote,
        };
    }

    public sealed record AdjustmentDecisionRequest(string? Note);

    [HttpGet("adjustments")]
    [HasPermission("leave.write")]
    public async Task<IActionResult> ListAdjustments([FromQuery] string? status, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        var q = _db.LeaveBalanceAdjustmentRequests.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
        if (!scope.IsUnrestricted) q = q.Where(x => scope.AllowedEmployeeIds!.Contains(x.EmployeeId));
        return Ok((await q.OrderByDescending(x => x.RequestedAtUtc).Take(500).ToListAsync(ct)).Select(AdjustmentDto.Project));
    }

    [HttpPost("adjustments/{id:guid}/approve")]
    [HasPermission("leave.write")]
    public Task<IActionResult> ApproveAdjustment(Guid id, [FromBody] AdjustmentDecisionRequest? req, CancellationToken ct) =>
        DecideAdjustmentAsync(id, LeaveBalanceAdjustmentRequest.Approved, req?.Note, ct);

    [HttpPost("adjustments/{id:guid}/reject")]
    [HasPermission("leave.write")]
    public Task<IActionResult> RejectAdjustment(Guid id, [FromBody] AdjustmentDecisionRequest? req, CancellationToken ct) =>
        DecideAdjustmentAsync(id, LeaveBalanceAdjustmentRequest.Rejected, req?.Note, ct);

    /// <summary>The proposer withdraws their own pending adjustment.</summary>
    [HttpPost("adjustments/{id:guid}/cancel")]
    [HasPermission("leave.write")]
    public Task<IActionResult> CancelAdjustment(Guid id, CancellationToken ct) =>
        DecideAdjustmentAsync(id, LeaveBalanceAdjustmentRequest.Cancelled, null, ct);

    /// <summary>
    /// One locked transaction for approve, reject and cancel: the row is read FOR UPDATE, so an approve and a reject of the same
    /// request can no longer both succeed (the second finds it decided and gets 409 not_pending).
    /// </summary>
    private async Task<IActionResult> DecideAdjustmentAsync(Guid id, string decision, string? note, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();
        var userId = this.GetUserId();
        var name = User.Identity?.Name ?? userId?.ToString() ?? "HR";
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
            var result = await DecideAdjustmentCoreAsync(tenantId.Value, id, decision, note, userId, name, ct);
            if (tx is not null)
            {
                if (result is OkObjectResult) await tx.CommitAsync(ct); else await tx.RollbackAsync(ct);
            }
            return result;
        });
    }

    private async Task<IActionResult> DecideAdjustmentCoreAsync(Guid tenantId, Guid id, string decision, string? note, Guid? userId, string name, CancellationToken ct)
    {
        var row = await _db.LeaveBalanceAdjustmentRequests.TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag)
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (row is null) return NotFound();
        if (row.Status != LeaveBalanceAdjustmentRequest.Pending)
            return Conflict(new { error = "not_pending", message = "This leave adjustment has already been decided." });
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        if (!scope.CanAccessEmployee(row.EmployeeId)) return NotFound();

        if (decision == LeaveBalanceAdjustmentRequest.Cancelled)
        {
            if (row.RequestedByUserId != userId) return Forbid();
        }
        else
        {
            if (userId is null || userId == row.RequestedByUserId)
                return BadRequest(new { error = "maker_cannot_approve", message = "You proposed this leave adjustment, so a different HR approver must decide it." });
            if (await Zayra.Api.Infrastructure.Approvals.SubjectDecisionBar.CallerIsSubjectAsync(_db, tenantId, userId, row.EmployeeId, ct))
                return BadRequest(Zayra.Api.Infrastructure.Approvals.SubjectDecisionBar.Refusal("This leave adjustment is about you, so you cannot decide it. Ask another HR approver."));
        }

        if (decision == LeaveBalanceAdjustmentRequest.Approved)
        {
            // The same limits as at proposal time: a grant that has since used up the year's cap is refused, not applied.
            if (await CheckAdjustmentLimitsAsync(tenantId, row.EmployeeId, row.Year, row.Amount, ct) is { } limit)
                return Conflict(new { error = "leave_adjustment_stale", message = "This adjustment no longer fits the year's limits. Reject it and propose it again.", detail = (limit as ObjectResult)?.Value });
            var leaveType = await _db.LeaveTypes.FirstOrDefaultAsync(t => t.Id == row.LeaveTypeId && t.TenantId == tenantId, ct);
            if (leaveType is null || !await Zayra.Api.Infrastructure.Leave.LeaveCompanyScope.IsUsableByAsync(_db, leaveType, row.CompanyId, ct))
                return Conflict(new { error = "leave_adjustment_stale", message = "The leave type is no longer usable for this employee. Reject this adjustment." });
        }

        row.Status = decision;
        row.DecidedByUserId = userId; row.DecidedByName = name; row.DecidedAtUtc = DateTime.UtcNow; row.DecisionNote = (note ?? string.Empty).Trim();
        if (decision == LeaveBalanceAdjustmentRequest.Approved)
        {
            // Saves the row and the balance together.
            await _leaveService.ApplyLeaveBalanceAsync(tenantId, row.EmployeeId, row.LeaveTypeId, row.Amount, row.Year, "Adjustment", $"MANUAL-ADJ-{row.Id:N}", name, ct);
        }
        else await _db.SaveChangesAsync(ct);
        await _leaveService.LogAuditAsync(tenantId, "LeaveBalanceAdjustmentRequest", row.Id.ToString(), $"Adjustment{decision}",
            string.Empty, row.Amount.ToString("F2"), row.Reason, name, ct);
        return Ok(AdjustmentDto.Project(row));
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
