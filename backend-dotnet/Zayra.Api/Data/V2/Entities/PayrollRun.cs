using System;
using System.Collections.Generic;
using NpgsqlTypes;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// One payroll execution for a company and period — regular, off-cycle, correction, final settlement or the mid-year Opening import — carrying its selection, its cached totals, the rules version it applied and the attendance range it locked. @tier:C @owner:Finance @retention:Keep
/// </summary>
public partial class PayrollRun
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid? ParentRunId { get; set; }

    /// <summary>
    /// Opening-run provenance. FK deferred to the cross-domain constraints pass: background_jobs is domain R.
    /// </summary>
    public Guid? SourceImportJobId { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public string RunType { get; set; } = null!;

    public string Status { get; set; } = null!;

    public short Year { get; set; }

    public short Month { get; set; }

    /// <summary>
    /// Authoritative for the attendance lock; attendance_days.locked_run_id is the per-row projection written in the same transaction (§11.6). CHECK-constrained to lie inside the run period. Set on Approved, cleared on Void.
    /// </summary>
    public NpgsqlRange<DateOnly>? AttendanceLockedRange { get; set; }

    public int EmployeeCount { get; set; }

    /// <summary>
    /// The run&apos;s exit condition: it leaves Processing only when slips + explicitly excluded = this count (§10.1, §19.5).
    /// </summary>
    public int SelectedEmployeeCount { get; set; }

    /// <summary>
    /// CACHE of SUM over included slips, reconciled by trg_run_totals only in the transaction that moves Processing -&gt; Processed. While Processing, every total_* column is UNDEFINED and must not be displayed as authoritative (§11.2).
    /// </summary>
    public decimal TotalGross { get; set; }

    public decimal TotalDeductions { get; set; }

    public decimal TotalNet { get; set; }

    public decimal TotalEmployerStatutory { get; set; }

    public string? RulesVersion { get; set; }

    public string? SourceSystem { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? Selection { get; set; }

    public DateTime? CalculatedAt { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public DateTime? LockedAt { get; set; }

    public string? VoidReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual ApprovalRequest? ApprovalRequest { get; set; }

    public virtual BackgroundJob? BackgroundJob { get; set; }

    public virtual Company Company { get; set; } = null!;

    public virtual ICollection<FinalSettlement> FinalSettlements { get; set; } = new List<FinalSettlement>();

    public virtual ICollection<PayrollRun> InversePayrollRunNavigation { get; set; } = new List<PayrollRun>();

    public virtual ICollection<PayrollInput> PayrollInputPayrollRunNavigations { get; set; } = new List<PayrollInput>();

    public virtual ICollection<PayrollInput> PayrollInputPayrollRuns { get; set; } = new List<PayrollInput>();

    public virtual ICollection<PayrollIssue> PayrollIssues { get; set; } = new List<PayrollIssue>();

    public virtual PayrollRun? PayrollRunNavigation { get; set; }

    public virtual ICollection<PayrollSlip> PayrollSlips { get; set; } = new List<PayrollSlip>();

    public virtual ICollection<Timesheet> Timesheets { get; set; } = new List<Timesheet>();

    public virtual ICollection<WpsBatch> WpsBatches { get; set; } = new List<WpsBatch>();
}
