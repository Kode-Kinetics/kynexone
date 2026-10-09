using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Auth;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Benefits;

public record AdditionalBenefitGrantRequest(
    int EmployeeId, Guid BenefitPlanId, string CoverageTier, string EntitlementTier,
    decimal? MaximumBenefitAmount, decimal? RequestedBenefitAmount, string LimitPeriod,
    DateOnly EffectiveFrom, DateOnly? EffectiveTo, DateOnly? ReviewDate, string Reason, string InternalJustification,
    Guid? EnrollmentId = null, DateTime? ExpectedUpdatedAtUtc = null, string Treatment = "Coverage",
    decimal? PlannedEmployerCost = null, decimal? PlannedEmployeeCost = null, string? CostFrequency = null);

public record AdditionalBenefitProposal(int Version, Guid RequestId, Guid TenantId, Guid? CompanyId, Guid? GradeId,
    string EmployeeName, string PlanName, string Currency, AdditionalBenefitGrantRequest Terms, BenefitEnrollmentDto? Baseline, string PlanClassification = BenefitPlanClassifications.Discretionary, string? WorkflowSha256 = null);
public record AdditionalBenefitRequestDto(Guid Id, string Status, int EmployeeId, string EmployeeName, Guid BenefitPlanId,
    string PlanName, string Currency, string? RequestedByName, DateTime CreatedAtUtc, AdditionalBenefitGrantRequest Terms,
    BenefitEnrollmentDto? Baseline, Guid ApprovalRequestId, Guid? AppliedEnrollmentId, ApprovalRequestDto? Approval = null);

/// <summary>Additional entitlement proposals reuse the shared approval witness and decision ledger.
/// Only final approval stages an enrollment; no money, contribution or deduction is created by a grant.</summary>
public static class AdditionalBenefitGrants
{
    public const string EntityName = "BenefitAdditionalGrant";
    public const string Source = "IndividualAdditional";
    public const string LockScope = "benefits.enrollment";
    public static readonly string[] Treatments = ["Coverage", "CashAllowance", "Reimbursement", "LoanEligibility", "OtherNonCash"];
    public static readonly string[] CostFrequencies = ["OneTime", "Monthly", "Annual"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool IsAdditional(ApprovalRequest approval) => string.Equals(approval.EntityName, EntityName, StringComparison.OrdinalIgnoreCase);

    public static EntityScopeContext ResolveScope(RequestContext context, IHttpContextAccessor? http)
    {
        var principal = http?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true || context.UserId is null
            || principal.FindFirstValue(ClaimTypes.NameIdentifier) != context.UserId.ToString()
            || principal.FindFirstValue("tenant_id") != context.TenantId?.ToString()) return EntityScopeContext.Empty;
        return EntityScopeContext.FromClaims(principal, strictMode: true)
            .NarrowTo(http!.HttpContext!.Request.Headers[ZayraDbContext.CompanySelectionHeader].FirstOrDefault());
    }

    public static async Task<ApprovalRequest> SubmitAsync(ZayraDbContext db, IApprovalRouter router, Guid tenantId,
        AdditionalBenefitGrantRequest input, RequestContext context, ITenantClock clock, CancellationToken ct,
        EntityScopeContext? scope = null)
    {
        EnsureActor(context, tenantId, "employees.write");
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == input.EmployeeId && !e.IsDeleted, ct)
            ?? throw new InvalidOperationException("Employee not found in your company scope.");
        if (!(scope ?? EntityScopeContext.Empty).CanAccessCompany(employee.CompanyId)) throw new InvalidOperationException("This employee is outside your company scope.");
        var requestId = Guid.NewGuid();
        return await FinanceDecisionSerializer.SerializeAsync(db, LockScope, tenantId, employee.PublicId, async () =>
        {
            // Stable request id makes an acknowledged-but-retried commit an idempotent replay.
            var existing = await db.ApprovalRequests.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == requestId, ct);
            if (existing is not null) return existing;
            employee = await db.Employees.AsNoTracking().FirstAsync(e => e.TenantId == tenantId && e.Id == input.EmployeeId && !e.IsDeleted, ct);
            if (!(scope ?? EntityScopeContext.Empty).CanAccessCompany(employee.CompanyId)) throw new InvalidOperationException("This employee is outside your company scope.");
            var terms = Normalize(input);
            var plan = await ValidateTermsAsync(db, tenantId, employee, terms, clock, ct);
            var baseline = await ValidateBaselineAsync(db, tenantId, employee, terms, ct);
            await EnsureNoOverlapAsync(db, tenantId, terms, null, ct);
            var route = await router.ResolveAsync(tenantId, employee.Id, EntityName, ct);
            await FinanceDecisionSerializer.AcquireAsync(db, "approval.workflow", tenantId, route.WorkflowId, ct);
            var currentRoute = await router.ResolveAsync(tenantId, employee.Id, EntityName, ct);
            if (currentRoute.WorkflowId != route.WorkflowId) throw new InvalidOperationException("The approval route changed during submission. Retry with the current workflow.");
            route = currentRoute;
            if (route.Steps.Any(x => string.IsNullOrWhiteSpace(x.ApproverRole) || x.ApproverRole.Equals("Any", StringComparison.OrdinalIgnoreCase)))
                throw new ApprovalRouteInvalidException(tenantId, EntityName, route.WorkflowId, route.Code, "every benefit approval step must name an explicit approver role.");
            var first = route.FirstStep;
            var approver = await router.ResolveApproverAsync(tenantId, employee.Id, first, ct);
            var proposal = new AdditionalBenefitProposal(1, requestId, tenantId, employee.CompanyId, employee.GradeId,
                employee.FullName, plan.Name, plan.Currency, terms, baseline is null ? null : BenefitEnrollmentDto.From(baseline), plan.Classification, WorkflowDigest(route.Steps));
            var payload = JsonSerializer.Serialize(proposal, Json);
            var now = DateTime.UtcNow;
            var approval = new ApprovalRequest
            {
                Id = requestId, TenantId = tenantId, EntityName = EntityName, EntityId = requestId.ToString(),
                WorkflowId = route.WorkflowId, CompanyId = employee.CompanyId, RequestedForEmployeeId = employee.Id,
                RequestedByUserId = context.UserId, Title = $"{(baseline is null ? "Additional benefit" : "Benefit amendment")}: {plan.Name} · {employee.FullName}",
                Payload = payload, PayloadSha256 = Digest(payload), CurrentStepOrder = first.StepOrder,
                CurrentApproverType = approver.EmployeeId.HasValue ? approver.ApproverType : "Role",
                CurrentApproverEmployeeId = approver.EmployeeId, CurrentApproverUserId = approver.UserId,
                CurrentApproverName = approver.Name, CurrentApproverRole = approver.QueueRole,
                CurrentQueue = approver.EmployeeId.HasValue ? $"{approver.ApproverType}:{approver.Name}" : $"Role:{approver.QueueRole}",
                SlaHours = Math.Clamp(first.EscalationAfterHours ?? 24, 1, 720), LastRoutedAtUtc = now,
                DueAtUtc = now.AddHours(Math.Clamp(first.EscalationAfterHours ?? 24, 1, 720)),
            };
            db.ApprovalRequests.Add(approval);
            StageAudit(db, approval, context.UserId, "benefits.additional.requested", new { proposal, route.WorkflowId, route.MatchedOn });
            await db.SaveChangesAsync(ct);
            return approval;
        }, ct);
    }

    public static async Task ValidateDecisionAsync(ZayraDbContext db, ApprovalRequest approval, RequestContext context,
        CancellationToken ct, EntityScopeContext? scope = null)
    {
        if (!IsAdditional(approval)) return;
        EnsureActor(context, approval.TenantId, "employees.approve");
        if (!(scope ?? EntityScopeContext.Empty).CanAccessCompany(approval.CompanyId)) throw new InvalidOperationException("This benefit request is outside your company scope.");
        var proposal = Read(approval);
        if (approval.RequestedByUserId == context.UserId || await SubjectDecisionBar.CallerIsSubjectAsync(db, approval.TenantId, context.UserId, proposal.Terms.EmployeeId, ct)
            || await ScopedBypass.TenantWide(db.EmployeeUserAccounts, approval.TenantId,
                "Benefit approval separation of duties: retain historical beneficiary account links, including deleted links, for this tenant and exact subject; no account data is returned.")
                .AsNoTracking().AnyAsync(x => x.EmployeeId == proposal.Terms.EmployeeId && x.UserId == context.UserId, ct))
            throw new InvalidOperationException("The requester and benefiting employee cannot decide this benefit request. Another authorized person must decide it.");
        var steps = await db.ApprovalWorkflowSteps.AsNoTracking().Where(x => x.TenantId == approval.TenantId && x.WorkflowId == approval.WorkflowId).ToListAsync(ct);
        var stepWitness = steps.Select(x => new ApprovalRouteStep(x.StepOrder, x.StepName, string.IsNullOrWhiteSpace(x.ApproverType) ? "Role" : x.ApproverType.Trim(),
            x.ApproverRole?.Trim() ?? "", x.SpecificEmployeeId, x.EscalationAfterHours, x.IsFinalStep)).ToList();
        if (proposal.WorkflowSha256 is null || proposal.WorkflowSha256 != WorkflowDigest(stepWitness))
            throw new InvalidOperationException("The approval workflow changed after this benefit was submitted. Withdraw and submit a new request for the current route.");
        if (await EntitlementMatrixService.ReleaseAEnabledAsync(db, approval.TenantId, ct))
            throw new InvalidOperationException("Individual additional benefits must use the current Benefits by grade package authority for this tenant.");
    }

    public static async Task ApplyAsync(ZayraDbContext db, ApprovalRequest approval, string decision, RequestContext context,
        CancellationToken ct, EntityScopeContext? scope = null, ITenantClock? clock = null)
    {
        if (!IsAdditional(approval)) return;
        await ValidateDecisionAsync(db, approval, context, ct, scope);
        if (decision == "Rejected")
        {
            StageAudit(db, approval, context.UserId, "benefits.additional.rejected", new { requestId = approval.Id });
            return;
        }
        if (decision != "Approved") throw new InvalidOperationException("Invalid benefit approval decision.");
        var proposal = Read(approval);
        var terms = proposal.Terms;
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == approval.TenantId && e.Id == terms.EmployeeId && !e.IsDeleted, ct)
            ?? throw new InvalidOperationException("Employee is no longer available in your company scope.");
        if (employee.CompanyId != proposal.CompanyId || employee.GradeId != proposal.GradeId)
            throw new InvalidOperationException("The employee's company or grade changed after submission. Withdraw this request and submit it against the current package.");
        var plan = await ValidateTermsAsync(db, approval.TenantId, employee, terms, clock ?? new TenantClock(db, TimeProvider.System), ct);
        if (plan.Currency != proposal.Currency || plan.Classification != proposal.PlanClassification)
            throw new InvalidOperationException("The benefit plan currency or classification changed after submission. Submit a new request for review.");
        var previous = await ValidateBaselineAsync(db, approval.TenantId, employee, terms, ct);
        await EnsureNoOverlapAsync(db, approval.TenantId, terms, approval.Id, ct);
        if (await db.BenefitEnrollments.AnyAsync(e => e.TenantId == approval.TenantId && e.ApprovalRequestId == approval.Id, ct))
            throw new InvalidOperationException("This benefit approval has already been applied.");
        var sameStart = previous?.EffectiveFrom == terms.EffectiveFrom;
        var row = sameStart ? previous! : new BenefitEnrollment
        {
            Id = approval.Id, TenantId = approval.TenantId, CompanyId = employee.CompanyId, EmployeeId = employee.Id,
            EmployeeName = employee.FullName, BenefitPlanId = terms.BenefitPlanId, AssignmentSource = Source,
            CreatedBy = context.UserId, OriginalEnrollmentId = previous is null ? null : previous.OriginalEnrollmentId ?? previous.Id,
        };
        row.CoverageTier = terms.CoverageTier; row.EntitlementTier = terms.EntitlementTier;
        row.MaximumBenefitAmount = terms.MaximumBenefitAmount; row.RequestedBenefitAmount = terms.RequestedBenefitAmount;
        row.LimitPeriod = terms.LimitPeriod; row.EffectiveFrom = terms.EffectiveFrom; row.EffectiveTo = terms.EffectiveTo;
        row.ReviewDate = terms.ReviewDate; row.GrantReason = terms.Reason; row.ApprovalRequestId = approval.Id;
        row.EligibilitySnapshotJson = approval.Payload!; row.Status = "Active"; row.UpdatedBy = context.UserId;
        if (previous is not null && !sameStart)
        {
            previous.EffectiveTo = terms.EffectiveFrom.AddDays(-1); previous.UpdatedBy = context.UserId;
            // Existing real contribution agreements retain their terms. Planned amounts in the request never
            // create or alter a contribution; only existing rows are continued across the dated successor.
            var contributions = await db.BenefitContributions.Where(x => x.TenantId == approval.TenantId && x.BenefitEnrollmentId == previous.Id
                && x.IsActive && (!x.EffectiveTo.HasValue || x.EffectiveTo >= terms.EffectiveFrom)).ToListAsync(ct);
            foreach (var c in contributions)
            {
                var start = c.EffectiveFrom > row.EffectiveFrom ? c.EffectiveFrom : row.EffectiveFrom;
                var end = !row.EffectiveTo.HasValue ? c.EffectiveTo : !c.EffectiveTo.HasValue || row.EffectiveTo < c.EffectiveTo ? row.EffectiveTo : c.EffectiveTo;
                if (!end.HasValue || end >= start) db.BenefitContributions.Add(new BenefitContribution
                {
                    TenantId = c.TenantId, CompanyId = c.CompanyId, BenefitEnrollmentId = row.Id, BenefitPlanId = c.BenefitPlanId,
                    EmployeeId = c.EmployeeId, EmployeeAmount = c.EmployeeAmount, EmployerAmount = c.EmployerAmount,
                    Frequency = c.Frequency, PayrollComponentCode = c.PayrollComponentCode, EffectiveFrom = start, EffectiveTo = end, CreatedBy = context.UserId,
                });
                if (c.EffectiveFrom >= terms.EffectiveFrom) c.IsActive = false; else c.EffectiveTo = terms.EffectiveFrom.AddDays(-1);
            }
        }
        if (!sameStart) db.BenefitEnrollments.Add(row);
        StageAudit(db, approval, context.UserId, "benefits.additional.approved", new
        {
            requestId = approval.Id, enrollmentId = row.Id, baseline = proposal.Baseline,
            approvedTerms = terms, authority = new { approval.WorkflowId, approval.CurrentStepOrder, context.UserId },
        });
    }

    private static AdditionalBenefitGrantRequest Normalize(AdditionalBenefitGrantRequest input) => input with
    {
        CoverageTier = input.CoverageTier?.Trim() ?? "", EntitlementTier = input.EntitlementTier?.Trim() ?? "",
        Reason = input.Reason?.Trim() ?? "", InternalJustification = input.InternalJustification?.Trim() ?? "",
        Treatment = input.Treatment?.Trim() ?? "", CostFrequency = string.IsNullOrWhiteSpace(input.CostFrequency) ? null : input.CostFrequency.Trim(),
    };

    private static async Task<BenefitPlan> ValidateTermsAsync(ZayraDbContext db, Guid tenantId, Employee employee,
        AdditionalBenefitGrantRequest terms, ITenantClock clock, CancellationToken ct)
    {
        if (await EntitlementMatrixService.ReleaseAEnabledAsync(db, tenantId, ct)) throw new InvalidOperationException("Use Benefits by grade for this tenant's individual package.");
        if (terms.CoverageTier.Length is < 1 or > 120 || terms.EntitlementTier.Length is < 1 or > 120)
            throw new InvalidOperationException("Coverage and entitlement tiers of 1 to 120 characters are required.");
        if (terms.Reason.Length is < 1 or > 1000 || terms.InternalJustification.Length is < 1 or > 2000)
            throw new InvalidOperationException("An employee-visible reason and an internal justification are required (maximum 1000 and 2000 characters).");
        if (!Treatments.Contains(terms.Treatment) || !BenefitLimitPeriods.All.Contains(terms.LimitPeriod)) throw new InvalidOperationException("Select a valid benefit treatment and entitlement period.");
        static bool Invalid(decimal? value, bool zeroAllowed = false) => value.HasValue && (value < (zeroAllowed ? 0m : 0.01m) || value > 999999999999.99m || decimal.Round(value.Value, 2) != value);
        if (Invalid(terms.MaximumBenefitAmount) || Invalid(terms.RequestedBenefitAmount) || Invalid(terms.PlannedEmployerCost, true) || Invalid(terms.PlannedEmployeeCost, true))
            throw new InvalidOperationException("Amounts must be valid currency amounts with at most two decimal places.");
        if (terms.RequestedBenefitAmount.HasValue && terms.MaximumBenefitAmount.HasValue && terms.RequestedBenefitAmount > terms.MaximumBenefitAmount)
            throw new InvalidOperationException("The entitlement amount cannot exceed its maximum.");
        if (terms.Treatment is "CashAllowance" or "Reimbursement" && terms.RequestedBenefitAmount is null && terms.MaximumBenefitAmount is null)
            throw new InvalidOperationException("A cash allowance or reimbursement requires an explicit amount or maximum.");
        if ((terms.PlannedEmployerCost.HasValue || terms.PlannedEmployeeCost.HasValue) && !CostFrequencies.Contains(terms.CostFrequency ?? ""))
            throw new InvalidOperationException("Select the frequency of the planned cost.");
        if (terms.CostFrequency is not null && !CostFrequencies.Contains(terms.CostFrequency)) throw new InvalidOperationException("Invalid planned cost frequency.");
        var today = await clock.TodayAsync(tenantId, ct);
        if (terms.EffectiveFrom < today) throw new InvalidOperationException("The start date has passed. Submit a new request with a current or future start date.");
        if (terms.EffectiveTo is null && terms.ReviewDate is null) throw new InvalidOperationException("An expiry date or review date is required.");
        if (terms.EffectiveTo < terms.EffectiveFrom || terms.ReviewDate < terms.EffectiveFrom || (terms.ReviewDate.HasValue && terms.EffectiveTo.HasValue && terms.ReviewDate > terms.EffectiveTo))
            throw new InvalidOperationException("Expiry and review dates must follow the start; review cannot be after expiry.");
        var plan = await db.BenefitPlans.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == terms.BenefitPlanId && p.IsActive && !p.IsDeleted, ct)
            ?? throw new InvalidOperationException("Benefit plan not found or inactive.");
        if (plan.CompanyId.HasValue && plan.CompanyId != employee.CompanyId) throw new InvalidOperationException("This benefit plan belongs to a different company.");
        if (terms.EffectiveFrom < plan.EffectiveFrom || (plan.EffectiveTo.HasValue && (!terms.EffectiveTo.HasValue || terms.EffectiveTo > plan.EffectiveTo)))
            throw new InvalidOperationException("The requested dates must fit within the active benefit plan period.");
        return plan;
    }

    private static async Task<BenefitEnrollment?> ValidateBaselineAsync(ZayraDbContext db, Guid tenantId, Employee employee, AdditionalBenefitGrantRequest terms, CancellationToken ct)
    {
        if (terms.EnrollmentId is not Guid id) return null;
        var row = await db.BenefitEnrollments.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct)
            ?? throw new InvalidOperationException("The original additional benefit was not found.");
        if (row.AssignmentSource != Source || row.EmployeeId != employee.Id || row.CompanyId != employee.CompanyId || row.BenefitPlanId != terms.BenefitPlanId || row.Status != "Active")
            throw new InvalidOperationException("Only an active additional benefit for this employee and company can be amended here. Use Adjust existing benefit for grade defaults.");
        if (row.UpdatedAtUtc?.Ticks / 10 != terms.ExpectedUpdatedAtUtc?.Ticks / 10) throw new InvalidOperationException("The benefit changed after this request was prepared. Refresh and submit a new request.");
        if (terms.EffectiveFrom < row.EffectiveFrom || (row.EffectiveTo.HasValue && terms.EffectiveFrom > row.EffectiveTo))
            throw new InvalidOperationException("The amendment must begin during the existing benefit period.");
        if (terms.EffectiveFrom == row.EffectiveFrom && (await db.BenefitContributions.AnyAsync(x => x.TenantId == tenantId && x.BenefitEnrollmentId == id, ct)
            || await db.BenefitPayrollDeductionLinks.AnyAsync(x => x.TenantId == tenantId && x.BenefitEnrollmentId == id, ct)))
            throw new InvalidOperationException("This benefit has financial history. Use a later amendment date to preserve it.");
        if (await (from link in db.BenefitPayrollDeductionLinks.AsNoTracking() join run in db.PayrollRuns.AsNoTracking() on link.PayrollRunId equals run.Id
            where link.TenantId == tenantId && run.TenantId == tenantId && link.BenefitEnrollmentId == id && (run.Status == "Locked" || run.Status == "Paid")
                && (run.Year > terms.EffectiveFrom.Year || run.Year == terms.EffectiveFrom.Year && run.Month >= terms.EffectiveFrom.Month) select link.Id).AnyAsync(ct))
            throw new InvalidOperationException("Finalized payroll exists on or after the amendment date. Use a date after those payroll periods.");
        var mandatory = await db.BenefitPlans.AnyAsync(x => x.TenantId == tenantId && x.Id == row.BenefitPlanId && x.Classification == BenefitPlanClassifications.Mandatory, ct);
        if (mandatory && (terms.LimitPeriod != row.LimitPeriod || terms.MaximumBenefitAmount.HasValue && (!row.MaximumBenefitAmount.HasValue || terms.MaximumBenefitAmount < row.MaximumBenefitAmount)))
            throw new InvalidOperationException("A mandatory benefit's minimum or limit period cannot be reduced by an individual amendment.");
        return row;
    }

    private static async Task EnsureNoOverlapAsync(ZayraDbContext db, Guid tenantId, AdditionalBenefitGrantRequest terms, Guid? currentRequest, CancellationToken ct)
    {
        if (await db.BenefitEnrollments.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.EmployeeId == terms.EmployeeId && x.BenefitPlanId == terms.BenefitPlanId
            && x.Id != terms.EnrollmentId && (x.Status == "Active" || x.Status == "Waived")
            && (!terms.EffectiveTo.HasValue || x.EffectiveFrom <= terms.EffectiveTo) && (!x.EffectiveTo.HasValue || x.EffectiveTo >= terms.EffectiveFrom), ct))
            throw new InvalidOperationException("This employee already has this plan for the requested period. Adjust the existing benefit instead of adding a duplicate.");
        var pending = await db.ApprovalRequests.AsNoTracking().Where(x => x.TenantId == tenantId && x.EntityName == EntityName && x.RequestedForEmployeeId == terms.EmployeeId
            && x.Status == "Pending" && x.Id != currentRequest).ToListAsync(ct);
        foreach (var request in pending)
        {
            var other = Read(request).Terms;
            if (other.BenefitPlanId == terms.BenefitPlanId && (!terms.EffectiveTo.HasValue || other.EffectiveFrom <= terms.EffectiveTo)
                && (!other.EffectiveTo.HasValue || other.EffectiveTo >= terms.EffectiveFrom))
                throw new InvalidOperationException("A pending request already covers this benefit plan. Decide or withdraw that request first.");
        }
    }

    public static AdditionalBenefitProposal Read(ApprovalRequest approval)
    {
        if (!IsAdditional(approval) || approval.Payload is null || approval.PayloadSha256 is null || Digest(approval.Payload) != approval.PayloadSha256)
            throw new InvalidOperationException("The benefit request witness is missing or changed. It cannot be decided.");
        AdditionalBenefitProposal? proposal;
        try { proposal = JsonSerializer.Deserialize<AdditionalBenefitProposal>(approval.Payload, Json); }
        catch (JsonException) { throw new InvalidOperationException("Invalid benefit request witness."); }
        if (proposal is null || proposal.Terms is null || proposal.Version != 1 || proposal.RequestId != approval.Id || approval.EntityId != approval.Id.ToString()
            || proposal.TenantId != approval.TenantId || proposal.CompanyId != approval.CompanyId || proposal.Terms.EmployeeId != approval.RequestedForEmployeeId)
            throw new InvalidOperationException("The benefit approval is not linked to the submitted employee and company.");
        return proposal;
    }

    public static async Task<AdditionalBenefitRequestDto> ToDtoAsync(ZayraDbContext db, ApprovalRequest approval, CancellationToken ct, ApprovalRequestDto? routing = null)
    {
        var name = await db.Users.AsNoTracking().Where(x => x.TenantId == approval.TenantId && x.Id == approval.RequestedByUserId).Select(x => x.FullName).FirstOrDefaultAsync(ct);
        var enrollment = approval.Status == "Approved" ? await db.BenefitEnrollments.AsNoTracking().Where(x => x.TenantId == approval.TenantId && x.ApprovalRequestId == approval.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct) : null;
        return ToDto(approval, name, enrollment, routing);
    }

    public static AdditionalBenefitRequestDto ToDto(ApprovalRequest approval, string? requesterName, Guid? appliedEnrollmentId, ApprovalRequestDto? routing = null)
    {
        var p = Read(approval);
        return new(approval.Id, approval.Status, p.Terms.EmployeeId, p.EmployeeName, p.Terms.BenefitPlanId, p.PlanName, p.Currency,
            requesterName, approval.CreatedAtUtc, p.Terms, p.Baseline, approval.Id, appliedEnrollmentId, routing);
    }

    public static string WorkflowDigest(IEnumerable<ApprovalRouteStep> steps) => Digest(JsonSerializer.Serialize(steps.OrderBy(x => x.StepOrder), Json));

    // jsonb normalizes property order and whitespace. Hash semantic JSON, with sorted keys and normalized
    // decimal numbers, so the submitted witness verifies identically after a PostgreSQL round trip.
    public static string Digest(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject(); foreach (var p in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal)) { writer.WritePropertyName(p.Name); WriteCanonical(writer, p.Value); } writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); break;
            case JsonValueKind.Number: writer.WriteRawValue(value.GetDecimal().ToString("G29", CultureInfo.InvariantCulture)); break;
            default: value.WriteTo(writer); break;
        }
    }
    private static void EnsureActor(RequestContext context, Guid tenantId, string permission)
    {
        if (context.TenantId != tenantId || context.UserId is null || context.UserId == Guid.Empty || !(context.Permissions?.Contains(permission, StringComparer.OrdinalIgnoreCase) ?? false))
            throw new InvalidOperationException($"An authenticated user with {permission} authority in this tenant is required.");
    }
    private static void StageAudit(ZayraDbContext db, ApprovalRequest request, Guid? actor, string action, object detail) => db.AuditLogs.Add(new AuditLog
    {
        TenantId = request.TenantId, CompanyId = request.CompanyId, UserId = actor, EntityName = EntityName, EntityId = request.Id.ToString(),
        Action = action, Metadata = JsonSerializer.Serialize(detail, Json),
    });
}
