using System;
using System.Collections.Generic;

namespace Zayra.Api.Data.V2.Entities;

/// <summary>
/// Is the single append-only log of every audited event outside payroll money movement, made tamper-evident by periodic Merkle checkpoint rows and verifiable after a PDPL erasure because each row keeps the digests of the payload it no longer holds. @tier:T/P @owner:Compliance @retention:indefinite-keep
/// </summary>
public partial class AuditLog
{
    public Guid Id { get; set; }

    public Guid? TenantId { get; set; }

    public Guid? CompanyId { get; set; }

    public string RecordKind { get; set; } = null!;

    public string? Category { get; set; }

    public string ChainKey { get; set; } = null!;

    public long Seq { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? Action { get; set; }

    public string? Entity { get; set; }

    public Guid? EntityId { get; set; }

    public Guid? ActorUserId { get; set; }

    public Guid? OnBehalfOfUserId { get; set; }

    public Guid? CorrelationId { get; set; }

    public string HashAlgorithm { get; set; } = null!;

    public string? Before { get; set; }

    public string? After { get; set; }

    public string? PersonalData { get; set; }

    public string? PersonalDataHash { get; set; }

    public string? BeforeHash { get; set; }

    public string? AfterHash { get; set; }

    public string? EnvelopeHash { get; set; }

    public DateTime? PersonalDataErasedAt { get; set; }

    public long? CoversSeqFrom { get; set; }

    public long? CoversSeqTo { get; set; }

    public DateTime? CoversCreatedFrom { get; set; }

    public DateTime? CoversCreatedTo { get; set; }

    public string? RootHash { get; set; }

    public string? PrevCheckpointHash { get; set; }

    public List<long>? ObservedGaps { get; set; }
}
