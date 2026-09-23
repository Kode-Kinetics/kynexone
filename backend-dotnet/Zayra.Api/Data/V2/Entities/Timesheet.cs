using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Groups one employee logged working minutes for a period into a single submittable, approvable and lockable record. @tier:C @owner:HR @retention:24m-purge
/// </summary>
public partial class Timesheet
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public Guid? LockedRunId { get; set; }

    public string? TimesheetNumber { get; set; }

    public string Status { get; set; } = null!;

    public DateOnly PeriodStart { get; set; }

    public DateOnly PeriodEnd { get; set; }

    public int TotalMinutes { get; set; }

    public DateTime? SubmittedAt { get; set; }

    public DateTime? DecidedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual ApprovalRequest? ApprovalRequest { get; set; }

    public virtual Company Company { get; set; } = null!;

    public virtual Employee Employee { get; set; } = null!;

    public virtual PayrollRun? PayrollRun { get; set; }

    public virtual ICollection<TimesheetDayReconciliation> TimesheetDayReconciliations { get; set; } = new List<TimesheetDayReconciliation>();
}
