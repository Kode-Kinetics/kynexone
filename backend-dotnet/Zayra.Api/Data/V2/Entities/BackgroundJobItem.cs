using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Records the per-row outcome of a background job so a partially failed import names the rows that failed and why, rather than failing as a whole. @tier:T/P @owner:Platform @retention:6m-purge
/// </summary>
public partial class BackgroundJobItem
{
    public Guid Id { get; set; }

    public Guid? TenantId { get; set; }

    public Guid JobId { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public string? RowRef { get; set; }

    public Guid? EntityId { get; set; }

    public Guid? CorrelationId { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime? ProcessedAt { get; set; }
}
