using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Leave;
using Zayra.Api.Data;

namespace Zayra.Api.Controllers.Leave;

[ApiController]
[Route("api/leave/reports")]
[Authorize]
public class LeaveReportsController : ControllerBase
{
    private readonly ZayraDbContext _db;
    private readonly IDataScopeService _scopeService;

    public LeaveReportsController(ZayraDbContext db, IDataScopeService scopeService)
    {
        _db = db;
        _scopeService = scopeService;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard([FromQuery] Guid? companyId, [FromQuery] Guid? branchId, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        var allowedIds = scope.IsUnrestricted ? null : scope.AllowedEmployeeIds!.ToList();

        var companyFilterIds = await LeaveGroupFilter.EmployeeIdsAsync(_db, tenantId.Value, companyId, branchId, ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var pendingStatuses = new[] { "Submitted", "PendingManagerApproval", "PendingHRApproval" };

        var leaveQuery = _db.LeaveRequests.Where(r => r.TenantId == tenantId);
        if (allowedIds is not null) leaveQuery = leaveQuery.Where(r => allowedIds.Contains(r.EmployeeId));
        if (companyFilterIds is not null) leaveQuery = leaveQuery.Where(r => companyFilterIds.Contains(r.EmployeeId));

        var onLeaveToday = await leaveQuery.CountAsync(r => r.Status == "Approved" && r.StartDate <= today && r.EndDate >= today, ct);
        var pendingApprovals = await leaveQuery.CountAsync(r => pendingStatuses.Contains(r.Status), ct);
        var pendingCancellations = await leaveQuery.CountAsync(r => r.Status == "CancellationRequested", ct);
        var upcomingLeaves = await leaveQuery.CountAsync(r => r.Status == "Approved" && r.StartDate > today && r.StartDate <= today.AddDays(14), ct);

        var encashQuery = _db.LeaveEncashmentRequests.Where(e => e.TenantId == tenantId);
        if (allowedIds is not null) encashQuery = encashQuery.Where(e => allowedIds.Contains(e.EmployeeId));
        if (companyFilterIds is not null) encashQuery = encashQuery.Where(e => companyFilterIds.Contains(e.EmployeeId));
        var pendingEncashments = await encashQuery.CountAsync(e => e.Status == "Pending" || e.Status == "HRApproved", ct);

        var absenceQuery = _db.AbsenceRecords.Where(a => a.TenantId == tenantId);
        if (allowedIds is not null) absenceQuery = absenceQuery.Where(a => allowedIds.Contains(a.EmployeeId));
        if (companyFilterIds is not null) absenceQuery = absenceQuery.Where(a => companyFilterIds.Contains(a.EmployeeId));
        var unauthorizedAbsences = await absenceQuery.CountAsync(a => !a.IsRegularized, ct);

        var compOffQuery = _db.CompOffCredits.Where(c => c.TenantId == tenantId);
        if (allowedIds is not null) compOffQuery = compOffQuery.Where(c => allowedIds.Contains(c.EmployeeId));
        if (companyFilterIds is not null) compOffQuery = compOffQuery.Where(c => companyFilterIds.Contains(c.EmployeeId));
        var pendingCompOff = await compOffQuery.CountAsync(c => c.Status == "Pending", ct);

        var delegationQuery = _db.LeaveDelegations.Where(d => d.TenantId == tenantId);
        if (allowedIds is not null) delegationQuery = delegationQuery.Where(d => allowedIds.Contains(d.EmployeeId));
        if (companyFilterIds is not null) delegationQuery = delegationQuery.Where(d => companyFilterIds.Contains(d.EmployeeId));
        var activeDelegations = await delegationQuery.CountAsync(d => d.Status == "Active", ct);

        return Ok(new
        {
            OnLeaveToday = onLeaveToday,
            PendingApprovals = pendingApprovals,
            PendingCancellations = pendingCancellations,
            PendingEncashments = pendingEncashments,
            UnauthorizedAbsences = unauthorizedAbsences,
            UpcomingLeaves = upcomingLeaves,
            ActiveDelegations = activeDelegations,
            PendingCompOff = pendingCompOff
        });
    }

    [HttpGet("balance-summary")]
    public async Task<IActionResult> BalanceSummary(
        [FromQuery] int? year, [FromQuery] Guid? companyId, [FromQuery] Guid? branchId, CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        year ??= DateTime.UtcNow.Year;

        var query = _db.EmployeeLeaveBalances.Where(b => b.TenantId == tenantId && b.Year == year);
        if (!scope.IsUnrestricted)
            query = query.Where(b => scope.AllowedEmployeeIds!.Contains(b.EmployeeId));
        var group = await LeaveGroupFilter.EmployeeIdsAsync(_db, tenantId.Value, companyId, branchId, ct);
        if (group is not null) query = query.Where(b => group.Contains(b.EmployeeId));

        var balances = await query.OrderBy(b => b.EmployeeName).ThenBy(b => b.LeaveTypeName).ToListAsync(ct);

        var summary = balances.Select(b => new
        {
            b.EmployeeId, b.EmployeeName, b.LeaveTypeName, b.Year,
            b.Entitled, b.Accrued, b.Used, b.Pending, b.CarriedForward,
            b.Encashed, b.Expired, b.ManualAdjustment, b.Available
        });

        return Ok(summary);
    }

    [HttpGet("usage")]
    public async Task<IActionResult> Usage(
        [FromQuery] DateOnly? fromDate,
        [FromQuery] DateOnly? toDate,
        [FromQuery] string? departmentName,
        [FromQuery] Guid? companyId,
        [FromQuery] Guid? branchId,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);

        var from = fromDate ?? DateOnly.FromDateTime(new DateTime(DateTime.UtcNow.Year, 1, 1));
        var to = toDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        var query = _db.LeaveRequests
            .Where(r => r.TenantId == tenantId && r.Status == "Approved" && r.StartDate >= from && r.StartDate <= to);

        if (!scope.IsUnrestricted)
            query = query.Where(r => scope.AllowedEmployeeIds!.Contains(r.EmployeeId));
        var group = await LeaveGroupFilter.EmployeeIdsAsync(_db, tenantId.Value, companyId, branchId, ct);
        if (group is not null) query = query.Where(r => group.Contains(r.EmployeeId));
        if (!string.IsNullOrWhiteSpace(departmentName))
            query = query.Where(r => r.DepartmentName == departmentName);

        var requests = await query.ToListAsync(ct);

        var byDepartmentAndType = requests
            .GroupBy(r => new { r.DepartmentName, r.LeaveTypeName })
            .Select(g => new { g.Key.DepartmentName, g.Key.LeaveTypeName, RequestCount = g.Count(), TotalDays = g.Sum(r => r.TotalDays) })
            .OrderBy(x => x.DepartmentName).ThenBy(x => x.LeaveTypeName);

        return Ok(new { from, to, usage = byDepartmentAndType });
    }

    [HttpGet("on-leave-today")]
    public async Task<IActionResult> OnLeaveToday(
        [FromQuery] Guid? companyId, [FromQuery] Guid? branchId, CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var query = _db.LeaveRequests
            .Where(r => r.TenantId == tenantId && r.Status == "Approved" && r.StartDate <= today && r.EndDate >= today);

        if (!scope.IsUnrestricted)
            query = query.Where(r => scope.AllowedEmployeeIds!.Contains(r.EmployeeId));
        var group = await LeaveGroupFilter.EmployeeIdsAsync(_db, tenantId.Value, companyId, branchId, ct);
        if (group is not null) query = query.Where(r => group.Contains(r.EmployeeId));

        var onLeave = await query
            .OrderBy(r => r.DepartmentName).ThenBy(r => r.EmployeeName)
            .Select(r => new { r.EmployeeId, r.EmployeeName, r.DepartmentName, r.LeaveTypeName, r.StartDate, r.EndDate, TotalDays = r.TotalDays })
            .ToListAsync(ct);

        return Ok(new { date = today, count = onLeave.Count, employees = onLeave });
    }

    [HttpGet("pending-approvals")]
    public async Task<IActionResult> PendingApprovals(
        [FromQuery] Guid? companyId, [FromQuery] Guid? branchId, CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        var pendingStatuses = new[] { "Submitted", "PendingManagerApproval", "PendingHRApproval" };

        var query = _db.LeaveRequests.Where(r => r.TenantId == tenantId && pendingStatuses.Contains(r.Status));
        if (!scope.IsUnrestricted)
            query = query.Where(r => scope.AllowedEmployeeIds!.Contains(r.EmployeeId));
        var group = await LeaveGroupFilter.EmployeeIdsAsync(_db, tenantId.Value, companyId, branchId, ct);
        if (group is not null) query = query.Where(r => group.Contains(r.EmployeeId));

        var pending = await query
            .OrderBy(r => r.SubmittedAtUtc)
            .Select(r => new { r.Id, r.EmployeeId, r.EmployeeName, r.DepartmentName, r.LeaveTypeName, r.StartDate, r.EndDate, Days = r.TotalDays, r.Status, r.SubmittedAtUtc })
            .ToListAsync(ct);

        var byStatus = pending.GroupBy(r => r.Status).Select(g => new { Status = g.Key, Count = g.Count() });

        return Ok(new { total = pending.Count, byStatus, requests = pending });
    }

    [HttpGet("sick-leave-trend")]
    public async Task<IActionResult> SickLeaveTrend(CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);

        var sickLeaveTypeIds = await _db.LeaveTypes
            .Where(t => t.TenantId == tenantId && (t.Category == "Sick" || t.NameEn.Contains("Sick")))
            .Select(t => t.Id).ToListAsync(ct);

        var twelveMonthsAgo = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-12));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var query = _db.LeaveRequests
            .Where(r => r.TenantId == tenantId && sickLeaveTypeIds.Contains(r.LeaveTypeId) && r.Status == "Approved" && r.StartDate >= twelveMonthsAgo && r.StartDate <= today);

        if (!scope.IsUnrestricted)
            query = query.Where(r => scope.AllowedEmployeeIds!.Contains(r.EmployeeId));

        var sickLeave = await query.ToListAsync(ct);

        var byMonth = sickLeave
            .GroupBy(r => new { r.StartDate.Year, r.StartDate.Month })
            .Select(g => new { Month = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMM yyyy"), Count = g.Count(), TotalDays = g.Sum(r => r.TotalDays) })
            .OrderBy(x => x.Month);

        return Ok(byMonth);
    }

    [HttpGet("encashment-summary")]
    public async Task<IActionResult> EncashmentSummary([FromQuery] int? year, CancellationToken ct)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        year ??= DateTime.UtcNow.Year;

        var query = _db.LeaveEncashmentRequests.Where(e => e.TenantId == tenantId && e.Year == year);
        if (!scope.IsUnrestricted)
            query = query.Where(e => scope.AllowedEmployeeIds!.Contains(e.EmployeeId));

        var encashments = await query.ToListAsync(ct);

        var byType = encashments
            .GroupBy(e => e.LeaveTypeName)
            .Select(g => new { LeaveTypeName = g.Key, RequestCount = g.Count(), TotalDays = g.Sum(e => e.DaysToEncash), TotalAmount = g.Sum(e => e.TotalAmount), ProcessedCount = g.Count(e => e.Status == "Processed"), PendingCount = g.Count(e => e.Status == "Pending" || e.Status == "HRApproved") });

        return Ok(new { year, summary = byType, grandTotalAmount = encashments.Where(e => e.Status == "Processed").Sum(e => e.TotalAmount) });
    }

    // Salary-linked liability data — restricted to HR/Finance/Payroll roles only
    [HttpGet("liability")]
    [Authorize(Roles = "Admin,HR Manager,HR Officer,Payroll Officer,Payroll Manager,Finance Approver,Auditor")]
    public async Task<IActionResult> Liability(
        [FromQuery] int? year, [FromQuery] Guid? companyId, [FromQuery] Guid? branchId, CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var scope = await _scopeService.ResolveAsync(User, tenantId.Value, ct);
        year ??= DateTime.UtcNow.Year;

        var query = _db.EmployeeLeaveBalances.Where(b => b.TenantId == tenantId && b.Year == year);
        if (!scope.IsUnrestricted)
            query = query.Where(b => scope.AllowedEmployeeIds!.Contains(b.EmployeeId));
        var group = await LeaveGroupFilter.EmployeeIdsAsync(_db, tenantId.Value, companyId, branchId, ct);
        if (group is not null) query = query.Where(b => group.Contains(b.EmployeeId));

        var balances = (await query.ToListAsync(ct)).Where(b => b.Available > 0).ToList();

        var employeeIds = balances.Select(b => b.EmployeeId).Distinct().ToList();
        var employees = await _db.Employees
            .Where(e => employeeIds.Contains(e.Id) && e.TenantId == tenantId)
            .ToDictionaryAsync(e => e.Id, e => e.Salary ?? 0m, ct);

        var liability = balances.Select(b =>
        {
            var monthlySalary = employees.TryGetValue(b.EmployeeId, out var salary) ? salary : 0m;
            var dailyRate = monthlySalary > 0 ? monthlySalary / 30 : 0m;
            return new { b.EmployeeId, b.EmployeeName, b.LeaveTypeName, b.Available, DailyRate = dailyRate, LiabilityAmount = b.Available * dailyRate };
        }).OrderByDescending(x => x.LiabilityAmount);

        return Ok(new { year, totalLiability = liability.Sum(x => x.LiabilityAmount), details = liability });
    }

    [HttpGet("blackout-dates")]
    public async Task<IActionResult> BlackoutDates(
        [FromQuery] DateOnly? fromDate,
        [FromQuery] DateOnly? toDate,
        CancellationToken ct = default)
    {
        var tenantId = this.GetTenantId();
        if (tenantId is null) return Unauthorized();

        var from = fromDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var to = toDate ?? from.AddMonths(3);

        var blackouts = await _db.LeaveBlackoutDates
            .Where(b => b.TenantId == tenantId && b.StartDate <= to && b.EndDate >= from)
            .OrderBy(b => b.StartDate)
            .ToListAsync(ct);

        return Ok(blackouts);
    }
}
