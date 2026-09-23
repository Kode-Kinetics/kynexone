using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Represents one general-ledger journal per source event, carrying the exported file, its hash, the ERP acknowledgement and the reversal chain. @tier:C @owner:Finance @retention:84m-keep
/// </summary>
public partial class GlJournal
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public string SourceType { get; set; } = null!;

    public Guid? SourceId { get; set; }

    public string Status { get; set; } = null!;

    public short Year { get; set; }

    public short Month { get; set; }

    public Guid? ReversalOfId { get; set; }

    public string? ErpReference { get; set; }

    public Guid? FileId { get; set; }

    public string? ExportFileSha256 { get; set; }

    public string? IdempotencyKey { get; set; }

    public DateTime? PostedAt { get; set; }

    public DateTime? ExportedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual Company Company { get; set; } = null!;

    public virtual File? File { get; set; }

    public virtual ICollection<GlJournalLine> GlJournalLines { get; set; } = new List<GlJournalLine>();

    public virtual GlJournal? GlJournalNavigation { get; set; }

    public virtual ICollection<GlJournal> InverseGlJournalNavigation { get; set; } = new List<GlJournal>();
}
