using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Every stored blob with its hash and purge state, so PDPL erasure can delete the object while the referencing row keeps the sha256 as evidence the document existed. @tier:T @owner:HR @retention:84-months-from-Expiry-then-Purge
/// </summary>
public partial class StoredFile
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid? UploadedBy { get; set; }

    public string StorageKey { get; set; } = null!;

    public string Purpose { get; set; } = null!;

    public string PurgeState { get; set; } = null!;

    public string Bucket { get; set; } = null!;

    public string Mime { get; set; } = null!;

    public long SizeBytes { get; set; }

    /// <summary>
    /// Lowercase hex digest. Survives a purge as evidence (§12.3).
    /// </summary>
    public string Sha256 { get; set; } = null!;

    public DateOnly? RetentionUntil { get; set; }

    public DateTime? PurgedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
