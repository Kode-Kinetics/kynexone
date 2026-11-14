using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// The service a final settlement has already closed. Owner rule (2026-11-13): once a final settlement is approved or paid, a
/// returning employee's end-of-service and leave service count from the rehire date, and every rule tells the periods apart by
/// <c>LastWorkingDay &gt;= JoiningDate</c>. An Approved, Disbursing or Paid settlement has posted its accrual journal
/// (<see cref="FinalSettlementStatuses.HasAccrued"/>), so the service up to its last working day is settled.
/// </summary>
public static class SettledServicePeriod
{
    /// <summary>
    /// The latest last working day of an Approved, Disbursing or Paid settlement for the employee, or null when there is none.
    /// With <paramref name="onOrAfter"/> only settlements whose last working day is on or after that date are read: passing the
    /// current joining date asks "has the CURRENT service period already been settled?". Read across every company of the tenant:
    /// a settlement raised under another legal entity still settled the service.
    /// </summary>
    public static async Task<DateOnly?> LatestSettledLastWorkingDayAsync(
        ZayraDbContext db, Guid tenantId, int employeeId, DateOnly? onOrAfter, CancellationToken ct)
    {
        var query = ScopedBypass.TenantWide(db.EmployeeFinalSettlements, tenantId,
                "Settled-service check: a settlement under any legal entity of the tenant closes the service it paid.")
            .AsNoTracking()
            .Where(s => s.EmployeeId == employeeId
                     && (s.Status == FinalSettlementStatuses.Approved
                         || s.Status == FinalSettlementStatuses.Disbursing
                         || s.Status == FinalSettlementStatuses.Paid));
        if (onOrAfter is DateOnly from) query = query.Where(s => s.LastWorkingDay >= from);
        var days = await query.Select(s => s.LastWorkingDay).ToListAsync(ct);
        return days.Count == 0 ? null : days.Max();
    }
}
