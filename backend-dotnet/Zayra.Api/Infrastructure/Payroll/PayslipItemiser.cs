using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// The line items of the admin payslip PDF (single download and bulk bundle), built from what Process PERSISTED
/// and never recomputed.
///
/// <para>The slip header carries two catch-all buckets: <c>OtherAllowances</c> (which also holds overtime, bonus,
/// adjustments, arrears, settlements and customer-defined allowances) and <c>Deductions</c> (everything the
/// employee pays: statutory share, late/early, loss of pay, leave, loans, advances, tax, recovery). Printing the
/// buckets alone told a Saudi inspector nothing. Process also writes one <see cref="PayrollEarning"/> /
/// <see cref="PayrollDeduction"/> row per item (the rows the ESS payslip is rendered from), and
/// <c>PayslipLineInvariant</c> proves they add up to the header. Each such row is lifted OUT of its bucket and
/// printed under its own persisted name, so the page can never exceed the slip; whatever no row explains stays in
/// "Other Allowances" / "Other Deductions". If rows ever claimed MORE than their bucket holds the bucket is
/// printed unitemised instead: the page total is the slip, always.</para>
/// </summary>
public static class PayslipItemiser
{
    private static readonly HashSet<string> HeaderEarningCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BASIC", "HOUSING", "TRANSPORT",
        // The fixed-allowance bucket's own row: it is the residual's explanation, not a separate line.
        "OTHER_ALLOWANCES",
    };

    private static readonly System.Text.RegularExpressions.Regex TrailingWorking = new(@"\s*\(.*\d.*\)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static List<PayslipLineItem> Build(
        PayrollSlip slip,
        string basicLabel,
        IReadOnlyCollection<PayrollEarning> earningRows,
        IReadOnlyCollection<PayrollDeduction> deductionRows,
        IReadOnlyCollection<PayrollArrearsLine> arrearsLines,
        bool canSeeSalary)
    {
        decimal Show(decimal amount) => canSeeSalary ? amount : 0m;
        // A persisted name carries its working ("Overtime (5.00 h × (62.50 + ...)/h)", "Loss of Pay (2.00 d × 266.67/d)",
        // "Income tax (5%)"): rates are pay data, so a caller who may not see amounts sees the bare name.
        string Name(string name) => canSeeSalary ? name : TrailingWorking.Replace(name ?? string.Empty, string.Empty);
        basicLabel = Name(basicLabel);
        var items = new List<PayslipLineItem>();

        // ── Earnings ────────────────────────────────────────────────────────────────────────────
        if (slip.BasicSalary > 0)        items.Add(new(basicLabel, Show(slip.BasicSalary), "Earning"));
        if (slip.HousingAllowance > 0)   items.Add(new("Housing Allowance", Show(slip.HousingAllowance), "Earning"));
        if (slip.TransportAllowance > 0) items.Add(new("Transport Allowance", Show(slip.TransportAllowance), "Earning"));

        // slip.OtherAllowances INCLUDES the arrears, so they are lifted out before anything else is.
        var bucket = slip.OtherAllowances - slip.ArrearsAmount;
        var earningLines = earningRows
            .Where(r => r.Amount > 0m
                        && !HeaderEarningCodes.Contains(r.ComponentCode)
                        && !string.Equals(r.Source, "Arrears", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (earningLines.Sum(r => r.Amount) > bucket) earningLines.Clear();   // rows claim more than the bucket holds: print the bucket
        var otherAllowances = bucket - earningLines.Sum(r => r.Amount);
        if (otherAllowances > 0) items.Add(new("Other Allowances", Show(otherAllowances), "Earning"));
        foreach (var r in earningLines) items.Add(new(Name(r.ComponentName), Show(r.Amount), "Earning"));

        foreach (var a in arrearsLines)
            items.Add(new($"Arrears — {PayrollArrearsComponents.Label(a.ComponentCode)} ({a.CoveredYear}-{a.CoveredMonth:D2})", Show(a.Amount), "Earning"));
        // Any arrears the slip carries that no line accounts for is still shown, never dropped silently.
        var unitemisedArrears = slip.ArrearsAmount - arrearsLines.Sum(a => a.Amount);
        if (unitemisedArrears > 0) items.Add(new("Arrears", Show(unitemisedArrears), "Earning"));

        // ── Deductions ──────────────────────────────────────────────────────────────────────────
        var employeeRows = deductionRows.Where(d => !d.IsEmployerContribution && d.Amount > 0m).ToList();
        bool IsStatutoryEmployee(PayrollDeduction d) => d.Source == "Statutory" && d.ComponentCode.EndsWith("-EE", StringComparison.OrdinalIgnoreCase);
        bool IsRecovery(PayrollDeduction d) => d.Source == PayrollRecoveryComponents.RecoverySource || d.ComponentCode == PayrollRecoveryComponents.ReceivableRecovery;

        var statutory = employeeRows.Where(IsStatutoryEmployee).ToList();
        foreach (var line in statutory) items.Add(new(Name(line.ComponentName), Show(line.Amount), "Deduction"));

        var recoveryTotal = employeeRows.Where(IsRecovery).Sum(d => d.Amount);
        if (recoveryTotal > 0) items.Add(new(PayrollRecoveryComponents.ReceivableRecoveryName, Show(recoveryTotal), "Deduction"));

        var remaining = slip.Deductions - statutory.Sum(d => d.Amount) - recoveryTotal;
        var deductionLines = employeeRows.Where(d => !IsStatutoryEmployee(d) && !IsRecovery(d)).ToList();
        if (deductionLines.Sum(d => d.Amount) > remaining) deductionLines.Clear();
        var otherDeductions = remaining - deductionLines.Sum(d => d.Amount);
        foreach (var d in deductionLines) items.Add(new(Name(d.ComponentName), Show(d.Amount), "Deduction"));
        if (otherDeductions > 0) items.Add(new("Other Deductions", Show(otherDeductions), "Deduction"));

        items.Add(new("Net Pay", Show(slip.NetSalary), "Net"));
        return items;
    }
}
