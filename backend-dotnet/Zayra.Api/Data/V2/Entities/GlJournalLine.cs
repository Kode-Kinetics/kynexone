using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Carries the balanced debit and credit lines of a GL journal together with their cost-centre and project segments. @tier:C @owner:Finance @retention:84m-keep
/// </summary>
public partial class GlJournalLine
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid JournalId { get; set; }

    public Guid? CostCenterId { get; set; }

    public int LineOrder { get; set; }

    public string Account { get; set; } = null!;

    public string? ProjectCode { get; set; }

    public string? Description { get; set; }

    public decimal Debit { get; set; }

    public decimal Credit { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public virtual CostCenter? CostCenter { get; set; }

    public virtual GlJournal GlJournal { get; set; } = null!;
}
