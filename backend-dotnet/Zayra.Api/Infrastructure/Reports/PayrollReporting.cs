using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Reports;

/// <summary>
/// What the payroll reports and the analytics tab count as "the payroll for a period", in one place.
///
/// <list type="bullet">
/// <item><b>Which runs.</b> Every run of the period in the caller's companies — Regular, Replacement,
/// OffCycle, Supplementary, Correction — because all of them paid people. Not a <c>Voided</c> run (it
/// did not happen; its Replacement is the month) and not a <c>Draft</c> run (it has not been calculated,
/// so its lines are not figures anyone has signed off). Processed and approved-but-unlocked runs are
/// included and labelled with their status, so a register can be reviewed before it is locked.</item>
/// <item><b>Which currency.</b> A run is in its legal entity's <c>DefaultCurrency</c> — the rule payroll
/// lock and the GL export already apply. A run whose company has none is in an unknown currency (null),
/// never assumed. Amounts are only ever added up within one currency.</item>
/// </list>
/// </summary>
public static class PayrollReporting
{
    public const string Voided = "Voided";
    public const string Draft = "Draft";

    public static IQueryable<PayrollRun> ReportableRuns(IQueryable<PayrollRun> runs, Guid tenantId) =>
        runs.Where(x => x.TenantId == tenantId && x.Status != Voided && x.Status != Draft);

    /// <summary>Each company's currency, upper-cased; companies with none are absent.</summary>
    public static async Task<Dictionary<Guid, string>> CompanyCurrenciesAsync(
        ZayraDbContext db, Guid tenantId, IEnumerable<Guid?> companyIds, CancellationToken ct)
    {
        var ids = companyIds.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        var rows = await db.Companies.AsNoTracking()
            .Where(c => c.TenantId == tenantId && ids.Contains(c.Id))
            .Select(c => new { c.Id, c.DefaultCurrency })
            .ToListAsync(ct);
        return rows.Where(r => !string.IsNullOrWhiteSpace(r.DefaultCurrency))
            .ToDictionary(r => r.Id, r => r.DefaultCurrency.Trim().ToUpperInvariant());
    }

    public static string? CurrencyOf(Guid? companyId, IReadOnlyDictionary<Guid, string> currencies) =>
        companyId is { } id && currencies.TryGetValue(id, out var currency) ? currency : null;

    /// <summary>
    /// One status for several runs of a period, in plain words: the status itself when they agree,
    /// otherwise how many runs are in which status ("2 Locked, 1 Processed").
    /// </summary>
    public static string? DescribeStatuses(IEnumerable<string> statuses)
    {
        var counts = statuses.GroupBy(s => s).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
        return counts.Count switch
        {
            0 => null,
            1 => counts[0].Key,
            _ => string.Join(", ", counts.Select(g => $"{g.Count()} {g.Key}")),
        };
    }
}
