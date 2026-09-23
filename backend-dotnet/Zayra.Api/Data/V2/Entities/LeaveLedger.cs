using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Is the append-only record of every movement in an employee leave balance, where a correction is a reversing row and the balance itself is only ever read through v_leave_balances. @tier:T @owner:HR @retention:84m-keep
/// </summary>
public partial class LeaveLedger
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid LeaveTypeId { get; set; }

    public string EntryType { get; set; } = null!;

    public DateOnly EntryDate { get; set; }

    public decimal Days { get; set; }

    public string? Reason { get; set; }

    public string? SourceType { get; set; }

    public Guid? SourceId { get; set; }

    public string IdempotencyKey { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public virtual Employee Employee { get; set; } = null!;

    public virtual LeaveType LeaveType { get; set; } = null!;
}
