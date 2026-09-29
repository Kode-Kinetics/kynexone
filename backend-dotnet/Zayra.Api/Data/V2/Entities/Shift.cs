using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Defines a working shift with its start and end times, unpaid break, weekly off days and the grace, lateness and Ramadan rules the attendance engine applies to it. @tier:T @owner:HR @retention:tenant-lifecycle
/// </summary>
public partial class Shift
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public TimeOnly StartTime { get; set; }

    public TimeOnly EndTime { get; set; }

    public int BreakMinutes { get; set; }

    public bool CrossesMidnight { get; set; }

    public List<string> WeeklyOffDays { get; set; } = null!;

    public string Rules { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
