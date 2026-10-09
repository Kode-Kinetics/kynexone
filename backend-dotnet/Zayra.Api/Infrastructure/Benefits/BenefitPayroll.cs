using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Benefits;

public sealed class BenefitPaymentException(string message) : InvalidOperationException(message);
public record BenefitPayrollComponent(Guid Id, string Code, string Name, string ComponentType, bool IsTaxable);
public record BenefitPayrollPolicy(string Delivery = "Coverage", decimal? Amount = null, string Frequency = "Monthly",
    int? PaymentMonth = null, bool Prorate = false, Guid? SalaryComponentId = null);
public record BenefitPayrollPolicyEnvelope(int Version, Guid PlanId, string Currency, BenefitPayrollPolicy Policy,
    BenefitPayrollComponent? SalaryComponent);
public record BenefitPayrollWitness(int Version, Guid BenefitPlanId, Guid EnrollmentId, Guid ChainId, string PlanName,
    string Currency, DateOnly PeriodStart, DateOnly PeriodEnd, string PolicySnapshot, BenefitPayrollComponent Component,
    string? GlDriverKey = null);

/// <summary>Creates source-authorized payroll adjustments inside the payroll transaction. The enrollment
/// snapshot is the authority; current catalogue edits never change a previously agreed benefit.</summary>
public static class BenefitPayroll
{
    public const string RecurringSource = "BenefitRecurring";
    public const string ClaimSource = "BenefitClaim";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    /// <summary>Serializes only the agreed, typed payment witness, never a payroll or Employee entity.</summary>
    public static string SerializeWitness(BenefitPayrollWitness witness) => JsonSerializer.Serialize(witness, Json);
    public static bool IsBenefit(PayrollAdjustment row) => row.SourceType is RecurringSource or ClaimSource;
    public static BenefitPayrollWitness? Read(PayrollAdjustment row)
    {
        if (!IsBenefit(row)) return null;
        try { return JsonSerializer.Deserialize<BenefitPayrollWitness>(row.SourceSnapshotJson, Json); }
        catch (JsonException) { throw new BenefitPaymentException("A benefit payroll witness is invalid. Reconcile this source before processing payroll."); }
    }
    public static BenefitPayrollPolicyEnvelope? Policy(string snapshot)
    {
        try
        {
            var policy = JsonSerializer.Deserialize<BenefitPayrollPolicyEnvelope>(snapshot, Json);
            return policy is { Version: > 0, Policy: not null } ? policy : null;
        }
        catch (JsonException) { throw new BenefitPaymentException("A benefit payment policy snapshot is invalid."); }
    }
    public static Guid SourceId(Guid chainId, string frequency, DateOnly periodStart, string delivery)
    {
        var period = frequency == "OneTime" ? "once" : frequency == "Annual" ? periodStart.Year.ToString() : periodStart.ToString("yyyy-MM");
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"benefit:{chainId:D}:{delivery}:{period}"))[..16]);
    }
    public static decimal CalculateDue(BenefitEnrollment enrollment, BenefitPayrollPolicy policy, DateOnly start, DateOnly end)
        => Math.Round(CalculateUnrounded(enrollment, policy, start, end), 2, MidpointRounding.AwayFromZero);
    private static decimal CalculateUnrounded(BenefitEnrollment enrollment, BenefitPayrollPolicy policy, DateOnly start, DateOnly end)
    {
        if (enrollment.Status != "Active" || enrollment.EffectiveFrom > end || enrollment.EffectiveTo < start
            || policy.Delivery is not ("SalaryAllowance" or "PayrollDeduction") || policy.Amount is null or <= 0) return 0;
        if (policy.Frequency == "Annual" && policy.PaymentMonth != start.Month) return 0;
        if (policy.Frequency is not ("Monthly" or "Annual" or "OneTime")) throw new BenefitPaymentException("Unsupported benefit payment frequency.");
        var amount = policy.Amount.Value;
        if (policy.Prorate && policy.Frequency == "Monthly")
        {
            var from = enrollment.EffectiveFrom > start ? enrollment.EffectiveFrom : start;
            var to = enrollment.EffectiveTo is { } until && until < end ? until : end;
            amount *= (decimal)(to.DayNumber - from.DayNumber + 1) / (end.DayNumber - start.DayNumber + 1);
        }
        return amount;
    }

    public static async Task<List<PayrollAdjustment>> StageAsync(ZayraDbContext db, PayrollRun run, Guid companyId,
        string currency, IReadOnlyList<Employee> employees, bool includeRecurring, DateOnly start, DateOnly end,
        CancellationToken ct)
    {
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            throw new BenefitPaymentException("Benefit payroll preparation requires the payroll transaction.");
        var tenantId = run.TenantId;
        var ids = employees.Select(e => e.Id).ToArray();
        foreach (var employee in employees.OrderBy(e => e.PublicId))
            await FinanceDecisionSerializer.AcquireAsync(db, AdditionalBenefitGrants.LockScope, tenantId, employee.PublicId, ct);
        var enrollments = await db.BenefitEnrollments.AsNoTracking().Where(e => e.TenantId == tenantId
            && e.CompanyId == companyId && ids.Contains(e.EmployeeId)).ToListAsync(ct);
        if (await Entitlements.EntitlementMatrixService.ReleaseAEnabledAsync(db, tenantId, ct))
        {
            if (enrollments.Any(e => Policy(e.PaymentPolicySnapshotJson)?.Policy.Delivery is "SalaryAllowance" or "PayrollDeduction" or "Reimbursement"))
                throw new BenefitPaymentException("This company has benefit payment terms from the previous benefits authority. Reconcile them before running payroll under the current package authority.");
            return [];
        }
        var allSourceRows = await db.PayrollAdjustments.Where(a => a.TenantId == tenantId && ids.Contains(a.EmployeeId)
            && (a.SourceType == RecurringSource || a.SourceType == ClaimSource)).ToListAsync(ct);
        var sourceIds = allSourceRows.Where(a => a.SourceId.HasValue).ToDictionary(a => (a.SourceType, a.SourceId!.Value));
        var priorRunIds = allSourceRows.Select(a => a.PayrollRunId).Distinct().ToList();
        var runs = await db.PayrollRuns.AsNoTracking().Where(r => r.TenantId == tenantId && priorRunIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var plans = await db.BenefitPlans.AsNoTracking().Where(p => p.TenantId == tenantId && enrollments.Select(e => e.BenefitPlanId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var results = new List<PayrollAdjustment>();
        var expected = new HashSet<Guid>();

        async Task Add(string source, Guid sourceId, BenefitEnrollment enrollment, string policyJson, decimal amount, string planName)
        {
            var envelope = Policy(policyJson) ?? throw new BenefitPaymentException("The benefit payment policy is missing.");
            var component = envelope.SalaryComponent ?? throw new BenefitPaymentException("The benefit has no agreed payroll component.");
            if (!string.Equals(envelope.Currency, currency, StringComparison.OrdinalIgnoreCase)
                || envelope.PlanId != enrollment.BenefitPlanId || enrollment.CompanyId != companyId)
                throw new BenefitPaymentException("Benefit currency or company does not match this payroll run.");
            if (string.IsNullOrWhiteSpace(component.Code) || component.ComponentType != (amount < 0 ? "Deduction" : "Earning"))
                throw new BenefitPaymentException("Benefit payroll component does not match its payment direction.");
            if (sourceIds.TryGetValue((source, sourceId), out var existing))
            {
                if (!runs.TryGetValue(existing.PayrollRunId, out var previous) || previous.CompanyId != companyId)
                    throw new BenefitPaymentException("The previous benefit payroll source cannot be reconciled.");
                if (existing.PayrollRunId == run.Id)
                {
                    if (existing.Amount != amount || existing.Status != "Approved")
                        throw new BenefitPaymentException("Benefit terms changed after payroll preparation. Reconcile the existing source before reprocessing.");
                    expected.Add(existing.Id); results.Add(existing); return;
                }
                if (previous.Status == "Voided")
                {
                    if (existing.Status != "Approved" || existing.Amount != amount)
                        throw new BenefitPaymentException("The voided benefit source requires reconciliation before replacement payroll.");
                    var oldRunId = existing.PayrollRunId;
                    existing.PayrollRunId = run.Id;
                    db.AuditLogs.Add(new AuditLog { TenantId = tenantId, CompanyId = companyId, EntityName = "PayrollAdjustment",
                        EntityId = existing.Id.ToString(), Action = "benefits.payroll.rebound", Metadata = JsonSerializer.Serialize(new { sourceId, oldRunId, payrollRunId = run.Id }), CreatedAtUtc = DateTime.UtcNow });
                    expected.Add(existing.Id); results.Add(existing); return;
                }
                if (existing.Status == "Processed" && previous.Status is "Processed" or "Approved" or "Locked" or "Paid") return;
                throw new BenefitPaymentException($"A benefit is already reserved by payroll run {previous.Id}. Resolve that run before processing a replacement.");
            }
            var chain = enrollment.OriginalEnrollmentId ?? enrollment.Id;
            var witness = new BenefitPayrollWitness(1, enrollment.BenefitPlanId, enrollment.Id, chain, planName,
                envelope.Currency, start, end, policyJson, component);
            var row = new PayrollAdjustment { TenantId = tenantId, PayrollRunId = run.Id, EmployeeId = enrollment.EmployeeId,
                AdjustmentType = component.Code, Amount = amount, Reason = planName, Status = "Approved",
                SourceType = source, SourceId = sourceId, SourceSnapshotJson = SerializeWitness(witness) };
            db.PayrollAdjustments.Add(row); allSourceRows.Add(row); sourceIds[(source, sourceId)] = row;
            expected.Add(row.Id); results.Add(row);
            db.AuditLogs.Add(new AuditLog { TenantId = tenantId, CompanyId = companyId, EntityName = "PayrollAdjustment",
                EntityId = row.Id.ToString(), Action = "benefits.payroll.prepared", Metadata = JsonSerializer.Serialize(new { source, sourceId, amount, runId = run.Id, employeeId = enrollment.EmployeeId }), CreatedAtUtc = DateTime.UtcNow });
            await Task.CompletedTask;
        }

        if (includeRecurring)
        foreach (var group in enrollments.Where(e => e.Status == "Active" && e.EffectiveFrom <= end && (!e.EffectiveTo.HasValue || e.EffectiveTo >= start))
            .GroupBy(e => e.OriginalEnrollmentId ?? e.Id))
        {
            var ordered = group.OrderByDescending(e => e.EffectiveFrom).ThenByDescending(e => e.CreatedAtUtc).ToList();
            var latest = ordered[0];
            var envelope = Policy(latest.PaymentPolicySnapshotJson);
            if (envelope?.Policy.Delivery is not ("SalaryAllowance" or "PayrollDeduction")) continue;
            var policy = envelope.Policy;
            if (ordered.Any(e => {
                var other = Policy(e.PaymentPolicySnapshotJson);
                return other is null || other.Policy.Delivery != policy.Delivery || other.Policy.Frequency != policy.Frequency
                    || other.Policy.Prorate != policy.Prorate || other.Policy.PaymentMonth != policy.PaymentMonth
                    || other.SalaryComponent != envelope.SalaryComponent || other.Currency != envelope.Currency;
            })) throw new BenefitPaymentException("Benefit payment rules changed within the payroll period. Review the dated change before processing.");
            var amount = CalculateDue(latest, policy, start, end);
            // A dated successor must not cause two full monthly payments. Daily proration sums the
            // non-overlapping versions; an unprorated policy pays the latest agreed monthly amount once.
            if (policy.Prorate && policy.Frequency == "Monthly")
                amount = Math.Round(ordered.Sum(e => { var p = Policy(e.PaymentPolicySnapshotJson)?.Policy; return p is null ? 0m : CalculateUnrounded(e, p, start, end); }), 2, MidpointRounding.AwayFromZero);
            if (amount <= 0) continue;
            var sourceId = SourceId(group.Key, policy.Frequency, start, policy.Delivery);
            var cap = latest.MaximumBenefitAmount;
            if (cap.HasValue && !sourceIds.ContainsKey((RecurringSource, sourceId)))
            {
                var spent = allSourceRows.Where(a => a.EmployeeId == latest.EmployeeId && a.SourceId != sourceId)
                    .Select(a => (Row: a, Witness: Read(a))).Where(x => x.Witness?.BenefitPlanId == latest.BenefitPlanId
                        && (latest.LimitPeriod != "PerEnrollment" || x.Witness.ChainId == group.Key)
                        && InLimitPeriod(latest.LimitPeriod, x.Witness.PeriodStart, start)).Sum(x => Math.Abs(x.Row.Amount));
                if (spent + amount > cap.Value)
                    throw new BenefitPaymentException($"{plans.GetValueOrDefault(latest.BenefitPlanId)?.Name ?? "Benefit"} exceeds the employee's remaining benefit limit. Review the benefit before payroll.");
            }
            await Add(RecurringSource, sourceId, latest, latest.PaymentPolicySnapshotJson,
                policy.Delivery == "PayrollDeduction" ? -amount : amount, plans.GetValueOrDefault(latest.BenefitPlanId)?.Name ?? "Benefit");
        }

        var claims = await db.ApprovalRequests.AsNoTracking().Where(a => a.TenantId == tenantId && a.CompanyId == companyId
            && a.EntityName == "BenefitClaim" && a.Status == "Approved" && a.RequestedForEmployeeId.HasValue
            && ids.Contains(a.RequestedForEmployeeId.Value)).ToListAsync(ct);
        foreach (var approval in claims)
        {
            var claim = BenefitClaims.Read(approval);
            if (claim.ExpenseDate > end || approval.CompletedAtUtc is { } approvedAt && DateOnly.FromDateTime(approvedAt) > end) continue;
            var enrollment = enrollments.FirstOrDefault(e => e.Id == claim.EnrollmentId && e.EmployeeId == claim.EmployeeId)
                ?? throw new BenefitPaymentException("An approved benefit claim no longer has its original employee enrollment.");
            await Add(ClaimSource, approval.Id, enrollment, claim.PolicySnapshot, claim.Amount, claim.PlanName);
        }
        if (allSourceRows.Any(a => a.PayrollRunId == run.Id && a.Status == "Approved" && !expected.Contains(a.Id)))
            throw new BenefitPaymentException("A prepared benefit is no longer eligible for this payroll. Reconcile its source before processing.");
        await db.SaveChangesAsync(ct);
        return results;
    }

    public static bool InLimitPeriod(string period, DateOnly date, DateOnly target) => period switch
    {
        "Monthly" => date.Year == target.Year && date.Month == target.Month,
        "Annual" => date.Year == target.Year,
        "Lifetime" or "PerEnrollment" => true,
        _ => throw new BenefitPaymentException("The benefit limit period is invalid.")
    };

    public static async Task EnsureMutableAsync(ZayraDbContext db, Guid tenantId, Guid enrollmentId,
        DateOnly affectedFrom, CancellationToken ct)
    {
        var enrollment = await db.BenefitEnrollments.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == enrollmentId, ct);
        if (enrollment is null) return;
        var chain = enrollment.OriginalEnrollmentId ?? enrollment.Id;
        var sources = await (from a in db.PayrollAdjustments.AsNoTracking()
            join r in db.PayrollRuns.AsNoTracking() on a.PayrollRunId equals r.Id
            where a.TenantId == tenantId && r.TenantId == tenantId && a.EmployeeId == enrollment.EmployeeId
                && a.SourceType == RecurringSource && r.Status != "Voided"
            select a).ToListAsync(ct);
        if (sources.Any(a => Read(a) is { } witness && witness.ChainId == chain && witness.PeriodEnd >= affectedFrom))
            throw new BenefitPaymentException("This benefit has already been included in payroll for the affected dates. Use an effective date after that payroll period.");
    }
}
