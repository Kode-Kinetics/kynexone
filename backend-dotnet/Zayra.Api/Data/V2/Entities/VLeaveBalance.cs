using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

public partial class VLeaveBalance
{
    public Guid? TenantId { get; set; }

    public Guid? EmployeeId { get; set; }

    public Guid? LeaveTypeId { get; set; }

    public decimal? BalanceDays { get; set; }

    public decimal? AccruedDays { get; set; }

    public decimal? DebitedDays { get; set; }

    public long? MovementCount { get; set; }

    public DateOnly? LastMovementOn { get; set; }
}
