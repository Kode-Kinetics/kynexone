using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>One approved overtime impact that no payroll run has paid, dated before the run being validated.</summary>
public readonly record struct PriorPeriodOvertime(int EmployeeId, Guid OvertimeRequestId, DateOnly WorkDate, int Minutes);

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
/// </summary>
public static class PriorPeriodOvertimeFinder
{
    /// <summary>
    /// Statuses of a run that has consumed its period's impacts. Process marks them <c>Processed</c> in the same
    /// transaction that sets the run to <c>Processed</c>, and every later status follows from that. <c>Draft</c> has
    /// consumed nothing and <c>Voided</c> returned everything to PendingPayroll.
    /// </summary>
    private static readonly string[] ConsumedRunStatuses = { "Processed", "PendingFinanceReview", "Approved", "Locked", "Paid" };

    public static async Task<IReadOnlyList<PriorPeriodOvertime>> FindAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, IReadOnlyCollection<int> employeeIds, DateOnly periodStart,
        CancellationToken ct)
    {
        if (employeeIds.Count == 0) return Array.Empty<PriorPeriodOvertime>();
        // Tenant-wide read with the company named in the predicate, as the run's own period query does; a
        // legacy request with no company stamp belongs to whichever run pays its employee.
        var consumed = ConsumedRunStatuses;
        var rows = await (from i in db.OvertimePayrollImpacts.AsNoTracking()
                          join r in db.OvertimeRequests.AsNoTracking() on i.OvertimeRequestId equals r.Id
                          where i.TenantId == tenantId && i.Status == "PendingPayroll" && i.PayrollRunId == null
                                && employeeIds.Contains(i.EmployeeId)
                                && r.TenantId == tenantId && r.Status == "Approved" && r.WorkDate < periodStart
                                && (r.CompanyId == companyId || r.CompanyId == null)
                                // The overtime's own period was already run for this company (Regular/Replacement own the month).
                                && db.PayrollRuns.AsNoTracking().Any(pr =>
                                       pr.TenantId == tenantId && pr.CompanyId == companyId
                                       && pr.Year == r.WorkDate.Year && pr.Month == r.WorkDate.Month
                                       && (pr.RunType == PayrollRunTypes.Regular || pr.RunType == PayrollRunTypes.Replacement)
                                       && consumed.Contains(pr.Status))
                          orderby i.EmployeeId, r.WorkDate
                          select new PriorPeriodOvertime(i.EmployeeId, r.Id, r.WorkDate, i.Minutes))
            .ToListAsync(ct);
        return rows;
    }
}
