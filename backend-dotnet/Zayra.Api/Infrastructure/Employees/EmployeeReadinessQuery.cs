using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Employees;

/// <summary>
/// SERVER-SIDE readiness / import-gap list filter, shared by every People-list query path (the service
/// SearchAsync and the controller's restricted-scope branch) so the post-import "Needs info" deep-link
/// filters the WHOLE dataset — not just the current page. The old client-side page-local filter silently
/// broke past page 1 and mis-counted totals.
/// </summary>
public static class EmployeeReadinessQuery
{
    public static IQueryable<Employee> ApplyReadinessFilter(
        IQueryable<Employee> query, ZayraDbContext db, Guid tenantId,
        string? readiness, Guid? importBatchId, string? gapType)
    {
        if (!string.IsNullOrWhiteSpace(readiness))
        {
            query = readiness switch
            {
                "Blocked" => query.Where(e => e.ReadinessState == "Blocked"),
                "NeedsAttention" => query.Where(e => e.ReadinessState == "NeedsAttention"),
                "Ready" => query.Where(e => e.ReadinessState == "Ready"),
                "NotReady" => query.Where(e => e.ReadinessState != "Ready"),
                _ => query,
            };
        }

        if (importBatchId is not null || !string.IsNullOrWhiteSpace(gapType))
        {
            var batch = importBatchId;
            var gt = gapType;
            query = query.Where(e => db.EmployeeImportGaps.Any(g =>
                g.TenantId == tenantId
                && g.EmployeeId == e.Id
                && g.ResolvedAtUtc == null
                && (batch == null || g.ImportBatchId == batch)
                && (gt == null || g.GapType == gt)));
        }

        return query;
    }
}
