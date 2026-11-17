using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Entitlements;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Whether a Processed run was computed with a customer-defined cash allowance definition that Finance has corrected since. Judged from data, never from an
/// earlier response, and never from a clock: Process pins the paying terms of the definition VERSION it actually read on each allowance line
/// (<see cref="PayrollEarning.DefinitionTerms"/>), and a run is stale when the version applicable to the run's month today has different terms (GL driver,
/// end-of-service answer). A correction that landed after the processor read the definitions but before it stamped the run processed is therefore caught
/// (its timestamp may precede the run's), while a correction of a LATER month that merely closes the version the run used, or an edit to a version
/// that ended before the run's month, changes nothing the run used and does not block it. A run whose lines carry no record of the version they used is
/// treated as stale: it is processed again rather than approved on a guess.
/// </summary>
public static class PayrollRunDefinitionStaleness
{
    public sealed record Stale(string Code, DateOnly EffectiveFrom);

    /// <summary>The first (by code) allowance whose applicable definition no longer matches what <paramref name="run"/> consumed, or null when none does.</summary>
    public static async Task<Stale?> FindAsync(ZayraDbContext db, Guid tenantId, PayrollRun run, CancellationToken ct)
    {
        if (run.ProcessedAtUtc is null) return null;
        var runMonth = new DateOnly(run.Year, run.Month, 1);
        var consumed = (await db.PayrollEarnings.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.PayrollRunId == run.Id).Select(e => new { e.ComponentCode, e.DefinitionTerms }).Distinct().ToListAsync(ct))
            .Select(e => (Code: PayrollArrearsComponents.SourceComponent(e.ComponentCode), Terms: e.DefinitionTerms,
                BackPay: PayrollArrearsComponents.SourceComponent(e.ComponentCode) != e.ComponentCode))
            .Where(e => EntitlementComponentRules.IsAllowance(e.Code))
            .ToList();
        if (consumed.Count == 0) return null;
        var codes = consumed.Select(c => c.Code).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        // Payroll's view (a change waiting for Finance reads as the confirmed terms), the same one Process read: confirming the change makes the run stale.
        var versions = await CashAllowanceDefinitions.VersionsAsync(db, tenantId, codes, ct, includeRemoved: true, companyId: run.CompanyId, payable: true);
        foreach (var code in codes)
        {
            // The month's own allowance line must still be paid for that month: a version switched on and in effect then. One that was switched off, ended
            // or removed after the run was processed (a change confirmed after it) makes the run stale, never approved as it stands. Back pay keeps the
            // payroll run's own fallback (ForPeriod): the expense of a definition removed since still posts on its driver.
            var inEffect = CashAllowanceDefinitions.InEffect(versions[code], runMonth);
            if (consumed.Any(c => c.Code == code && !c.BackPay) && inEffect is null)
                return new Stale(code, runMonth);
            var applicable = CashAllowanceDefinitions.ForPeriod(versions[code], runMonth);
            var applicableTerms = applicable is null ? null : CashAllowanceDefinitions.Terms(applicable);
            if (consumed.Any(c => c.Code == code && (c.Terms is null || c.Terms != applicableTerms)))
                return new Stale(code, applicable?.EffectiveFrom ?? runMonth);
        }
        return null;
    }
}
