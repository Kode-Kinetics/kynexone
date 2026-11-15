using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// The plan's cap on ACTIVE employees (<c>TenantSubscription.MaxEmployees</c>; 0 or no subscription row = no cap). One answer for every
/// writer that lets someone into the Active head count: creating an employee, and undoing a merge that would bring an Active person back.
/// </summary>
public static class EmployeeSeatLimit
{
    /// <summary>The current Active count and the cap when the cap is already reached; null when there is room or no cap.</summary>
    public static async Task<(int Count, int Max)?> ReachedAsync(ZayraDbContext db, Guid tenantId, CancellationToken ct)
    {
        var sub = await db.TenantSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (sub is null || sub.MaxEmployees <= 0) return null;
        var count = await db.Employees.CountAsync(e => e.TenantId == tenantId && e.Status == "Active" && !e.IsDeleted, ct);
        return count >= sub.MaxEmployees ? (count, sub.MaxEmployees) : null;
    }
}
