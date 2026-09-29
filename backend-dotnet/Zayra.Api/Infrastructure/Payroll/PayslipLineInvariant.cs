using Zayra.Api.Models;

namespace Zayra.Api.Infrastructure.Payroll;

/// <summary>
/// F2 — the invariant whose absence WAS the pre-F2 net-pay defect: for one employee on one run,
/// Σ(earning lines) must equal the slip's GrossSalary and Σ(employee deduction lines) its Deductions.
///
/// <para>The Lock journal is DR Σ earning lines / CR Σ deduction lines + CR Σ NetSalary, and NetSalary is
/// derived from the slip aggregates — so for any employee where lines and aggregates diverge, the journal
/// cannot balance. Pre-F2 a tenant-configured component produced exactly that divergence: a payslip line
/// net pay never saw, surfacing only at Lock as an anonymous <c>gl_unbalanced</c> 422.</para>
///
/// <para><b>Pure and per-employee by design.</b> It takes one slip and that employee's lines and touches
/// nothing else — no DbContext, no cross-employee state — so a per-employee compute step (the Wave-2
/// job-queue <c>ComputeEmployeeAsync</c>) can call it on the lines it has just staged, and the current
/// single-transaction Process calls it once per slip after emission.</para>
///
/// <para><b>Tolerance</b> is the documented rounding already in the aggregates, nothing more: the
/// loan/advance EMI lines are emitted unrounded while the aggregate rounds their sum (POD-B1b-FIX, at most
/// 0.005), and a capped settlement's deduction lines are rounded individually after scaling (at most 0.005
/// each).</para>
/// </summary>
public static class PayslipLineInvariant
{
    public sealed record Mismatch(decimal EarningLines, decimal Gross, decimal DeductionLines, decimal Deductions);

    public static Mismatch? Check(
        PayrollSlip slip, IEnumerable<PayrollEarning> earnings, IEnumerable<PayrollDeduction> deductions)
    {
        var earnLines = earnings.Sum(e => e.Amount);
        var employeeDeductions = deductions.Where(d => !d.IsEmployerContribution).ToList();
        var dedLines = employeeDeductions.Sum(d => d.Amount);
        var settlementDeductionLines = employeeDeductions.Count(d => d.Source == FinalSettlementComponents.SettlementSource);
        var tolerance = 0.01m + 0.005m * settlementDeductionLines;
        return Math.Abs(earnLines - slip.GrossSalary) > tolerance || Math.Abs(dedLines - slip.Deductions) > tolerance
            ? new Mismatch(earnLines, slip.GrossSalary, dedLines, slip.Deductions)
            : null;
    }
}
