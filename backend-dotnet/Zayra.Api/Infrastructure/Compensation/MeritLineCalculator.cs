using System.Globalization;
using System.Text.Json;
using Zayra.Api.Application.Employees;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Compensation;

/// <summary>
/// Turns a planner's proposal into a worksheet line: snapshot of the basis, the guideline cell, the award at line
/// level (cycle currency authoritative, pay currency derived), the new basic/gross and every flag. Pure — the
/// caller persists. Formulas: <see cref="MeritMath"/>; worked examples in CMP_DESIGN.md.
/// </summary>
public static class MeritLineCalculator
{
    public static IReadOnlyList<string> Compute(CompWorksheetLine line, EmployeeBasis b, MeritBasisLoader.CycleContext ctx,
        decimal? proposedIncrease, decimal? proposedPercent, string? justification)
    {
        var e = b.Employee;
        line.EmployeeId = e.Id;
        line.DepartmentId = e.DepartmentId;
        line.GradeId = e.GradeId;
        line.DesignationId = e.DesignationId;
        line.PayCurrency = b.PayCurrency;
        line.FxRate = b.FxRate;
        line.SalaryRowId = b.SalaryRow?.Id;
        line.CurrentBasic = b.Basic;
        line.CurrentGross = b.Gross;
        line.CurrentBasicCycle = b.BasicCycle;
        line.CurrentGrossCycle = b.GrossCycle;
        line.CompaRatio = b.CompaRatio;
        line.RangePosition = b.RangePosition;
        line.Rating = b.Rating;
        line.EligibleDays = b.EligibleDays;
        line.ProrationFactor = b.ProrationFactor;
        var cell = ctx.GuidelineFor(b.Rating, b.CompaRatio);
        line.GuidelineMinPercent = cell?.MinPercent;
        line.GuidelineTargetPercent = cell?.TargetPercent;
        line.GuidelineMaxPercent = cell?.MaxPercent;
        line.Justification = string.IsNullOrWhiteSpace(justification) ? null : justification.Trim();

        var flags = new List<string>();
        var blocking = new List<string>();

        // ── the proposal, in cycle currency ──
        // A percent entered is kept as entered (ProposedAsPercent) and the guideline is tested against THAT percent; an
        // amount entered is tested against the guideline in money, with a half-cent tolerance. Re-deriving a percent from
        // a rounded amount (6,123.45 × 3 % = 183.70 → 2.9999 %) must never push a line outside its own guideline.
        decimal increase = 0m;
        bool? enteredAsPercent = null;
        if (b.BasicCycle is decimal basicCycle)
        {
            if (proposedPercent is decimal pct && proposedIncrease is null)
            {
                if (pct < 0 || pct > 100) throw new CompRuleException("invalid_percent", $"{e.FullName}: the increase must be between 0 % and 100 %.");
                increase = MeritMath.IncreaseFromPercent(basicCycle, pct);
                line.ProposedPercent = MeritMath.Ratio(pct);
                enteredAsPercent = true;
            }
            else
            {
                var amount = proposedIncrease ?? 0m;
                if (amount < 0) throw new CompRuleException("negative_increase", $"{e.FullName}: an increase cannot be negative. Use a reversal for a decrease.");
                increase = MeritMath.Money(amount);
                line.ProposedPercent = MeritMath.Percent(increase, basicCycle);
                enteredAsPercent = false;
            }
            line.ProposedIncrease = increase;
        }
        else
        {
            line.ProposedIncrease = 0m;
            line.ProposedPercent = 0m;
            if ((proposedIncrease ?? 0m) > 0 || (proposedPercent ?? 0m) > 0)
                blocking.Add($"{e.FullName}: no {b.PayCurrency} → {ctx.Cycle.Currency} rate is entered for this cycle, so no award can be planned.");
        }
        line.ProposedAsPercent = enteredAsPercent ?? false;
        line.ProratedIncrease = MeritMath.Money(line.ProposedIncrease * line.ProrationFactor);
        line.ProratedIncreasePay = b.FxRate is decimal rate && line.ProratedIncrease > 0 ? MeritMath.FromCycle(line.ProratedIncrease, rate) : 0m;
        line.NewBasic = line.CurrentBasic + line.ProratedIncreasePay;
        line.NewGross = line.CurrentGross + line.ProratedIncreasePay;
        line.NewCompaRatio = MeritBasisLoader.GradePosition(ctx, b.Grade, line.NewGross, b.PayCurrency).Compa;

        var awarded = line.ProratedIncreasePay > 0;
        line.OutOfGuideline = awarded
            ? cell is null || !WithinGuideline(line, cell, b.BasicCycle ?? 0m)
            : cell is not null && cell.MinPercent > 0 && b.Eligible;

        // ── flags ──
        if (b.FxRate is null) { flags.Add(CompFlags.FxMissing); blocking.Add($"{e.FullName}: paid in {b.PayCurrency}; enter a {b.PayCurrency} rate for this cycle."); }
        if (b.SalaryRow is null) { flags.Add(CompFlags.NoSalaryRow); if (awarded) blocking.Add($"{e.FullName}: has no salary record on or before {ctx.Cycle.EffectiveDate:yyyy-MM-dd}, so there is no basic to increase."); }
        if (b.ContractBound) { flags.Add(CompFlags.ContractBoundPay); if (awarded) blocking.Add($"{e.FullName}: pay is bound to a contract renewal or an allowance basis; change it through Contract renewals."); }
        if (b.HasLaterSalaryRow) { flags.Add(CompFlags.LaterSalaryRow); if (awarded) blocking.Add($"{e.FullName}: a salary change dated after {ctx.Cycle.EffectiveDate:yyyy-MM-dd} already exists and would override this award."); }
        if (b.Grade is null || b.Grade.MidSalary <= 0) flags.Add(CompFlags.NoGradeRange);
        // The single salary writer refuses a package outside the grade's range (raw amounts, as it compares them),
        // so an award that would land outside it can never be applied. Refused here, not discovered on the 1st.
        if (awarded && b.Grade is { } grade)
        {
            if (grade.MaxSalary > 0 && line.NewGross > grade.MaxSalary) { flags.Add(CompFlags.AboveRangeMax); blocking.Add($"{e.FullName}: new gross {Money(line.NewGross)} is above grade {grade.Code}'s maximum {Money(grade.MaxSalary)}. Lower the award or change the grade first."); }
            if (grade.MinSalary > 0 && line.NewGross < grade.MinSalary) { flags.Add(CompFlags.BelowRangeMin); blocking.Add($"{e.FullName}: new gross {Money(line.NewGross)} is still below grade {grade.Code}'s minimum {Money(grade.MinSalary)}; the salary record refuses packages outside the range."); }
        }
        if (b.HasPendingSalaryChange) flags.Add(CompFlags.PendingSalaryChange);
        if (line.OutOfGuideline) flags.Add(CompFlags.OutOfGuideline);
        if (line.ProrationFactor < 1m && b.Eligible) flags.Add(CompFlags.Prorated);
        if (b.Benchmark.State == "missing") flags.Add(CompFlags.MissingBenchmark);
        if (b.Benchmark.State == "stale") flags.Add(CompFlags.StaleBenchmark);
        if (b.Benchmark.State == "out_of_scope") flags.Add(CompFlags.OutOfScopeBenchmark);
        line.Flags = string.Join(',', flags.Distinct());
        line.UpdatedAtUtc = DateTime.UtcNow;
        return blocking;
    }

    /// <summary>Entered percent: exact comparison. Entered amount: compared in money with a half-cent tolerance on each
    /// side, because the amount itself is rounded to the cent.</summary>
    internal static bool WithinGuideline(CompWorksheetLine line, CompGuideline cell, decimal basicCycle)
    {
        if (line.ProposedAsPercent) return line.ProposedPercent >= cell.MinPercent && line.ProposedPercent <= cell.MaxPercent;
        const decimal halfCent = 0.005m;
        return line.ProposedIncrease >= basicCycle * cell.MinPercent / 100m - halfCent
               && line.ProposedIncrease <= basicCycle * cell.MaxPercent / 100m + halfCent;
    }

    /// <summary>The proposal to re-apply when a stored line is recomputed: its entered percent, or its amount.</summary>
    public static (decimal? Amount, decimal? Percent) StoredProposal(CompWorksheetLine? line) =>
        line is null ? (0m, null) : line.ProposedAsPercent ? (null, line.ProposedPercent) : (line.ProposedIncrease, null);

    private static string Money(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// The salaryBreakdown patch an award writes: basic raised by the prorated pay-currency amount, every allowance and
    /// the fixed deduction carried from the row the award is based on, the same structure code, the cycle's effective
    /// date and the pay currency. Exactly the shape <see cref="EmployeeSalaryBreakdownChanges.Read"/> accepts.
    /// </summary>
    public static Dictionary<string, JsonElement> Package(EmployeeSalaryStructure row, string? structureCode, decimal newBasic, DateOnly effectiveDate, string currency)
    {
        var package = new Dictionary<string, object?>
        {
            ["basicSalary"] = newBasic,
            ["housingAllowance"] = row.HousingAllowance,
            ["transportAllowance"] = row.TransportAllowance,
            ["foodAllowance"] = row.FoodAllowance,
            ["mobileAllowance"] = row.MobileAllowance,
            ["otherAllowance"] = row.OtherAllowance,
            ["fixedDeduction"] = row.FixedDeduction,
            ["effectiveDate"] = effectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["currency"] = currency,
        };
        if (!string.IsNullOrWhiteSpace(structureCode)) package["salaryStructureCode"] = structureCode;
        return new Dictionary<string, JsonElement> { [EmployeeSalaryBreakdownChanges.Key] = JsonSerializer.SerializeToElement(package) };
    }
}
