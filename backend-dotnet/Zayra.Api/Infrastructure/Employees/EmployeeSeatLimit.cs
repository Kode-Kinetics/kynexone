using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// The plan's cap on employed people (<c>TenantSubscription.MaxEmployees</c>; 0 or no subscription row = no cap). One answer for every
/// writer that lets someone into the Active head count: creating an employee, and undoing a merge that would bring an Active person back.
/// </summary>
public static class EmployeeSeatLimit
{
    /// <summary>The statuses that count against the plan's cap: the employed ones. A person on probation, confirmed, on leave or serving notice
    /// is as employed as an Active one. One list for the count below and for the check on a status that is about to be restored.</summary>
    public static readonly string[] CountedStatuses =
    {
        EmployeeStatuses.Active, "Probation", "Confirmed", "On leave", "Notice period",
    };

    public static bool IsCounted(string? status) =>
        status is not null && CountedStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);

    /// <summary>The current Active count and the cap when the cap is already reached; null when there is room or no cap.</summary>
    /// <param name="lockSubscription">Read the plan row FOR UPDATE (inside a transaction): two status changes into a counted status then
    /// queue behind each other instead of both passing the same count. Inert outside a transaction and on providers without row locks.</param>
    public static async Task<(int Count, int Max)?> ReachedAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct, bool lockSubscription = false)
    {
        var plans = db.TenantSubscriptions.AsNoTracking();
        if (lockSubscription) plans = plans.TagWith(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.ForUpdateTag);
        var sub = await plans.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (sub is null || sub.MaxEmployees <= 0) return null;
        var count = await db.Employees.CountAsync(e => e.TenantId == tenantId && CountedStatuses.Contains(e.Status) && !e.IsDeleted, ct);
        return count >= sub.MaxEmployees ? (count, sub.MaxEmployees) : null;
    }
}
