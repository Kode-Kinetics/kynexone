using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Employees;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Pay HELD for a suspended employee: computed by the run, kept out of net pay, and never lost.
///
/// <para><b>Where it lives.</b> A hold is a <see cref="PayrollAdjustment"/> on the run that computed it, with
/// <see cref="HoldSource"/> as its source type and status <see cref="Held"/>. Process consumes only Approved adjustments,
/// so a Held row is never paid by the run it sits on, and every other adjustment reader filters on its own source type.
/// No new table: the fact is "an amount owed to one employee, attached to one run, awaiting a decision".</para>
///
/// <para><b>What happens to it.</b> Finance (<c>payroll.approve</c>, never the run's preparer) either releases it, which
/// writes ONE Approved <see cref="ReleaseSource"/> adjustment on the next open run (unique per hold through the
/// (tenant, source type, source id) index, so it is paid exactly once, as a labelled earnings line), or forfeits it with a
/// reason. Both write an audit entry. The hold stays listed (on its own run and on every later run of the company) until
/// one of the two happens, whatever the employee's status becomes.</para>
///
/// <para><b>Not decided here.</b> When a forfeiture is lawful (KSA Labour Law Art. 69) is a lawyer's question: this is the
/// mechanism only. The held amount is the recurring package for the period (prorated for a joiner), before deductions;
/// when it is released it is an earning of the run that pays it, so that run's deductions apply to it.</para>
/// </summary>
public static class HeldPay
{
    public const string HoldSource = "SuspendedPayHold";
    public const string ReleaseSource = "HeldPayRelease";

    public const string Held = "Held";
    public const string Released = "Released";
    public const string Forfeited = "Forfeited";

    /// <summary>The reason recorded on every hold this mechanism writes.</summary>
    public const string SuspendedReason = "suspended";

    public const string HoldAdjustmentType = "Held pay (suspended)";
    public const string ReleaseAdjustmentType = "Held pay released";

    public const string ActionPay = "pay";
    public const string ActionForfeit = "forfeit";

    /// <summary>One suspended employee's pay for the period, computed by the run.</summary>
    public sealed record Candidate(int EmployeeId, string EmployeeCode, string EmployeeName, decimal Amount, object Breakdown);

    /// <summary>Statuses in which a run can still be processed (and so could still write or rewrite a hold).</summary>
    public static bool RunIsOpen(string? status) =>
        status is not ("Processed" or "PendingFinanceReview" or "Locked" or "Approved" or "Paid" or "Voided");

    /// <summary>
    /// Writes this run's holds, inside the run's own transaction. A re-process replaces the Held rows it wrote before; a
    /// hold already released or forfeited (on this run, or on any other run of the same company and period, voided ones
    /// included) is never written again, so a reopen or a replacement run cannot hold, and later pay, the same month twice.
    /// </summary>
    public static async Task StageAsync(
        ZayraDbContext db, PayrollRun run, Guid companyId, IReadOnlyCollection<Candidate> candidates, CancellationToken ct)
    {
        var tenantId = run.TenantId;
        var stale = await db.PayrollAdjustments
            .Where(a => a.TenantId == tenantId && a.PayrollRunId == run.Id && a.SourceType == HoldSource && a.Status == Held)
            .ToListAsync(ct);
        db.PayrollAdjustments.RemoveRange(stale);
        if (candidates.Count == 0) return;

        var ids = candidates.Select(c => c.EmployeeId).ToList();
        var decided = await (from a in db.PayrollAdjustments.AsNoTracking()
                             join r in db.PayrollRuns.AsNoTracking() on a.PayrollRunId equals r.Id
                             where a.TenantId == tenantId && r.TenantId == tenantId && a.SourceType == HoldSource
                                   && a.Status != Held && ids.Contains(a.EmployeeId)
                                   && r.Year == run.Year && r.Month == run.Month
                                   && (r.CompanyId == companyId || r.CompanyId == null)
                             // Including a VOIDED run's: a hold released or forfeited stays decided when the run that held it is
                             // voided, so a replacement run of the month can never hold, and later pay, the same month again.
                             select a.EmployeeId).Distinct().ToListAsync(ct);
        var period = $"{run.Year}-{run.Month:D2}";
        foreach (var c in candidates.Where(c => c.Amount > 0m && !decided.Contains(c.EmployeeId)))
        {
            db.PayrollAdjustments.Add(new PayrollAdjustment
            {
                TenantId = tenantId, PayrollRunId = run.Id, EmployeeId = c.EmployeeId,
                AdjustmentType = HoldAdjustmentType, Amount = c.Amount, Status = Held,
                Reason = SuspendedReason, SourceType = HoldSource, SourceId = null,
                SourceSnapshotJson = JsonSerializer.Serialize(new
                {
                    reason = SuspendedReason, period, employeeCode = c.EmployeeCode, employeeName = c.EmployeeName,
                    breakdown = c.Breakdown,
                }),
            });
        }
    }

    /// <summary>The result of a release or forfeit: an HTTP status and the body to answer with.</summary>
    public sealed record DecisionOutcome(int StatusCode, object Body);

    /// <summary>
    /// Finance's decision on one hold. The caller has already checked <c>payroll.approve</c>; this enforces everything
    /// else: maker-checker against the holding run's creator and processor, a holding run that can no longer be
    /// re-processed, exactly one decision per hold, a reason for a forfeit, an open run to pay into, and the audit entry.
    /// </summary>
    public static async Task<DecisionOutcome> DecideAsync(
        ZayraDbContext db, Guid tenantId, Guid holdId, Guid? actorUserId, string? action, string? reason, CancellationToken ct)
    {
        var verb = action?.Trim().ToLowerInvariant();
        if (verb is not (ActionPay or ActionForfeit))
            return new(400, new { error = "held_pay_action_invalid", message = "Choose to pay the held amount in the next open run, or to forfeit it." });
        var trimmedReason = reason?.Trim() ?? string.Empty;
        if (verb == ActionForfeit && trimmedReason.Length < 5)
            return new(400, new { error = "held_pay_reason_required", message = "Give the reason the held pay is forfeited (at least 5 characters)." });
        if (trimmedReason.Length > 1000)
            return new(400, new { error = "held_pay_reason_too_long", message = "Keep the reason under 1,000 characters." });

        var hold = await db.PayrollAdjustments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Id == holdId && a.SourceType == HoldSource, ct);
        if (hold is null) return new(404, new { error = "held_pay_not_found", message = "This held pay was not found." });
        // The company filter on runs applies here: a hold on a run the caller cannot see is not found either.
        var holdingRun = await db.PayrollRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == hold.PayrollRunId, ct);
        if (holdingRun is null) return new(404, new { error = "held_pay_not_found", message = "This held pay was not found." });
        if (hold.Status != Held)
            return new(409, new { error = "held_pay_already_decided", message = $"This held pay was already {hold.Status.ToLowerInvariant()}." });
        if (holdingRun.Status == "Voided")
            return new(409, new { error = "held_pay_run_voided", message = "The run that held this pay was voided. The replacement run holds it again." });
        if (RunIsOpen(holdingRun.Status))
            return new(409, new { error = "held_pay_run_open", message = "The run that held this pay can still be processed again. Release or forfeit the hold once that run is processed." });
        if (actorUserId is Guid actor && (holdingRun.CreatedByUserId == actor || holdingRun.ProcessedByUserId == actor))
            return new(403, new { error = "maker_checker_violation", message = "The user who created or processed this run cannot release or forfeit its held pay. A different approver is required (maker-checker policy)." });

        var heldPeriod = $"{holdingRun.Year}-{holdingRun.Month:D2}";
        PayrollRun? target = null;
        if (verb == ActionPay)
        {
            var employee = await db.Employees.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.Id == hold.EmployeeId)
                .Select(e => new { e.Status, e.IsDeleted })
                .FirstOrDefaultAsync(ct);
            if (employee is not null && !employee.IsDeleted && EmploymentStanding.Classify(employee.Status) == EmploymentStatusClass.PayHold)
                return new(409, new { error = "held_pay_employee_still_suspended", message = "The employee is still suspended, so no run would pay them. Reactivate the employee first, or forfeit the held pay." });
            var candidates = await db.PayrollRuns.AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.Id != holdingRun.Id && r.IncludesRecurringPay
                         && (r.CompanyId == holdingRun.CompanyId || (holdingRun.CompanyId == null && r.CompanyId == null)))
                .OrderBy(r => r.Year).ThenBy(r => r.Month).ThenBy(r => r.CreatedAtUtc)
                .ToListAsync(ct);
            target = candidates.FirstOrDefault(r => RunIsOpen(r.Status));
            if (target is null)
                return new(409, new { error = "held_pay_no_open_run", message = "There is no open payroll run to pay this into. Create the next payroll run, then release the held pay." });
        }

        var newStatus = verb == ActionPay ? Released : Forfeited;
        var won = await InTransactionAsync(db, async () =>
        {
            // Compare-and-swap on the status: of two decisions racing on one hold, exactly one wins; the loser writes nothing.
            var claimed = await db.PayrollAdjustments
                .Where(a => a.TenantId == tenantId && a.Id == hold.Id && a.SourceType == HoldSource && a.Status == Held)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, newStatus), ct);
            if (claimed != 1) return false;
            if (target is not null)
            {
                // One stable type (so one payslip code, ADJ_HELD_PAY_RELEASED); the period it was held in is in the label.
                // The (tenant, source type, source id) unique index makes the payment exactly-once even past this check.
                var label = $"{SuspendedReason}, held in {heldPeriod}";
                db.PayrollAdjustments.Add(new PayrollAdjustment
                {
                    TenantId = tenantId, PayrollRunId = target.Id, EmployeeId = hold.EmployeeId,
                    AdjustmentType = ReleaseAdjustmentType, Amount = hold.Amount, Status = "Approved",
                    Reason = trimmedReason.Length > 0 ? $"{label}. {trimmedReason}" : label,
                    SourceType = ReleaseSource, SourceId = hold.Id,
                    SourceSnapshotJson = JsonSerializer.Serialize(new { holdId = hold.Id, heldRunId = holdingRun.Id, heldPeriod, reason = SuspendedReason }),
                });
            }
            db.AuditLogs.Add(new AuditLog
            {
                TenantId = tenantId, CompanyId = holdingRun.CompanyId, UserId = actorUserId,
                EntityName = nameof(PayrollAdjustment), EntityId = hold.Id.ToString(),
                Action = verb == ActionPay ? "payroll.held_pay.released" : "payroll.held_pay.forfeited",
                Metadata = JsonSerializer.Serialize(new
                {
                    employeeId = hold.EmployeeId, amount = hold.Amount, heldRunId = holdingRun.Id, heldPeriod,
                    reason = trimmedReason, paidInRunId = target?.Id,
                    paidInPeriod = target is null ? null : $"{target.Year}-{target.Month:D2}",
                }),
                CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
        if (!won)
            return new(409, new { error = "held_pay_already_decided", message = "This held pay was decided by someone else a moment ago. Reload to see the decision." });
        return new(200, new
        {
            holdId = hold.Id, status = newStatus, amount = hold.Amount, employeeId = hold.EmployeeId, heldPeriod,
            paidInRunId = target?.Id, paidInPeriod = target is null ? null : $"{target.Year}-{target.Month:D2}",
            message = verb == ActionPay
                ? $"The held pay is added to the {target!.Year}-{target.Month:D2} run as an earning. It is paid when that run is processed."
                : "The held pay was forfeited. The decision and its reason are in the audit trail.",
        });
    }

    /// <summary>One transaction inside the execution strategy (Npgsql's retrying strategy requires it); a caller's own
    /// transaction is joined instead. A retry starts from a clean change tracker.</summary>
    private static async Task<bool> InTransactionAsync(ZayraDbContext db, Func<Task<bool>> body, CancellationToken ct)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null) return await body();
        var attempt = 0;
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (attempt++ > 0) db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var result = await body();
            if (result) await tx.CommitAsync(ct);
            return result;
        });
    }

    /// <summary>One row of the run screen's held-pay list.</summary>
    public sealed record HoldRow(
        Guid HoldId, int EmployeeId, string EmployeeCode, string EmployeeName, string? EmployeeStatus,
        decimal Amount, string Reason, string Status, Guid HeldRunId, string HeldPeriod, bool HeldByThisRun,
        Guid? PaidInRunId, string? PaidInPeriod);

    /// <summary>
    /// What the run screen lists: the holds this run wrote (in any status), plus every hold of the same company still
    /// awaiting a decision from an earlier run, so a hold stays visible until it is released or forfeited.
    /// </summary>
    public static async Task<List<HoldRow>> ListForRunAsync(ZayraDbContext db, Guid tenantId, PayrollRun run, CancellationToken ct)
    {
        var rows = await (from a in db.PayrollAdjustments.AsNoTracking()
                          join r in db.PayrollRuns.AsNoTracking() on a.PayrollRunId equals r.Id
                          where a.TenantId == tenantId && r.TenantId == tenantId && a.SourceType == HoldSource
                                && (a.PayrollRunId == run.Id
                                    || (a.Status == Held && r.Status != "Voided"
                                        && (r.CompanyId == run.CompanyId || (run.CompanyId == null && r.CompanyId == null))))
                          select new { a, r.Year, r.Month }).ToListAsync(ct);
        if (rows.Count == 0) return new List<HoldRow>();

        var holdIds = rows.Select(x => (Guid?)x.a.Id).ToList();
        var releases = await (from a in db.PayrollAdjustments.AsNoTracking()
                              join r in db.PayrollRuns.AsNoTracking() on a.PayrollRunId equals r.Id
                              where a.TenantId == tenantId && a.SourceType == ReleaseSource && holdIds.Contains(a.SourceId)
                              select new { a.SourceId, RunId = r.Id, r.Year, r.Month }).ToListAsync(ct);
        var empIds = rows.Select(x => x.a.EmployeeId).Distinct().ToList();
        // The caller's own view of the employees (company and data filters apply). One it cannot see, or a deleted record, is
        // still named from the hold's own snapshot, so an amount owed is never anonymous.
        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.TenantId == tenantId && empIds.Contains(e.Id))
            .Select(e => new { e.Id, e.EmployeeCode, e.FullName, e.Status })
            .ToDictionaryAsync(e => e.Id, ct);

        static (string Code, string Name) Snapshot(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var root = doc.RootElement;
                return (root.TryGetProperty("employeeCode", out var c) ? c.GetString() ?? string.Empty : string.Empty,
                        root.TryGetProperty("employeeName", out var n) ? n.GetString() ?? string.Empty : string.Empty);
            }
            catch (JsonException) { return (string.Empty, string.Empty); }
        }

        return rows
            .OrderBy(x => x.Year).ThenBy(x => x.Month).ThenBy(x => x.a.EmployeeId)
            .Select(x =>
            {
                employees.TryGetValue(x.a.EmployeeId, out var e);
                var snap = e is null ? Snapshot(x.a.SourceSnapshotJson) : (string.Empty, string.Empty);
                var rel = releases.FirstOrDefault(r => r.SourceId == x.a.Id);
                return new HoldRow(
                    x.a.Id, x.a.EmployeeId, e?.EmployeeCode ?? snap.Item1, e?.FullName ?? snap.Item2, e?.Status,
                    x.a.Amount, x.a.Reason, x.a.Status, x.a.PayrollRunId, $"{x.Year}-{x.Month:D2}", x.a.PayrollRunId == run.Id,
                    rel?.RunId, rel is null ? null : $"{rel.Year}-{rel.Month:D2}");
            })
            .ToList();
    }
}
