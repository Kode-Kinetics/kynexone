using Zayra.Api.Application.CountryPack;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// One emitted payslip line (an as-persisted PayrollEarning or PayrollDeduction shape).
/// </summary>
public readonly record struct PayComponentLine(
    string Code, string Name, decimal Amount, string Source, bool IsEmployerContribution);

/// <summary>
/// Everything the engine needs to value one employee's components, already computed by the caller.
///
/// This is the crux of the "provably-equivalent cutover": the ENGINE owns line SELECTION, ORDER,
/// CODE, NAME, SOURCE and CLASSIFICATION (driven by the data-driven <see cref="PayComponent"/> set), but
/// the AMOUNTS are produced by the same subsystems as today — a salary-structure column, the tax
/// resolver, the overtime/attendance/loan ledgers, and (critically) the country pack for statutory —
/// and are passed in here verbatim. The engine performs NO arithmetic on statutory amounts and never
/// derives the social-insurance base; that stays pack-owned (per the country-pack contract). Rounding
/// is therefore identical to the current per-line round-then-sum.
///
/// Family/statutory line lists are pre-built by the caller (so the caller's existing NormalizeCode /
/// AdjustmentLabel / pack-label helpers produce byte-identical codes and labels).
/// </summary>
public sealed record PayComponentContext
{
    // ── Salary-structure scalars (CalcMethod=StructureField / Percent providers) ──
    public decimal Basic { get; init; }
    public decimal Housing { get; init; }
    public decimal Transport { get; init; }
    public decimal OtherAllowances { get; init; }   // Food + Mobile + Other (composite)
    public decimal FixedDeduction { get; init; }
    public decimal Gross { get; init; }

    // ── Subsystem-produced amounts + their dynamic-label inputs ──────────────────
    public decimal OvertimePay { get; init; }
    public decimal OtHours { get; init; }
    public decimal HourlyRate { get; init; }
    public decimal OtMultiplier { get; init; }

    public decimal TaxDeduction { get; init; }
    public decimal IncomeTaxRate { get; init; }

    public decimal AttendanceDeduction { get; init; }

    public decimal LopDeduction { get; init; }
    public decimal LopDays { get; init; }
    public decimal LopDayRate { get; init; }

    public decimal LeaveDeduction { get; init; }
    public decimal LoanEmi { get; init; }
    public decimal AdvEmi { get; init; }

    // ── Pre-built family + statutory lines (caller owns code/label generation) ───
    public IReadOnlyList<PayComponentLine> BonusLines { get; init; } = Array.Empty<PayComponentLine>();
    public IReadOnlyList<PayComponentLine> AdjustmentEarningLines { get; init; } = Array.Empty<PayComponentLine>();
    public IReadOnlyList<PayComponentLine> AdjustmentDeductionLines { get; init; } = Array.Empty<PayComponentLine>();
    /// <summary>Country-pack statutory result lines — the SOLE source of statutory amounts.</summary>
    public IReadOnlyList<StatutoryDeductionLine> StatutoryLines { get; init; } = Array.Empty<StatutoryDeductionLine>();
}

/// <summary>The ordered lines for one employee's payslip.</summary>
public sealed record PayComponentComputation(
    IReadOnlyList<PayComponentLine> Earnings,
    IReadOnlyList<PayComponentLine> Deductions);

/// <summary>
/// The data-driven, configurable pay-component engine. Pure (no DB / no HTTP): given the active
/// component definitions and an already-computed <see cref="PayComponentContext"/>, it produces the
/// exact ordered earning/deduction line set — replacing the fixed compiled sequence in
/// PayrollController.Process while keeping the persisted line shape (Code/Name/Amount/Source/employer
/// flag) unchanged so GL routing, WPS and the payslip builder downstream are untouched.
///
/// When fed the seeded system components (or the compiled <see cref="PayComponentCatalog"/> fallback),
/// the output is byte-identical to the current engine — proven by the golden-master + equivalence-twin
/// tests. Client-added components (new codes / Fixed / Percent / structure fields) compose additively.
/// </summary>
public static class PayComponentEngine
{
    public static PayComponentComputation Compute(
        IReadOnlyList<PayComponent> components, PayComponentContext ctx)
    {
        var earnings = new List<PayComponentLine>();
        var deductions = new List<PayComponentLine>();

        // Earnings and deductions are ordered INDEPENDENTLY (each stream starts fresh), matching the two
        // separate emission blocks today. EmployerContribution rows persist as deduction-table rows.
        var earningDefs = components
            .Where(c => c.IsActive && !c.IsDeleted && c.ComponentType == PayComponentTypes.Earning)
            .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Code, StringComparer.Ordinal);
        var deductionDefs = components
            .Where(c => c.IsActive && !c.IsDeleted
                && (c.ComponentType == PayComponentTypes.Deduction || c.ComponentType == PayComponentTypes.EmployerContribution))
            .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Code, StringComparer.Ordinal);

        foreach (var c in earningDefs)
            earnings.AddRange(Resolve(c, ctx));
        foreach (var c in deductionDefs)
            deductions.AddRange(Resolve(c, ctx));

        return new PayComponentComputation(earnings, deductions);
    }

    private static IReadOnlyList<PayComponentLine> Resolve(PayComponent c, PayComponentContext ctx)
    {
        // 1) Statutory rows are pack-owned: amount ALWAYS from the country pack, never from config.
        if (c.IsStatutory || c.CalcMethod == PayComponentCalcMethods.Statutory
            || c.ProviderKey == PayComponentProviders.Statutory)
            return StatutoryLines(c, ctx);

        // 2) Built-in provider rows (integration-sourced or subsystem-formula amounts).
        if (!string.IsNullOrEmpty(c.ProviderKey))
            return c.ProviderKey switch
            {
                PayComponentProviders.Bonus            => c.ComponentType == PayComponentTypes.Earning ? ctx.BonusLines : Array.Empty<PayComponentLine>(),
                PayComponentProviders.Adjustment       => c.ComponentType == PayComponentTypes.Earning ? ctx.AdjustmentEarningLines : ctx.AdjustmentDeductionLines,
                PayComponentProviders.Overtime         => Single(c, c.Code, OvertimeLabel(ctx), ctx.OvertimePay, "Overtime"),
                PayComponentProviders.Tax              => Single(c, c.Code, TaxLabel(ctx), ctx.TaxDeduction, "Tax"),
                PayComponentProviders.AttendanceShort  => Single(c, c.Code, c.NameEn, ctx.AttendanceDeduction, "Attendance"),
                PayComponentProviders.AttendanceLop    => Single(c, c.Code, LopLabel(ctx), ctx.LopDeduction, "Attendance"),
                PayComponentProviders.Leave            => Single(c, c.Code, c.NameEn, ctx.LeaveDeduction, "Leave"),
                PayComponentProviders.Loan             => Single(c, c.Code, c.NameEn, ctx.LoanEmi, "Loan"),
                PayComponentProviders.Advance          => Single(c, c.Code, c.NameEn, ctx.AdvEmi, "Loan"),
                _                                      => Array.Empty<PayComponentLine>(),
            };

        // 3) Structure-field + client value methods (the genuinely data-driven, client-editable surface).
        return c.CalcMethod switch
        {
            PayComponentCalcMethods.StructureField => Single(c, c.Code, c.NameEn, StructureFieldValue(c, ctx), "Salary"),
            PayComponentCalcMethods.Fixed          => Single(c, c.Code, c.NameEn, c.Value ?? 0m, "Salary"),
            PayComponentCalcMethods.PercentOfBasic => Single(c, c.Code, c.NameEn, Math.Round(ctx.Basic * (c.Value ?? 0m) / 100m, 2), "Salary"),
            PayComponentCalcMethods.PercentOfGross => Single(c, c.Code, c.NameEn, Math.Round(ctx.Gross * (c.Value ?? 0m) / 100m, 2), "Salary"),
            // Formula is a later client opt-in surface; never on the equivalence path (no seed uses it).
            _ => Array.Empty<PayComponentLine>(),
        };
    }

    private static decimal StructureFieldValue(PayComponent c, PayComponentContext ctx) => c.StructureField switch
    {
        PayComponentStructureFields.BasicSalary               => ctx.Basic,
        PayComponentStructureFields.HousingAllowance          => ctx.Housing,
        PayComponentStructureFields.TransportAllowance        => ctx.Transport,
        PayComponentStructureFields.OtherAllowancesComposite  => ctx.OtherAllowances,
        PayComponentStructureFields.FixedDeduction            => ctx.FixedDeduction,
        _ => 0m,
    };

    private static IReadOnlyList<PayComponentLine> StatutoryLines(PayComponent c, PayComponentContext ctx)
    {
        // EE side (Deduction) re-emits pack EmployeeAmount>0 lines; ER side (EmployerContribution) re-emits
        // EmployerAmount>0 lines flagged IsEmployerContribution — identical to the two pack loops today.
        var isEmployer = c.ComponentType == PayComponentTypes.EmployerContribution;
        var lines = new List<PayComponentLine>();
        foreach (var l in ctx.StatutoryLines)
        {
            var amount = isEmployer ? l.EmployerAmount : l.EmployeeAmount;
            if (amount > 0m)
                lines.Add(new PayComponentLine(l.Code, l.Label, amount, "Statutory", isEmployer));
        }
        return lines;
    }

    /// <summary>Single-line providers: emit when EmitWhenZero (BASIC) or the amount is strictly positive —
    /// reproducing the current "always emit BASIC, else <c>if (x &gt; 0)</c>" guards exactly.</summary>
    private static IReadOnlyList<PayComponentLine> Single(PayComponent c, string code, string name, decimal amount, string source)
        => (!c.EmitWhenZero && amount <= 0m)
            ? Array.Empty<PayComponentLine>()
            : new[] { new PayComponentLine(code, name, amount, source, false) };

    // ── Dynamic labels — MUST stay byte-identical to PayrollController.Process ────
    private static string OvertimeLabel(PayComponentContext ctx)
        => $"Overtime ({ctx.OtHours:N2} h × {Math.Round(ctx.HourlyRate, 2):N2}/h × {ctx.OtMultiplier:N2})";

    private static string TaxLabel(PayComponentContext ctx)
        => $"Income tax ({ctx.IncomeTaxRate}%)";

    private static string LopLabel(PayComponentContext ctx)
        => $"Loss of Pay ({ctx.LopDays:N2} d × {Math.Round(ctx.LopDayRate, 2):N2}/d)";
}
