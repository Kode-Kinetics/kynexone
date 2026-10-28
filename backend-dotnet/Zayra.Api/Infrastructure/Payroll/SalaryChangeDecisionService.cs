using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Employees;
using Zayra.Api.Infrastructure.Employees.Onboarding;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>Who is deciding, as the audit row and the maker/subject rules need it.</summary>
public sealed record SalaryDecisionActor(Guid? UserId, string UserName, string Ip);

/// <summary>
/// The ONE decision path for a staged salary change (and so for a hire's first package): separation of duties, the
/// "who may approve a hire's pay" test, the grade-range override, stale checks, the locked transaction, conflict handling,
/// the first-package restamp and the pay-ledger close. <c>PayrollController</c> (the Payroll page) and
/// <see cref="FirstPackageDecision"/> (the scoped pay-approval view and the compensation approve route) both call it.
/// Visibility is the CALLER's rule, passed in as <c>canDecideEmployee</c> (null = the caller has already scoped the employee):
/// the Payroll page uses the caller's data scope, the scoped approval view uses the company scope it already applied.
/// </summary>
public sealed class SalaryChangeDecisionService
{
    public const string MakerRefusalCode = "maker_cannot_approve";

    private readonly ZayraDbContext _db;
    private readonly IEmployeeActivationGuard _activationGuard;

    /// <summary>The active salary rows each decision found per (employee, date) on its first attempt, so a retry can tell a concurrent writer got in.</summary>
    private readonly Dictionary<(int EmployeeId, DateOnly EffectiveDate), HashSet<Guid>> _seenSalaryRows = new();

    public SalaryChangeDecisionService(ZayraDbContext db, IEmployeeActivationGuard activationGuard)
    {
        _db = db;
        _activationGuard = activationGuard;
    }

    /// <summary>A retried import found that another writer changed a key's salary rows after the first attempt.</summary>
    internal sealed class SalaryImportRaceException((int EmployeeId, DateOnly EffectiveDate) key) : Exception("salary row race")
    {
        public (int EmployeeId, DateOnly EffectiveDate) Key { get; } = key;
    }

    public async Task<IActionResult> ApproveAsync(Guid tenantId, Guid id, SalaryDecisionActor actor, string? note, bool overrideGradeRange,
        Func<int, bool>? canDecideEmployee, CancellationToken ct) =>
        await InTransactionAsync(async () =>
        {
            var change = await _db.SalaryChangeRequests.TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (change is null) return new NotFoundResult();
            var refusal = await DecideAsync(tenantId, change, SalaryChangeRequest.Approved, note, actor, canDecideEmployee, ct, overrideGradeRange);
            if (refusal is not null) return refusal;
            await _db.SaveChangesAsync(ct);
            await RestampFirstPackageEmployeesAsync(tenantId, [change], ct);
            return new OkObjectResult(PayrollController.SalaryChangeDto.Project(change));
        }, ct);

    public async Task<IActionResult> RejectAsync(Guid tenantId, Guid id, SalaryDecisionActor actor, string? note,
        Func<int, bool>? canDecideEmployee, CancellationToken ct) =>
        // Same locked transaction as approve: a reject and an approve of one request can no longer both succeed.
        await InTransactionAsync(async () =>
        {
            var change = await _db.SalaryChangeRequests.TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag).FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (change is null) return new NotFoundResult();
            var refusal = await DecideAsync(tenantId, change, SalaryChangeRequest.Rejected, note, actor, canDecideEmployee, ct);
            if (refusal is not null) return refusal;
            await _db.SaveChangesAsync(ct);
            await RestampFirstPackageEmployeesAsync(tenantId, [change], ct);
            return new OkObjectResult(PayrollController.SalaryChangeDto.Project(change));
        }, ct);

    /// <summary>Decides every pending row of one import. Rows the caller may not decide stay pending and are reported.</summary>
    public async Task<IActionResult> ApproveBatchAsync(Guid tenantId, Guid batchId, SalaryDecisionActor actor, string? note, bool overrideGradeRange,
        Func<int, bool>? canDecideEmployee, CancellationToken ct) =>
        await InTransactionAsync(async () =>
        {
            var rows = await _db.SalaryChangeRequests.TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag).Where(x => x.TenantId == tenantId && x.BatchId == batchId && x.Status == SalaryChangeRequest.Pending).ToListAsync(ct);
            if (rows.Count == 0) return new NotFoundResult();
            int approved = 0;
            var held = new List<object>();
            foreach (var row in rows)
            {
                var refusal = await DecideAsync(tenantId, row, SalaryChangeRequest.Approved, note, actor, canDecideEmployee, ct, overrideGradeRange);
                if (refusal is null) approved++;
                else held.Add(new { id = row.Id, employeeCode = row.EmployeeCode, reason = (refusal as ObjectResult)?.Value });
            }
            await _db.SaveChangesAsync(ct);
            await RestampFirstPackageEmployeesAsync(tenantId, rows, ct);
            return new OkObjectResult(new { approved, held });
        }, ct);

    /// <summary>The "salary package waiting for approval" blocker clears with the decision, and an approved first package closes the pay ledger with it;
    /// the denormalised readiness badge follows (the snapshot reads the saved request, so this runs after the decision is saved, in the same transaction).</summary>
    private async Task RestampFirstPackageEmployeesAsync(Guid tenantId, IEnumerable<SalaryChangeRequest> decided, CancellationToken ct)
    {
        var ids = decided.Where(c => c.Status != SalaryChangeRequest.Pending && SalaryChangeSources.IsFirstPackage(c.Source))
            .Select(c => c.EmployeeId).Distinct().ToList();
        if (ids.Count == 0) return;
        var approvedIds = decided.Where(c => c.Status == SalaryChangeRequest.Approved && SalaryChangeSources.IsFirstPackage(c.Source))
            .Select(c => c.EmployeeId).ToHashSet();
        var lastEditors = new Dictionary<int, Guid?>();
        foreach (var employee in await _db.Employees.Where(x => x.TenantId == tenantId && ids.Contains(x.Id) && !x.IsDeleted).ToListAsync(ct))
        {
            lastEditors[employee.Id] = employee.UpdatedBy;
            // The approval of the figures is the approval of the hire's pay: the pay ledger closes with it, in this transaction,
            // so "approved" can never be true in one record and false in the other.
            if (approvedIds.Contains(employee.Id))
                await CompensationApprovalGate.CloseAfterPackageApprovalAsync(_db, employee, ct);
            await _activationGuard.StampReadinessAsync(employee, ct);
        }
        await _db.SaveChangesAsync(ct);
        // Re-stamping readiness is bookkeeping, not an edit of the hire: the decider must not become the employee's last editor,
        // or the pay-approval rules (a later writer may not decide) would bar the very person who just reviewed the package.
        // (Bulk update: the context stamps UpdatedBy on every tracked save. The in-memory test provider has no bulk update.)
        if (!_db.Database.IsRelational()) return;
        foreach (var (employeeId, editor) in lastEditors)
            await _db.Employees.Where(x => x.TenantId == tenantId && x.Id == employeeId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedBy, editor), ct);
    }

    /// <summary>
    /// Applying a change deactivates the date's current row and inserts the new one. That happens in one transaction so a
    /// concurrent writer's row for the same date (the database allows one active row per employee and date) refuses the whole
    /// decision as a conflict instead of leaving half of it applied or overwriting the other writer.
    /// </summary>
    public async Task<IActionResult> InTransactionAsync(Func<Task<IActionResult>> work, CancellationToken ct)
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
            catch (Exception ex) when (ex is SalaryImportRaceException || PayrollController.SalaryRowConflict(ex) is not null)
            {
                // A refused COMMIT has already ended the transaction; there is nothing left to roll back.
                if (tx is not null)
                {
                    try { await tx.RollbackAsync(ct); }
                    catch (InvalidOperationException) { /* already ended by the refused COMMIT */ }
                }
                var key = ex is SalaryImportRaceException race ? race.Key : PayrollController.SalaryRowConflict(ex)!.Value;
                return new ConflictObjectResult(new
                {
                    error = "salary_row_conflict",
                    message = "Another change to this employee's salary for the same date was saved at the same time. Nothing was applied; review the employee's salary and decide again.",
                    employeeId = key.EmployeeId,
                });
            }
        });
    }

    /// <summary>Stages a payroll audit row (sealed by the context when the business save flushes it).</summary>
    public void Audit(Guid tenantId, SalaryDecisionActor actor, string action, string entity, string entityId, object? metadata)
    {
        var meta = new { ip = actor.Ip, userId = actor.UserId?.ToString(), data = metadata };
        _db.PayrollAuditLogs.Add(new PayrollAuditLog
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Action = action, EntityName = entity, EntityId = entityId,
            UserId = actor.UserId, MetadataJson = JsonSerializer.Serialize(meta),
        });
    }

    /// <summary>Checks the separation-of-duties rules and applies or rejects. Returns null on success, else the refusal to send.</summary>
    private async Task<IActionResult?> DecideAsync(Guid tenantId, SalaryChangeRequest change, string decision, string? note, SalaryDecisionActor actor,
        Func<int, bool>? canDecideEmployee, CancellationToken ct, bool overrideGradeRange = false)
    {
        if (change.Status != SalaryChangeRequest.Pending)
            return new ConflictObjectResult(new { error = "not_pending", message = "This salary change has already been decided." });
        var userId = actor.UserId;
        if (userId is null || userId == change.RequestedByUserId)
            return new BadRequestObjectResult(new { error = MakerRefusalCode, message = "You proposed this salary change, so a different Finance approver must decide it." });
        if (canDecideEmployee is not null && !canDecideEmployee(change.EmployeeId))
            return new NotFoundResult();
        if (await SubjectDecisionBar.CallerIsSubjectAsync(_db, tenantId, userId, change.EmployeeId, ct))
            return new BadRequestObjectResult(SubjectDecisionBar.Refusal("This salary change is about you, so you cannot decide it. Ask another Finance approver."));

        if (decision == SalaryChangeRequest.Approved)
        {
            var firstPackage = SalaryChangeSources.IsFirstPackage(change.Source);
            // A first package also sets the employee's own gross and payroll code, so the row is tracked then.
            var employeeQuery = firstPackage ? _db.Employees : _db.Employees.AsNoTracking();
            var employee = await employeeQuery.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == change.EmployeeId && !x.IsDeleted, ct);
            // ONE rule for who may approve a hire's pay, whichever door the approver used: the maker and the subject are barred above,
            // and so is anyone who wrote this hire's pay since the last approval (including the workflow approver of a change), the last
            // editor of the record, and whoever handed the subject their sign-in code (the scoped pay-approval view applies the same test).
            if (firstPackage && employee is not null
                && await CompensationApprovalGate.IsBarredAsync(_db, tenantId, employee, userId.Value, ct))
                return new ObjectResult(new { error = "second_person_required",
                    message = "A pay package must be approved by someone other than the person who entered it, the employee, or the person who issued the employee's sign-in code." })
                { StatusCode = 403 };
            var structure = await _db.SalaryStructures.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == change.SalaryStructureId && !x.IsDeleted, ct);
            if (employee is null || structure is null)
                return new ConflictObjectResult(new { error = "salary_change_stale", message = "The employee or salary structure no longer exists. Reject this change and propose it again." });
            // The package was checked when it was staged; the world can have moved since. A person who has left cannot be given a
            // new package, and a grade whose range changed (or an employee moved to another grade) is checked again here.
            if (employee.Status is EmployeeStatuses.Terminated or EmployeeStatuses.Exited or EmployeeStatuses.Archived)
                return new ConflictObjectResult(new { error = "salary_change_stale", message = $"The employee is now {employee.Status}, so this package cannot be applied. Reject it." });
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
                        return new ConflictObjectResult(new { error = "salary_outside_grade_range", message = $"The package {gross:N2} {change.Currency} is {(below ? "below" : "above")} grade {grade!.Code} {(below ? "minimum " + grade.MinSalary.ToString("N2") : "maximum " + grade.MaxSalary.ToString("N2"))} {grade.Currency}. Approve it with the grade-range override and a reason, or reject it." });
                    if (string.IsNullOrWhiteSpace(note))
                        return new BadRequestObjectResult(new { error = "override_reason_required", message = "Overriding the grade range needs a reason in the note." });
                    Audit(tenantId, actor, "payroll.employee_salary.grade_range_override", "SalaryChangeRequest", change.Id.ToString(),
                        new { employeeId = change.EmployeeId, gross, gradeCode = grade!.Code, grade.MinSalary, grade.MaxSalary, reason = note.Trim(), requestedBy = change.RequestedByUserId, approvedBy = userId });
                }
            }
            var stale = PayrollController.ValidateEmployeeSalaryAssignment(structure, employee, new EmployeeSalaryStructureRequest(change.EmployeeId, change.SalaryStructureId, change.BasicSalary,
                change.HousingAllowance, change.TransportAllowance, change.FoodAllowance, change.MobileAllowance, change.OtherAllowance, change.FixedDeduction, change.EffectiveDate, change.Currency),
                checkEffectiveDate: false);
            if (stale is not null) return new ConflictObjectResult(new { error = "salary_change_stale", message = stale });

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
                return new ConflictObjectResult(new { error = "salary_change_stale", message = $"The salary dated {change.EffectiveDate:yyyy-MM-dd} has a percent-of-basic or in-kind allowance, or a Qiwa-confirmed contract, so an amount-only package cannot replace it. Use a different effective date or the contract renewal workflow." });
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
                await EmployeeSalaryBreakdownWriter.ApplyFirstPackageEffectsAsync(_db, employee, structure, change, ct);
            Audit(tenantId, actor, "payroll.employee_salary.assigned", "EmployeeSalaryStructure", assignment.Id.ToString(),
                new { employeeId = change.EmployeeId, basicSalary = change.BasicSalary, requestId = change.Id, requestedBy = change.RequestedByUserId, approvedBy = userId });
        }
        else
        {
            Audit(tenantId, actor, "payroll.employee_salary.change_rejected", "SalaryChangeRequest", change.Id.ToString(),
                new { employeeId = change.EmployeeId, requestedBy = change.RequestedByUserId, rejectedBy = userId });
        }
        change.Status = decision; change.DecidedByUserId = userId; change.DecidedByName = actor.UserName;
        change.DecidedAtUtc = DateTime.UtcNow; change.DecisionNote = (note ?? string.Empty).Trim();
        return null;
    }
}
