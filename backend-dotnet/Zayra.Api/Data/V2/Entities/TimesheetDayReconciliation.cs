using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Compares the minutes an employee booked on a timesheet day against the minutes attendance computed for the same day and holds the variance until it is explained or accepted. @tier:C @owner:HR @retention:24m-purge
/// </summary>
public partial class TimesheetDayReconciliation
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid TimesheetId { get; set; }

    public Guid? AttendanceDayId { get; set; }

    public string Status { get; set; } = null!;

    public DateOnly WorkDate { get; set; }

    public int TimesheetMinutes { get; set; }

    public int AttendanceMinutes { get; set; }

    public int VarianceMinutes { get; set; }

    public string? Explanation { get; set; }

    public Guid? ResolvedBy { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual Timesheet Timesheet { get; set; } = null!;
}
