using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Holds the computed attendance result for one employee on one local working day in whole minutes, with its exceptions and the payroll run that locked it. @tier:T @owner:HR @retention:24m-purge
/// </summary>
public partial class AttendanceDay
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid? ShiftId { get; set; }

    public Guid? LockedRunId { get; set; }

    public string Status { get; set; } = null!;

    public DateOnly WorkDate { get; set; }

    public DateTime? FirstIn { get; set; }

    public DateTime? LastOut { get; set; }

    public int ScheduledMinutes { get; set; }

    public int WorkedMinutes { get; set; }

    public int BreakMinutes { get; set; }

    public int LateMinutes { get; set; }

    public int EarlyOutMinutes { get; set; }

    public int OvertimeMinutes { get; set; }

    public int AbsentMinutes { get; set; }

    public string Exceptions { get; set; } = null!;

    public DateTime? ComputedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
