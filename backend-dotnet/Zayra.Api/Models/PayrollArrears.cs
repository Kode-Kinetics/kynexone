using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>POD-C3 — the itemised arrears components. One code per package component so the payslip and
/// the GL both keep the retro amount in the account it belongs to, never in a single opaque "arrears" bucket.</summary>
public static class PayrollArrearsComponents
{
    public const string Basic           = "ARREARS_BASIC";
    public const string Housing         = "ARREARS_HOUSING";
    public const string Transport       = "ARREARS_TRANSPORT";
    public const string OtherAllowances = "ARREARS_OTHER_ALLOWANCES";

    public static readonly string[] All = { Basic, Housing, Transport, OtherAllowances };

    /// <summary>The prefix <see cref="ForSourceComponent"/> uses for any component outside the four
    /// named ones, and therefore the prefix <see cref="SourceComponent"/> must strip to invert it.</summary>
    public const string Prefix = "ARREARS_";

    /// <summary>
    /// The salary earning code each arrears code settles the shortfall on. Exact inverse of
    /// <see cref="ForSourceComponent"/>, INCLUDING its generic <c>"ARREARS_" + code</c> branch — the two
    /// used to disagree there, which would have made a non-standard code's already-settled amount
    /// (ArrearsEngine.cs:148) key on <c>ARREARS_X</c> while its paid amount keyed on <c>X</c>, i.e. an
    /// arrears line that could be settled twice. Unreachable today (the engine only ever iterates
    /// <c>SalaryComponentCodes</c>) — closed anyway, because the GL routing fix
    /// (<c>EarningDriverKeyFor</c>) now depends on this being a real inverse.
    /// A code that is NOT an arrears code is returned unchanged, which is what makes the routing
    /// fix inert for every ordinary component.
    /// </summary>
    public static string SourceComponent(string arrearsCode) => arrearsCode switch
    {
        Basic           => "BASIC",
        Housing         => "HOUSING",
        Transport       => "TRANSPORT",
        OtherAllowances => "OTHER_ALLOWANCES",
        _ => arrearsCode.StartsWith(Prefix, StringComparison.Ordinal) && arrearsCode.Length > Prefix.Length
                ? arrearsCode[Prefix.Length..]
                : arrearsCode,
    };

    /// <summary>The arrears code for a salary earning code.</summary>
    public static string ForSourceComponent(string componentCode) => componentCode switch
    {
        "BASIC"            => Basic,
        "HOUSING"          => Housing,
        "TRANSPORT"        => Transport,
        "OTHER_ALLOWANCES" => OtherAllowances,
        _                  => Prefix + componentCode,
    };

    /// <summary>Contributory components only — mirrors <c>SalaryBreakdown.GosiCoveredWage = Basic + Housing</c>.</summary>
    public static bool IsGosiCovered(string arrearsCode) => arrearsCode is Basic or Housing;

    public static string Label(string arrearsCode) => arrearsCode switch
    {
        Basic           => "Basic salary",
        Housing         => "Housing allowance",
        Transport       => "Transport allowance",
        OtherAllowances => "Other allowances",
        _               => arrearsCode,
    };
}

/// <summary>POD-C3 — arrears line lifecycle.</summary>
public static class PayrollArrearsStatuses
{
    /// <summary>Paid by <see cref="PayrollArrearsLine.PayrollRunId"/>. Counts against the next run's
    /// "already settled" term, and is what the A1 reconstruction reads.</summary>
    public const string Settled = "Settled";

    /// <summary>The settling run was voided. Excluded from every sum, so the amount becomes due again on
    /// the replacement — which is exactly right, because the void un-paid it.</summary>
    public const string Voided = "Voided";

    /// <summary>
    /// A retro DECREASE (backdated pay cut, over-keyed package corrected, an LWD moved earlier). It is
    /// COMPUTED and PERSISTED — never netted into pay and never silently dropped — and the run REFUSES
    /// with a 422 naming each employee and period. B2's stated posture is ADDITIVE-ONLY: a negative delta
    /// (clawback) has no vehicle in a payroll run, and a negative earning would break WPS and the control
    /// accounts. Silence is the one option that must not ship.
    /// </summary>
    public const string PendingRecovery = "PendingRecovery";
}

/// <summary>
/// POD-C3 — ONE settled (or refused) retro amount, for ONE employee, for ONE covered period, for ONE
/// package component.
///
/// <para><b>THE ARREARS CONTRACT.</b> <c>Amount = EntitledAmount − PaidAmount − Σ(previously settled,
/// non-voided)</c>. Running the same run twice cannot double-pay because the third term absorbs the first
/// result — the formula is SELF-CORRECTING, not merely guarded. "What was actually paid" is read from
/// <c>PayrollEarning</c> rows keyed on COMPONENT CODE over the NON-VOIDED runs of that period, never from
/// the slip header (<c>PayrollSlip.OtherAllowances</c> is polluted with overtime, bonus and adjustment
/// earnings and is unusable as a witness).</para>
///
/// <para>Mirrors <c>EmployeeBonus</c>'s run-stamping pattern deliberately, so POD-A1's reconciliation can
/// re-derive the GOSI-bearing slice of a run the same way it already re-derives its bonuses.</para>
/// </summary>
public class PayrollArrearsLine : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal-entity scope — always stamped from the settling run.</summary>
    public Guid? CompanyId { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;

    /// <summary>The run that SETTLES this line (pays it). Null only for a <see cref="PayrollArrearsStatuses.PendingRecovery"/>
    /// line, which is refused rather than paid.</summary>
    public Guid? PayrollRunId { get; set; }

    /// <summary>The period the amount is FOR — not the period it is paid in.</summary>
    public int CoveredYear { get; set; }
    public int CoveredMonth { get; set; }

    /// <summary>One of <see cref="PayrollArrearsComponents"/>.</summary>
    public string ComponentCode { get; set; } = string.Empty;

    /// <summary>What the employee SHOULD have been paid for that component in that period, on the
    /// assignment now effective for it, prorated by THAT period's own joiner/leaver window.</summary>
    public decimal EntitledAmount { get; set; }

    /// <summary>What the non-voided runs for that period actually paid on that component.</summary>
    public decimal PaidAmount { get; set; }

    /// <summary>What earlier non-voided arrears runs already settled for that (employee, period, component).</summary>
    public decimal PreviouslySettledAmount { get; set; }

    /// <summary>The amount THIS run pays: entitled − paid − previously settled (see the class remarks).</summary>
    public decimal Amount { get; set; }

    /// <summary>The <c>EmployeeSalaryStructure</c> that produced the entitlement — the audit trail from a
    /// number back to the decision that caused it.</summary>
    public Guid? SourceAssignmentId { get; set; }
    public DateOnly? SourceEffectiveDate { get; set; }

    /// <summary>Does this amount enter the social-insurance base in the period PAID? Governed by
    /// <c>payparameter.arrears_gosi_treatment</c>; the SAME flag switches the A1 reconstruction, so the
    /// run and the reconciliation cannot drift.</summary>
    public bool IsGosiBearing { get; set; }

    /// <summary>
    /// [FLAG-COMPLIANCE-KSA] CEILING ABSORPTION, AS A NUMBER RATHER THAN A WARNING.
    /// <c>min(entitledGosiBase(P), ceiling) − min(paidGosiBase(P), ceiling)</c> — what the contributory
    /// base WOULD have increased by in the month EARNED, where that month had its own ceiling headroom.
    /// Because arrears ride inside the CURRENT month's single 45,000 cap, an employee already at the
    /// ceiling contributes nothing on their arrears; this column is the difference between "flagged" and
    /// "auditable" and is what an amended declaration would be prepared from. Carried on ONE line per
    /// (employee, covered period) — the first contributory component — so Σ over lines is the period total.
    /// </summary>
    public decimal EarnedBasisGosiDelta { get; set; }

    /// <summary>The proration basis and factor used to compute <see cref="EntitledAmount"/> for the covered period.</summary>
    public string Basis { get; set; } = string.Empty;
    public decimal ProrationFactor { get; set; } = 1m;

    /// <summary>See <see cref="PayrollArrearsStatuses"/>.</summary>
    public string Status { get; set; } = PayrollArrearsStatuses.Settled;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>POD-C3 — the deduction component that nets a prior void's 1420 receivable out of pay.</summary>
public static class PayrollRecoveryComponents
{
    public const string ReceivableRecovery = "RECEIVABLE_RECOVERY";
    public const string ReceivableRecoveryName = "Overpayment recovery (prior voided run)";
    /// <summary>Deduction source that routes to <c>DED:RECEIVABLE_RECOVERY</c> (credits 1420, not 2199).</summary>
    public const string RecoverySource = "Recovery";
}

/// <summary>POD-C3 — receivable lifecycle.</summary>
public static class PayrollReceivableStatuses
{
    public const string Outstanding = "Outstanding";
    public const string Recovered   = "Recovered";
    /// <summary>A legacy void journal with no payment records behind it: the 1420 debit is real but cannot
    /// be attributed to an employee, so it is carried EXPLICITLY rather than lost.</summary>
    public const string Unattributed = "Unattributed";
}

/// <summary>
/// POD-C3 — the PER-EMPLOYEE sub-ledger behind the aggregate <c>DR 1420 Employee Overpayment Receivable</c>
/// that a <c>FundsDisbursed</c> void posts.
///
/// <para><b>WHY IT HAS TO EXIST.</b> POD-B3 assigned the netting of that receivable to C3, but
/// <c>FinanceGlEntry</c> has NO employee dimension — <c>CompanyId</c> is its only extra scope. The 1420
/// debit is therefore one aggregate number per (event, period) and nothing can ever net it into a
/// replacement run. Per-employee attribution is recoverable ONLY from <c>PayrollPaymentRecord</c>, at the
/// moment of the void, which is where these rows are written.</para>
///
/// <para><b>ATTRIBUTION IS A HARD ASSERTION, NOT A BEST EFFORT.</b> Σ(sub-ledger) must equal the 1420
/// debit the void is about to post. On a mismatch the VOID IS REFUSED (<c>VoidRunResult.Blocked</c>)
/// rather than degraded to a partly-unattributed balance — under-recovering by exactly the unattributed
/// slice is the failure B3 handed to C3, so re-creating it here would be pointless. The only exception is
/// a journal with NO payment records at all (a pre-batch/legacy settlement), which is carried as
/// <see cref="PayrollReceivableStatuses.Unattributed"/> and reported on the ageing view.</para>
/// </summary>
public class PayrollEmployeeReceivable : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    /// <summary>0 for an <see cref="PayrollReceivableStatuses.Unattributed"/> row.</summary>
    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;

    /// <summary>The VOIDED run whose disbursed cash this receivable carries.</summary>
    public Guid SourceRunId { get; set; }
    /// <summary>The GL event that recognised it (NetSettlementReclass / RemitReclass:LOAN …).</summary>
    public string EventType { get; set; } = string.Empty;
    /// <summary>GL period of the recognition, so the ageing view can bucket it.</summary>
    public string Period { get; set; } = string.Empty;

    /// <summary>Amount recognised as recoverable (the employee's slice of the aggregate 1420 debit).</summary>
    public decimal Amount { get; set; }
    /// <summary>How much has since been netted back through a replacement/regular run.</summary>
    public decimal RecoveredAmount { get; set; }
    /// <summary>The run that recovered it (the last one, when recovery spans runs).</summary>
    public Guid? RecoveredByRunId { get; set; }

    public string Status { get; set; } = PayrollReceivableStatuses.Outstanding;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }

    public decimal Outstanding => Math.Round(Amount - RecoveredAmount, 2);
}
