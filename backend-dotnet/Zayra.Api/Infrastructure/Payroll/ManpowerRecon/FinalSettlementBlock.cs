using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll.ManpowerRecon;

public sealed record FsSettlementFact(
    Guid SettlementId, int EmployeeId, string EmployeeCode, string EmployeeName, string? GlPeriod, string Status,
    DateOnly LastWorkingDay, decimal GrossPayable, decimal TotalDeductions);

public sealed record FsLineFact(Guid SettlementId, string ComponentCode, string LineType, decimal Amount, string Source);

public sealed record FsLeaverFact(int EmployeeId, string EmployeeCode, string EmployeeName, bool FsMissing);

public sealed class FinalSettlementBlockInput
{
    /// <summary>Every candidate settlement of the run's company; the block selects the accrued ones for each period.</summary>
    public IReadOnlyList<FsSettlementFact> Settlements { get; init; } = Array.Empty<FsSettlementFact>();
    public IReadOnlyList<FsLineFact> Lines { get; init; } = Array.Empty<FsLineFact>();
    public string CurrentPeriod { get; init; } = string.Empty;
    public string? PriorPeriod { get; init; }
    public DateOnly PeriodStart { get; init; }
    public IReadOnlyDictionary<int, ReconCategory> Categories { get; init; } = new Dictionary<int, ReconCategory>();
    /// <summary>Employees whose slip in the CURRENT run is their final wage month.</summary>
    public ISet<int> CurrentFinalWageEmployees { get; init; } = new HashSet<int>();
    public IReadOnlyList<FsLeaverFact> Leavers { get; init; } = Array.Empty<FsLeaverFact>();
}

public sealed record FinalSettlementBlockResult(
    ReconFinalSettlementDto Block, int AccruedCount, IReadOnlyList<ReconIssueDto> Issues);

/// <summary>
/// The final-settlement block of the reconciliation. Settlements are NOT headcount exits (the leaver already is one, and
/// counting both would double-count); they are shown on an accrual basis - the settlements that accrued into this run's
/// GL period - and each lands in exactly one cell so the count ties to the number accrued.
/// </summary>
public static class FinalSettlementBlock
{
    public const string CellLeaverInBridge = "LEAVER_IN_BRIDGE";
    public const string CellLeftEarlier = "LEFT_EARLIER_PERIOD";
    public const string CellFinalWageInRun = "FINAL_WAGE_MONTH_IN_RUN";
    public const string CellOther = "OTHER";

    private static readonly string[] KnownComponents =
    {
        FinalSettlementComponents.Gratuity, FinalSettlementComponents.LeaveEncashment, FinalSettlementComponents.NoticePay,
        FinalSettlementComponents.OtherDues, FinalSettlementComponents.NoticeShortfall, FinalSettlementComponents.OtherDeduction,
    };

    public static FinalSettlementBlockResult Build(FinalSettlementBlockInput input)
    {
        var issues = new List<ReconIssueDto>();
        var current = Accrued(input.Settlements, input.CurrentPeriod);
        var prior = input.PriorPeriod is null ? new List<FsSettlementFact>() : Accrued(input.Settlements, input.PriorPeriod);

        // Cells (current period only), by precedence.
        var counts = new Dictionary<string, (int Count, decimal Gross)>
        {
            [CellLeaverInBridge] = (0, 0m), [CellLeftEarlier] = (0, 0m), [CellFinalWageInRun] = (0, 0m), [CellOther] = (0, 0m),
        };
        foreach (var s in current)
        {
            var cell =
                input.Categories.TryGetValue(s.EmployeeId, out var cat) && cat == ReconCategory.Leaver ? CellLeaverInBridge
                : input.CurrentFinalWageEmployees.Contains(s.EmployeeId) ? CellFinalWageInRun
                : s.LastWorkingDay < input.PeriodStart ? CellLeftEarlier
                : CellOther;
            var (n, g) = counts[cell];
            counts[cell] = (n + 1, g + s.GrossPayable);
        }
        var cells = new[] { CellLeaverInBridge, CellLeftEarlier, CellFinalWageInRun, CellOther }
            .Select(name => new ReconFsCellDto(name, counts[name].Count, counts[name].Gross)).ToList();

        // Per component: prior period, current period, delta.
        decimal Sum(IEnumerable<FsSettlementFact> settlements, string code)
        {
            var ids = settlements.Select(s => s.SettlementId).ToHashSet();
            return input.Lines.Where(l => ids.Contains(l.SettlementId) && l.ComponentCode == code).Sum(l => l.Amount);
        }
        var codes = KnownComponents.Concat(
                input.Lines.Where(l => current.Concat(prior).Any(s => s.SettlementId == l.SettlementId))
                    .Select(l => l.ComponentCode).Where(c => !KnownComponents.Contains(c)).Distinct().OrderBy(c => c, StringComparer.Ordinal))
            .ToList();
        var perComponent = codes.Select(code =>
        {
            var p = Sum(prior, code);
            var c = Sum(current, code);
            return new ReconFsComponentDto(code, p, c, c - p);
        }).ToList();

        // I6: the lines of a settlement add up to its header. Only the settlement's OWN lines count (loan and receivable
        // recovery lines ride the run and are not in the header). Deduction lines are rounded one by one after scaling,
        // hence the per-line tolerance (the same one the payslip invariant allows).
        decimal totalDifference = 0m;
        var ok = true;
        foreach (var s in current.Concat(prior))
        {
            var own = input.Lines.Where(l => l.SettlementId == s.SettlementId && l.Source == FinalSettlementComponents.SettlementSource).ToList();
            var earnings = own.Where(l => l.LineType == FinalSettlementLineTypes.Earning).Sum(l => l.Amount);
            var deductionLines = own.Where(l => l.LineType == FinalSettlementLineTypes.Deduction).ToList();
            var deductions = deductionLines.Sum(l => l.Amount);
            var tolerance = 0.01m + 0.005m * deductionLines.Count;
            var earnDiff = Math.Abs(s.GrossPayable - earnings);
            var dedDiff = Math.Abs(s.TotalDeductions - deductions);
            if (earnDiff > tolerance || dedDiff > tolerance)
            {
                ok = false;
                totalDifference += earnDiff + dedDiff;
                issues.Add(new ReconIssueDto("settlement_lines_differ_from_header",
                    $"The lines of the final settlement for {s.EmployeeCode} do not add up to its totals (difference {earnDiff + dedDiff:0.00}).",
                    s.EmployeeId));
            }
        }

        var withoutFs = input.Leavers.Where(l => l.FsMissing)
            .Select(l => new ReconLeaverWithoutFsDto(l.EmployeeId, l.EmployeeCode, l.EmployeeName)).ToList();

        var block = new ReconFinalSettlementDto(cells, perComponent, withoutFs, new ReconTieDto(ok, totalDifference));
        return new FinalSettlementBlockResult(block, current.Count, issues);
    }

    private static List<FsSettlementFact> Accrued(IReadOnlyList<FsSettlementFact> all, string period) =>
        all.Where(s => FinalSettlementStatuses.HasAccrued(s.Status) && string.Equals(s.GlPeriod, period, StringComparison.Ordinal)).ToList();
}
