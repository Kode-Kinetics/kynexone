using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

/// <summary>
/// Which run a payroll run is compared with. <see cref="Mode"/> is <c>Full</c> when there is a comparison run and
/// <c>NoPrior</c> when there is none (then <see cref="LastRunPeriod"/> and <see cref="PriorVoided"/> say why).
/// <see cref="OverrideError"/> is set only when a caller-chosen run was refused.
/// </summary>
public sealed record PriorRunSelection(
    string Mode, PayrollRun? Prior, bool PriorVoided, bool PriorNotFinal, string? LastRunPeriod, string? OverrideError);

public static class PriorRunSelector
{
    public const string PriorRunInvalid = "prior_run_invalid";

    /// <summary>
    /// The default comparison is the same company's period-owning (Regular or Replacement) run for the immediately
    /// preceding calendar month, ignoring voided and draft runs. A different run may be chosen explicitly, but only
    /// one of the same company and tenant that is period-owning, earlier, and neither voided nor draft. A refused choice is
    /// never quietly swapped for the default.
    /// </summary>
    public static async Task<PriorRunSelection> SelectAsync(
        ZayraDbContext db, Guid tenantId, PayrollRun run, Guid? overrideRunId, CancellationToken ct)
    {
        if (overrideRunId is { } chosenId)
        {
            var chosen = await db.PayrollRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == chosenId, ct);
            var valid = chosen is not null
                && chosen.Id != run.Id
                && chosen.CompanyId == run.CompanyId
                && PayrollRunTypes.IsPeriodOwning(chosen.RunType)
                && chosen.Status is not ("Voided" or "Draft")
                && (chosen.Year, chosen.Month).CompareTo((run.Year, run.Month)) < 0;
            return valid
                ? new PriorRunSelection(ReconModes.Full, chosen, false, IsNotFinal(chosen!), null, null)
                : new PriorRunSelection(ReconModes.NoPrior, null, false, false, null, PriorRunInvalid);
        }

        var (year, month) = run.Month == 1 ? (run.Year - 1, 12) : (run.Year, run.Month - 1);
        // Period-owning runs of the SAME company in the preceding month. A voided one is kept in the result only to say so.
        var candidates = await db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.CompanyId == run.CompanyId && r.Id != run.Id
                     && r.Year == year && r.Month == month
                     && (r.RunType == PayrollRunTypes.Regular || r.RunType == PayrollRunTypes.Replacement))
            .ToListAsync(ct);
        var prior = candidates
            .Where(r => r.Status is not ("Voided" or "Draft"))
            .OrderBy(r => string.Equals(r.RunType, PayrollRunTypes.Replacement, StringComparison.Ordinal) ? 0 : 1)
            .ThenByDescending(r => r.CreatedAtUtc).ThenBy(r => r.Id) // the newest, and never "whichever row came back first"
            .FirstOrDefault();
        if (prior is not null)
            return new PriorRunSelection(ReconModes.Full, prior, false, IsNotFinal(prior), null, null);

        var priorVoided = candidates.Any(r => r.Status == "Voided");
        var last = await db.PayrollRuns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.CompanyId == run.CompanyId && r.Id != run.Id
                     && (r.RunType == PayrollRunTypes.Regular || r.RunType == PayrollRunTypes.Replacement)
                     && r.Status != "Voided" && r.Status != "Draft"
                     && (r.Year < run.Year || (r.Year == run.Year && r.Month < run.Month)))
            .OrderByDescending(r => r.Year).ThenByDescending(r => r.Month)
            .Select(r => new { r.Year, r.Month })
            .FirstOrDefaultAsync(ct);
        return new PriorRunSelection(ReconModes.NoPrior, null, priorVoided, false,
            last is null ? null : $"{last.Year}-{last.Month:D2}", null);
    }

    /// <summary>A comparison run still being worked on can still change under the reviewer.</summary>
    private static bool IsNotFinal(PayrollRun prior) => prior.Status is "Processed" or "PendingFinanceReview";
}
