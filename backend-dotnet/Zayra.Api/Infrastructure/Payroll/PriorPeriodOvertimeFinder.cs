using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// One approved overtime impact that no payroll run has paid, dated before the run being validated. The trailing
/// members are what <see cref="OvertimeLateSettlement"/> needs to pay it; rule 11b reads the first four and
/// <paramref name="OtherCompanyStamp"/>.
/// </summary>
/// <param name="OtherCompanyStamp">The request is stamped with a different company than the run's (the employee moved
/// companies after filing it). It is flagged, never settled: paying it would be a cross-company payment.</param>
public readonly record struct PriorPeriodOvertime(int EmployeeId, Guid OvertimeRequestId, DateOnly WorkDate, int Minutes,
    Guid ImpactId = default, decimal Amount = 0m, decimal Multiplier = 0m, Guid? OvertimePolicyId = null, bool OtherCompanyStamp = false);

/// <summary>
/// The ONE source for Rule 11b (<see cref="PayrollValidationEngine.PriorPeriodOvertimeUnpaid"/>): approved
/// overtime impacts still <c>PendingPayroll</c> whose request is dated BEFORE the run's period. Process and
/// /validate both call this, so the stored findings cannot disagree.
///
/// <para>Why it exists: <c>PayrollRunProcessor</c> pays only the impacts whose request work date falls inside
/// the run's own period (that is what lets it price a rest-day or holiday hour). Overtime approved AFTER that
/// period's run had locked therefore matched no run ever: the impact stayed PendingPayroll, the employee was
/// never paid, and no validation named it.</para>
///
/// <para>It flags only overtime whose OWN work-date period (year, month) already has a run for the company that
/// consumed its impacts: a period-owning (Regular or Replacement) run in a status past Draft and not Voided.
/// Overtime dated in a period with no such run is that period's to pay: it may not have been processed yet
/// (November before October), or its run was voided, which returns the overtime to PendingPayroll for the
/// replacement run. Flagging it here would invite paying it a second time.</para>
///
/// <para>Reconciled with <c>overtime.late_approval_mode</c> (<see cref="OvertimeLateSettlement"/>): in <c>BackPay</c> mode a run
/// that pays recurring items settles exactly the impacts this finder would otherwise return, so <see cref="FindAsync"/> leaves
/// out the ones the mode will settle (and <see cref="FindSettleableAsync"/> returns them to the run that pays them). Both read
/// ONE query, so what is flagged and what is paid cannot drift apart. In <c>Refuse</c> mode (the default) nothing is settled
/// and everything the query returns is flagged, as before. An impact already settled another way (CarriedForward at approval,
/// or Processed by a run) is no longer PendingPayroll and is never returned.</para>
/// </summary>
public static class PriorPeriodOvertimeFinder
{
    /// <summary>
    /// Statuses of a run that has consumed its period's impacts. Process marks them <c>Processed</c> in the same
    /// transaction that sets the run to <c>Processed</c>, and every later status follows from that. <c>Draft</c> has
    /// consumed nothing and <c>Voided</c> returned everything to PendingPayroll.
    /// </summary>
    private static readonly string[] ConsumedRunStatuses = { "Processed", "PendingFinanceReview", "Approved", "Locked", "Paid" };

    /// <summary>
    /// What rule 11b flags: the candidates, less those the tenant's late-approval mode will have THIS run pay.
    /// <paramref name="lateApprovalMode"/> is the tenant's <c>overtime.late_approval_mode</c>; <paramref name="runPaysRecurringItems"/>
    /// is whether the run being validated pays overtime at all (a supplemental run does not, so it settles nothing);
    /// <paramref name="runId"/> is that run, whose own back-pay adjustments may be reused.
    /// </summary>
    public static async Task<IReadOnlyList<PriorPeriodOvertime>> FindAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, IReadOnlyCollection<int> employeeIds, DateOnly periodStart,
        CancellationToken ct, string lateApprovalMode = OvertimeLateSettlement.Refuse, bool runPaysRecurringItems = true,
        Guid? runId = null)
    {
        var candidates = await QueryAsync(db, tenantId, companyId, employeeIds, periodStart, ct);
        if (candidates.Count == 0 || !OvertimeLateSettlement.SettlesInRun(lateApprovalMode, runPaysRecurringItems)) return candidates;
        var settled = (await SettleableAsync(db, tenantId, candidates, runId, ct)).Select(c => c.ImpactId).ToHashSet();
        return candidates.Where(c => !settled.Contains(c.ImpactId)).ToList();
    }

    /// <summary>The candidates the late-approval mode settles in the run that pays them (BackPay only; empty otherwise).</summary>
    public static async Task<IReadOnlyList<PriorPeriodOvertime>> FindSettleableAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, IReadOnlyCollection<int> employeeIds, DateOnly periodStart,
        CancellationToken ct, string lateApprovalMode, bool runPaysRecurringItems = true, Guid? runId = null)
    {
        if (!OvertimeLateSettlement.SettlesInRun(lateApprovalMode, runPaysRecurringItems)) return Array.Empty<PriorPeriodOvertime>();
        var candidates = await QueryAsync(db, tenantId, companyId, employeeIds, periodStart, ct);
        return await SettleableAsync(db, tenantId, candidates, runId, ct);
    }

    /// <summary>
    /// The candidates a run can actually pay: a positive amount to pay, and no back-pay adjustment for the same request
    /// already attached to another live run (adjustments are unique per request, and paying it here as well would be a second
    /// payment). Those it cannot pay stay flagged, so nothing is left unpaid and unnamed.
    /// </summary>
    private static async Task<IReadOnlyList<PriorPeriodOvertime>> SettleableAsync(
        ZayraDbContext db, Guid tenantId, IReadOnlyList<PriorPeriodOvertime> candidates, Guid? runId, CancellationToken ct)
    {
        // A request stamped with another company is flagged, never paid here: that would be a cross-company payment.
        var payable = candidates.Where(c => !c.OtherCompanyStamp && OvertimeLateSettlement.IsSettleable(c)).ToList();
        if (payable.Count == 0) return payable;
        var requestIds = payable.Select(c => c.OvertimeRequestId).ToList();
        var blocked = await (from a in db.PayrollAdjustments.AsNoTracking()
                             join r in db.PayrollRuns.AsNoTracking() on a.PayrollRunId equals r.Id
                             where a.TenantId == tenantId && a.SourceType == PayrollAdjustmentSources.OvertimeBackPay
                                   && a.SourceId != null && requestIds.Contains(a.SourceId.Value)
                                   && a.PayrollRunId != runId && r.Status != "Voided"
                             select a.SourceId!.Value).ToListAsync(ct);
        var blockedSet = blocked.ToHashSet();
        return payable.Where(c => !blockedSet.Contains(c.OvertimeRequestId)).ToList();
    }

    private static async Task<IReadOnlyList<PriorPeriodOvertime>> QueryAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, IReadOnlyCollection<int> employeeIds, DateOnly periodStart,
        CancellationToken ct)
    {
        if (employeeIds.Count == 0) return Array.Empty<PriorPeriodOvertime>();
        // Tenant-wide read; the employee ids are the run's own population, so the employee's CURRENT company is this
        // run's company. A legacy request with no company stamp belongs to whichever run pays its employee. A request
        // stamped with a DIFFERENT company (the employee moved after filing it) is returned too, marked OtherCompanyStamp:
        // it used to be filtered out, so it was neither paid (the settle path is company-bound) nor flagged.
        var consumed = ConsumedRunStatuses;
        var rows = await (from i in db.OvertimePayrollImpacts.AsNoTracking()
                          join r in db.OvertimeRequests.AsNoTracking() on i.OvertimeRequestId equals r.Id
                          where i.TenantId == tenantId && i.Status == "PendingPayroll" && i.PayrollRunId == null
                                && employeeIds.Contains(i.EmployeeId)
                                && r.TenantId == tenantId && r.Status == "Approved" && r.WorkDate < periodStart
                                // The overtime's own period was already run for this company, or for the company the request is stamped
                                // with (Regular/Replacement own the month).
                                && db.PayrollRuns.AsNoTracking().Any(pr =>
                                       pr.TenantId == tenantId
                                       && (pr.CompanyId == companyId || (r.CompanyId != null && pr.CompanyId == r.CompanyId))
                                       && pr.Year == r.WorkDate.Year && pr.Month == r.WorkDate.Month
                                       && (pr.RunType == PayrollRunTypes.Regular || pr.RunType == PayrollRunTypes.Replacement)
                                       && consumed.Contains(pr.Status))
                          orderby i.EmployeeId, r.WorkDate
                          select new PriorPeriodOvertime(i.EmployeeId, r.Id, r.WorkDate, i.Minutes, i.Id, i.Amount, i.ApprovedMultiplier, r.OvertimePolicyId, r.CompanyId != null && r.CompanyId != companyId))
            .ToListAsync(ct);
        return rows;
    }
}
