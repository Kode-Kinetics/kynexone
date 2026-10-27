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
    internal const string MakerRefusalCode = "maker_cannot_approve";

    /// <param name="OverrideGradeRange">The approver knowingly approves a package outside the employee's grade range; needs a <paramref name="Note"/> (the reason) and is audited.</param>
    public sealed record SalaryChangeDecisionRequest(string? Note, bool OverrideGradeRange = false);

    /// <summary>The active salary rows each decision found per (employee, date) on its first attempt, so a retry can tell a concurrent writer got in.</summary>
    private readonly Dictionary<(int EmployeeId, DateOnly EffectiveDate), HashSet<Guid>> _seenSalaryRows = new();

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
        return await InSalaryDecisionTransactionAsync(async () =>
        {
            var change = await _db.SalaryChangeRequests.TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (change is null) return NotFound();
            var refusal = await DecideSalaryChangeAsync(tenantId, change, SalaryChangeRequest.Approved, req?.Note, ct, req?.OverrideGradeRange == true);
            if (refusal is not null) return refusal;
            await _db.SaveChangesAsync(ct);
            await RestampFirstPackageEmployeesAsync(tenantId, [change], ct);
            return Ok(SalaryChangeDto.Project(change));
        }, ct);
    }

    /// <summary>The "salary package waiting for approval" blocker clears with the decision; the denormalised readiness badge follows
    /// (the snapshot reads the saved request, so this runs after the decision is saved, in the same transaction).</summary>
    private async Task RestampFirstPackageEmployeesAsync(Guid tenantId, IEnumerable<SalaryChangeRequest> decided, CancellationToken ct)
    {
        var ids = decided.Where(c => c.Status != SalaryChangeRequest.Pending && Zayra.Api.Infrastructure.Employees.SalaryChangeSources.IsFirstPackage(c.Source))
            .Select(c => c.EmployeeId).Distinct().ToList();
        if (ids.Count == 0) return;
        foreach (var employee in await _db.Employees.Where(x => x.TenantId == tenantId && ids.Contains(x.Id) && !x.IsDeleted).ToListAsync(ct))
            await _activationGuard.StampReadinessAsync(employee, ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Applying a change deactivates the date's current row and inserts the new one. That happens in one transaction so a
    /// concurrent writer's row for the same date (the database allows one active row per employee and date) refuses the whole
    /// decision as a conflict instead of leaving half of it applied or overwriting the other writer.
    /// </summary>
    private async Task<IActionResult> InSalaryDecisionTransactionAsync(Func<Task<IActionResult>> work, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // A retry (transient failure) starts from a clean tracker: the first attempt's half-saved state must not leak into it.
            _db.ChangeTracker.Clear();
            await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
            try
            {
                var result = await work();
                if (tx is not null)
                {
                    if (result is OkObjectResult) await tx.CommitAsync(ct); else await tx.RollbackAsync(ct);
                }
                return result;
            }
            // The 23P01 can surface raw (at COMMIT, or from the audit seal / execution strategy), not only wrapped in a DbUpdateException.
            catch (Exception ex) when (ex is SalaryImportRaceException || SalaryRowConflict(ex) is not null)
            {
                // A refused COMMIT has already ended the transaction; there is nothing left to roll back.
                if (tx is not null)
                {
                    try { await tx.RollbackAsync(ct); }
                    catch (InvalidOperationException) { /* already ended by the refused COMMIT */ }
                }
                var key = ex is SalaryImportRaceException race ? race.Key : SalaryRowConflict(ex)!.Value;
                return Conflict(new
                {
                    error = "salary_row_conflict",
                    message = "Another change to this employee's salary for the same date was saved at the same time. Nothing was applied; review the employee's salary and decide again.",
                    employeeId = key.EmployeeId,
                });
            }
        });
    }

    /// <summary>Decides every pending row of one import. Rows the caller may not decide (their own pay, or rows they proposed) stay pending and are reported.</summary>
    [HttpPost("salary-changes/batches/{batchId:guid}/approve")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> ApproveSalaryChangeBatch(Guid batchId, [FromBody] SalaryChangeDecisionRequest? req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        return await InSalaryDecisionTransactionAsync(async () =>
        {
            var rows = await _db.SalaryChangeRequests.TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag).Where(x => x.TenantId == tenantId && x.BatchId == batchId && x.Status == SalaryChangeRequest.Pending).ToListAsync(ct);
            if (rows.Count == 0) return NotFound();
            int approved = 0;
            var held = new List<object>();
            foreach (var row in rows)
            {
                var refusal = await DecideSalaryChangeAsync(tenantId, row, SalaryChangeRequest.Approved, req?.Note, ct, req?.OverrideGradeRange == true);
                if (refusal is null) approved++;
                else held.Add(new { id = row.Id, employeeCode = row.EmployeeCode, reason = (refusal as ObjectResult)?.Value });
            }
            await _db.SaveChangesAsync(ct);
            await RestampFirstPackageEmployeesAsync(tenantId, rows, ct);
            return Ok(new { approved, held });
        }, ct);
    }

    [HttpPost("salary-changes/{id:guid}/reject")]
    [HasPermission("payroll.approve")]
    public async Task<IActionResult> RejectSalaryChange(Guid id, [FromBody] SalaryChangeDecisionRequest? req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        // Same locked transaction as approve: a reject and an approve of one request can no longer both succeed.
        return await InSalaryDecisionTransactionAsync(async () =>
        {
            var change = await _db.SalaryChangeRequests.TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (change is null) return NotFound();
            var refusal = await DecideSalaryChangeAsync(tenantId, change, SalaryChangeRequest.Rejected, req?.Note, ct);
            if (refusal is not null) return refusal;
            await _db.SaveChangesAsync(ct);
            await RestampFirstPackageEmployeesAsync(tenantId, [change], ct);
            return Ok(SalaryChangeDto.Project(change));
        }, ct);
    }

    /// <summary>The proposer withdraws their own pending change.</summary>
    [HttpPost("salary-changes/{id:guid}/cancel")]
    [HasPermission("payroll.write")]
    public async Task<IActionResult> CancelSalaryChange(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        return await InSalaryDecisionTransactionAsync(async () =>
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

    /// <summary>Checks the separation-of-duties rules and applies or rejects. Returns null on success, else the refusal to send.</summary>
    private async Task<IActionResult?> DecideSalaryChangeAsync(Guid tenantId, SalaryChangeRequest change, string decision, string? note, CancellationToken ct, bool overrideGradeRange = false)
    {
        if (change.Status != SalaryChangeRequest.Pending)
            return Conflict(new { error = "not_pending", message = "This salary change has already been decided." });
        var userId = GetUserId();
        if (userId is null || userId == change.RequestedByUserId)
            return BadRequest(new { error = MakerRefusalCode, message = "You proposed this salary change, so a different Finance approver must decide it." });
        if (!(await _scopeService.ResolveAsync(User, tenantId, ct)).CanAccessEmployee(change.EmployeeId))
            return NotFound();
        if (await SubjectDecisionBar.CallerIsSubjectAsync(_db, tenantId, userId, change.EmployeeId, ct))
            return BadRequest(SubjectDecisionBar.Refusal("This salary change is about you, so you cannot decide it. Ask another Finance approver."));

        if (decision == SalaryChangeRequest.Approved)
        {
            var firstPackage = Zayra.Api.Infrastructure.Employees.SalaryChangeSources.IsFirstPackage(change.Source);
            // A first package also sets the employee's own gross and payroll code, so the row is tracked then.
            var employeeQuery = firstPackage ? _db.Employees : _db.Employees.AsNoTracking();
            var employee = await employeeQuery.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == change.EmployeeId && !x.IsDeleted, ct);
            var structure = await _db.SalaryStructures.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == change.SalaryStructureId && !x.IsDeleted, ct);
            if (employee is null || structure is null)
                return Conflict(new { error = "salary_change_stale", message = "The employee or salary structure no longer exists. Reject this change and propose it again." });
            // The package was checked when it was staged; the world can have moved since. A person who has left cannot be given a
            // new package, and a grade whose range changed (or an employee moved to another grade) is checked again here.
            if (employee.Status is EmployeeStatuses.Terminated or EmployeeStatuses.Exited or EmployeeStatuses.Archived)
                return Conflict(new { error = "salary_change_stale", message = $"The employee is now {employee.Status}, so this package cannot be applied. Reject it." });
            if (employee.GradeId.HasValue)
            {
                var grade = await _db.Grades.AsNoTracking().FirstOrDefaultAsync(g => g.TenantId == tenantId && g.Id == employee.GradeId && !g.IsDeleted, ct);
                var gross = change.BasicSalary + change.HousingAllowance + change.TransportAllowance + change.FoodAllowance + change.MobileAllowance + change.OtherAllowance - change.FixedDeduction;
                var below = grade is not null && grade.MinSalary > 0 && gross < grade.MinSalary;
                var above = grade is not null && grade.MaxSalary > 0 && gross > grade.MaxSalary;
                if (below || above)
                {
                    // An out-of-range package (the import flags it for review) is approved only knowingly: explicit override + a reason, audited.
                    if (!overrideGradeRange)
                        return Conflict(new { error = "salary_outside_grade_range", message = $"The package {gross:N2} {change.Currency} is {(below ? "below" : "above")} grade {grade!.Code} {(below ? "minimum " + grade.MinSalary.ToString("N2") : "maximum " + grade.MaxSalary.ToString("N2"))} {grade.Currency}. Approve it with the grade-range override and a reason, or reject it." });
                    if (string.IsNullOrWhiteSpace(note))
                        return BadRequest(new { error = "override_reason_required", message = "Overriding the grade range needs a reason in the note." });
                    await PayrollAudit("payroll.employee_salary.grade_range_override", "SalaryChangeRequest", change.Id.ToString(),
                        new { employeeId = change.EmployeeId, gross, gradeCode = grade!.Code, grade.MinSalary, grade.MaxSalary, reason = note.Trim(), requestedBy = change.RequestedByUserId, approvedBy = userId }, ct);
                }
            }
            var stale = ValidateEmployeeSalaryAssignment(structure, employee, new EmployeeSalaryStructureRequest(change.EmployeeId, change.SalaryStructureId, change.BasicSalary,
                change.HousingAllowance, change.TransportAllowance, change.FoodAllowance, change.MobileAllowance, change.OtherAllowance, change.FixedDeduction, change.EffectiveDate, change.Currency),
                checkEffectiveDate: false);
            if (stale is not null) return Conflict(new { error = "salary_change_stale", message = stale });

            // An effective-dated schedule: replacing one date must not deactivate the current package when the new one starts later.
            var replaced = await _db.EmployeeSalaryStructures
                .Where(x => x.TenantId == tenantId && x.EmployeeId == change.EmployeeId && x.IsActive && x.EffectiveDate == change.EffectiveDate)
                .ToListAsync(ct);
            // A retried attempt that finds a different set of rows than the first one saw means another writer got in between:
            // refuse rather than overwrite their row.
            var seen = replaced.Select(r => r.Id).ToHashSet();
            var rowKey = (change.EmployeeId, change.EffectiveDate);
            if (_seenSalaryRows.TryGetValue(rowKey, out var firstSeen) && !firstSeen.SetEquals(seen))
                throw new SalaryImportRaceException(rowKey);
            _seenSalaryRows[rowKey] = seen;
            // Same refusal as the Approvals path: an amount-only package must not silently replace a row carrying percent-of-basic /
            // in-kind allowances, a Qiwa-confirmed contract or a renewal case.
            if (replaced.Any(r => r.HousingBasis != AllowanceBases.Amount || r.TransportBasis != AllowanceBases.Amount || r.QiwaConfirmedOn != null || r.RenewalCaseId != null))
                return Conflict(new { error = "salary_change_stale", message = $"The salary dated {change.EffectiveDate:yyyy-MM-dd} has a percent-of-basic or in-kind allowance, or a Qiwa-confirmed contract, so an amount-only package cannot replace it. Use a different effective date or the contract renewal workflow." });
            foreach (var old in replaced) old.IsActive = false;
            // Flushed before the insert so the one-active-row-per-date guard never sees both at once.
            if (replaced.Count > 0) await _db.SaveChangesAsync(ct);
            var assignment = new EmployeeSalaryStructure
            {
                TenantId = tenantId, EmployeeId = change.EmployeeId, SalaryStructureId = change.SalaryStructureId, BasicSalary = change.BasicSalary,
                HousingAllowance = change.HousingAllowance, TransportAllowance = change.TransportAllowance, FoodAllowance = change.FoodAllowance,
                MobileAllowance = change.MobileAllowance, OtherAllowance = change.OtherAllowance, FixedDeduction = change.FixedDeduction,
                EffectiveDate = change.EffectiveDate, Currency = change.Currency, CreatedBy = change.RequestedByUserId,
            };
            _db.EmployeeSalaryStructures.Add(assignment);
            change.AppliedAssignmentId = assignment.Id;
            if (firstPackage)
            {
                await Zayra.Api.Infrastructure.Employees.EmployeeSalaryBreakdownWriter.ApplyFirstPackageEffectsAsync(_db, employee, structure, change, ct);
            }
            await PayrollAudit("payroll.employee_salary.assigned", "EmployeeSalaryStructure", assignment.Id.ToString(),
                new { employeeId = change.EmployeeId, basicSalary = change.BasicSalary, requestId = change.Id, requestedBy = change.RequestedByUserId, approvedBy = userId }, ct);
        }
        else
        {
            await PayrollAudit("payroll.employee_salary.change_rejected", "SalaryChangeRequest", change.Id.ToString(),
                new { employeeId = change.EmployeeId, requestedBy = change.RequestedByUserId, rejectedBy = userId }, ct);
        }
        change.Status = decision; change.DecidedByUserId = userId; change.DecidedByName = GetUserName();
        change.DecidedAtUtc = DateTime.UtcNow; change.DecisionNote = (note ?? string.Empty).Trim();
        return null;
    }
}
