using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Records each employee payment line exactly as filed in a WPS SIF batch, frozen at submission, alongside the bank confirmation result returned for it. @tier:C @owner:Finance @retention:84m-keep
/// </summary>
public partial class WpsLine
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid BatchId { get; set; }

    public Guid SlipId { get; set; }

    public Guid EmployeeId { get; set; }

    public string? IdNumber { get; set; }

    public string EmployeeNumber { get; set; } = null!;

    public string Iban { get; set; } = null!;

    public string? BankCode { get; set; }

    public string? MolId { get; set; }

    public decimal Basic { get; set; }

    public decimal Housing { get; set; }

    public decimal OtherEarnings { get; set; }

    public decimal Deductions { get; set; }

    public decimal Net { get; set; }

    public string BankStatus { get; set; } = null!;

    public string? BankReference { get; set; }

    public decimal? ConfirmedAmount { get; set; }

    public string? ReasonCode { get; set; }

    public DateOnly? ValueDate { get; set; }

    public Guid? ConfirmationJobId { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public virtual BackgroundJob? ConfirmationJob { get; set; }

    public virtual Employee Employee { get; set; } = null!;

    public virtual PayrollSlip PayrollSlip { get; set; } = null!;

    public virtual WpsBatch WpsBatch { get; set; } = null!;
}
