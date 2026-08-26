using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// POD-C1 — THE TERMINATION SETTLEMENT, as a first-class auditable payable.
///
/// <para><b>WHY IT EXISTS.</b> POD-A2 unified the end-of-service NUMBER (both <c>/eosb/calculate</c> and
/// <c>/final-settlement</c> route through the country pack's <c>IEndOfServiceCalculator</c>) but
/// deliberately left the other half open: <c>/final-settlement</c> persisted NO payable, posted NO GL and
/// generated NO payment — it returned JSON plus one audit line, and <c>/eosb/calculate</c> only upserted a
/// Draft <see cref="EOSBCalculation"/> whose <c>Status</c> nothing ever advanced. The accrued gratuity
/// liability therefore never appeared on the books and every leaver was settled by spreadsheet plus a
/// manual bank transfer. This row is the missing object: computed ONCE by A2's engine, persisted with the
/// whole <c>EndOfServiceResult.Breakdown</c> (which <c>/eosb/calculate</c> used to discard), posted as a
/// balanced journal on approval, and disbursed through the EXISTING payroll rails.</para>
///
/// <para><b>THE WAGE SEAM WITH POD-C3 IS EXPLICIT AND ENFORCED.</b> C3 already pays a leaver through their
/// last working day inside the ordinary run and stamps <c>PayrollSlip.IsFinalWageMonth</c>. This record
/// therefore carries <see cref="UnpaidWagesAmount"/> as a REPORTED figure sourced from that slip and
/// prices it at ZERO in <see cref="GrossPayable"/>. Approve refuses unless the slip exists, or the
/// operator explicitly acknowledges that the wage side is NOT done — in which case the wage is added as a
/// real settlement line and the acknowledging actor is recorded. That turns C3's handoff comment into an
/// assertion, and it kills the naive day-of-month pro rata the old endpoint computed (which ignored
/// <c>ProrationCalculator</c>, the proration policy and the joining date entirely).</para>
///
/// <para><b>SCOPE.</b> Company-scoped operational (<see cref="ICompanyScopedOperational"/>): the legal
/// entity decides which <c>GlAccountMapping</c> overrides apply and which period-close row binds, so it is
/// resolved server-side exactly the way a payroll run resolves it (the employee's company; else the
/// tenant's single active company; 2+ active companies with an unscoped employee is refused).</para>
/// </summary>
public class EmployeeFinalSettlement : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>Legal entity that owes this settlement. Required for new writes — see the class remarks.</summary>
    public Guid? CompanyId { get; set; }

    public int EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;

    /// <summary>
    /// The <see cref="EmployeeOffboarding"/> this settlement settles. NOT nullable and NOT the employee:
    /// uniqueness is keyed on it precisely so a RE-HIRE who leaves a second time is settleable (the
    /// leaver union at PayrollController.LoadEligibleWithLeaversAsync already contemplates re-hire, and
    /// <c>EmployeeOffboarding.RehireEligible</c> exists). Approve additionally refuses an overlapping
    /// service window, so the same service can never be paid twice.
    /// </summary>
    public Guid OffboardingId { get; set; }

    /// <summary>Taken from the OFFBOARDING record, never from the request body — a caller cannot invent a
    /// last working day for someone who is not actually leaving.</summary>
    public DateOnly LastWorkingDay { get; set; }
    /// <summary>Service start used by the pack (the employee's joining date at the time of computation).</summary>
    public DateOnly ServiceStartDate { get; set; }

    /// <summary>
    /// [FLAG-COMPLIANCE-KSA] KSA Labour Law Art. 88 — the employer must settle within ONE WEEK of the
    /// contract ending, or within TWO WEEKS where the WORKER terminated the contract. Derived from
    /// <see cref="LastWorkingDay"/> and <see cref="TerminationReason"/> so an overdue settlement is
    /// visible rather than discovered at a labour-court hearing.
    /// </summary>
    public DateOnly SettlementDueDate { get; set; }

    /// <summary>Normalised through <c>PayrollController.NormalizeTerminationReason</c>: "Resignation"
    /// (Art.85 reduction), "Article80" (forfeiture), or an employer-side reason (full award).</summary>
    public string TerminationReason { get; set; } = string.Empty;
    /// <summary>
    /// The reason an APPROVER explicitly confirmed. <c>EmployeeOffboarding.SeparationType</c> DEFAULTS to
    /// "Resignation", and a record saved without changing that default silently applies the Art.85 haircut
    /// (nil / ⅓ / ⅔). Paying a genuinely-terminated employee ⅓ of their statutory award is a legal
    /// determination, not a default, so approve refuses until this matches the resolved reason.
    /// </summary>
    public string? ConfirmedTerminationReason { get; set; }

    public decimal ServiceYears { get; set; }
    public string Currency { get; set; } = "SAR";

    /// <summary>The WHOLE <c>EndOfServiceResult</c> from POD-A2's engine — total, applicable rule, AND the
    /// per-tier breakdown (Art.84 tier-1/tier-2 plus any Art.85/Art.80 adjustment line). The pre-C1
    /// <c>/eosb/calculate</c> kept only the rule string.</summary>
    public string EosbResultJson { get; set; } = "{}";
    /// <summary>Every input the number was computed from (wage base, dates, reason, pack country).</summary>
    public string InputsSnapshotJson { get; set; } = "{}";
    /// <summary>The Draft <see cref="EOSBCalculation"/> this settlement promoted, when one exists.</summary>
    public Guid? EosbCalculationId { get; set; }

    // ── Money: earnings ───────────────────────────────────────────────────────────────────────────
    public decimal GratuityAmount { get; set; }
    public decimal LeaveEncashmentAmount { get; set; }
    public decimal LeaveEncashmentDays { get; set; }
    /// <summary>Payment in LIEU of notice — owed by the EMPLOYER who terminated without serving it.</summary>
    public decimal NoticePayAmount { get; set; }
    public decimal OtherDuesAmount { get; set; }

    // ── Money: the settlement's OWN deductions ────────────────────────────────────────────────────
    /// <summary>Compensation owed by a WORKER who resigned without serving notice (Art. 75/76). Refused
    /// outright for any employer-side reason — the notice compensation flows to whichever party did NOT
    /// give notice, and deducting it from an employer-terminated settlement is an unlawful deduction.</summary>
    public decimal NoticeShortfallDeduction { get; set; }
    public decimal OtherDeductionsAmount { get; set; }

    // ── Money: PLANNED recovery of the leaver's debts (executed by the disbursing run) ────────────
    // Planned at approve, RE-CAPPED against the live balance inside the run transaction, and taken
    // through the run's own deduction lines — so there is exactly ONE decrement path and no
    // double-recovery with POD-C3's own receivable netting.
    public decimal PlannedLoanRecovery { get; set; }
    public decimal PlannedAdvanceRecovery { get; set; }
    public decimal PlannedReceivableRecovery { get; set; }

    /// <summary>Σ of the settlement's own EARNING lines. This is what the GL accrues to Final Settlement
    /// Payable, and what the disbursing run's clearing debits.</summary>
    public decimal GrossPayable { get; set; }
    /// <summary>Σ of the settlement's OWN deduction lines only (never the loan/receivable recoveries,
    /// which ride the run and clear against their own control accounts). Capped at
    /// <see cref="GrossPayable"/> at plan time so one leaver can never 422 a whole batch.</summary>
    public decimal TotalDeductions { get; set; }
    public decimal NetPayable { get; set; }

    // ── The POD-C3 wage seam ──────────────────────────────────────────────────────────────────────
    /// <summary>REPORTED ONLY, priced at ZERO in <see cref="GrossPayable"/> unless
    /// <see cref="WagesAcknowledgedUnpaid"/>. See the class remarks.</summary>
    public decimal UnpaidWagesAmount { get; set; }
    public Guid? WagesPaidByRunId { get; set; }
    public DateOnly? WagesPaidThroughDate { get; set; }
    public bool WagesAcknowledgedUnpaid { get; set; }
    public string? WagesAcknowledgementReason { get; set; }

    // ── [FLAG-COMPLIANCE-KSA] the Art. 84 wage base ───────────────────────────────────────────────
    /// <summary>
    /// The pack computes gratuity on the LAST BASIC wage. POD-A2 documented that as a per-company FLOOR,
    /// not the statutory Art. 84 "last wage" (basic + regular allowances). A DISPLAYED shortfall was an
    /// advisory error; a DISBURSED one is underpayment of a statutory entitlement, evidenced by the
    /// employer's own signed settlement. This is the indicative delta between the floor and the full
    /// package, computed by re-running the SAME pack with the allowances populated. Non-zero requires an
    /// explicit, recorded acknowledgement at approve.
    /// </summary>
    public decimal WageBaseDeltaAmount { get; set; }
    public Guid? WageBaseAcknowledgedByUserId { get; set; }
    public string? WageBaseAcknowledgedByName { get; set; }
    public DateTime? WageBaseAcknowledgedAtUtc { get; set; }

    // ── Lifecycle ─────────────────────────────────────────────────────────────────────────────────
    public string Status { get; set; } = FinalSettlementStatuses.Draft;

    public Guid? CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? SubmittedByUserId { get; set; }
    public string? SubmittedByName { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public string? ApprovedByName { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public Guid? CancelledByUserId { get; set; }
    public string? CancelledByName { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? CancelReason { get; set; }

    /// <summary>When the accrual journal posted, and into which GL period.</summary>
    public DateTime? GlPostedAtUtc { get; set; }
    public string? GlPeriod { get; set; }

    // ── Disbursement (the EXISTING rails — never a second payment mechanism) ──────────────────────
    public Guid? PayrollRunId { get; set; }
    public Guid? PaymentBatchId { get; set; }
    public DateTime? PaidAtUtc { get; set; }

    /// <summary>POD-C1 (F2) — residual employee debt RECLASSIFIED to the 1420 Employee Overpayment
    /// Receivable when the settlement was paid, so an ex-employee's loan does not sit Active on 1400
    /// forever with nobody able to collect it.</summary>
    public decimal ResidualDebtReclassed { get; set; }
    /// <summary>The part of the residual that could NOT be reclassified because 1400/1410 never carried a
    /// debit for it (a loan disbursed before the disbursement GL existed, or a seeded one). Recognising
    /// 1420 for it would create an asset out of nothing, so the loan stays Active and this figure is
    /// reported instead of being silently forgiven.</summary>
    public decimal ResidualDebtUnbooked { get; set; }

    /// <summary>Advisory findings raised while computing (compliance flags, missing encashment policy,
    /// wage-base floor). Surfaced on the detail endpoint; never silently dropped.</summary>
    public string WarningsJson { get; set; } = "[]";

    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// POD-C1 — one component of a settlement. <b>The disbursing run emits its payroll lines VERBATIM from
/// these rows and never recomputes anything</b> — that is POD-A2's lesson ("compute once") applied one
/// layer down, and it is what makes the GL accrual and the payslip provably the same numbers.
/// </summary>
public class FinalSettlementLine : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid SettlementId { get; set; }

    public string ComponentCode { get; set; } = string.Empty;
    public string ComponentName { get; set; } = string.Empty;
    /// <summary><see cref="FinalSettlementLineTypes"/>.</summary>
    public string LineType { get; set; } = FinalSettlementLineTypes.Earning;
    /// <summary>The payroll <c>Source</c> the emitted line carries — this is what drives GL routing.</summary>
    public string Source { get; set; } = FinalSettlementComponents.SettlementSource;
    public decimal Amount { get; set; }
    /// <summary>Days (leave encashment) or notice days. Zero when the line is a pure money amount.</summary>
    public decimal Quantity { get; set; }
    /// <summary>The loan / advance / receivable / leave-balance row this line is derived from, so the
    /// disbursing run can decrement exactly what was planned and the void can restore exactly that.</summary>
    public Guid? SourceEntityId { get; set; }
    public string? Narrative { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class FinalSettlementLineTypes
{
    public const string Earning   = "Earning";
    public const string Deduction = "Deduction";
}

/// <summary>
/// POD-C1 — settlement lifecycle.
///
/// <code>
/// Draft ──submit──▶ PendingApproval ──approve──▶ Approved ──Process──▶ Disbursing ──batch settle──▶ Paid
///   │                     │                        │                       │
///   └─────────────────────┴────────cancel──────────┘                       │
///                                                     ◀── run void (witness replay restores Approved)
/// </code>
///
/// The GL accrual posts at <see cref="Approved"/> — post-once, mirroring Lock/settle — and is contra'd on
/// <see cref="Cancelled"/>. A run VOID restores <see cref="Paid"/>/<see cref="Disbursing"/> back to
/// <see cref="Approved"/> through POD-B3's consumption-witness replay, which also re-opens 2320 because
/// the void contras the originating clearing line.
/// </summary>
public static class FinalSettlementStatuses
{
    public const string Draft           = "Draft";
    public const string PendingApproval = "PendingApproval";
    public const string Approved        = "Approved";
    public const string Disbursing      = "Disbursing";
    public const string Paid            = "Paid";
    public const string Cancelled       = "Cancelled";

    public static readonly string[] All =
        { Draft, PendingApproval, Approved, Disbursing, Paid, Cancelled };

    /// <summary>Statuses that still occupy the (tenant, offboarding) uniqueness slot.</summary>
    public static bool IsLive(string? status) =>
        !string.Equals(status, Cancelled, StringComparison.Ordinal);

    /// <summary>Has the accrual journal posted (and therefore needs a contra to unwind)?</summary>
    public static bool HasAccrued(string? status) =>
        status is Approved or Disbursing or Paid;
}

/// <summary>
/// POD-C1 — the component vocabulary the settlement emits into payroll.
///
/// <para><b>LOAN / ADVANCE / RECEIVABLE RECOVERY DELIBERATELY REUSE THE EXISTING CODES</b>
/// (<c>LOAN_EMI</c> / <c>ADVANCE_EMI</c> with <c>Source = "Loan"</c>, and
/// <see cref="PayrollRecoveryComponents.ReceivableRecovery"/> with <c>Source = "Recovery"</c>) so
/// <c>RemitGroups.ForSource</c>, the POD-B1b loan-remittance receivable split and POD-C3's own recovery
/// metering pick them up with ZERO new machinery — and so there is exactly one recovery mechanism, which
/// is what makes "no double recovery" true by construction rather than by convention.</para>
/// </summary>
public static class FinalSettlementComponents
{
    /// <summary>Payroll <c>Source</c> tag on every settlement-owned line. Drives GL routing and is what
    /// tells the statutory path these are not monthly wage (see the GOSI decision on the controller).</summary>
    public const string SettlementSource = "Settlement";

    // Earnings
    public const string Gratuity        = "EOSB_GRATUITY";
    public const string LeaveEncashment = "LEAVE_ENCASHMENT";
    public const string NoticePay       = "NOTICE_PAY";
    public const string OtherDues       = "SETTLEMENT_OTHER";
    // The settlement's OWN deductions (never third-party payables — see DED:SETTLEMENT_RECOVERY).
    public const string NoticeShortfall = "NOTICE_SHORTFALL";
    public const string OtherDeduction  = "SETTLEMENT_DED_OTHER";

    public static readonly string[] Earnings   = { Gratuity, LeaveEncashment, NoticePay, OtherDues };
    public static readonly string[] Deductions = { NoticeShortfall, OtherDeduction };

    public static bool IsSettlementEarning(string componentCode) =>
        Array.IndexOf(Earnings, componentCode) >= 0;

    public static bool IsSettlementDeduction(string componentCode) =>
        Array.IndexOf(Deductions, componentCode) >= 0;

    /// <summary>Display label for a settlement component code.</summary>
    public static string Label(string componentCode) => componentCode switch
    {
        Gratuity        => "End of service gratuity",
        LeaveEncashment => "Leave encashment",
        NoticePay       => "Payment in lieu of notice",
        OtherDues       => "Other final dues",
        NoticeShortfall => "Notice shortfall (Art. 75/76)",
        OtherDeduction  => "Other final deductions",
        _               => componentCode,
    };
}

/// <summary>POD-C1 — stable, remap-immune identifiers embedded in settlement journal descriptions. The
/// clearing path locates a settlement's accrual by (SourceModule, SourceEntityId, EventType) and then
/// DEBITS its STORED CreditAccount, so these strings are for humans and reporting, never arithmetic.</summary>
public static class FinalSettlementGlDescriptions
{
    public const string SourceModule            = "Payroll";
    public const string AccrualPrefix           = "Final settlement accrual: ";
    public const string ReversalPrefix          = "Final settlement accrual reversal: ";
    public const string PayrollClearingPrefix   = "Final settlement payable cleared via payroll: ";
    public const string ProvisionReliefPrefix   = "EOSB provision consumed on settlement: ";
    public const string ResidualReclassPrefix   = "Residual employee debt reclassified on settlement: ";

    /// <summary>Machine-precise link from a clearing line back to its settlement. Stored in
    /// <c>FinanceGlEntry.SourceEntityRef</c> because the clearing line lives INSIDE the payroll journal,
    /// whose SourceEntityId is the run — the exact pattern <c>BonusGlLedger.BatchRef</c> established.</summary>
    public static string SettlementRef(Guid settlementId) => settlementId.ToString();
}
