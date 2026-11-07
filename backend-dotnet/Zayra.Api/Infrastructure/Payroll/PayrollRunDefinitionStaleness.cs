using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Whether a Processed run was computed with a customer-defined cash allowance definition that Finance has corrected since. Judged from data, never from an
/// earlier response: a version of an allowance on the run's lines whose effective month is on or before the run's month and which was written (created or
/// amended) after the run was processed. Such a run's slips and journal basis (GL driver, end-of-service answer) are out of date until it is re-processed.
/// </summary>
public static class PayrollRunDefinitionStaleness
{
    public sealed record Stale(string Code, DateOnly EffectiveFrom);

    /// <summary>The first (by code, then month) stale definition of <paramref name="run"/>, or null when none is.</summary>
    public static async Task<Stale?> FindAsync(ZayraDbContext db, Guid tenantId, PayrollRun run, CancellationToken ct)
    {
        if (run.ProcessedAtUtc is not { } processedAt) return null;
        var runMonth = new DateOnly(run.Year, run.Month, 1);
        var codes = (await db.PayrollEarnings.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.PayrollRunId == run.Id).Select(e => e.ComponentCode).Distinct().ToListAsync(ct))
            .Select(PayrollArrearsComponents.SourceComponent)
            .Where(EntitlementComponentRules.IsAllowance)
            .Distinct(StringComparer.Ordinal).ToList();
        if (codes.Count == 0) return null;
        var versions = await CashAllowanceDefinitions.VersionsAsync(db, tenantId, codes, ct);
        return codes.OrderBy(c => c, StringComparer.Ordinal)
            .SelectMany(code => versions[code])
            .Where(v => v.EffectiveFrom is DateOnly from && from <= runMonth
                        && (v.UpdatedAtUtc is DateTime updated && updated > v.CreatedAtUtc ? updated : v.CreatedAtUtc) > processedAt)
            .OrderBy(v => v.Code, StringComparer.Ordinal).ThenBy(v => v.EffectiveFrom)
            .Select(v => new Stale(v.Code, v.EffectiveFrom!.Value))
            .FirstOrDefault();
    }
}
