using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

/// <summary>
/// POD-C2 — one employee's end-of-service OBLIGATION as measured at a payroll month end, and the
/// provision MOVEMENT the Lock journal booked for it.
///
/// <para><b>WHY A TABLE.</b> The GL line alone says how much moved; it cannot say what the obligation
/// WAS (the figure an auditor re-performs) or which wage and service it was measured on. The snapshot
/// is that witness, written in the same transaction as the Lock journal and deleted by the run's void,
/// so the panel's per-employee drill-down always agrees with the live 2310 lines behind it. The carried
/// BALANCE is never read from here: it is <see cref="Zayra.Api.Infrastructure.Payroll.EosbProvisionLedger"/>
/// over the ledger, so the snapshot is evidence, not a second source of the balance.</para>
///
/// <para>Unique (tenant, employee, period): one measurement per employee per month, which is also what
/// makes a retried Lock unable to provide twice.</para>
/// </summary>
public class EosbProvisionSnapshot : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public int EmployeeId { get; set; }
    /// <summary>"YYYY-MM" — the pay period the obligation was measured at the END of.</summary>
    public string Period { get; set; } = string.Empty;
    public DateOnly MeasuredAt { get; set; }
    /// <summary>The run whose Lock journal carries the movement (and whose void removes this row).</summary>
    public Guid PayrollRunId { get; set; }
    /// <summary>Statutory obligation at <see cref="MeasuredAt"/> (Art. 84 full award, see the planner).</summary>
    public decimal Obligation { get; set; }
    /// <summary>The provision already carried for this employee before this month's movement.</summary>
    public decimal CarriedBefore { get; set; }
    /// <summary>Obligation − CarriedBefore: positive = DR 5110 / CR 2310, negative = DR 2310 / CR 5110.</summary>
    public decimal Movement { get; set; }
    /// <summary>First measurement for this employee — the movement is the opening catch-up to date.</summary>
    public bool IsCatchUp { get; set; }
    public decimal WageBase { get; set; }
    public decimal ServiceYears { get; set; }
    public string Currency { get; set; } = "SAR";
    /// <summary>The calculator rule the obligation came from (e.g. "KSA-LaborLaw-Art84").</summary>
    public string Rule { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A finance journal that needs a SECOND person to approve it before it posts: the EOSB actuarial
/// true-up and the legacy 2104/2105 absence-deduction reclassification. Both are manual, judgemental
/// postings to a liability, so neither is ever automatic and the requester can never approve their own.
/// One table for both because the lifecycle (Pending → Approved | Rejected, maker ≠ checker, posted
/// lines linked by <c>FinanceGlEntry.SourceEntityId</c>) is identical; <see cref="Kind"/> says which.
/// </summary>
public class GlAdjustmentRequest : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public string Kind { get; set; } = GlAdjustmentKinds.EosbTrueUp;
    public string Status { get; set; } = GlAdjustmentStatuses.Pending;
    /// <summary>"YYYY-MM" the journal posts into.</summary>
    public string Period { get; set; } = string.Empty;
    public DateOnly AccountingDate { get; set; }
    /// <summary>Signed. True-up: + raises the provision (DR expense / CR 2310), − lowers it.
    /// Reclass: the CREDIT balance being moved (+ = the liability carries a credit).</summary>
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "SAR";
    /// <summary>Reclass only: the legacy account label being emptied ("2104 - …").</summary>
    public string? SourceAccount { get; set; }
    /// <summary>The account the other side lands on (true-up: the provision; reclass: the target).</summary>
    public string? TargetAccount { get; set; }
    public string Reason { get; set; } = string.Empty;
    /// <summary>True-up: the actuarial report the figure comes from.</summary>
    public string? ReportReference { get; set; }
    public Guid? RequestedByUserId { get; set; }
    public string? RequestedByName { get; set; }
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? DecidedByUserId { get; set; }
    public string? DecidedByName { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionNote { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public static class GlAdjustmentKinds
{
    public const string EosbTrueUp = "EosbTrueUp";
    public const string LegacyAbsenceReclass = "LegacyAbsenceReclass";
    public static readonly string[] All = { EosbTrueUp, LegacyAbsenceReclass };
}

public static class GlAdjustmentStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public static readonly string[] All = { Pending, Approved, Rejected };
}
