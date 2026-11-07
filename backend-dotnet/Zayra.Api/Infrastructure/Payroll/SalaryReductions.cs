using Zayra.Api.Data;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>The six pay slots of a salary record, and its fixed deduction. The deduction is not pay (it is not in <see cref="Total"/>), but a higher one
/// lowers take-home pay, so the reduction rule watches it.</summary>
public sealed record PaySlots(decimal Basic, decimal Housing, decimal Transport, decimal Food, decimal Mobile, decimal Other, decimal FixedDeduction)
{
    public decimal Total => Basic + Housing + Transport + Food + Mobile + Other;

    public static PaySlots Of(EmployeeSalaryStructure s) =>
        new(s.BasicSalary, s.HousingAllowance, s.TransportAllowance, s.FoodAllowance, s.MobileAllowance, s.OtherAllowance, s.FixedDeduction);

    public static PaySlots Of(SalaryChangeRequest c) =>
        new(c.BasicSalary, c.HousingAllowance, c.TransportAllowance, c.FoodAllowance, c.MobileAllowance, c.OtherAllowance, c.FixedDeduction);
}

/// <summary>One pay item that goes down: its name (<c>BASIC</c>, <c>HOUSING</c>, <c>TRANSPORT</c>, <c>FOOD</c>, <c>MOBILE</c>, <c>OTHER</c>, or an allowance's
/// code) with what it was and what it becomes (0 when it is removed). The one exception is <c>FIXED_DEDUCTION</c>, which is listed when the deduction goes UP
/// (a bigger deduction lowers take-home pay): its From is the deduction now and its To the higher one.</summary>
public sealed record ReducedItem(string Item, decimal From, decimal To);

/// <summary>What a proposed salary does against the record in force.</summary>
/// <param name="IsReduction">The whole monthly pay (slots plus allowance lines) goes down, or any single slot or allowance line goes down or is removed.</param>
/// <param name="Items">Every item that goes down, with what it was and what it becomes (a lower total always has at least one).</param>
/// <param name="ChangesContractWage">A slot (basic, housing, transport, food, mobile, other) or any allowance line differs from the record in force, up or down.
/// False when nothing is in force (a first package sets the contract wage, it does not change it).</param>
public sealed record ReductionResult(bool IsReduction, decimal CurrentTotal, decimal ProposedTotal, IReadOnlyList<ReducedItem> Items, bool ChangesContractWage)
{
    public static readonly ReductionResult None = new(false, 0m, 0m, [], false);
}

/// <summary>
/// Thrown where a salary package is validated deep inside a workflow (employee update, approval apply, merit planning) and it lowers pay without the employee's
/// agreement reference. It IS an <see cref="InvalidOperationException"/> (every existing catch still sees the same refusal, same message), but it keeps the stable
/// code and the items that go down, so an endpoint can answer the structured refusal the salary endpoints give instead of a generic invalid-change error.
/// </summary>
public sealed class SalaryReductionConsentRequiredException(AllowanceProblem problem, IReadOnlyList<ReducedItem> items) : InvalidOperationException(problem.Message)
{
    public string Code { get; } = problem.Code;
    public IReadOnlyList<ReducedItem> Items { get; } = items;
}

/// <summary>
/// The rule that lowering an employee's pay needs their written agreement. "Pay" is the six slots and every cash allowance line (a percentage resolved on
/// the proposed basic, carried lines included); the fixed deduction is not pay, but a HIGHER one (or a new one) lowers take-home pay and counts as a reduction
/// too. A proposal is a reduction when the total goes down OR any single item goes down or disappears OR the fixed deduction goes up. It must then carry a consent reference (the signed amendment, the Qiwa request number: free text up to 200), or it is refused.
/// Whether a change IS a reduction is always worked out against the record in force at that moment, so it is never stored and cannot go stale.
/// A first package (nothing in force) is never a reduction.
/// </summary>
public static class SalaryReductions
{
    public const string ConsentRequiredCode = "reduction_consent_required";
    public const string ReferenceTooLongCode = "reduction_consent_reference_too_long";
    public const int ReferenceMaxLength = 200;
    public const string ReferenceTooShortCode = "reduction_consent_reference_too_short";
    /// <summary>A real reference is at least this many characters (after trimming) and carries a letter or a digit: "x" and "---" are not references.</summary>
    public const int ReferenceMinLength = 3;
    /// <summary>The item name a higher fixed deduction is listed under.</summary>
    public const string FixedDeductionItem = "FIXED_DEDUCTION";

    public const string ConsentRequiredMessage = "Lowering an employee's pay needs their written agreement; enter its reference.";

    private static IEnumerable<(string Item, decimal Value)> Named(PaySlots s) =>
        [("BASIC", s.Basic), ("HOUSING", s.Housing), ("TRANSPORT", s.Transport), ("FOOD", s.Food), ("MOBILE", s.Mobile), ("OTHER", s.Other)];

    /// <summary>Compares a proposal with the record in force. <paramref name="current"/> null = nothing in force.</summary>
    public static ReductionResult Compare(PaySlots? current, IReadOnlyCollection<(string Code, decimal Amount)> currentLines,
        PaySlots proposed, IReadOnlyCollection<(string Code, decimal Amount)> proposedLines)
    {
        if (current is null) return ReductionResult.None;
        var items = new List<ReducedItem>();
        var wageChanged = false;
        foreach (var (before, after) in Named(current).Zip(Named(proposed)))
        {
            if (after.Value != before.Value) wageChanged = true;
            if (after.Value < before.Value) items.Add(new ReducedItem(before.Item, before.Value, after.Value));
        }
        // A bigger deduction (or one where there was none) takes more out of the employee's pay: it needs their agreement like a lower item does.
        if (proposed.FixedDeduction > current.FixedDeduction) items.Add(new ReducedItem(FixedDeductionItem, current.FixedDeduction, proposed.FixedDeduction));
        var proposedByCode = proposedLines.GroupBy(l => l.Code, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount), StringComparer.Ordinal);
        var currentByCode = currentLines.GroupBy(l => l.Code, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount), StringComparer.Ordinal);
        foreach (var (code, before) in currentByCode.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var after = proposedByCode.GetValueOrDefault(code);   // 0 when the line is gone
            if (after != before) wageChanged = true;
            if (after < before) items.Add(new ReducedItem(code, before, after));
        }
        if (proposedByCode.Keys.Any(code => !currentByCode.ContainsKey(code))) wageChanged = true;
        var currentTotal = current.Total + currentByCode.Values.Sum();
        var proposedTotal = proposed.Total + proposedByCode.Values.Sum();
        return new ReductionResult(proposedTotal < currentTotal || items.Count > 0, currentTotal, proposedTotal, items, wageChanged);
    }

    /// <summary>The lines a proposal that does not mention allowances carries: the record in force's, a percentage re-worked on the proposed basic.</summary>
    public static List<(string Code, decimal Amount)> CarriedLines(IEnumerable<EmployeeSalaryAllowance> inForce, decimal proposedBasic) =>
        inForce.Select(l => (l.Code, l.Basis == AllowanceBases.PercentOfBasic && l.Rate is decimal rate ? SalaryAllowanceLines.PercentAmount(rate, proposedBasic) : l.Amount)).ToList();

    /// <summary>
    /// Whether a proposal for <paramref name="employeeId"/> from <paramref name="effectiveDate"/> is a reduction against the salary record in force then.
    /// <paramref name="proposedLines"/> null = the proposal does not mention allowances (the record's lines are carried).
    /// </summary>
    public static async Task<ReductionResult> EvaluateAsync(ZayraDbContext db, Guid tenantId, int employeeId, DateOnly effectiveDate, PaySlots proposed,
        IReadOnlyCollection<ResolvedAllowanceLine>? proposedLines, CancellationToken ct)
    {
        var (record, lines) = await SalaryAllowanceLines.InForceAsync(db, tenantId, employeeId, effectiveDate, ct);
        if (record is null) return ReductionResult.None;
        var after = proposedLines is null
            ? CarriedLines(lines, proposed.Basic)
            : proposedLines.Select(l => (l.Code, l.Amount)).ToList();
        return Compare(PaySlots.Of(record), lines.Select(l => (l.Code, l.Amount)).ToList(), proposed, after);
    }

    /// <summary>A reference as it is kept: trimmed; null when blank.</summary>
    public static string? CleanReference(string? reference)
    {
        var text = (reference ?? string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>The refusal for a reference that cannot be kept (too long, too short, no letter or digit, or with control characters); null when it can.</summary>
    public static AllowanceProblem? CheckReference(string? reference)
    {
        if (CleanReference(reference) is not { } text) return null;
        if (text.Length > ReferenceMaxLength)
            return new(ReferenceTooLongCode, $"Keep the consent reference under {ReferenceMaxLength} characters.", 400);
        if (text.Any(char.IsControl))
            return new("reduction_consent_reference_invalid", "The consent reference can't contain control characters.", 400);
        if (text.Length < ReferenceMinLength || !text.Any(char.IsLetterOrDigit))
            return new(ReferenceTooShortCode, $"Enter a real reference: at least {ReferenceMinLength} characters, including a letter or a number.", 400);
        return null;
    }

    /// <summary>The refusal for a reduction without a consent reference, naming what goes down; null when it is not a reduction or carries a reference.</summary>
    public static AllowanceProblem? RequireConsent(ReductionResult result, string? reference)
    {
        if (!result.IsReduction || CleanReference(reference) is not null) return null;
        var what = result.Items.Count == 0 ? string.Empty
            : " (" + string.Join(", ", result.Items.Take(4).Select(i => i.Item == FixedDeductionItem ? $"Fixed deduction raised {i.From:N2} to {i.To:N2}"
                                                                    : i.To == 0m ? $"{i.Item} removed" : $"{i.Item} {i.From:N2} to {i.To:N2}")) + (result.Items.Count > 4 ? ", and more" : string.Empty) + ")";
        return new(ConsentRequiredCode, ConsentRequiredMessage + what, 400);
    }
}
