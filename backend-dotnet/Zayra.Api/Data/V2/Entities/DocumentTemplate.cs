using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Versioned letter, payslip and contract bodies in English and Arabic with their merge-field contract, scoped to one company or to the whole tenant. @tier:T @owner:HR
/// </summary>
public partial class DocumentTemplate
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid? CompanyId { get; set; }

    public string Code { get; set; } = null!;

    public string Kind { get; set; } = null!;

    /// <summary>
    /// Immutable once a document or slip references it; a change is a NEW row with the next version (§6).
    /// </summary>
    public int Version { get; set; }

    public string? BodyEn { get; set; }

    public string? BodyAr { get; set; }

    public string? MergeFields { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
