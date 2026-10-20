using Zayra.Api.Domain.Entities;

namespace Zayra.Api.Models;

public sealed class OfferCompanyPolicy : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public int Precision { get; set; } = 2;
    public string RequiredComponentCodesJson { get; set; } = "[\"BASIC\"]";
    public long Version { get; set; } = 1;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}

public sealed class OfferSeries : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid? LegacyRootOfferId { get; set; }
    public int NextRevisionNumber { get; set; } = 2;
    public long Version { get; set; } = 1;
}

public sealed class OfferLifecycleEvent : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid OfferId { get; set; }
    public Guid ApplicationId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public Guid ActorUserId { get; set; }
    public string ActorName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string DataJson { get; set; } = "{}";
    public string? IdempotencyKey { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class OfferArtifact : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid OfferId { get; set; }
    public Guid ApplicationId { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string State { get; set; } = "Pending";
    public string Provenance { get; set; } = "FrozenOriginal";
    public string RendererVersion { get; set; } = "OfferLetterDesign/v1";
    public long SourceOfferVersion { get; set; }
    public long PendingOfferVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinalizedAtUtc { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class OfferDispatch : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid OfferId { get; set; }
    public Guid ArtifactId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string RequestFingerprint { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Message { get; set; } = "";
    public string RecipientEmail { get; set; } = "";
    public string RecipientName { get; set; } = "";
    public Guid InitiatorUserId { get; set; }
    public string InitiatorName { get; set; } = "";
    public string? ResendReason { get; set; }
    public string? FromAddress { get; set; }
    public string? FromName { get; set; }
    public string SenderSource { get; set; } = "";
    public string SenderFingerprint { get; set; } = "";
    public string Outcome { get; set; } = "Queued";
    public long Version { get; set; } = 1;
    public long ReservationVersion { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public DateTime? TransportStartedAtUtc { get; set; }
    public DateTime? TransportCompletedAtUtc { get; set; }
    public DateTime? FinalizedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class OfferDispatchAttempt : ITenantOwned, ICompanyScopedOperational
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid OfferId { get; set; }
    public Guid DispatchId { get; set; }
    public Guid LeaseToken { get; set; }
    public string Stage { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Code { get; set; } = "";
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}
