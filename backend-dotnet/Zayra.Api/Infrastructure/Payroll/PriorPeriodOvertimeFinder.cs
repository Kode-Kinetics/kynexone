using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

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
/// </summary>
public static class PriorPeriodOvertimeFinder
{
    public static async Task<IReadOnlyList<PriorPeriodOvertime>> FindAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, IReadOnlyCollection<int> employeeIds, DateOnly periodStart,
        CancellationToken ct)
    {
        if (employeeIds.Count == 0) return Array.Empty<PriorPeriodOvertime>();
        // Tenant-wide read with the company named in the predicate, as the run's own period query does; a
        // legacy request with no company stamp belongs to whichever run pays its employee.
        var rows = await (from i in db.OvertimePayrollImpacts.AsNoTracking()
                          join r in db.OvertimeRequests.AsNoTracking() on i.OvertimeRequestId equals r.Id
                          where i.TenantId == tenantId && i.Status == "PendingPayroll" && i.PayrollRunId == null
                                && employeeIds.Contains(i.EmployeeId)
                                && r.TenantId == tenantId && r.Status == "Approved" && r.WorkDate < periodStart
                                && (r.CompanyId == companyId || r.CompanyId == null)
                          orderby i.EmployeeId, r.WorkDate
                          select new PriorPeriodOvertime(i.EmployeeId, r.Id, r.WorkDate, i.Minutes))
            .ToListAsync(ct);
        return rows;
    }
}
