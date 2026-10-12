using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Learning;

/// <summary>Who to assign: explicit employees, or everyone currently in a department / designation / grade.</summary>
public sealed record AssignInput(Guid PathId, string? TargetKind, IReadOnlyList<int>? EmployeeIds, Guid? TargetId, DateOnly? DueDate, int? DueInDays,
    bool? IsMandatory);

/// <summary>created · skipped (already has a live assignment of this path) · refused (with reasons).</summary>
public sealed record AssignmentOutcome(int EmployeeId, string EmployeeName, string Outcome, IReadOnlyList<string> Reasons, Guid? AssignmentId);

public sealed record AssignmentBulkResult(int Created, int Skipped, int Refused, IReadOnlyList<AssignmentOutcome> Results);

public static class AssignmentOutcomes
{
    public const string Created = "created";
    public const string Skipped = "skipped";
    public const string Refused = "refused";
}

/// <summary>
/// Assignment (LRN-03): by employee, department, designation, grade or saved rule, plus the renewal cycle the sweep
/// opens (LRN-10). One live assignment per (employee, path) is enforced by the database — this service pre-checks for a
/// clean answer, and a concurrent duplicate that slips past the pre-check loses on the partial unique index and is
/// reported as skipped (a single-employee request gets 409 duplicate_assignment).
/// </summary>
public sealed class LearningAssignmentService(ZayraDbContext db, IAuditService audit, TimeProvider clock, ITenantClock tenantClock)
{
    public const string EmployeeTarget = "Employee";

    public async Task<AssignmentBulkResult> AssignAsync(LearningActor actor, AssignInput input, CancellationToken ct)
    {
        var kind = input.TargetKind is null ? EmployeeTarget
            : new[] { EmployeeTarget, LearningRuleTargets.Department, LearningRuleTargets.Designation, LearningRuleTargets.Grade }
                .FirstOrDefault(k => string.Equals(k, input.TargetKind, StringComparison.OrdinalIgnoreCase))
              ?? throw LearningException.Invalid("Assign to employees, a department, a designation or a grade.");
        var today = await tenantClock.TodayAsync(actor.TenantId, ct);
        if (input.DueDate is { } due && due < today)
            throw LearningException.Invalid("The due date cannot be in the past.");
        if (input.DueInDays is < 0 or > 3650) throw LearningException.Invalid("Due in days must be between 0 and 3650.");

        List<int> employeeIds;
        string source;
        if (kind == EmployeeTarget)
        {
            employeeIds = (input.EmployeeIds ?? []).Distinct().ToList();
            if (employeeIds.Count == 0) throw LearningException.Invalid("Choose at least one employee.");
            if (employeeIds.Count > 2000) throw LearningException.Invalid("Assign at most 2,000 employees at a time.");
            source = LearningAssignmentSources.Employee;
        }
        else
        {
            if (input.TargetId is not Guid target) throw LearningException.Invalid("Choose the department, designation or grade.");
            employeeIds = await TargetEmployeesAsync(actor.TenantId, kind, target, ct);
            source = kind;
        }

        var dueDate = input.DueDate ?? (input.DueInDays is int days ? today.AddDays(days) : null);
        var result = await AssignCoreAsync(actor, input.PathId, employeeIds, source, ruleId: null, renewalOf: null, cycle: null, dueDate,
            input.IsMandatory ?? true, ct);
        if (kind == EmployeeTarget && employeeIds.Count == 1 && result.Created == 0 && result.Skipped == 1)
            throw LearningException.Conflict("duplicate_assignment", "This employee already has this path assigned and not yet finished.",
                result.Results);
        return result;
    }

    /// <summary>Applies a saved rule to everyone it matches who has never had the path (cancelled ones excepted).</summary>
    public async Task<AssignmentBulkResult> ApplyRuleAsync(LearningActor actor, Guid ruleId, CancellationToken ct)
    {
        var rule = await db.LearningAssignmentRules.FirstOrDefaultAsync(r => r.TenantId == actor.TenantId && r.Id == ruleId, ct)
            ?? throw LearningException.NotFound("Rule");
        if (!rule.IsActive) return new AssignmentBulkResult(0, 0, 0, []);
        var matching = await TargetEmployeesAsync(actor.TenantId, rule.TargetKind, rule.TargetId, ct);
        // A rule assigns a path once per person: completing it does not make the rule assign it again (renewal does that).
        var already = await db.LearningAssignments.AsNoTracking()
            .Where(a => a.TenantId == actor.TenantId && a.PathId == rule.PathId && a.Status != LearningAssignmentStatuses.Cancelled)
            .Select(a => a.EmployeeId).Distinct().ToListAsync(ct);
        var fresh = matching.Except(already).ToList();
        var result = fresh.Count == 0
            ? new AssignmentBulkResult(0, 0, 0, [])
            : await AssignCoreAsync(actor, rule.PathId, fresh, LearningAssignmentSources.Rule, rule.Id, renewalOf: null, cycle: null,
                (await tenantClock.TodayAsync(actor.TenantId, ct)).AddDays(rule.DueInDays), rule.IsMandatory, ct);
        rule.LastAppliedAtUtc = clock.GetUtcNow().UtcDateTime;
        rule.Version++;
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>
    /// Opens the next cycle for an expiring credential (LRN-10). If the person already has a live assignment of the path
    /// (assigned by hand, or by a rule), that one is linked as the renewal instead of failing on the unique index.
    /// </summary>
    public async Task<Guid?> OpenRenewalAsync(LearningActor actor, LearningCredential credential, CancellationToken ct)
    {
        var live = await db.LearningAssignments.AsNoTracking().FirstOrDefaultAsync(a => a.TenantId == actor.TenantId
            && a.EmployeeId == credential.EmployeeId && a.PathId == credential.PathId && LearningAssignmentStatuses.Active.Contains(a.Status), ct);
        if (live is not null) return live.Id;

        var previous = await db.LearningAssignments.AsNoTracking()
            .Where(a => a.TenantId == actor.TenantId && a.EmployeeId == credential.EmployeeId && a.PathId == credential.PathId)
            .MaxAsync(a => (int?)a.Cycle, ct) ?? 0;
        var today = await tenantClock.TodayAsync(actor.TenantId, ct);
        var due = credential.ExpiresOn is { } expires && expires > today ? expires : today.AddDays(30);
        var result = await AssignCoreAsync(actor, credential.PathId, [credential.EmployeeId], LearningAssignmentSources.Renewal, ruleId: null,
            renewalOf: credential.Id, cycle: previous + 1, due, isMandatory: true, ct, renewalCourseCode: credential.CourseCode);
        var outcome = result.Results.Single();
        if (outcome.AssignmentId is Guid created) return created;
        // Lost a race to a concurrent assignment: link whatever is live now.
        return await db.LearningAssignments.AsNoTracking().Where(a => a.TenantId == actor.TenantId && a.EmployeeId == credential.EmployeeId
            && a.PathId == credential.PathId && LearningAssignmentStatuses.Active.Contains(a.Status)).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
    }

    public async Task CancelAsync(LearningActor actor, Guid assignmentId, string? reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500) throw LearningException.Invalid("Give a reason for cancelling (up to 500 characters).");
        var assignment = await db.LearningAssignments.FirstOrDefaultAsync(a => a.TenantId == actor.TenantId && a.Id == assignmentId, ct);
        if (assignment is null || !actor.Scope.CanAccessEmployee(assignment.EmployeeId)) throw LearningException.NotFound("Assignment");
        if (!LearningAssignmentStatuses.Active.Contains(assignment.Status))
            throw LearningException.Conflict("assignment_not_live", "Only an assignment that is not finished can be cancelled.");
        var now = clock.GetUtcNow().UtcDateTime;
        assignment.Status = LearningAssignmentStatuses.Cancelled;
        assignment.CancelledAtUtc = now;
        assignment.CancelReason = reason.Trim();
        assignment.Version++;
        foreach (var enrollment in await db.LearningEnrollments.Where(e => e.AssignmentId == assignment.Id && e.Status != LearningEnrollmentStatuses.Completed).ToListAsync(ct))
        {
            enrollment.Status = LearningEnrollmentStatuses.Cancelled;
            enrollment.Version++;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("learning.assignment.cancelled", nameof(LearningAssignment), assignment.Id.ToString(), actor.Request,
            JsonSerializer.Serialize(new { assignment.EmployeeId, assignment.PathId, reason = assignment.CancelReason }), ct);
    }

    // ── Core ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<AssignmentBulkResult> AssignCoreAsync(LearningActor actor, Guid pathId, IReadOnlyList<int> employeeIds, string source,
        Guid? ruleId, Guid? renewalOf, int? cycle, DateOnly? dueDate, bool isMandatory, CancellationToken ct, string? renewalCourseCode = null)
    {
        var path = await db.LearningPaths.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == actor.TenantId && p.Id == pathId, ct)
            ?? throw LearningException.NotFound("Learning path");
        var today = await tenantClock.TodayAsync(actor.TenantId, ct);
        if (path.Status != LearningPathStatuses.Active) throw LearningException.Conflict("path_not_active", "Only an active learning path can be assigned.");
        if ((path.EffectiveFrom is { } from && today < from) || (path.EffectiveTo is { } to && today > to))
            throw LearningException.Conflict("path_not_effective", "This learning path is outside its effective dates.");

        // Every item pins the course version that is published today (LRN-01).
        var items = await db.LearningPathItems.AsNoTracking().Where(i => i.PathId == path.Id).OrderBy(i => i.Sequence).ToListAsync(ct);
        var itemCourseIds = items.Select(i => i.CourseId).ToList();
        var itemCodes = await db.LearningCourses.AsNoTracking().Where(c => c.TenantId == actor.TenantId && itemCourseIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        var codes = itemCodes.Values.Distinct().ToList();
        var published = await db.LearningCourses.AsNoTracking()
            .Where(c => c.TenantId == actor.TenantId && codes.Contains(c.Code) && c.Status == LearningCourseStatuses.Published)
            .ToDictionaryAsync(c => c.Code, ct);
        var missing = codes.Where(c => !published.ContainsKey(c)).ToList();
        if (missing.Count > 0)
            throw LearningException.Conflict("path_course_unavailable", "A course in this path has no published version, so it cannot be assigned.", missing);

        // A renewal repeats only what expires: the items whose course carries a validity period. If the path has
        // none (or the credential's own course), it repeats the credential's course; never an empty cycle.
        var cycleItems = items;
        if (source == LearningAssignmentSources.Renewal)
        {
            cycleItems = items.Where(i => published[itemCodes[i.CourseId]].ValidityMonths is not null
                                          || itemCodes[i.CourseId] == renewalCourseCode).ToList();
            if (cycleItems.Count == 0) cycleItems = items;
        }

        var ids = employeeIds.Distinct().ToList();
        // The ambient company filter applies here too: an HR user scoped to one company cannot even load another's employee.
        var employees = await db.Employees.AsNoTracking().Where(e => e.TenantId == actor.TenantId && ids.Contains(e.Id) && !e.IsDeleted)
            .ToDictionaryAsync(e => e.Id, ct);
        var live = await db.LearningAssignments.AsNoTracking()
            .Where(a => a.TenantId == actor.TenantId && a.PathId == path.Id && ids.Contains(a.EmployeeId) && LearningAssignmentStatuses.Active.Contains(a.Status))
            .Select(a => a.EmployeeId).ToListAsync(ct);
        var cycles = await db.LearningAssignments.AsNoTracking()
            .Where(a => a.TenantId == actor.TenantId && a.PathId == path.Id && ids.Contains(a.EmployeeId))
            .GroupBy(a => a.EmployeeId).Select(g => new { g.Key, Max = g.Max(a => a.Cycle) }).ToDictionaryAsync(x => x.Key, x => x.Max, ct);
        var eligibility = LearningEligibilityCriteria.Parse(path.EligibilityJson);
        var now = clock.GetUtcNow().UtcDateTime;

        var outcomes = new List<AssignmentOutcome>();
        var toCreate = new List<(LearningAssignment Assignment, List<LearningEnrollment> Enrollments, string Name)>();
        foreach (var id in ids)
        {
            if (!employees.TryGetValue(id, out var employee) || !actor.Scope.CanAccessEmployee(id))
            {
                // Not found and not in scope look the same: an out-of-scope id is not confirmed to exist.
                outcomes.Add(new AssignmentOutcome(id, string.Empty, AssignmentOutcomes.Refused, ["employee_not_in_scope"], null));
                continue;
            }
            var reasons = new List<string>();
            if (employee.Status != EmployeeStatuses.Active) reasons.Add("employee_not_active");
            reasons.AddRange(eligibility.Reasons(employee));
            if (reasons.Count > 0)
            {
                outcomes.Add(new AssignmentOutcome(id, employee.FullName, AssignmentOutcomes.Refused, reasons, null));
                continue;
            }
            if (live.Contains(id))
            {
                outcomes.Add(new AssignmentOutcome(id, employee.FullName, AssignmentOutcomes.Skipped, ["already_assigned"], null));
                continue;
            }

            var assignment = new LearningAssignment
            {
                TenantId = actor.TenantId, CompanyId = employee.CompanyId, EmployeeId = id, PathId = path.Id,
                Cycle = cycle ?? (cycles.TryGetValue(id, out var max) ? max + 1 : 1),
                Source = source, RuleId = ruleId, RenewalOfCredentialId = renewalOf, Status = LearningAssignmentStatuses.Assigned,
                IsMandatory = isMandatory, DueDate = dueDate, DepartmentId = employee.DepartmentId, DesignationId = employee.DesignationId,
                GradeId = employee.GradeId, AssignedAtUtc = now, AssignedByUserId = actor.UserId,
            };
            var enrollments = cycleItems.Select(item =>
            {
                var course = published[itemCodes[item.CourseId]];
                return new LearningEnrollment
                {
                    TenantId = actor.TenantId, CompanyId = employee.CompanyId, AssignmentId = assignment.Id, EmployeeId = id, PathItemId = item.Id,
                    CourseId = course.Id, Sequence = item.Sequence, IsRequired = item.IsRequired,
                    AttemptAllowance = course.CompletionRule == LearningCompletionRules.Assessment ? course.MaxAttempts ?? 1 : 0,
                };
            }).ToList();
            toCreate.Add((assignment, enrollments, employee.FullName));
        }

        if (toCreate.Count > 0)
        {
            foreach (var (assignment, enrollments, _) in toCreate)
            {
                db.LearningAssignments.Add(assignment);
                db.LearningEnrollments.AddRange(enrollments);
            }
            try
            {
                await db.SaveChangesAsync(ct);
                outcomes.AddRange(toCreate.Select(x => new AssignmentOutcome(x.Assignment.EmployeeId, x.Name, AssignmentOutcomes.Created, [], x.Assignment.Id)));
            }
            catch (DbUpdateException ex) when (LearningDb.IsUniqueViolation(ex))
            {
                // A concurrent request assigned some of these people between the pre-check and the insert. Retry one
                // by one so everyone else is still assigned and the losers are reported, not failed.
                LearningDb.DetachAdded(db);
                foreach (var (assignment, enrollments, name) in toCreate)
                {
                    db.LearningAssignments.Add(assignment);
                    db.LearningEnrollments.AddRange(enrollments);
                    try
                    {
                        await db.SaveChangesAsync(ct);
                        outcomes.Add(new AssignmentOutcome(assignment.EmployeeId, name, AssignmentOutcomes.Created, [], assignment.Id));
                    }
                    catch (DbUpdateException inner) when (LearningDb.IsUniqueViolation(inner))
                    {
                        LearningDb.DetachAdded(db);
                        outcomes.Add(new AssignmentOutcome(assignment.EmployeeId, name, AssignmentOutcomes.Skipped, ["already_assigned"], null));
                    }
                }
            }
        }

        foreach (var created in outcomes.Where(o => o.Outcome == AssignmentOutcomes.Created))
            await audit.WriteAsync("learning.assignment.created", nameof(LearningAssignment), created.AssignmentId!.Value.ToString(), actor.Request,
                JsonSerializer.Serialize(new { created.EmployeeId, pathId = path.Id, source, ruleId, renewalOf, dueDate }), ct);

        var ordered = outcomes.OrderBy(o => o.Outcome).ThenBy(o => o.EmployeeName).ToList();
        return new AssignmentBulkResult(
            ordered.Count(o => o.Outcome == AssignmentOutcomes.Created),
            ordered.Count(o => o.Outcome == AssignmentOutcomes.Skipped),
            ordered.Count(o => o.Outcome == AssignmentOutcomes.Refused),
            ordered);
    }

    /// <summary>Active employees currently in the target (company, department, designation or grade).</summary>
    internal async Task<List<int>> TargetEmployeesAsync(Guid tenantId, string kind, Guid target, CancellationToken ct)
    {
        var query = db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && !e.IsDeleted && e.Status == EmployeeStatuses.Active);
        query = kind switch
        {
            LearningRuleTargets.Company => query.Where(e => e.CompanyId == target),
            LearningRuleTargets.Department => query.Where(e => e.DepartmentId == target),
            LearningRuleTargets.Designation => query.Where(e => e.DesignationId == target),
            LearningRuleTargets.Grade => query.Where(e => e.GradeId == target),
            _ => throw LearningException.Invalid("Unknown target."),
        };
        return await query.Select(e => e.Id).ToListAsync(ct);
    }

}
