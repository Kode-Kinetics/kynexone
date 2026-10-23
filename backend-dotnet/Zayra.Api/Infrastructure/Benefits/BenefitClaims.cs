using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Approvals;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Auth;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Approvals;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Benefits;

public record BenefitClaimLine(string Type, decimal Amount);
public record BenefitClaimRequest(Guid EnrollmentId, decimal Amount, DateOnly ExpenseDate, string InvoiceReference,
    string Description, IReadOnlyList<Guid> DocumentIds,
    Guid? DependantId = null, string? SchoolName = null, string? SchoolCountry = null,
    string? ClaimTerm = null, IReadOnlyList<BenefitClaimLine>? Lines = null);
public record BenefitReceiptWitness(Guid Id, int VersionNumber, string FileName, string ContentType, string Sha256, string StorageKey);
public record BenefitReceiptDto(Guid Id, string FileName, string ContentType, int VersionNumber);
public record BenefitReceiptUploadWitness(int EmployeeId, int VersionNumber, string Sha256);
public record BenefitClaimProposal(int Version, Guid RequestId, Guid TenantId, Guid? CompanyId, Guid? GradeId,
    int EmployeeId, string EmployeeName, Guid EnrollmentId, Guid EnrollmentChainId, Guid BenefitPlanId,
    string PlanName, string Currency, decimal Amount, DateOnly ExpenseDate, string InvoiceReference, string Description,
    string LimitPeriod, decimal MaximumAmount, DateOnly PeriodFrom, DateOnly PeriodTo, string PolicySnapshot,
    DateTime? EnrollmentUpdatedAtUtc, IReadOnlyList<BenefitReceiptWitness> Receipts, string WorkflowSha256,
    // Version 2 (claim rules). A Version 1 payload reads these as null.
    Guid? DependantId = null, string? DependantName = null, string? SchoolName = null, string? SchoolCountry = null,
    string? ClaimTerm = null, IReadOnlyList<BenefitClaimLine>? Lines = null, decimal? InvoiceTotal = null, string? ExceptionReason = null);
public record BenefitClaimDto(Guid Id, string Status, int EmployeeId, string EmployeeName, Guid EnrollmentId,
    Guid BenefitPlanId, string PlanName, string Currency, decimal Amount, DateOnly ExpenseDate, string InvoiceReference,
    string Description, IReadOnlyList<BenefitReceiptDto> Receipts, DateTime CreatedAtUtc, Guid ApprovalRequestId,
    Guid? PayrollRunId, string SettlementStatus, decimal ReservedAmount, decimal RemainingAmount,
    Guid? RequestedByUserId = null, bool CanWithdraw = false,
    Guid? DependantId = null, string? DependantName = null, string? SchoolName = null, string? SchoolCountry = null,
    string? ClaimTerm = null, IReadOnlyList<BenefitClaimLine>? Lines = null, decimal? InvoiceTotal = null, string? ExceptionReason = null,
    IReadOnlyList<string>? CoveredCosts = null);
public record BenefitChildBalanceDto(Guid DependantId, string Name, decimal MaximumAmount, decimal ReservedAmount,
    decimal ApprovedAmount, decimal RemainingAmount, bool WithinChildLimit);
public record BenefitClaimRulesDto(string ClaimFor, bool LimitPerChild, int? MaxChildren, bool ExtraChildByException,
    IReadOnlyList<string>? CoveredCosts, IReadOnlyList<string>? AllowedCountries, IReadOnlyList<string>? ClaimTerms, string LimitPeriod);
public record BenefitClaimBalanceDto(bool CanClaim, decimal MaximumAmount, decimal ReservedAmount, decimal ApprovedAmount,
    decimal RemainingAmount, DateOnly PeriodFrom, DateOnly PeriodTo,
    IReadOnlyList<BenefitChildBalanceDto>? Children = null, BenefitClaimRulesDto? Rules = null, string? BlockedReason = null);

/// <summary>Claim, evidence and decision use the sealed shared approval aggregate. No approval creates a payment.</summary>
public static class BenefitClaims
{
    public const string EntityName = "BenefitClaim";
    public const string ReceiptType = "BenefitReceipt";
    public const string PayrollSource = "BenefitClaim";
    private static readonly JsonSerializerOptions Json = BenefitPaymentPolicies.Json;
    public static bool IsClaim(ApprovalRequest approval) => IsEntity(approval.EntityName);
    public static bool IsEntity(string? value) => string.Equals(value, EntityName, StringComparison.OrdinalIgnoreCase);
    public static bool IsGovernedBenefit(string? value) => IsEntity(value) || string.Equals(value, AdditionalBenefitGrants.EntityName, StringComparison.OrdinalIgnoreCase);
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static async Task<ApprovalRequest> SubmitAsync(ZayraDbContext db, IApprovalRouter router, IDocumentStorage storage,
        Guid tenantId, BenefitClaimRequest input, RequestContext context, ITenantClock clock, CancellationToken ct,
        EntityScopeContext? scope = null, bool selfService = false)
    {
        Actor(context, tenantId, selfService ? "ess.write" : "employees.write");
        var owner = await db.BenefitEnrollments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == input.EnrollmentId, ct)
            ?? throw new InvalidOperationException("Benefit enrollment was not found.");
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == owner.EmployeeId && !x.IsDeleted, ct)
            ?? throw new InvalidOperationException("The benefiting employee is unavailable.");
        await CheckSubmitScopeAsync(db, tenantId, employee, context, scope, selfService, ct);
        var requestId = Guid.NewGuid();
        return await FinanceDecisionSerializer.SerializeAsync(db, AdditionalBenefitGrants.LockScope, tenantId, employee.PublicId, async () =>
        {
            var replay = await db.ApprovalRequests.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == requestId, ct);
            if (replay is not null) return replay;
            employee = await db.Employees.AsNoTracking().FirstAsync(x => x.TenantId == tenantId && x.Id == owner.EmployeeId && !x.IsDeleted, ct);
            await CheckSubmitScopeAsync(db, tenantId, employee, context, scope, selfService, ct);
            var row = await db.BenefitEnrollments.AsNoTracking().FirstAsync(x => x.TenantId == tenantId && x.Id == input.EnrollmentId, ct);
            var (policy, maximum, from, to) = await ValidateEntitlementAsync(db, tenantId, employee, row, input.Amount, input.ExpenseDate, clock, ct);
            if (string.IsNullOrWhiteSpace(input.InvoiceReference) || input.InvoiceReference.Trim().Length > 120
                || string.IsNullOrWhiteSpace(input.Description) || input.Description.Trim().Length > 2000)
                throw new InvalidOperationException("An invoice reference (up to 120 characters) and description (up to 2000) are required.");
            if (input.DocumentIds is null || input.DocumentIds.Count > 10 || input.DocumentIds.Distinct().Count() != input.DocumentIds.Count)
                throw new InvalidOperationException("Provide up to ten distinct receipt documents.");
            if (policy.Policy.ReceiptRequired && input.DocumentIds.Count == 0) throw new InvalidOperationException($"{policy.Policy.ReceiptLabel} is required by this benefit policy.");
            var receipts = new List<BenefitReceiptWitness>();
            foreach (var documentId in input.DocumentIds)
            {
                var doc = await db.EmployeeDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == documentId
                    && x.EmployeeId == employee.Id && x.CompanyId == employee.CompanyId && !x.IsDeleted && x.DocumentType == ReceiptType, ct)
                    ?? throw new InvalidOperationException("A receipt does not belong to this employee and company. Upload it through Benefit receipts.");
                var upload = await db.AuditLogs.AsNoTracking().Where(x => x.TenantId == tenantId && x.EntityName == nameof(EmployeeDocument)
                    && x.EntityId == doc.Id.ToString() && x.Action == "benefits.receipt.uploaded").Select(x => x.Metadata).FirstOrDefaultAsync(ct);
                if (upload is null)
                    throw new InvalidOperationException("Upload this receipt through the dedicated Benefit receipts action.");
                var bytes = await storage.GetBytesAsync(tenantId, doc.StorageUrl, ct);
                CheckReceipt(doc, bytes);
                BenefitReceiptUploadWitness? uploaded;
                try { uploaded = JsonSerializer.Deserialize<BenefitReceiptUploadWitness>(upload, Json); }
                catch (JsonException) { throw new InvalidOperationException("The receipt upload witness is invalid."); }
                if (uploaded is null || uploaded.EmployeeId != employee.Id || uploaded.VersionNumber != doc.VersionNumber || uploaded.Sha256 != Hash(bytes))
                    throw new InvalidOperationException("The receipt differs from its original verified upload. Upload the intended evidence again.");
                receipts.Add(new(doc.Id, doc.VersionNumber, doc.FileName, doc.ContentType, Hash(bytes), doc.StorageUrl));
            }
            var route = await router.ResolveAsync(tenantId, employee.Id, EntityName, ct);
            await FinanceDecisionSerializer.AcquireAsync(db, "approval.workflow", tenantId, route.WorkflowId, ct);
            var current = await router.ResolveAsync(tenantId, employee.Id, EntityName, ct);
            if (current.WorkflowId != route.WorkflowId) throw new InvalidOperationException("The claim approval route changed. Retry submission.");
            route = current;
            if (route.Steps.Any(x => string.IsNullOrWhiteSpace(x.ApproverRole) || x.ApproverRole.Equals("Any", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Every claim approval step must name an independent authorized role.");
            var planName = await db.BenefitPlans.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == row.BenefitPlanId).Select(x => x.Name).FirstOrDefaultAsync(ct) ?? "Benefit";
            var facts = await ValidateClaimRulesAsync(db, tenantId, employee, policy.Policy, input.Amount, input.DependantId, input.SchoolName,
                input.SchoolCountry, input.ClaimTerm, input.Lines, ct);
            var proposal = new BenefitClaimProposal(2, requestId, tenantId, employee.CompanyId, employee.GradeId, employee.Id, employee.FullName,
                row.Id, row.OriginalEnrollmentId ?? row.Id, row.BenefitPlanId, planName, policy.Currency, input.Amount, input.ExpenseDate,
                input.InvoiceReference.Trim(), input.Description.Trim(), row.LimitPeriod, maximum, from, to, row.PaymentPolicySnapshotJson,
                row.UpdatedAtUtc, receipts, AdditionalBenefitGrants.WorkflowDigest(route.Steps),
                facts.DependantId, facts.DependantName, facts.SchoolName, facts.SchoolCountry, facts.ClaimTerm, facts.Lines, facts.InvoiceTotal);
            proposal = proposal with { ExceptionReason = await ValidateBudgetAsync(db, proposal, null, ct) };
            var first = route.FirstStep;
            // Nobody could ever decide it ⇒ refused (422) before anything is written; its approver cannot act but
            // Admin / approvals.override can ⇒ the Admin queue, audited (ApprovalFirstStep).
            var routed = await Zayra.Api.Infrastructure.Approvals.ApprovalFirstStep.RouteAsync(db, router, tenantId, EntityName, employee.Id, context.UserId, first, ct);
            var approver = routed.Approver;
            var now = DateTime.UtcNow;
            var payload = JsonSerializer.Serialize(proposal, Json);
            var approval = new ApprovalRequest
            {
                Id = requestId, TenantId = tenantId, CompanyId = employee.CompanyId, EntityName = EntityName, EntityId = requestId.ToString(),
                WorkflowId = route.WorkflowId, RequestedForEmployeeId = employee.Id, RequestedByUserId = context.UserId,
                Title = Clip($"{(proposal.ExceptionReason is null ? "" : "Exception · ")}Benefit claim: {planName} · {employee.FullName}", 240), Payload = payload, PayloadSha256 = AdditionalBenefitGrants.Digest(payload),
                CurrentStepOrder = first.StepOrder, CurrentApproverType = approver.EmployeeId.HasValue ? approver.ApproverType : "Role",
                CurrentApproverEmployeeId = approver.EmployeeId, CurrentApproverUserId = approver.UserId, CurrentApproverName = approver.Name,
                CurrentApproverRole = approver.QueueRole, CurrentQueue = approver.EmployeeId.HasValue ? $"{approver.ApproverType}:{approver.Name}" : $"Role:{approver.QueueRole}",
                SlaHours = Math.Clamp(first.EscalationAfterHours ?? 24, 1, 720), LastRoutedAtUtc = now,
                DueAtUtc = now.AddHours(Math.Clamp(first.EscalationAfterHours ?? 24, 1, 720)),
            };
            Zayra.Api.Infrastructure.Approvals.ApprovalFirstStep.StageOverride(db, approval, context.UserId, routed);
            db.ApprovalRequests.Add(approval);
            Audit(db, approval, context.UserId, "benefits.claim.requested", facts == ClaimFacts.None && proposal.ExceptionReason is null
                ? (object)new { requestId, proposal.Amount, proposal.EnrollmentId, proposal.InvoiceReference }
                : new { requestId, proposal.Amount, proposal.EnrollmentId, proposal.InvoiceReference, proposal.DependantId, proposal.SchoolCountry,
                    proposal.ClaimTerm, proposal.InvoiceTotal, proposal.ExceptionReason });
            await db.SaveChangesAsync(ct);
            return approval;
        }, ct);
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

    private static async Task CheckSubmitScopeAsync(ZayraDbContext db, Guid tenantId, Employee employee, RequestContext context,
        EntityScopeContext? scope, bool selfService, CancellationToken ct)
    {
        if (selfService)
        {
            if (employee.UserAccountId != context.UserId && !await db.EmployeeUserAccounts.AsNoTracking().AnyAsync(x => x.TenantId == tenantId
                && x.EmployeeId == employee.Id && x.UserId == context.UserId && x.Status == "Active" && !x.IsDeleted && x.AccessMode != "NoLogin" && x.AccessMode != "KioskOnly", ct))
                throw new InvalidOperationException("You can claim only your own benefits.");
        }
        else if (!(scope ?? EntityScopeContext.Empty).CanAccessCompany(employee.CompanyId)) throw new InvalidOperationException("This employee is outside your company scope.");
    }

    private static async Task<(BenefitPolicySnapshot Policy, decimal Maximum, DateOnly From, DateOnly To)> ValidateEntitlementAsync(
        ZayraDbContext db, Guid tenantId, Employee employee, BenefitEnrollment row, decimal amount, DateOnly expenseDate, ITenantClock clock, CancellationToken ct, bool enforceSubmissionWindow = true)
    {
        if (await EntitlementMatrixService.ReleaseAEnabledAsync(db, tenantId, ct)) throw new InvalidOperationException("Use the current Benefits by grade authority for this tenant.");
        if (row.EmployeeId != employee.Id || row.CompanyId != employee.CompanyId || row.Status != "Active") throw new InvalidOperationException("The benefit is not active for this employee and company.");
        var policy = BenefitPaymentPolicies.ReadSnapshotEnvelope(row.PaymentPolicySnapshotJson);
        if (policy is null || policy.PlanId != row.BenefitPlanId || policy.Policy.Delivery != "Reimbursement") throw new InvalidOperationException("This enrollment has no approved reimbursement policy.");
        var today = await clock.TodayAsync(tenantId, ct);
        if (expenseDate > today || expenseDate < row.EffectiveFrom || row.EffectiveTo.HasValue && expenseDate > row.EffectiveTo)
            throw new InvalidOperationException("The expense date must be within the assigned benefit period and cannot be in the future.");
        if (enforceSubmissionWindow && policy.Policy.ClaimWindowDays is int days && today.DayNumber - expenseDate.DayNumber > days) throw new InvalidOperationException("The policy's claim submission window has passed.");
        var limits = new[] { row.MaximumBenefitAmount, row.RequestedBenefitAmount, policy.Policy.Amount }.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        if (limits.Count == 0 || limits.Min() <= 0) throw new InvalidOperationException("An explicit reimbursement limit is required before a claim can be submitted.");
        if (amount <= 0 || amount > 999999999999.99m || decimal.Round(amount, 2) != amount) throw new InvalidOperationException("Claim amount must be positive with at most two decimal places.");
        var (from, to) = await PeriodAsync(db, tenantId, employee.PublicId, row, expenseDate, ct);
        return (policy, limits.Min(), from, to);
    }
    public static (DateOnly From, DateOnly To) Period(BenefitEnrollment row, DateOnly date) => row.LimitPeriod switch
    {
        BenefitLimitPeriods.Monthly => (new(date.Year, date.Month, 1), new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month))),
        BenefitLimitPeriods.Annual => (new(date.Year, 1, 1), new(date.Year, 12, 31)),
        BenefitLimitPeriods.Lifetime or BenefitLimitPeriods.PerEnrollment => (DateOnly.MinValue, DateOnly.MaxValue),
        BenefitLimitPeriods.ContractYear => throw new InvalidOperationException("A contract-year limit is resolved from the employee's contract."),
        _ => throw new InvalidOperationException("The reimbursement entitlement needs a supported limit period."),
    };
    public static readonly string[] ContractYearStatuses = ["Active", "Expired", "Superseded", "Terminated"];
    /// <summary>The budget period containing <paramref name="date"/>. ContractYear follows the latest-starting contract term covering the
    /// date (renewals start a new year); no covering contract ⇒ refused, never the calendar year.</summary>
    public static async Task<(DateOnly From, DateOnly To)> PeriodAsync(ZayraDbContext db, Guid tenantId, Guid employeePublicId,
        BenefitEnrollment row, DateOnly date, CancellationToken ct)
    {
        if (row.LimitPeriod != BenefitLimitPeriods.ContractYear) return Period(row, date);
        var term = await db.EmployeeContracts.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == employeePublicId && !x.IsDeleted
                && ContractYearStatuses.Contains(x.Status) && x.StartDate <= date && (x.EndDate == null || x.EndDate >= date))
            .OrderByDescending(x => x.StartDate).Select(x => new { x.StartDate, x.EndDate }).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"This allowance runs on the employee's contract year, and no contract covering {date:yyyy-MM-dd} is on file. HR needs to add the contract first.");
        return ContractYear(term.StartDate, term.EndDate, date);
    }
    public static (DateOnly From, DateOnly To) ContractYear(DateOnly start, DateOnly? end, DateOnly date)
    {
        var k = date.Year - start.Year; if (start.AddYears(k) > date) k--;
        var to = start.AddYears(k + 1).AddDays(-1);
        return (start.AddYears(k), end.HasValue && end.Value < to ? end.Value : to);
    }
    private static bool PerChild(BenefitClaimProposal p) => p.DependantId.HasValue && BenefitPaymentPolicies.ReadSnapshotEnvelope(p.PolicySnapshot)?.Policy.LimitPerChild == true;
    // Same plan, same period (and chain for PerEnrollment). The per-child budget narrows this further in SameBudget.
    private static bool SamePeriod(BenefitClaimProposal current, BenefitClaimProposal other) => current.BenefitPlanId == other.BenefitPlanId
        && other.ExpenseDate >= current.PeriodFrom && other.ExpenseDate <= current.PeriodTo
        && (current.LimitPeriod != BenefitLimitPeriods.PerEnrollment || current.EnrollmentChainId == other.EnrollmentChainId);
    private static bool SameBudget(BenefitClaimProposal current, BenefitClaimProposal other, bool perChild) => SamePeriod(current, other)
        && (!perChild || current.DependantId == other.DependantId);
    public static async Task<decimal> ReservedAsync(ZayraDbContext db, BenefitClaimProposal p, Guid? exclude, CancellationToken ct)
    {
        var rows = await db.ApprovalRequests.AsNoTracking().Where(x => x.TenantId == p.TenantId && x.EntityName == EntityName
            && x.RequestedForEmployeeId == p.EmployeeId && (x.Status == "Pending" || x.Status == "Approved") && x.Id != exclude).ToListAsync(ct);
        var perChild = PerChild(p);
        return rows.Select(Read).Where(x => SameBudget(p, x, perChild)).Sum(x => x.Amount);
    }
    /// <summary>Duplicate evidence, the (per-child) budget and the child limit. Returns the exception reason when this child is beyond
    /// MaxChildren and the policy allows it as an HR exception; refuses otherwise.</summary>
    private static async Task<string?> ValidateBudgetAsync(ZayraDbContext db, BenefitClaimProposal p, Guid? exclude, CancellationToken ct)
    {
        var rows = await db.ApprovalRequests.AsNoTracking().Where(x => x.TenantId == p.TenantId && x.EntityName == EntityName
            && x.RequestedForEmployeeId == p.EmployeeId && (x.Status == "Pending" || x.Status == "Approved") && x.Id != exclude).ToListAsync(ct);
        var existing = rows.Select(Read).ToList();
        if (existing.Any(x => x.BenefitPlanId == p.BenefitPlanId && x.InvoiceReference.Equals(p.InvoiceReference, StringComparison.OrdinalIgnoreCase)
            || x.Receipts.Any(r => p.Receipts.Any(n => n.Sha256 == r.Sha256))))
            throw new InvalidOperationException("This invoice or receipt already has a pending or approved claim. A receipt cannot be claimed again through another benefit plan.");
        var perChild = PerChild(p);
        if (existing.Where(x => SameBudget(p, x, perChild)).Sum(x => x.Amount) + p.Amount > p.MaximumAmount)
            throw new InvalidOperationException(perChild
                ? "The claim exceeds this child's remaining benefit limit, including pending and approved claims."
                : "The claim exceeds the remaining benefit limit, including pending and approved claims.");
        var policy = BenefitPaymentPolicies.ReadSnapshotEnvelope(p.PolicySnapshot)?.Policy;
        if (policy?.MaxChildren is not int max || p.DependantId is not Guid child) return null;
        var counted = existing.Where(x => SamePeriod(p, x) && x.DependantId.HasValue).Select(x => x.DependantId!.Value).ToHashSet();
        if (counted.Contains(child) || counted.Count + 1 <= max) return null;
        if (!policy.ExtraChildByException) throw new InvalidOperationException($"This policy covers up to {max} children.");
        return $"Child {counted.Count + 1} of {max}: beyond the policy's child limit — HR exception";
    }

    private sealed record ClaimFacts(Guid? DependantId, string? DependantName, string? SchoolName, string? SchoolCountry, string? ClaimTerm,
        IReadOnlyList<BenefitClaimLine>? Lines, decimal? InvoiceTotal)
    { public static readonly ClaimFacts None = new(null, null, null, null, null, null, null); }
    /// <summary>The policy's claim rules (child, school, country, term, itemised covered costs). Run at submit and again at approval.</summary>
    private static async Task<ClaimFacts> ValidateClaimRulesAsync(ZayraDbContext db, Guid tenantId, Employee employee, BenefitPaymentPolicy policy,
        decimal amount, Guid? dependantId, string? schoolName, string? schoolCountry, string? claimTerm, IReadOnlyList<BenefitClaimLine>? lines, CancellationToken ct)
    {
        schoolName = string.IsNullOrWhiteSpace(schoolName) ? null : schoolName.Trim();
        schoolCountry = string.IsNullOrWhiteSpace(schoolCountry) ? null : schoolCountry.Trim().ToUpperInvariant();
        claimTerm = string.IsNullOrWhiteSpace(claimTerm) ? null : claimTerm.Trim();
        if (lines is { Count: 0 }) lines = null;
        string? dependantName = null;
        if ((policy.ClaimFor ?? "Employee") == "Child")
        {
            if (dependantId is not Guid id) throw new InvalidOperationException("Choose the child this claim is for.");
            var child = await db.EmployeeDependents.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id
                && x.EmployeeId == employee.Id && !x.IsDeleted, ct);
            if (child is null || !PackageRules.IsChild(child.Relationship))
                throw new InvalidOperationException("The selected child is not recorded as this employee's child. HR records children under the employee's dependants.");
            dependantName = child.FullName;
            if (schoolName is null) throw new InvalidOperationException("The school name is required (up to 200 characters).");
        }
        else if (dependantId.HasValue) throw new InvalidOperationException("This benefit is claimed for the employee, not for a dependant.");
        if (schoolName is { Length: > 200 }) throw new InvalidOperationException("The school name is required (up to 200 characters).");
        if (schoolCountry is not null && !BenefitPaymentPolicies.IsCountryCode(schoolCountry)) throw new InvalidOperationException("The school country must be a two-letter country code, for example SA.");
        if (policy.AllowedCountries is { Count: > 0 } countries && (schoolCountry is null || !countries.Contains(schoolCountry)))
            throw new InvalidOperationException($"Schools outside {string.Join(", ", countries.Select(BenefitPaymentPolicies.CountryName))} are not covered by this policy.");
        if (policy.ClaimTerms is { Count: > 0 } terms)
        {
            if (claimTerm is null || !terms.Contains(claimTerm)) throw new InvalidOperationException($"Choose a claim term this policy allows: {string.Join(", ", terms)}.");
        }
        else if (claimTerm is not null) throw new InvalidOperationException("This policy does not ask for a claim term.");
        decimal? invoiceTotal = null;
        if (policy.CoveredCosts is { Count: > 0 } covered)
        {
            if (lines is null || lines.Count > 10) throw new InvalidOperationException("Itemise the invoice in one to ten lines, each with a cost type and an amount.");
            if (lines.Any(x => x is null || !BenefitPaymentPolicies.CostLineTypes.Contains(x.Type)))
                throw new InvalidOperationException($"Each invoice line type must be one of: {string.Join(", ", BenefitPaymentPolicies.CostLineTypes)}.");
            if (lines.Any(x => x.Amount <= 0 || x.Amount > 999999999999.99m || decimal.Round(x.Amount, 2) != x.Amount))
                throw new InvalidOperationException("Each invoice line must be a positive amount with at most two decimal places.");
            var coveredSum = lines.Where(x => covered.Contains(x.Type)).Sum(x => x.Amount);
            if (amount != coveredSum)
                throw new InvalidOperationException($"The claim amount must equal the covered costs ({string.Join(", ", covered)}): {coveredSum.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}.");
            invoiceTotal = lines.Sum(x => x.Amount);
            lines = lines.Select(x => new BenefitClaimLine(x.Type, x.Amount)).ToList();
        }
        else if (lines is not null) throw new InvalidOperationException("This policy does not itemise invoice lines; enter the claim amount only.");
        return new(dependantId, dependantName, schoolName, schoolCountry, claimTerm, lines, invoiceTotal);
    }

    public static async Task ValidateDecisionAsync(ZayraDbContext db, ApprovalRequest approval, RequestContext context,
        CancellationToken ct, EntityScopeContext? scope = null)
    {
        if (!IsClaim(approval)) return;
        Actor(context, approval.TenantId, "employees.approve");
        if (!(scope ?? EntityScopeContext.Empty).CanAccessCompany(approval.CompanyId)) throw new InvalidOperationException("The claim is outside your company scope.");
        var p = Read(approval);
        if (approval.RequestedByUserId == context.UserId || await SubjectDecisionBar.CallerIsSubjectAsync(db, approval.TenantId, context.UserId, p.EmployeeId, ct)
            || await ScopedBypass.TenantWide(db.EmployeeUserAccounts, approval.TenantId,
                "Claim separation of duties checks historical beneficiary account links, including deleted links, for this exact tenant and employee; no account data is returned.")
                .AsNoTracking().AnyAsync(x => x.EmployeeId == p.EmployeeId && x.UserId == context.UserId, ct))
            throw new InvalidOperationException("The requester and benefiting employee cannot decide this claim.");
        var steps = await db.ApprovalWorkflowSteps.AsNoTracking().Where(x => x.TenantId == approval.TenantId && x.WorkflowId == approval.WorkflowId).ToListAsync(ct);
        var digest = AdditionalBenefitGrants.WorkflowDigest(steps.Select(x => new ApprovalRouteStep(x.StepOrder, x.StepName,
            string.IsNullOrWhiteSpace(x.ApproverType) ? "Role" : x.ApproverType.Trim(), x.ApproverRole?.Trim() ?? "", x.SpecificEmployeeId, x.EscalationAfterHours, x.IsFinalStep)));
        if (digest != p.WorkflowSha256) throw new InvalidOperationException("The claim approval route changed. Withdraw and resubmit for the current route.");
    }

    public static async Task ApplyAsync(ZayraDbContext db, ApprovalRequest approval, string decision, RequestContext context,
        CancellationToken ct, EntityScopeContext? scope = null, IDocumentStorage? storage = null, ITenantClock? clock = null)
    {
        if (!IsClaim(approval)) return;
        await ValidateDecisionAsync(db, approval, context, ct, scope);
        if (decision == "Rejected") { Audit(db, approval, context.UserId, "benefits.claim.rejected", new { approval.Id }); return; }
        if (decision != "Approved") throw new InvalidOperationException("Invalid claim decision.");
        var p = Read(approval);
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == approval.TenantId && x.Id == p.EmployeeId && !x.IsDeleted, ct)
            ?? throw new InvalidOperationException("The benefiting employee is unavailable.");
        if (employee.CompanyId != p.CompanyId || employee.GradeId != p.GradeId) throw new InvalidOperationException("The employee's company or grade changed. Resubmit this claim for the current package.");
        var row = await db.BenefitEnrollments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == approval.TenantId && x.Id == p.EnrollmentId, ct)
            ?? throw new InvalidOperationException("The original reimbursement entitlement is unavailable.");
        if (row.UpdatedAtUtc?.Ticks / 10 != p.EnrollmentUpdatedAtUtc?.Ticks / 10
            || AdditionalBenefitGrants.Digest(row.PaymentPolicySnapshotJson) != AdditionalBenefitGrants.Digest(p.PolicySnapshot))
            throw new InvalidOperationException("The reimbursement entitlement changed. Resubmit the claim for review.");
        var (policy, _, from, to) = await ValidateEntitlementAsync(db, approval.TenantId, employee, row, p.Amount, p.ExpenseDate, clock ?? new TenantClock(db, TimeProvider.System), ct, enforceSubmissionWindow: false);
        if (from != p.PeriodFrom || to != p.PeriodTo) throw new InvalidOperationException("The employee's benefit period changed since submission (for example a contract was added or changed). Resubmit the claim.");
        await ValidateClaimRulesAsync(db, approval.TenantId, employee, policy.Policy, p.Amount, p.DependantId, p.SchoolName, p.SchoolCountry, p.ClaimTerm, p.Lines, ct);
        var exception = await ValidateBudgetAsync(db, p, approval.Id, ct);
        if (exception is not null && p.ExceptionReason is null)
            throw new InvalidOperationException("This claim is now beyond the policy's child limit. Withdraw and resubmit it so the HR exception is flagged.");
        foreach (var receipt in p.Receipts)
        {
            if (storage is null) throw new InvalidOperationException("Receipt storage is unavailable; the claim cannot be approved.");
            var document = await db.EmployeeDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == p.TenantId && x.Id == receipt.Id
                && x.EmployeeId == p.EmployeeId && x.CompanyId == p.CompanyId && !x.IsDeleted, ct)
                ?? throw new InvalidOperationException("Claim receipt is no longer available.");
            var bytes = await storage.GetBytesAsync(p.TenantId, receipt.StorageKey, ct);
            CheckReceipt(document, bytes);
            if (document.VersionNumber != receipt.VersionNumber || document.StorageUrl != receipt.StorageKey || Hash(bytes) != receipt.Sha256)
                throw new InvalidOperationException("Receipt evidence changed after claim submission.");
        }
        Audit(db, approval, context.UserId, "benefits.claim.approved", p.ExceptionReason is null
            ? (object)new { p.Amount, p.Currency, p.EnrollmentId, approval.WorkflowId, approval.CurrentStepOrder }
            : new { p.Amount, p.Currency, p.EnrollmentId, approval.WorkflowId, approval.CurrentStepOrder, p.DependantId, p.ExceptionReason });
    }

    public static BenefitClaimProposal Read(ApprovalRequest approval)
    {
        if (!IsClaim(approval) || approval.Payload is null || approval.PayloadSha256 is null || AdditionalBenefitGrants.Digest(approval.Payload) != approval.PayloadSha256)
            throw new InvalidOperationException("The sealed claim witness is missing or changed.");
        BenefitClaimProposal? p;
        try { p = JsonSerializer.Deserialize<BenefitClaimProposal>(approval.Payload, Json); }
        catch (JsonException) { throw new InvalidOperationException("Invalid claim witness."); }
        if (p is null || p.Version is not (1 or 2) || p.RequestId != approval.Id || p.TenantId != approval.TenantId || p.CompanyId != approval.CompanyId
            || p.EmployeeId != approval.RequestedForEmployeeId || approval.EntityId != approval.Id.ToString() || p.Receipts is null)
            throw new InvalidOperationException("Claim witness is not bound to this employee, company and approval.");
        return p;
    }
    public static async Task<BenefitClaimDto> ToDtoAsync(ZayraDbContext db, ApprovalRequest approval, CancellationToken ct, Guid? callerUserId = null)
    {
        var p = Read(approval);
        var adjustment = await db.PayrollAdjustments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == p.TenantId && x.SourceType == PayrollSource && x.SourceId == approval.Id, ct);
        var run = adjustment is null ? null : await db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == p.TenantId && x.Id == adjustment.PayrollRunId, ct);
        var settlement = approval.Status == "Pending" ? "AwaitingApproval" : approval.Status == "Cancelled" ? "Withdrawn" : approval.Status;
        if (approval.Status == "Approved") settlement = run?.Status == "Paid" ? "Paid"
            : adjustment?.Status == "Processed" && run?.Status != "Voided" ? "IncludedInPayroll" : "AwaitingPayroll";
        var reserved = await ReservedAsync(db, p, null, ct);
        return new(approval.Id, approval.Status, p.EmployeeId, p.EmployeeName, p.EnrollmentId, p.BenefitPlanId, p.PlanName, p.Currency,
            p.Amount, p.ExpenseDate, p.InvoiceReference, p.Description, p.Receipts.Select(x => new BenefitReceiptDto(x.Id, x.FileName, x.ContentType, x.VersionNumber)).ToList(),
            approval.CreatedAtUtc, approval.Id, adjustment?.PayrollRunId, settlement, reserved, Math.Max(0, p.MaximumAmount - reserved), approval.RequestedByUserId, approval.Status == "Pending" && approval.RequestedByUserId == callerUserId && callerUserId.HasValue,
            p.DependantId, p.DependantName, p.SchoolName, p.SchoolCountry, p.ClaimTerm, p.Lines, p.InvoiceTotal, p.ExceptionReason,
            BenefitPaymentPolicies.ReadSnapshotEnvelope(p.PolicySnapshot)?.Policy.CoveredCosts);
    }
    public static async Task<IReadOnlyDictionary<Guid, BenefitClaimBalanceDto>> BalancesAsync(ZayraDbContext db, Guid tenantId,
        int employeeId, IReadOnlyList<BenefitEnrollment> enrollments, DateOnly today, CancellationToken ct, DateOnly? expenseDate = null)
    {
        var requests = await db.ApprovalRequests.AsNoTracking().Where(x => x.TenantId == tenantId && x.EntityName == EntityName
            && x.RequestedForEmployeeId == employeeId && (x.Status == "Pending" || x.Status == "Approved")).ToListAsync(ct);
        var claims = requests.Select(x => (Proposal: Read(x), x.Status)).ToList();
        var result = new Dictionary<Guid, BenefitClaimBalanceDto>();
        Guid? publicId = null; List<EmployeeDependent>? children = null;
        foreach (var row in enrollments)
        {
            var policy = BenefitPaymentPolicies.ReadSnapshot(row);
            if (policy.Delivery != "Reimbursement" || !BenefitLimitPeriods.All.Contains(row.LimitPeriod)) continue;
            var limits = new[] { row.MaximumBenefitAmount, row.RequestedBenefitAmount, policy.Amount }.Where(x => x.HasValue).Select(x => x!.Value).ToList();
            var maximum = limits.Count == 0 ? 0 : limits.Min();
            var balanceDate = expenseDate ?? today;
            var forChild = (policy.ClaimFor ?? "Employee") == "Child";
            var rules = new BenefitClaimRulesDto(policy.ClaimFor ?? "Employee", policy.LimitPerChild, policy.MaxChildren, policy.ExtraChildByException,
                policy.CoveredCosts, policy.AllowedCountries, policy.ClaimTerms, row.LimitPeriod);
            DateOnly from, to;
            if (row.LimitPeriod == BenefitLimitPeriods.ContractYear)
            {
                publicId ??= await db.Employees.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == employeeId).Select(x => x.PublicId).FirstOrDefaultAsync(ct);
                try { (from, to) = await PeriodAsync(db, tenantId, publicId.Value, row, balanceDate, ct); }
                catch (InvalidOperationException ex)
                {
                    result[row.Id] = new(false, maximum, 0, 0, 0, balanceDate, balanceDate, null, rules, ex.Message);
                    continue;
                }
            }
            else (from, to) = Period(row, balanceDate);
            var chain = row.OriginalEnrollmentId ?? row.Id;
            var matching = claims.Where(x => x.Proposal.BenefitPlanId == row.BenefitPlanId && x.Proposal.ExpenseDate >= from && x.Proposal.ExpenseDate <= to
                && (row.LimitPeriod != BenefitLimitPeriods.PerEnrollment || x.Proposal.EnrollmentChainId == chain)).ToList();
            var reserved = matching.Where(x => x.Status == "Pending").Sum(x => x.Proposal.Amount);
            var approved = matching.Where(x => x.Status == "Approved").Sum(x => x.Proposal.Amount);
            var remaining = Math.Max(0, maximum - reserved - approved);
            var inWindow = !row.EffectiveTo.HasValue || !policy.ClaimWindowDays.HasValue
                || today.DayNumber - row.EffectiveTo.Value.DayNumber <= policy.ClaimWindowDays.Value;
            if (expenseDate.HasValue) inWindow = balanceDate <= today && balanceDate >= row.EffectiveFrom
                && (!row.EffectiveTo.HasValue || balanceDate <= row.EffectiveTo)
                && (!policy.ClaimWindowDays.HasValue || today.DayNumber - balanceDate.DayNumber <= policy.ClaimWindowDays.Value);
            var open = row.Status == "Active" && row.EffectiveFrom <= today && inWindow;
            if (!forChild)
            {
                result[row.Id] = new(open && remaining > 0, maximum, reserved, approved, remaining, from, to, null, rules);
                continue;
            }
            // Per child: every recorded child plus anyone who already claimed in this period. With LimitPerChild each child has the
            // full limit and the headline Remaining is the most one claim can take; otherwise children share the one budget.
            children ??= await db.EmployeeDependents.AsNoTracking().Where(x => x.TenantId == tenantId && x.EmployeeId == employeeId).ToListAsync(ct);
            var counted = matching.Where(x => x.Proposal.DependantId.HasValue).Select(x => x.Proposal.DependantId!.Value).ToHashSet();
            var listed = children.Where(x => !x.IsDeleted && PackageRules.IsChild(x.Relationship)).OrderBy(x => x.DateOfBirth ?? DateOnly.MaxValue).ThenBy(x => x.FullName)
                .Select(x => (x.Id, Name: x.FullName)).ToList();
            foreach (var id in counted.Where(id => listed.All(x => x.Id != id)))
                listed.Add((id, children.FirstOrDefault(x => x.Id == id)?.FullName ?? matching.First(x => x.Proposal.DependantId == id).Proposal.DependantName ?? "Child"));
            var items = listed.Select(c =>
            {
                var mine = matching.Where(x => x.Proposal.DependantId == c.Id).ToList();
                var r = mine.Where(x => x.Status == "Pending").Sum(x => x.Proposal.Amount);
                var a = mine.Where(x => x.Status == "Approved").Sum(x => x.Proposal.Amount);
                var within = policy.MaxChildren is not int max || counted.Contains(c.Id) || counted.Count + 1 <= max;
                return new BenefitChildBalanceDto(c.Id, c.Name, maximum, r, a, policy.LimitPerChild ? Math.Max(0, maximum - r - a) : remaining, within);
            }).ToList();
            var claimable = items.Where(x => x.RemainingAmount > 0 && (x.WithinChildLimit || policy.ExtraChildByException)).ToList();
            var headline = policy.LimitPerChild ? (claimable.Count == 0 ? 0 : claimable.Max(x => x.RemainingAmount)) : remaining;
            var blocked = listed.Count == 0 ? "No children are recorded for this employee. HR adds them under the employee's dependants." : null;
            result[row.Id] = new(open && claimable.Count > 0, maximum, reserved, approved, headline, from, to, items, rules, blocked);
        }
        return result;
    }

    public static async Task EnsureReceiptMutableAsync(ZayraDbContext db, Guid tenantId, Guid documentId, CancellationToken ct)
    {
        var document = await db.EmployeeDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == documentId, ct);
        if (document is null) return;
        var claims = await db.ApprovalRequests.AsNoTracking().Where(x => x.TenantId == tenantId && x.EntityName == EntityName
            && x.RequestedForEmployeeId == document.EmployeeId).ToListAsync(ct);
        if (claims.Any(x => Read(x).Receipts.Any(r => r.Id == documentId)))
            throw new InvalidOperationException("This document is retained as benefit claim evidence and cannot be changed or archived.");
    }

    public static void CheckReceipt(EmployeeDocument document, byte[] bytes)
    {
        var verdict = EssUploadPolicy.Check(document.ContentType, document.FileName, bytes, EssUploadPolicy.MaxDocumentBytes, EssUploadPolicy.DocumentTypes);
        if (!verdict.Ok) throw new InvalidOperationException(verdict.Error);
    }
    public static void Actor(RequestContext context, Guid tenantId, string permission)
    {
        if (context.TenantId != tenantId || context.UserId is null || context.UserId == Guid.Empty || !(context.Permissions?.Contains(permission, StringComparer.OrdinalIgnoreCase) ?? false))
            throw new InvalidOperationException($"Authenticated {permission} authority is required in this tenant.");
    }
    public static void Audit(ZayraDbContext db, ApprovalRequest approval, Guid? actor, string action, object detail) => db.AuditLogs.Add(new AuditLog
    {
        TenantId = approval.TenantId, CompanyId = approval.CompanyId, UserId = actor, EntityName = EntityName, EntityId = approval.Id.ToString(),
        Action = action, Metadata = JsonSerializer.Serialize(detail, Json),
    });
}
