using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Captures an employee request to take leave or to encash it, with the per-day breakdown and the approval decision that turns it into a ledger movement. @tier:T @owner:HR @retention:84m-keep
/// </summary>
public partial class LeaveRequest
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid LeaveTypeId { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public string RequestKind { get; set; } = null!;

    public string Status { get; set; } = null!;

    public DateOnly StartDate { get; set; }

    public DateOnly EndDate { get; set; }

    public DateOnly? ReturnDate { get; set; }

    public decimal Days { get; set; }

    public string? Reason { get; set; }

    public string? ContactDuringLeave { get; set; }

    public string DayBreakdown { get; set; } = null!;

    public DateTime? SubmittedAt { get; set; }

    public DateTime? DecidedAt { get; set; }

    public DateTime? CancelledAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
