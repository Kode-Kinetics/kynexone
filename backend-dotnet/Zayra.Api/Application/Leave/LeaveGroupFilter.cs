using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;

namespace Zayra.Api.Application.Leave;

/// <summary>
/// The company / branch selector on the Leave screens, resolved to the employees it names.
///
/// <para>THE DEFECT THIS REPLACES. Every Leave list and report takes this filter in its URL, because the
/// screens all sit under one group selector. Several actions never declared the two parameters, and an
/// undeclared query parameter is not an error in ASP.NET Core — it is silently dropped. So a group-scope
/// user who picked one company still got every company's encashments, comp-off credits, absences, balance
/// summary, liability and "on leave today", with the selector on screen saying otherwise. A filter that is
/// accepted and ignored is worse than one that is refused: nothing on screen says the answer is wider than
/// the question.</para>
///
/// <para>This is the filter <c>LeaveRequestsController</c> and <c>LeaveBalancesController</c> already
/// applied, lifted out so that every Leave endpoint resolves it the same way and cannot drift again.</para>
///
/// <para>It NARROWS only. The caller's own data scope and the company-scope query filters are applied
/// separately and still bound the result, so this can never widen what a caller may see.</para>
/// </summary>
public static class LeaveGroupFilter
{
    /// <summary>
    /// The ids of the employees in the selected company/branch, or null when nothing was selected (the
    /// caller then applies no filter). An empty list means the selection matched nobody, which correctly
    /// yields an empty page rather than everybody.
    /// </summary>
    public static async Task<List<int>?> EmployeeIdsAsync(
        ZayraDbContext db, Guid tenantId, Guid? companyId, Guid? branchId, CancellationToken ct)
    {
        if (companyId is null && branchId is null) return null;

        var employees = db.Employees.AsNoTracking().Where(e => e.TenantId == tenantId && !e.IsDeleted);
        if (companyId.HasValue) employees = employees.Where(e => e.CompanyId == companyId);
        if (branchId.HasValue) employees = employees.Where(e => e.BranchId == branchId);
        return await employees.Select(e => e.Id).ToListAsync(ct);
    }
}
