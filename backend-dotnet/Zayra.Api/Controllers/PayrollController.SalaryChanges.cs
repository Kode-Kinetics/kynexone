using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Authorization;
using Zayra.Api.Models;

namespace Zayra.Api.Controllers;

/// <summary>
/// Salary package changes are made by HR and approved by Finance, by a different person. This partial holds the
/// staging, the decision endpoints and the one place a staged change becomes a salary assignment.
/// </summary>
public partial class PayrollController
{
    internal const string OwnSalaryRefusal = "You cannot change your own salary. Ask another HR administrator to propose it.";
    internal const string MakerRefusalCode = Zayra.Api.Infrastructure.Payroll.SalaryChangeDecisionService.MakerRefusalCode;

    /// <param name="OverrideGradeRange">The approver knowingly approves a package outside the employee's grade range; needs a <paramref name="Note"/> (the reason) and is audited.</param>
    public sealed record SalaryChangeDecisionRequest(string? Note, bool OverrideGradeRange = false);

    private Zayra.Api.Infrastructure.Payroll.SalaryDecisionActor SalaryActor() =>
        new(GetUserId(), GetUserName(), _http.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    /// <summary>The Payroll page's visibility rule for a decision: the caller's data scope must reach the employee.</summary>
    private async Task<Func<int, bool>> SalaryDecisionVisibilityAsync(Guid tenantId, CancellationToken ct)
    {
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        return scope.CanAccessEmployee;
    }

    internal static class SalaryChangeDto
    {
        public static object Project(SalaryChangeRequest c) => new
        {
            c.Id, c.BatchId, c.Source, c.EmployeeId, c.EmployeeCode, c.EmployeeName, c.SalaryStructureId,
            c.BasicSalary, c.HousingAllowance, c.TransportAllowance, c.FoodAllowance, c.MobileAllowance, c.OtherAllowance, c.FixedDeduction,
            c.EffectiveDate, c.Currency, c.PreviousBasicSalary, c.Status,
            c.RequestedByName, c.RequestedAtUtc, c.DecidedByName, c.DecidedAtUtc, c.DecisionNote, c.AppliedAssignmentId,
        };
    }

    /// <summary>Stages a validated change as Pending. An older pending change for the same employee and date is superseded.</summary>
    private async Task<SalaryChangeRequest> StageSalaryChangeAsync(
        Guid tenantId, Employee employee, Guid structureId, EmployeeSalaryStructureRequest req, string source, Guid? batchId, CancellationToken ct)
    {
        var superseded = await _db.SalaryChangeRequests
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.EffectiveDate == req.EffectiveDate && x.Status == SalaryChangeRequest.Pending)
            .ToListAsync(ct);
        foreach (var old in superseded)
        {
            old.Status = SalaryChangeRequest.Cancelled;
            old.DecidedAtUtc = DateTime.UtcNow;
            old.DecisionNote = "Superseded by a newer proposal for the same date.";
            await PayrollAudit("payroll.employee_salary.change_superseded", "SalaryChangeRequest", old.Id.ToString(),
                new { employeeId = employee.Id, originallyRequestedBy = old.RequestedByUserId, supersededBy = GetUserId() }, ct);
            // The first maker is told their proposal was replaced and by whom, instead of finding it silently Cancelled.
            if (old.RequestedByUserId is Guid firstMaker && firstMaker != GetUserId())
                await _notifications.NotifyAsync(tenantId, firstMaker, "Your salary proposal was replaced",
                    $"Your pending salary proposal for {employee.EmployeeCode} {employee.FullName} (effective {old.EffectiveDate:yyyy-MM-dd}) was replaced by a newer proposal from {GetUserName()}. Nothing was applied.",
                    "SalaryChangeRequest", old.Id.ToString(), ct);
        }
        var previous = await _db.EmployeeSalaryStructures.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EmployeeId == employee.Id && x.IsActive && x.EffectiveDate <= req.EffectiveDate)
            .OrderByDescending(x => x.EffectiveDate).Select(x => (decimal?)x.BasicSalary).FirstOrDefaultAsync(ct);
        var change = new SalaryChangeRequest
        {
            TenantId = tenantId, CompanyId = employee.CompanyId, BatchId = batchId, Source = source,
            EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode, EmployeeName = employee.FullName,
            SalaryStructureId = structureId, BasicSalary = req.BasicSalary, HousingAllowance = req.HousingAllowance,
            TransportAllowance = req.TransportAllowance, FoodAllowance = req.FoodAllowance, MobileAllowance = req.MobileAllowance,
            OtherAllowance = req.OtherAllowance, FixedDeduction = req.FixedDeduction, EffectiveDate = req.EffectiveDate,
            Currency = req.Currency ?? string.Empty, PreviousBasicSalary = previous,
            RequestedByUserId = GetUserId(), RequestedByName = GetUserName(),
        };
        _db.SalaryChangeRequests.Add(change);
        return change;
    }

    /// <summary>Whether anyone besides the caller could approve a salary change; the page shows the refusal's words in advance.</summary>
    [HttpGet("salary-changes/approver-status")]
    [HasPermission("payroll.write", "payroll.approve")]
    public async Task<IActionResult> SalaryApproverStatus(CancellationToken ct)
    {
        var exists = await Zayra.Api.Infrastructure.Approvals.SecondApproverCheck.ExistsAsync(_db, GetTenantId(), "payroll.approve", GetUserId(), null, null, ct);
        var text = Zayra.Api.Infrastructure.Approvals.SecondApproverCheck.Refusal("payroll.approve", "proposing salary changes", "اقتراح تغييرات الرواتب");
        return Ok(exists ? new { hasEligibleApprover = true, message = (string?)null, messageAr = (string?)null }
                         : new { hasEligibleApprover = false, message = text.message, messageAr = text.messageAr });
    }

    [HttpGet("salary-changes")]
    [HasPermission("payroll.write", "payroll.approve")]
    public async Task<IActionResult> ListSalaryChanges([FromQuery] string? status, [FromQuery] Guid? batchId, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var scope = await _scopeService.ResolveAsync(User, tenantId, ct);
        var q = _db.SalaryChangeRequests.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
        if (batchId.HasValue) q = q.Where(x => x.BatchId == batchId);
        if (!scope.IsUnrestricted) q = q.Where(x => scope.AllowedEmployeeIds!.Contains(x.EmployeeId));
        var items = await q.OrderByDescending(x => x.RequestedAtUtc).Take(500).ToListAsync(ct);
        return Ok(items.Select(SalaryChangeDto.Project));
    }

    [HttpPost("salary-changes/{id:guid}/approve")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> ApproveSalaryChange(Guid id, [FromBody] SalaryChangeDecisionRequest? req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        return await _salaryDecisions.ApproveAsync(tenantId, id, SalaryActor(), req?.Note, req?.OverrideGradeRange == true,
            await SalaryDecisionVisibilityAsync(tenantId, ct), ct);
    }

    /// <summary>Decides every pending row of one import. Rows the caller may not decide (their own pay, or rows they proposed) stay pending and are reported.</summary>
    [HttpPost("salary-changes/batches/{batchId:guid}/approve")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> ApproveSalaryChangeBatch(Guid batchId, [FromBody] SalaryChangeDecisionRequest? req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        return await _salaryDecisions.ApproveBatchAsync(tenantId, batchId, SalaryActor(), req?.Note, req?.OverrideGradeRange == true,
            await SalaryDecisionVisibilityAsync(tenantId, ct), ct);
    }

    [HttpPost("salary-changes/{id:guid}/reject")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> RejectSalaryChange(Guid id, [FromBody] SalaryChangeDecisionRequest? req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        return await _salaryDecisions.RejectAsync(tenantId, id, SalaryActor(), req?.Note, await SalaryDecisionVisibilityAsync(tenantId, ct), ct);
    }

    /// <summary>The proposer withdraws their own pending change.</summary>
    [HttpPost("salary-changes/{id:guid}/cancel")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> CancelSalaryChange(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        return await _salaryDecisions.InTransactionAsync(async () =>
        {
            var change = await _db.SalaryChangeRequests.TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (change is null) return NotFound();
            if (change.Status != SalaryChangeRequest.Pending) return Conflict(new { error = "not_pending", message = "This salary change has already been decided." });
            if (change.RequestedByUserId != GetUserId()) return Forbid();
            change.Status = SalaryChangeRequest.Cancelled; change.DecidedAtUtc = DateTime.UtcNow; change.DecidedByUserId = GetUserId(); change.DecidedByName = GetUserName();
            await PayrollAudit("payroll.employee_salary.change_cancelled", "SalaryChangeRequest", change.Id.ToString(),
                new { employeeId = change.EmployeeId, requestedBy = change.RequestedByUserId }, ct);
            await _db.SaveChangesAsync(ct);
            return Ok(SalaryChangeDto.Project(change));
        }, ct);
    }
}
