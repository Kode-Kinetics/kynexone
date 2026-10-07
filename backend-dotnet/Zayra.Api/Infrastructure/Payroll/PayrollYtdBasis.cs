using Microsoft.EntityFrameworkCore;
using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// Which sources make up a payslip's year-to-date, so that no pay period is ever counted twice.
///
/// <para><b>The double count.</b> A payslip's YTD is (locked payslips earlier in the year) + (carried
/// opening balances for the year). An opening balance is "as at the day before the cutover"
/// (<see cref="CompanyCutover.CutoverDate"/>), so it already contains every month before the cutover —
/// including any month this product ALSO ran and locked before go-live (a parallel run, a re-migration,
/// a wave). Summing both counted those months twice on every payslip for the rest of the year. The
/// importer refuses balances once a run on or after the cutover month is locked, and refuses two rows
/// under one aggregate (MI1), but nothing on the payroll side knew where the carried figures ended.</para>
///
/// <para><b>The partition (one period, one source).</b> With a governing cutover (any status but
/// Planned — <see cref="CutoverStatuses.Governs"/>) for the run's legal entity, and for an employee who
/// HAS carried YTD balances, months are split at the cutover MONTH — the same boundary the importer's locked-period
/// refusal uses:</para>
/// <list type="bullet">
/// <item>A run for a month ON OR AFTER the cutover month counts the opening balances plus locked
/// payslips for months on or after the cutover month. Earlier payslips are inside the opening balance.</item>
/// <item>A run for a month BEFORE the cutover month counts locked payslips only. The opening balance is
/// stated as at a later date and already contains this very month.</item>
/// </list>
///
/// <para><b>No cutover, both sources present.</b> Then nothing says where the carried figures end, and
/// the overlap cannot be resolved without guessing. The legacy sum is kept so nothing silently changes,
/// and the run is BLOCKED by validation rule <see cref="UnresolvedOverlapCode"/> naming the employee,
/// until a cutover is declared for the legal entity.</para>
/// </summary>
public static class PayrollYtdBasis
{
    /// <summary>Validation Error: opening balances and in-product payslips for one year, with no cutover to split them.</summary>
    public const string UnresolvedOverlapCode = "YTD_OPENING_BALANCE_OVERLAP_UNRESOLVED";

    /// <summary>Validation Warning: locked pre-cutover payslips were left out of YTD because the opening balance contains them.</summary>
    public const string PreCutoverExcludedCode = "YTD_PRE_CUTOVER_PAYSLIPS_IN_OPENING_BALANCE";

    /// <summary>The balance types PayrollController sums into the payslip's YTD (normalised spellings).</summary>
    public static readonly string[] YtdGrossTypes = { "YTD_GROSS", "GROSS", "EARNINGS" };
    public static readonly string[] YtdDeductionTypes = { "YTD_DEDUCTIONS", "YTD_DEDUCTION", "DEDUCTIONS", "DEDUCTION" };
    public static readonly string[] YtdNetTypes = { "YTD_NET", "NET" };

    private static readonly HashSet<string> PayslipYtdTypes =
        new(YtdGrossTypes.Concat(YtdDeductionTypes).Concat(YtdNetTypes), StringComparer.OrdinalIgnoreCase);

    /// <summary>The first day of the cutover month, or null when no cutover governs the entity.</summary>
    public static DateOnly? CutoverMonth(DateOnly? cutover) =>
        cutover is DateOnly c ? new DateOnly(c.Year, c.Month, 1) : null;

    /// <summary>Whether a run for (<paramref name="runYear"/>, <paramref name="runMonth"/>) adds the carried opening balances.</summary>
    public static bool CountsOpeningBalances(int runYear, int runMonth, DateOnly? cutover) =>
        CutoverMonth(cutover) is not DateOnly cm || new DateOnly(runYear, runMonth, 1) >= cm;

    /// <summary>Whether a run counts an earlier locked payslip for (<paramref name="slipYear"/>, <paramref name="slipMonth"/>).</summary>
    public static bool CountsPriorSlip(int runYear, int runMonth, int slipYear, int slipMonth, DateOnly? cutover)
    {
        if (CutoverMonth(cutover) is not DateOnly cm) return true;
        if (new DateOnly(runYear, runMonth, 1) < cm) return true;
        return new DateOnly(slipYear, slipMonth, 1) >= cm;
    }

    /// <summary>True for a balance row the payslip's YTD block would sum.</summary>
    public static bool IsPayslipYtdBalance(PayrollOpeningBalance b) =>
        b.Amount != 0m && PayslipYtdTypes.Contains(Normalise(b.BalanceType));

    private static string Normalise(string? value)
    {
        var chars = (value ?? string.Empty).Trim().ToUpperInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        return new string(chars).Trim('_');
    }

    /// <summary>
    /// Loads the YTD sources for one run, already partitioned. Process and /validate both call this, so
    /// the payslip figures and the validation findings are derived from one decision.
    /// </summary>
    public static async Task<YtdSources> LoadAsync(
        ZayraDbContext db, Guid tenantId, Guid companyId, PayrollRun run,
        IReadOnlyCollection<int> employeeIds, CancellationToken ct)
    {
        var cutover = await db.CompanyCutovers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CompanyId == companyId && x.Status != CutoverStatuses.Planned)
            .OrderByDescending(x => x.CutoverDate)
            .Select(x => (DateOnly?)x.CutoverDate)
            .FirstOrDefaultAsync(ct);

        // COMPLIANCE: YTD — locked runs earlier in the same year, PLUS any other locked run in this same
        // month (POD-B2: two runs in one month must see each other).
        var candidates = await db.PayrollSlips.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Join(db.PayrollRuns.AsNoTracking().Where(r => r.TenantId == tenantId && r.CompanyId == companyId && r.Year == run.Year
                    && (r.Month < run.Month || (r.Month == run.Month && r.Id != run.Id))
                    && r.Status == "Locked"),
                  s => s.RunId, r => r.Id, (s, r) => new { Slip = s, r.Year, r.Month })
            .ToListAsync(ct);

        var balances = await db.PayrollOpeningBalances.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                && x.Year == run.Year
                && employeeIds.Contains(x.EmployeeId)
                && (x.CompanyId == companyId || x.CompanyId == null))
            .ToListAsync(ct);

        var countsOpening = CountsOpeningBalances(run.Year, run.Month, cutover);
        var employeesWithCarriedYtd = balances.Where(IsPayslipYtdBalance).Select(b => b.EmployeeId).ToHashSet();
        var employeesWithPriorSlips = candidates.Select(c => c.Slip.EmployeeId).ToHashSet();

        // PER EMPLOYEE. A pre-cutover payslip is left out only for an employee whose carried YTD stands in
        // for it. Someone hired during the parallel run is not in the legacy file at all: their pre-cutover
        // payslips are the ONLY record of those months and must count.
        var priorSlips = candidates
            .Where(c => !employeesWithCarriedYtd.Contains(c.Slip.EmployeeId)
                     || CountsPriorSlip(run.Year, run.Month, c.Year, c.Month, cutover))
            .Select(c => c.Slip)
            .ToList();

        var unresolved = cutover is null
            ? employeesWithCarriedYtd.Where(employeesWithPriorSlips.Contains).ToHashSet()
            : new HashSet<int>();
        var preCutoverExcluded = cutover is not null && countsOpening
            ? candidates
                .Where(c => !CountsPriorSlip(run.Year, run.Month, c.Year, c.Month, cutover)
                         && employeesWithCarriedYtd.Contains(c.Slip.EmployeeId))
                .Select(c => c.Slip.EmployeeId).ToHashSet()
            : new HashSet<int>();

        // The month to declare when the overlap is unresolved: the first month this product locked for the
        // entity this year — the latest first cutover the importer accepts (CUTOVER_FIRST_DECLARATION_AFTER_LOCKED_RUN).
        DateOnly? suggestedCutover = candidates.Count == 0 ? null
            : candidates.Select(c => new DateOnly(c.Year, c.Month, 1)).Min();

        return new YtdSources(
            cutover,
            priorSlips,
            countsOpening
                ? balances.GroupBy(b => b.EmployeeId).ToDictionary(g => g.Key, g => g.ToList())
                : new Dictionary<int, List<PayrollOpeningBalance>>(),
            unresolved,
            preCutoverExcluded)
        {
            SuggestedCutover = suggestedCutover,
        };
    }
}

/// <summary>The partitioned YTD sources for one run. See <see cref="PayrollYtdBasis"/>.</summary>
public sealed record YtdSources(
    DateOnly? Cutover,
    IReadOnlyList<PayrollSlip> PriorSlips,
    IReadOnlyDictionary<int, List<PayrollOpeningBalance>> OpeningBalancesByEmployee,
    IReadOnlySet<int> UnresolvedOverlapEmployeeIds,
    IReadOnlySet<int> PreCutoverSlipsExcludedEmployeeIds)
{
    /// <summary>The cutover month to declare when the overlap is unresolved (see <see cref="PayrollYtdBasis.LoadAsync"/>).</summary>
    public DateOnly? SuggestedCutover { get; init; }
}
