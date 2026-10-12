namespace Zayra.Api.Application.AI;

public record PolicyDocumentDto(Guid Id, string OriginalName, string MimeType, long FileSizeBytes, string Status, int ChunkCount, string? ErrorMessage, DateTime CreatedAtUtc)
{
    public Guid? CompanyId { get; init; }
    public string PublicationStatus { get; init; } = "Draft";
    public DateTime? EffectiveFromUtc { get; init; }
    public DateTime? EffectiveToUtc { get; init; }
    public string ContentSha256 { get; init; } = "";
};

public sealed record PolicyCitation(Guid DocumentId, int ChunkIndex, string Source, string Excerpt, string VersionHash);
public sealed record PolicyDocumentText(Guid DocumentId, string Text, string ContentSha256);
public sealed record PublishPolicyRequest(Guid CompanyId, string ContentSha256, DateTime? EffectiveFromUtc = null, DateTime? EffectiveToUtc = null);
/// <summary>Server-constructed authorization scope; never accepted from a client.</summary>
public sealed record PolicyReadScope(bool IncludeDrafts, bool IsGroupLevel, IReadOnlyList<Guid> CompanyIds, Guid? UserId, bool IsTenantAdmin = false);
public record PolicyAskRequest(string Question);
/// <param name="IsGrounded">True when the answer came from the uploaded documents — whether the
/// model summarised them or the deterministic path quoted them verbatim.</param>
public record PolicyAskResponse(string Answer, string[] Sources, bool IsGrounded)
{
    /// <summary>
    /// Who actually produced this answer: the provider that replied, or "fallback" when the
    /// deterministic path did. Never the CONFIGURED provider — reporting "ollama" for a call that
    /// in fact fell back is the same class of lie as a banner claiming a live provider is down.
    /// Mirrors <see cref="AIQueryResponse.Provider"/>.
    /// </summary>
    public IReadOnlyList<PolicyCitation> Citations { get; init; } = Array.Empty<PolicyCitation>();

    public string Provider { get; init; } = "fallback";

    public string? Model { get; init; }

    /// <summary>Plain-language reason no model answer was produced; null when one was.</summary>
    public string? DegradedReason { get; init; }
}
