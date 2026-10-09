using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Organization;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Benefits;

/// <summary>Restricted grade-default decision evidence; never serializes an Employee entity.</summary>
public sealed record GradeBenefitDefaultWitness(Guid? GradeId,
    [property: JsonPropertyName("defaultBenefit")] GradeBenefitDefaultDto DefaultBenefit)
{
    public static string Serialize(Guid? gradeId, GradeBenefitDefaultDto benefit) =>
        JsonSerializer.Serialize(new GradeBenefitDefaultWitness(gradeId, benefit));
}

public sealed record GradeBenefitDraftPlacement(Guid? GradeId, Guid? CompanyId, DateOnly JoiningDate,
    DateOnly? ConfirmationDate, DateOnly? ProbationEndDate)
{
    public static GradeBenefitDraftPlacement Capture(Employee employee) => new(employee.GradeId, employee.CompanyId,
        DateOnly.FromDateTime(employee.JoiningDate), employee.ConfirmationDate, employee.ProbationEndDate);
}

public static class GradeBenefitDefaults
{
    public const string SupersededStatus = "Superseded";
    private sealed record Catalog(IReadOnlyList<BenefitPlan> Plans, IReadOnlyList<BenefitEligibilityRule> Rules, IReadOnlyDictionary<Guid, int> GradeLevels);

    /// <summary>Replaces only untouched automatic draft defaults. The caller owns the transaction and
    /// holds the employee row and benefits locks before reading or editing the employee.</summary>
    public static async Task ReconcileDraftAsync(ZayraDbContext db, Employee employee,
        GradeBenefitDraftPlacement previousPlacement, Guid? actorId, CancellationToken ct)
    {
        var currentPlacement = GradeBenefitDraftPlacement.Capture(employee);
        if (previousPlacement == currentPlacement || employee.TenantId is not Guid tenantId
            || employee.ActivatedAtUtc is not null || !string.Equals(employee.Status, EmployeeStatuses.Draft, StringComparison.OrdinalIgnoreCase)) return;
        var occupyingStatuses = EstablishmentOccupancy.OccupyingStatuses.Select(x => x.ToLowerInvariant()).ToArray();
        if (await db.EmployeeStatusHistories.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == employee.Id
            && (occupyingStatuses.Contains(x.OldStatus.ToLower()) || occupyingStatuses.Contains(x.NewStatus.ToLower())), ct)) return;
        if (await Entitlements.EntitlementMatrixService.ReleaseAEnabledAsync(db, tenantId, ct)) return;

        var defaults = await db.BenefitEnrollments.Where(x => x.TenantId == tenantId && x.EmployeeId == employee.Id
            && x.AssignmentSource == "GradeDefault" && x.Status != SupersededStatus).ToListAsync(ct);
        const string protectedMessage = "This draft has a grade benefit with an individual exception, waiver, or financial history. "
            + "HR must resolve that benefit through its governed review before changing the draft's grade, company, joining date, or probation dates.";
        if (defaults.Any(x => !IsUntouchedDefault(x))) throw new InvalidOperationException(protectedMessage);
        var ids = defaults.Select(x => x.Id).ToArray();
        if (ids.Length > 0)
        {
            if (await db.BenefitContributions.AnyAsync(x => x.TenantId == tenantId && ids.Contains(x.BenefitEnrollmentId), ct)
                || await db.BenefitPayrollDeductionLinks.AnyAsync(x => x.TenantId == tenantId && ids.Contains(x.BenefitEnrollmentId), ct))
                throw new InvalidOperationException(protectedMessage);
            var payroll = await db.PayrollAdjustments.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == employee.Id
                && x.SourceType == BenefitPayroll.RecurringSource).ToListAsync(ct);
            if (payroll.Any(x => BenefitPayroll.Read(x) is not { } witness || ids.Contains(witness.ChainId)))
                throw new InvalidOperationException(protectedMessage);
            var claims = await db.ApprovalRequests.AsNoTracking().Where(x => x.TenantId == tenantId
                && x.RequestedForEmployeeId == employee.Id && x.EntityName == BenefitClaims.EntityName
                && (x.Status == "Pending" || x.Status == "Approved")).ToListAsync(ct);
            if (claims.Any(x => BenefitClaims.Read(x) is { } claim && (ids.Contains(claim.EnrollmentId) || ids.Contains(claim.EnrollmentChainId))))
                throw new InvalidOperationException(protectedMessage);
        }

        var now = DateTime.UtcNow;
        foreach (var row in defaults)
        {
            // Preserve agreed dates and both immutable snapshots as evidence; these rows never become payable again.
            row.Status = SupersededStatus;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = actorId;
            db.AuditLogs.Add(new AuditLog
            {
                TenantId = tenantId, CompanyId = row.CompanyId, UserId = actorId,
                EntityName = nameof(BenefitEnrollment), EntityId = row.Id.ToString(),
                Action = "benefits.grade_default.superseded",
                Metadata = JsonSerializer.Serialize(new { EmployeeId = employee.Id, PreviousPlacement = previousPlacement, CurrentPlacement = currentPlacement }),
            });
        }
        await StageDefaultsAsync(db, employee, actorId, ct);
    }

    private static bool IsUntouchedDefault(BenefitEnrollment row)
    {
        if (row.Status != "Active" || row.HasException || row.OriginalEnrollmentId.HasValue || row.ApprovalRequestId.HasValue
            || !string.IsNullOrWhiteSpace(row.ExceptionReason) || row.CoverageTier != "Employee"
            || row.RequestedBenefitAmount.HasValue || row.ReviewDate.HasValue) return false;
        try
        {
            var witness = JsonSerializer.Deserialize<GradeBenefitDefaultWitness>(row.EligibilitySnapshotJson);
            var original = witness?.DefaultBenefit;
            return original is not null && original.BenefitPlanId == row.BenefitPlanId
                && original.EligibilityRuleId == row.EligibilityRuleId && (original.EntitlementTier ?? string.Empty) == row.EntitlementTier
                && original.MaximumBenefitAmount == row.MaximumBenefitAmount
                && (original.LimitPeriod ?? BenefitLimitPeriods.PerEnrollment) == row.LimitPeriod
                && original.EffectiveFrom == row.EffectiveFrom && original.EffectiveTo == row.EffectiveTo;
        }
        catch (JsonException) { return false; }
    }

    private static async Task<Catalog> LoadAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct) => new(
        await db.BenefitPlans.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted).OrderBy(x => x.Name).ToListAsync(ct),
        await db.BenefitEligibilityRules.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive && x.GradeId.HasValue).ToListAsync(ct),
        await db.Grades.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive && !x.IsDeleted).ToDictionaryAsync(x => x.Id, x => x.Level, ct));

    /// <summary>Stages defaults in the caller's hire transaction. Does not save, create contributions, or disburse loans.</summary>
    public static Task<IReadOnlyList<BenefitEnrollment>> StageDefaultsAsync(
        ZayraDbContext db, Employee employee, Guid? actorId, CancellationToken ct) =>
        StageDefaultsForEmployeesAsync(db, [employee], actorId, ct);

    /// <summary>One catalogue read per tenant and one existing-enrolment query for the whole import batch.</summary>
    public static async Task<IReadOnlyList<BenefitEnrollment>> StageDefaultsForEmployeesAsync(
        ZayraDbContext db, IReadOnlyList<Employee> employees, Guid? actorId, CancellationToken ct)
    {
        var staged = new List<BenefitEnrollment>();
        foreach (var batch in employees.Where(x => x.TenantId.HasValue && x.GradeId.HasValue && x.JoiningDate != default).GroupBy(x => x.TenantId!.Value))
        {
            var tenantId = batch.Key;
            if (await Entitlements.EntitlementMatrixService.ReleaseAEnabledAsync(db, tenantId, ct)) continue;
            var catalog = await LoadAsync(db, tenantId, ct);
            var ids = batch.Select(x => x.Id).ToArray();
            var existing = await db.BenefitEnrollments.AsNoTracking()
                .Where(x => x.TenantId == tenantId && ids.Contains(x.EmployeeId)).ToListAsync(ct);
            var local = db.BenefitEnrollments.Local.Where(x => x.TenantId == tenantId && ids.Contains(x.EmployeeId)).ToList();
            var localIds = local.Select(x => x.Id).ToHashSet();
            existing.RemoveAll(x => localIds.Contains(x.Id));
            existing.AddRange(local);
            foreach (var employee in batch)
            foreach (var item in Preview(catalog, employee, DateOnly.FromDateTime(employee.JoiningDate)).Where(x => x.Eligible))
            {
                // A previous assignment or waiver is intentional. Replaying a hire must never duplicate or undo it.
                if (existing.Any(x => x.EmployeeId == employee.Id && x.BenefitPlanId == item.BenefitPlanId
                    && !(x.AssignmentSource == "GradeDefault" && x.Status == SupersededStatus && !x.HasException)
                    && (!item.EffectiveTo.HasValue || x.EffectiveFrom <= item.EffectiveTo)
                    && (!x.EffectiveTo.HasValue || x.EffectiveTo >= item.EffectiveFrom))) continue;
                var row = new BenefitEnrollment
                {
                    TenantId = tenantId, CompanyId = employee.CompanyId, EmployeeId = employee.Id,
                    EmployeeName = employee.FullName, BenefitPlanId = item.BenefitPlanId,
                    EligibilityRuleId = item.EligibilityRuleId, EntitlementTier = item.EntitlementTier ?? string.Empty,
                    MaximumBenefitAmount = item.MaximumBenefitAmount, LimitPeriod = item.LimitPeriod ?? BenefitLimitPeriods.PerEnrollment,
                    EffectiveFrom = item.EffectiveFrom, EffectiveTo = item.EffectiveTo,
                    AssignmentSource = "GradeDefault", CreatedBy = actorId,
                    PaymentPolicySnapshotJson = BenefitPaymentPolicies.Snapshot(catalog.Plans.First(x => x.Id == item.BenefitPlanId)),
                    EligibilitySnapshotJson = GradeBenefitDefaultWitness.Serialize(employee.GradeId, item),
                };
                db.BenefitEnrollments.Add(row);
                db.AuditLogs.Add(new AuditLog
                {
                    TenantId = tenantId, CompanyId = employee.CompanyId, UserId = actorId,
                    EntityName = nameof(BenefitEnrollment), EntityId = row.Id.ToString(),
                    Action = "benefits.grade_default.assigned", Metadata = row.EligibilitySnapshotJson,
                });
                existing.Add(row);
                staged.Add(row);
            }
        }
        return staged;
    }

    public static async Task<IReadOnlyList<GradeBenefitDefaultDto>> PreviewAsync(
        ZayraDbContext db, Guid tenantId, Employee employee, DateOnly date, CancellationToken ct) =>
        Preview(await LoadAsync(db, tenantId, ct), employee, date);

    private static bool MatchesGrade(BenefitEligibilityRule rule, Employee employee, IReadOnlyDictionary<Guid, int> levels) =>
        employee.GradeId.HasValue && levels.TryGetValue(employee.GradeId.Value, out var level)
        && rule.GradeId.HasValue && levels.TryGetValue(rule.GradeId.Value, out var threshold)
        && (rule.GradeMatchMode == BenefitGradeMatchModes.Exact ? rule.GradeId == employee.GradeId
            : rule.GradeMatchMode == BenefitGradeMatchModes.LevelAndAbove && level >= threshold);

    private static IReadOnlyList<GradeBenefitDefaultDto> Preview(Catalog catalog, Employee employee, DateOnly date)
    {
        var result = new List<GradeBenefitDefaultDto>();
        foreach (var plan in catalog.Plans.Where(x => (!x.CompanyId.HasValue || x.CompanyId == employee.CompanyId)
            && (!x.EffectiveTo.HasValue || x.EffectiveTo >= date)))
        {
            var rules = catalog.Rules.Where(x => x.BenefitPlanId == plan.Id && (!x.CompanyId.HasValue || x.CompanyId == employee.CompanyId)
                && MatchesGrade(x, employee, catalog.GradeLevels) && (!x.EffectiveTo.HasValue || x.EffectiveTo >= date)).ToList();
            if (rules.Count == 0 && plan.Classification != BenefitPlanClassifications.Mandatory) continue;
            var earliest = date > plan.EffectiveFrom ? date : plan.EffectiveFrom;
            var dates = new SortedSet<DateOnly> { earliest };
            foreach (var rule in rules)
            {
                var next = earliest > rule.EffectiveFrom ? earliest : rule.EffectiveFrom;
                if (employee.JoiningDate != default)
                {
                    var serviceDate = DateOnly.FromDateTime(employee.JoiningDate).AddMonths(rule.MinimumServiceMonths);
                    if (serviceDate > next) next = serviceDate;
                }
                if (rule.RequireProbationCompleted)
                {
                    var completed = employee.ConfirmationDate;
                    if (employee.ProbationEndDate.HasValue)
                    {
                        var probationComplete = employee.ProbationEndDate.Value.AddDays(1);
                        if (!completed.HasValue || probationComplete < completed) completed = probationComplete;
                    }
                    if (!completed.HasValue) continue;
                    if (completed > next) next = completed.Value;
                }
                if ((!rule.EffectiveTo.HasValue || next <= rule.EffectiveTo) && (!plan.EffectiveTo.HasValue || next <= plan.EffectiveTo)) dates.Add(next);
            }
            var start = earliest;
            var evaluated = Evaluate(plan, employee, start, catalog.Rules, catalog.GradeLevels);
            foreach (var candidateDate in dates)
            {
                var candidate = Evaluate(plan, employee, candidateDate, catalog.Rules, catalog.GradeLevels);
                if (!candidate.Eligible) continue;
                start = candidateDate; evaluated = candidate; break;
            }
            if (!evaluated.Eligible && !employee.ConfirmationDate.HasValue && !employee.ProbationEndDate.HasValue
                && rules.Any(x => x.RequireProbationCompleted))
                evaluated = evaluated with { BlockingReason = "Set the employee's probation end or confirmation date to schedule this benefit tier." };
            var ruleEnd = catalog.Rules.FirstOrDefault(x => x.Id == evaluated.MatchedRuleId)?.EffectiveTo;
            var end = !plan.EffectiveTo.HasValue ? ruleEnd : !ruleEnd.HasValue ? plan.EffectiveTo
                : plan.EffectiveTo < ruleEnd ? plan.EffectiveTo : ruleEnd;
            result.Add(new(plan.Id, plan.Code, plan.Name, plan.PlanType, plan.Currency, evaluated.Eligible,
                evaluated.BlockingReason, evaluated.MatchedRuleId, evaluated.TierName, evaluated.MaximumBenefitAmount,
                evaluated.LimitPeriod, start, end));
        }
        return result;
    }

    /// <summary>
    /// The single enrolment eligibility evaluator: plan company scope, plan effective window, then the
    /// plan's active company/grade effective-dated rules. Contractual and discretionary plans are
    /// grade-based; a mandatory minimum plan cannot exclude an employee because a grade is missing.
    /// Every check is reported; the first failing one supplies the blocking reason (the exact message
    /// <see cref="BenefitsController.Enroll"/> returns as its 400).
    /// </summary>
    public static async Task<BenefitEligibilityEvaluation> EvaluateAsync(ZayraDbContext db, Guid tenantId, BenefitPlan plan, Employee employee, DateOnly date, CancellationToken ct)
    {
        var catalog = await LoadAsync(db, tenantId, ct);
        return Evaluate(plan, employee, date, catalog.Rules, catalog.GradeLevels);
    }

    private static BenefitEligibilityEvaluation Evaluate(BenefitPlan plan, Employee employee, DateOnly date,
        IReadOnlyList<BenefitEligibilityRule> allRules, IReadOnlyDictionary<Guid, int> gradeLevels)
    {
        var checks = new List<BenefitEligibilityCheckItem>();
        string? blocking = null;

        var active = plan.IsActive;
        checks.Add(new("plan_active", "Plan is active", active, active ? "Plan is open for enrolment." : "Plan is inactive; reactivate it before enrolling."));
        if (!active) blocking ??= "Benefit plan is inactive.";

        var companyOk = !plan.CompanyId.HasValue || plan.CompanyId == employee.CompanyId;
        checks.Add(new("company_scope", "Employee's company is in the plan's scope", companyOk,
            plan.CompanyId.HasValue ? (companyOk ? "Plan is limited to the employee's company." : "Plan is limited to a different company.") : "Plan applies to every company."));
        if (!companyOk) blocking ??= "Employee company is not eligible for this benefit plan.";

        var windowOk = date >= plan.EffectiveFrom && (!plan.EffectiveTo.HasValue || date <= plan.EffectiveTo.Value);
        checks.Add(new("plan_window", "Start date is inside the plan's effective period", windowOk,
            $"Plan runs {plan.EffectiveFrom:yyyy-MM-dd} to {(plan.EffectiveTo.HasValue ? plan.EffectiveTo.Value.ToString("yyyy-MM-dd") : "open-ended")}; requested start {date:yyyy-MM-dd}."));
        if (!windowOk) blocking ??= "Enrollment effective date is outside the benefit plan effective period.";

        var rules = allRules.Where(x => x.BenefitPlanId == plan.Id && x.EffectiveFrom <= date
            && (!x.EffectiveTo.HasValue || x.EffectiveTo >= date)).ToList();
        var mandatoryFloor = plan.Classification == BenefitPlanClassifications.Mandatory;
        var hasGrade = employee.GradeId.HasValue;
        checks.Add(new("employee_grade", "Employee has an assigned grade", mandatoryFloor || hasGrade,
            mandatoryFloor
                ? "Mandatory minimum coverage cannot be denied because a grade is missing."
                : hasGrade ? "Employee has a grade for benefit eligibility." : "Assign the employee a grade before enrolling them in benefits."));
        if (!mandatoryFloor && !hasGrade) blocking ??= "Employee must have a grade before benefit eligibility can be evaluated.";

        var hasRules = rules.Count > 0;
        checks.Add(new("grade_rules_configured", "Plan has a grade eligibility rule in effect", mandatoryFloor || hasRules,
            mandatoryFloor
                ? "Grade rules may select an enhanced tier, but cannot remove mandatory minimum coverage."
                : hasRules ? $"{rules.Count} grade rule(s) are in effect on the requested date." : "Add at least one active grade rule for the requested date."));
        if (!mandatoryFloor && !hasRules) blocking ??= "Benefit plan has no grade eligibility rule in effect for the requested date.";

        var candidates = rules
            .Where(x => (!x.CompanyId.HasValue || x.CompanyId == employee.CompanyId) && MatchesGrade(x, employee, gradeLevels))
            .OrderByDescending(x => x.CompanyId.HasValue)
            .ThenByDescending(x => x.GradeMatchMode == BenefitGradeMatchModes.Exact)
            .ThenByDescending(x => gradeLevels.GetValueOrDefault(x.GradeId!.Value))
            .ThenByDescending(x => x.EffectiveFrom)
            .ThenByDescending(x => x.CreatedAtUtc)
            .ToList();
        var matched = candidates.FirstOrDefault();
        var serviceOk = matched is null || matched.MinimumServiceMonths == 0
            || (employee.JoiningDate != default && DateOnly.FromDateTime(employee.JoiningDate).AddMonths(matched.MinimumServiceMonths) <= date);
        var probationOk = matched is null || !matched.RequireProbationCompleted
            || (employee.ConfirmationDate.HasValue && employee.ConfirmationDate.Value <= date)
            || (employee.ProbationEndDate.HasValue && employee.ProbationEndDate.Value < date);
        var ruleOk = mandatoryFloor || matched is not null && serviceOk && probationOk;
        checks.Add(new("eligibility_rules", "Matches an effective grade eligibility rule", ruleOk,
            mandatoryFloor
                ? matched is null ? "Mandatory minimum coverage applies; no grade enhancement matched."
                    : $"Mandatory minimum coverage applies; grade enhancement tier '{matched.TierName}' matched."
                : matched is not null
                ? $"Matched tier '{matched.TierName}' using {matched.GradeMatchMode} grade logic."
                : hasRules
                    ? $"None of the {rules.Count} grade rule(s) in effect match the employee's company and grade."
                    : "No grade rule is available to match."));
        if (!mandatoryFloor && matched is null) blocking ??= "Employee is not eligible for this benefit plan based on grade eligibility rules.";

        if (matched is not null)
        {
            checks.Add(new("minimum_service", "Minimum service requirement", mandatoryFloor || serviceOk,
                matched.MinimumServiceMonths == 0 ? "No minimum service period is configured."
                    : serviceOk ? $"Employee has completed the required {matched.MinimumServiceMonths} service month(s)."
                    : $"Employee must complete {matched.MinimumServiceMonths} service month(s) for this tier."));
            if (!mandatoryFloor && !serviceOk) blocking ??= $"Employee has not completed the required {matched.MinimumServiceMonths} service month(s).";
            checks.Add(new("probation", "Probation requirement", mandatoryFloor || probationOk,
                !matched.RequireProbationCompleted ? "Probation completion is not required."
                    : probationOk ? "Employee has completed probation."
                    : "Employee must complete probation for this tier."));
            if (!mandatoryFloor && !probationOk) blocking ??= employee.ConfirmationDate.HasValue || employee.ProbationEndDate.HasValue
                ? "Employee must complete probation for this benefit tier."
                : "Set the employee's probation end or confirmation date to schedule this benefit tier.";
        }

        var resolved = matched is not null && serviceOk && probationOk ? matched : null;
        return new BenefitEligibilityEvaluation(
            blocking is null,
            blocking,
            resolved?.Id,
            resolved?.TierName,
            resolved?.MaxBenefitAmount,
            resolved?.LimitPeriod,
            resolved?.CustomCriteriaNote,
            checks);
    }

    public sealed record BenefitEligibilityEvaluation(
        bool Eligible,
        string? BlockingReason,
        Guid? MatchedRuleId,
        string? TierName,
        decimal? MaximumBenefitAmount,
        string? LimitPeriod,
        string? CustomCriteriaNote,
        IReadOnlyList<BenefitEligibilityCheckItem> Checks);

}
