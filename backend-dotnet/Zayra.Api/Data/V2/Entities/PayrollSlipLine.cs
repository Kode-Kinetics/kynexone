using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// The single line table behind every payslip, freezing the component, the GOSI branch, payer, applied wage, rule and band that produced each amount, plus where the amount came from. @tier:C @owner:Finance @retention:84-months-from-RecordDate-then-Keep
/// </summary>
public partial class PayrollSlipLine
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid SlipId { get; set; }

    public string PayComponentCode { get; set; } = null!;

    public Guid? StatutoryRuleId { get; set; }

    public Guid? StatutoryRuleBandId { get; set; }

    /// <summary>
    /// The line points at the input; the input does NOT point back. That is how the second cycle was broken (§8.4).
    /// </summary>
    public Guid? PayrollInputId { get; set; }

    /// <summary>
    /// Recovery evidence, and the surviving half of the broken payroll_slip_lines &lt;-&gt; loan_installments cycle (§8.4). FK deferred to the cross-domain pass: loan_installments is domain I.
    /// </summary>
    public Guid? LoanInstallmentId { get; set; }

    public Guid? CostCenterId { get; set; }

    public string Kind { get; set; } = null!;

    public decimal Amount { get; set; }

    public decimal? Quantity { get; set; }

    public decimal? Rate { get; set; }

    public string? GosiBranch { get; set; }

    public string? GosiPayer { get; set; }

    /// <summary>
    /// The wage actually applied to THIS line&apos;s GOSI branch, which differs whenever a branch has its own floor or cap. Renamed in revision 3 to end the ambiguity with the slip-level figure (§11.4).
    /// </summary>
    public decimal? AppliedContributoryWage { get; set; }

    public string? RulesVersion { get; set; }

    public string? GlDriver { get; set; }

    public string? SourceType { get; set; }

    public string? SourceSystem { get; set; }

    /// <summary>
    /// Opening-run provenance alongside source_system: which row of which legacy system this opening amount came from (§2.F).
    /// </summary>
    public string? SourceRecordId { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }
}
