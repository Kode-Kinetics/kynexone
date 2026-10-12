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

public record BenefitClaimRequest(Guid EnrollmentId, decimal Amount, DateOnly ExpenseDate, string InvoiceReference,
    string Description, IReadOnlyList<Guid> DocumentIds);
public record BenefitReceiptWitness(Guid Id, int VersionNumber, string FileName, string ContentType, string Sha256, string StorageKey);
public record BenefitReceiptDto(Guid Id, string FileName, string ContentType, int VersionNumber);
public record BenefitReceiptUploadWitness(int EmployeeId, int VersionNumber, string Sha256);
public record BenefitClaimProposal(int Version, Guid RequestId, Guid TenantId, Guid? CompanyId, Guid? GradeId,
    int EmployeeId, string EmployeeName, Guid EnrollmentId, Guid EnrollmentChainId, Guid BenefitPlanId,
    string PlanName, string Currency, decimal Amount, DateOnly ExpenseDate, string InvoiceReference, string Description,
    string LimitPeriod, decimal MaximumAmount, DateOnly PeriodFrom, DateOnly PeriodTo, string PolicySnapshot,
    DateTime? EnrollmentUpdatedAtUtc, IReadOnlyList<BenefitReceiptWitness> Receipts, string WorkflowSha256);
public record BenefitClaimDto(Guid Id, string Status, int EmployeeId, string EmployeeName, Guid EnrollmentId,
    Guid BenefitPlanId, string PlanName, string Currency, decimal Amount, DateOnly ExpenseDate, string InvoiceReference,
    string Description, IReadOnlyList<BenefitReceiptDto> Receipts, DateTime CreatedAtUtc, Guid ApprovalRequestId,
    Guid? PayrollRunId, string SettlementStatus, decimal ReservedAmount, decimal RemainingAmount,
    Guid? RequestedByUserId = null, bool CanWithdraw = false);
public record BenefitClaimBalanceDto(bool CanClaim, decimal MaximumAmount, decimal ReservedAmount, decimal ApprovedAmount,
    decimal RemainingAmount, DateOnly PeriodFrom, DateOnly PeriodTo);

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
            var proposal = new BenefitClaimProposal(1, requestId, tenantId, employee.CompanyId, employee.GradeId, employee.Id, employee.FullName,
                row.Id, row.OriginalEnrollmentId ?? row.Id, row.BenefitPlanId, planName, policy.Currency, input.Amount, input.ExpenseDate,
                input.InvoiceReference.Trim(), input.Description.Trim(), row.LimitPeriod, maximum, from, to, row.PaymentPolicySnapshotJson,
                row.UpdatedAtUtc, receipts, AdditionalBenefitGrants.WorkflowDigest(route.Steps));
            await ValidateBudgetAsync(db, proposal, null, ct);
            var first = route.FirstStep;
            var approver = await router.ResolveApproverAsync(tenantId, employee.Id, first, ct);
            var now = DateTime.UtcNow;
            var payload = JsonSerializer.Serialize(proposal, Json);
            var approval = new ApprovalRequest
            {
                Id = requestId, TenantId = tenantId, CompanyId = employee.CompanyId, EntityName = EntityName, EntityId = requestId.ToString(),
                WorkflowId = route.WorkflowId, RequestedForEmployeeId = employee.Id, RequestedByUserId = context.UserId,
                Title = $"Benefit claim: {planName} · {employee.FullName}", Payload = payload, PayloadSha256 = AdditionalBenefitGrants.Digest(payload),
                CurrentStepOrder = first.StepOrder, CurrentApproverType = approver.EmployeeId.HasValue ? approver.ApproverType : "Role",
                CurrentApproverEmployeeId = approver.EmployeeId, CurrentApproverUserId = approver.UserId, CurrentApproverName = approver.Name,
                CurrentApproverRole = approver.QueueRole, CurrentQueue = approver.EmployeeId.HasValue ? $"{approver.ApproverType}:{approver.Name}" : $"Role:{approver.QueueRole}",
                SlaHours = Math.Clamp(first.EscalationAfterHours ?? 24, 1, 720), LastRoutedAtUtc = now,
                DueAtUtc = now.AddHours(Math.Clamp(first.EscalationAfterHours ?? 24, 1, 720)),
            };
            db.ApprovalRequests.Add(approval);
            Audit(db, approval, context.UserId, "benefits.claim.requested", new { requestId, proposal.Amount, proposal.EnrollmentId, proposal.InvoiceReference });
            await db.SaveChangesAsync(ct);
            return approval;
        }, ct);
    }

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
        var (from, to) = Period(row, expenseDate);
        return (policy, limits.Min(), from, to);
    }
    public static (DateOnly From, DateOnly To) Period(BenefitEnrollment row, DateOnly date) => row.LimitPeriod switch
    {
        BenefitLimitPeriods.Monthly => (new(date.Year, date.Month, 1), new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month))),
        BenefitLimitPeriods.Annual => (new(date.Year, 1, 1), new(date.Year, 12, 31)),
        BenefitLimitPeriods.Lifetime or BenefitLimitPeriods.PerEnrollment => (DateOnly.MinValue, DateOnly.MaxValue),
        _ => throw new InvalidOperationException("The reimbursement entitlement needs a supported limit period."),
    };
    private static bool SameBudget(BenefitClaimProposal current, BenefitClaimProposal other) => current.BenefitPlanId == other.BenefitPlanId
        && other.ExpenseDate >= current.PeriodFrom && other.ExpenseDate <= current.PeriodTo
        && (current.LimitPeriod != BenefitLimitPeriods.PerEnrollment || current.EnrollmentChainId == other.EnrollmentChainId);
    public static async Task<decimal> ReservedAsync(ZayraDbContext db, BenefitClaimProposal p, Guid? exclude, CancellationToken ct)
    {
        var rows = await db.ApprovalRequests.AsNoTracking().Where(x => x.TenantId == p.TenantId && x.EntityName == EntityName
            && x.RequestedForEmployeeId == p.EmployeeId && (x.Status == "Pending" || x.Status == "Approved") && x.Id != exclude).ToListAsync(ct);
        return rows.Select(Read).Where(x => SameBudget(p, x)).Sum(x => x.Amount);
    }
    private static async Task ValidateBudgetAsync(ZayraDbContext db, BenefitClaimProposal p, Guid? exclude, CancellationToken ct)
    {
        var rows = await db.ApprovalRequests.AsNoTracking().Where(x => x.TenantId == p.TenantId && x.EntityName == EntityName
            && x.RequestedForEmployeeId == p.EmployeeId && (x.Status == "Pending" || x.Status == "Approved") && x.Id != exclude).ToListAsync(ct);
        var existing = rows.Select(Read).ToList();
        if (existing.Any(x => x.BenefitPlanId == p.BenefitPlanId && x.InvoiceReference.Equals(p.InvoiceReference, StringComparison.OrdinalIgnoreCase)
            || x.Receipts.Any(r => p.Receipts.Any(n => n.Sha256 == r.Sha256))))
            throw new InvalidOperationException("This invoice or receipt already has a pending or approved claim. A receipt cannot be claimed again through another benefit plan.");
        if (existing.Where(x => SameBudget(p, x)).Sum(x => x.Amount) + p.Amount > p.MaximumAmount)
            throw new InvalidOperationException("The claim exceeds the remaining benefit limit, including pending and approved claims.");
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
        await ValidateEntitlementAsync(db, approval.TenantId, employee, row, p.Amount, p.ExpenseDate, clock ?? new TenantClock(db, TimeProvider.System), ct, enforceSubmissionWindow: false);
        await ValidateBudgetAsync(db, p, approval.Id, ct);
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
        Audit(db, approval, context.UserId, "benefits.claim.approved", new { p.Amount, p.Currency, p.EnrollmentId, approval.WorkflowId, approval.CurrentStepOrder });
    }

    public static BenefitClaimProposal Read(ApprovalRequest approval)
    {
        if (!IsClaim(approval) || approval.Payload is null || approval.PayloadSha256 is null || AdditionalBenefitGrants.Digest(approval.Payload) != approval.PayloadSha256)
            throw new InvalidOperationException("The sealed claim witness is missing or changed.");
        BenefitClaimProposal? p;
        try { p = JsonSerializer.Deserialize<BenefitClaimProposal>(approval.Payload, Json); }
        catch (JsonException) { throw new InvalidOperationException("Invalid claim witness."); }
        if (p is null || p.Version != 1 || p.RequestId != approval.Id || p.TenantId != approval.TenantId || p.CompanyId != approval.CompanyId
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
            approval.CreatedAtUtc, approval.Id, adjustment?.PayrollRunId, settlement, reserved, Math.Max(0, p.MaximumAmount - reserved), approval.RequestedByUserId, approval.Status == "Pending" && approval.RequestedByUserId == callerUserId && callerUserId.HasValue);
    }
    public static async Task<IReadOnlyDictionary<Guid, BenefitClaimBalanceDto>> BalancesAsync(ZayraDbContext db, Guid tenantId,
        int employeeId, IReadOnlyList<BenefitEnrollment> enrollments, DateOnly today, CancellationToken ct, DateOnly? expenseDate = null)
    {
        var requests = await db.ApprovalRequests.AsNoTracking().Where(x => x.TenantId == tenantId && x.EntityName == EntityName
            && x.RequestedForEmployeeId == employeeId && (x.Status == "Pending" || x.Status == "Approved")).ToListAsync(ct);
        var claims = requests.Select(x => (Proposal: Read(x), x.Status)).ToList();
        var result = new Dictionary<Guid, BenefitClaimBalanceDto>();
        foreach (var row in enrollments)
        {
            var policy = BenefitPaymentPolicies.ReadSnapshot(row);
            if (policy.Delivery != "Reimbursement" || !BenefitLimitPeriods.All.Contains(row.LimitPeriod)) continue;
            var limits = new[] { row.MaximumBenefitAmount, row.RequestedBenefitAmount, policy.Amount }.Where(x => x.HasValue).Select(x => x!.Value).ToList();
            var maximum = limits.Count == 0 ? 0 : limits.Min();
            var balanceDate = expenseDate ?? today;
            var (from, to) = Period(row, balanceDate);
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
            result[row.Id] = new(row.Status == "Active" && row.EffectiveFrom <= today && inWindow && remaining > 0,
                maximum, reserved, approved, remaining, from, to);
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
