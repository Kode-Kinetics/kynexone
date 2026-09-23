using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Variable pay waiting to be paid — adjustments, arrears, overtime, absence, encashment — each carrying the period it BELONGS to as well as the period it is paid in, so a backdated amount recalculates GOSI as data rather than as arithmetic in a service. @tier:C @owner:Finance @retention:Keep
/// </summary>
public partial class PayrollInput
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid EmployeeId { get; set; }

    public string PayComponentCode { get; set; } = null!;

    public Guid? CostCenterId { get; set; }

    /// <summary>
    /// SET NULL on delete so deleting a draft run releases the claim. A crashed run releases by predicate — status back to Pending where claimed_by_run_id points at a voided run — which is what makes a retry idempotent (§F).
    /// </summary>
    public Guid? ClaimedByRunId { get; set; }

    public Guid? ConsumedRunId { get; set; }

    public string Kind { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string? TargetRunType { get; set; }

    /// <summary>
    /// Polymorphic pointer with source_id (§16): Overtime -&gt; overtime_requests, Leave -&gt; leave_requests, Attendance -&gt; attendance_days, Timesheet -&gt; timesheets, Opening -&gt; background_jobs, Manual/Bonus -&gt; NULL. A nightly sweep reports unresolvable rows.
    /// </summary>
    public string SourceType { get; set; } = null!;

    public Guid? SourceId { get; set; }

    /// <summary>
    /// Cancelling an input BUMPS this and inserts the replacement; it never reuses the key its replacement needs (§2.F, CONVENTIONS.md §11).
    /// </summary>
    public int Revision { get; set; }

    public short RunYear { get; set; }

    public short RunMonth { get; set; }

    public short CoveredYear { get; set; }

    public short CoveredMonth { get; set; }

    public decimal EntitledAmount { get; set; }

    public decimal PreviouslySettledAmount { get; set; }

    public decimal Amount { get; set; }

    /// <summary>
    /// The contributory-wage change this backdated amount causes in the COVERED period. Stored so the GOSI recalculation is data, not arithmetic in a service (§2.F).
    /// </summary>
    public decimal GosiBasisDelta { get; set; }

    public DateTime? ClaimedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual Company Company { get; set; } = null!;

    public virtual CostCenter? CostCenter { get; set; }

    public virtual Employee Employee { get; set; } = null!;

    public virtual PayComponent PayComponent { get; set; } = null!;

    public virtual PayrollRun? PayrollRun { get; set; }

    public virtual PayrollRun? PayrollRunNavigation { get; set; }

    public virtual ICollection<PayrollSlipLine> PayrollSlipLines { get; set; } = new List<PayrollSlipLine>();
}
