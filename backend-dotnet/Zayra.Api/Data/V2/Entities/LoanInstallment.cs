using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Schedules each recovery of a loan or advance by due period and records whether it was recovered, waived or cancelled; the recovering payroll or settlement line points back at this row. @tier:T @owner:Finance @retention:84m-keep
/// </summary>
public partial class LoanInstallment
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid LoanId { get; set; }

    public int InstallmentNumber { get; set; }

    public string Kind { get; set; } = null!;

    public string Status { get; set; } = null!;

    public short DueYear { get; set; }

    public short DueMonth { get; set; }

    public decimal Amount { get; set; }

    public DateTime? RecoveredAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual ICollection<FinalSettlementLine> FinalSettlementLines { get; set; } = new List<FinalSettlementLine>();

    public virtual Loan Loan { get; set; } = null!;

    public virtual ICollection<PayrollSlipLine> PayrollSlipLines { get; set; } = new List<PayrollSlipLine>();
}
