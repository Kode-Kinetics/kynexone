using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Defines each leave type a tenant offers together with its contractual entitlement, accrual, carry-forward and eligibility policy above the statutory floor. @tier:T @owner:HR @retention:tenant-lifecycle
/// </summary>
public partial class LeaveType
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Code { get; set; } = null!;

    public string NameEn { get; set; } = null!;

    public string? NameAr { get; set; }

    public bool IsStatutory { get; set; }

    public bool IsPaid { get; set; }

    public string? PayRuleKey { get; set; }

    public string Policy { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual ICollection<LeaveLedger> LeaveLedgers { get; set; } = new List<LeaveLedger>();

    public virtual ICollection<LeaveRequest> LeaveRequests { get; set; } = new List<LeaveRequest>();

    public virtual Tenant Tenant { get; set; } = null!;
}
