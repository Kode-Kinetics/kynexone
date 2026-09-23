using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Holds one Wage Protection System SIF file per payroll run as generated, submitted and acknowledged by the bank, including its resubmission chain. @tier:C @owner:Finance @retention:84m-keep
/// </summary>
public partial class WpsBatch
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid RunId { get; set; }

    public string BatchNumber { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string FormatVersion { get; set; } = null!;

    public int EmployeeCount { get; set; }

    public decimal TotalAmount { get; set; }

    public string? SubmissionReference { get; set; }

    public Guid? FileId { get; set; }

    public string? FileSha256 { get; set; }

    public Guid? ResubmissionOfId { get; set; }

    public Guid? GeneratedBy { get; set; }

    public DateTime? SubmittedAt { get; set; }

    public DateTime? AcknowledgedAt { get; set; }

    public DateTime? RejectedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual Company Company { get; set; } = null!;

    public virtual File? File { get; set; }

    public virtual ICollection<WpsBatch> InverseWpsBatchNavigation { get; set; } = new List<WpsBatch>();

    public virtual PayrollRun PayrollRun { get; set; } = null!;

    public virtual User? User { get; set; }

    public virtual WpsBatch? WpsBatchNavigation { get; set; }

    public virtual ICollection<WpsLine> WpsLines { get; set; } = new List<WpsLine>();
}
