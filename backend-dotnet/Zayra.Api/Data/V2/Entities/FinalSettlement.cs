using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Is the separation case and settlement header for one employee, carrying the clearance checklist, the approved totals and the payroll run the settlement was paid through. @tier:C @owner:Finance @retention:84m-keep
/// </summary>
public partial class FinalSettlement
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid? PaidViaRunId { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public string? SettlementNumber { get; set; }

    public string SeparationType { get; set; } = null!;

    public string Status { get; set; } = null!;

    public DateOnly LastWorkingDay { get; set; }

    public DateOnly? NoticeGivenOn { get; set; }

    public int? NoticeServedDays { get; set; }

    public decimal Gross { get; set; }

    public decimal Deductions { get; set; }

    public decimal Net { get; set; }

    public string Clearance { get; set; } = null!;

    public DateTime? ApprovedAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime? CancelledAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual ICollection<FinalSettlementLine> FinalSettlementLines { get; set; } = new List<FinalSettlementLine>();
}
