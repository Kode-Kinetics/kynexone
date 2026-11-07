using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// One customer-defined monthly cash allowance (a <c>pay_components</c> row coded <c>ALW_&lt;NAME&gt;</c>, see
/// <c>EntitlementComponentRules.AllowancePrefix</c>) paid to one employee through the salary record in force. It belongs to an
/// <see cref="EmployeeSalaryStructure"/> row exactly as that row's housing and transport do, so it is dated, approved and
/// superseded with it: a new salary record carries its own copies, and an old one keeps what it paid.
///
/// <para><b>The money lives here and only here.</b> The ALW_ definition is a grade-package component the engine never pays
/// (<c>PayComponentEngine</c> emits nothing for it); payroll reads these rows. <see cref="Amount"/> is the full monthly amount
/// (for <c>PercentOfBasic</c> it is <c>Rate × basic</c> rounded to two decimals when the row was written, so a run never
/// recomputes a percentage of a different basic). The CHECKs hold the shape: Amount ⇒ no rate, PercentOfBasic ⇒ a rate in
/// (0, 1], and a positive amount either way.</para>
/// </summary>
public class EmployeeSalaryAllowance : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>The <see cref="EmployeeSalaryStructure"/> row (its <c>Id</c>) this allowance is part of.</summary>
    public Guid SalaryStructureId { get; set; }

    /// <summary>The ALW_ pay-component code.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary><see cref="AllowanceBases.Amount"/> or <see cref="AllowanceBases.PercentOfBasic"/> (never InKind: nothing is paid in kind here).</summary>
    public string Basis { get; set; } = AllowanceBases.Amount;

    /// <summary>The fraction of basic (0.10 = 10%) for <c>PercentOfBasic</c>; null for <c>Amount</c>.</summary>
    public decimal? Rate { get; set; }

    /// <summary>The full monthly amount, before any proration.</summary>
    public decimal Amount { get; set; }
}
