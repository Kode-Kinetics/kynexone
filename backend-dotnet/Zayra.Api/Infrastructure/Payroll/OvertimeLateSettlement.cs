using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// ONE behaviour for overtime that is approved after its own payroll period was run, shared by the approval endpoint
/// (OVT-04), payroll validation (rule 11b, <see cref="PayrollValidationEngine.PriorPeriodOvertimeUnpaid"/>) and the run itself.
///
/// <para>The tenant's <c>overtime.late_approval_mode</c> decides:</para>
/// <list type="bullet">
/// <item><b>Refuse</b> (default): nothing pays it automatically. At approval the request stays pending (409); an approved impact
/// that is nonetheless still <c>PendingPayroll</c> after its period's run consumed its month is flagged by rule 11b, which names
/// this setting.</item>
/// <item><b>BackPay</b>: the next run that pays recurring items pays it, as an <c>Overtime Back Pay</c> adjustment line (GL driver
/// <c>EARN:OVERTIME</c>), and moves the impact to <c>Processed</c> with the run's id in the SAME transaction as the run's
/// processing. A later run, or rule 11b, can then never see it again. Rule 11b does not flag what this will pay.</item>
/// </list>
///
/// <para>Approval-time back pay (period locked) is unchanged: the impact is already <c>CarriedForward</c>, so this class never sees it.</para>
/// </summary>
public static class OvertimeLateSettlement
{
    public const string Refuse = "Refuse";
    public const string BackPay = "BackPay";

    /// <summary>The adjustment type; <c>PayrollRunProcessor.EarningDriverKey</c> routes ADJ_OVERTIME_BACK_PAY to overtime expense.</summary>
    public const string AdjustmentType = "Overtime Back Pay";

    /// <summary>Whether a run in this mode pays prior-period overtime itself. Only a run that pays recurring items pays overtime.</summary>
    public static bool SettlesInRun(string? lateApprovalMode, bool runPaysRecurringItems) =>
        runPaysRecurringItems && string.Equals(lateApprovalMode, BackPay, StringComparison.Ordinal);

    /// <summary>An impact with nothing to pay (an unresolved rate gives amount 0) cannot be settled; it stays flagged.</summary>
    public static bool IsSettleable(PriorPeriodOvertime c) => c.ImpactId != Guid.Empty && c.Amount > 0m;

    /// <summary>One prior-period impact this run pays, with the adjustment that carries the money.</summary>
    public sealed record Staged(PriorPeriodOvertime Source, PayrollAdjustment Adjustment);

    /// <summary>
    /// Creates (or reuses/rebinds) the one <c>Overtime Back Pay</c> adjustment per request on <paramref name="run"/>, Approved, so
    /// the run's slip loop pays it like any other adjustment. Runs INSIDE the processing transaction: the caller then marks the
    /// impacts <c>Processed</c> in the same transaction (compare-and-swap on PendingPayroll), so the money and the
    /// "paid" mark commit or roll back together.
    ///
    /// <para>Adjustments are unique per request (tenant, source type, source id). One already on THIS run is reused (a re-process);
    /// one on a voided run is rebound to this run; <see cref="PriorPeriodOvertimeFinder"/> never offers a request whose adjustment
    /// sits on another live run.</para>
    /// </summary>
    public static async Task<IReadOnlyList<Staged>> StageAsync(
        ZayraDbContext db, PayrollRun run, IReadOnlyCollection<PriorPeriodOvertime> settleable, CancellationToken ct)
    {
        var staged = new List<Staged>();
        if (settleable.Count == 0) return staged;
        var tenantId = run.TenantId;
        var requestIds = settleable.Select(s => s.OvertimeRequestId).ToList();
        var existing = await db.PayrollAdjustments
            .Where(a => a.TenantId == tenantId && a.SourceType == PayrollAdjustmentSources.OvertimeBackPay
                        && a.SourceId != null && requestIds.Contains(a.SourceId.Value))
            .ToDictionaryAsync(a => a.SourceId!.Value, ct);
        var carriedTo = $"{run.Year}-{run.Month:00}";

        foreach (var s in settleable.OrderBy(x => x.EmployeeId).ThenBy(x => x.WorkDate))
        {
            var reason = $"Overtime back pay for work date {s.WorkDate:yyyy-MM-dd} (closed period {s.WorkDate:yyyy-MM}): paid by the "
                       + $"{carriedTo} run under {Policy.PolicyRegistry.OvertimeLateApprovalMode} = {BackPay}.";
            var snapshot = JsonSerializer.Serialize(new
            {
                overtimeRequestId = s.OvertimeRequestId,
                originalWorkDate = s.WorkDate.ToString("yyyy-MM-dd"),
                originalPeriod = s.WorkDate.ToString("yyyy-MM"),
                carriedToPeriod = carriedTo,
                reason,
                approvedMinutes = s.Minutes,
                multiplier = s.Multiplier,
                amount = s.Amount,
                overtimePolicyId = s.OvertimePolicyId,
                settledBy = "payroll_run",
            });

            if (existing.TryGetValue(s.OvertimeRequestId, out var adj))
            {
                // Same run (re-process) or a voided run (replacement): bring it to this run at the impact's amount.
                adj.PayrollRunId = run.Id;
                adj.EmployeeId = s.EmployeeId;
                adj.Amount = s.Amount;
                adj.Status = "Approved";
                adj.Reason = reason;
                adj.SourceSnapshotJson = snapshot;
            }
            else
            {
                adj = new PayrollAdjustment
                {
                    TenantId = tenantId, PayrollRunId = run.Id, EmployeeId = s.EmployeeId,
                    AdjustmentType = AdjustmentType, Amount = s.Amount, Status = "Approved", Reason = reason,
                    SourceType = PayrollAdjustmentSources.OvertimeBackPay, SourceId = s.OvertimeRequestId,
                    SourceSnapshotJson = snapshot,
                };
                db.PayrollAdjustments.Add(adj);
            }
            staged.Add(new Staged(s, adj));
            db.AuditLogs.Add(new AuditLog
            {
                TenantId = tenantId, CompanyId = run.CompanyId, EntityName = "OvertimeRequest",
                EntityId = s.OvertimeRequestId.ToString(), Action = "overtime.request.backpay_settled_by_run",
                Metadata = JsonSerializer.Serialize(new
                {
                    originalWorkDate = s.WorkDate.ToString("yyyy-MM-dd"), runId = run.Id, carriedToPeriod = carriedTo,
                    amount = s.Amount, adjustmentId = adj.Id, impactId = s.ImpactId,
                }),
                CreatedAtUtc = DateTime.UtcNow,
            });
        }
        return staged;
    }
}
