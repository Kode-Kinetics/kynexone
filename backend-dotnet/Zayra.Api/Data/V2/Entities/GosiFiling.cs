using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Holds the monthly GOSI return per establishment exactly as filed, with its seven branch-by-payer totals, the invoice it is reconciled against and the revision that supersedes a correction. @tier:C @owner:Finance @retention:84m-keep
/// </summary>
public partial class GosiFiling
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public string GosiRegistrationNo { get; set; } = null!;

    public string Status { get; set; } = null!;

    public short Year { get; set; }

    public short Month { get; set; }

    public int Revision { get; set; }

    public int EmployeeCount { get; set; }

    public decimal AnnuitiesEmployee { get; set; }

    public decimal AnnuitiesEmployer { get; set; }

    public decimal SanedEmployee { get; set; }

    public decimal SanedEmployer { get; set; }

    public decimal OccupationalHazardsEmployer { get; set; }

    public decimal TotalContributoryWage { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal? GosiInvoiceAmount { get; set; }

    public decimal? VarianceAmount { get; set; }

    public string? VarianceReason { get; set; }

    public string? RulesVersion { get; set; }

    public Guid? FileId { get; set; }

    public string? FileSha256 { get; set; }

    public DateTime? FiledAt { get; set; }

    public Guid? FiledBy { get; set; }

    public DateTime? ReconciledAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual Company Company { get; set; } = null!;

    public virtual Company CompanyNavigation { get; set; } = null!;

    public virtual File? File { get; set; }

    public virtual User? User { get; set; }
}
