using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Holds an employee loan or salary advance with its principal, opening balance carried in at go-live, approval and the outstanding amount reconciled against its recoveries. @tier:T @owner:Finance @retention:84m-keep
/// </summary>
public partial class Loan
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid? ApprovalRequestId { get; set; }

    public string Kind { get; set; } = null!;

    public string? TypeCode { get; set; }

    public string Status { get; set; } = null!;

    public short StartYear { get; set; }

    public short StartMonth { get; set; }

    public int InstallmentCount { get; set; }

    public string? Reason { get; set; }

    public decimal Principal { get; set; }

    public decimal OpeningOutstanding { get; set; }

    public decimal Outstanding { get; set; }

    public DateTime? DisbursedAt { get; set; }

    public DateTime? SettledAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual ApprovalRequest? ApprovalRequest { get; set; }

    public virtual Employee Employee { get; set; } = null!;

    public virtual ICollection<LoanInstallment> LoanInstallments { get; set; } = new List<LoanInstallment>();
}
