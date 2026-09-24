namespace Zayra.Api.Application.AI;

public record PolicyDocumentDto(Guid Id, string OriginalName, string MimeType, long FileSizeBytes, string Status, int ChunkCount, string? ErrorMessage, DateTime CreatedAtUtc);
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
    public string Provider { get; init; } = "fallback";

    public string? Model { get; init; }

    /// <summary>Plain-language reason no model answer was produced; null when one was.</summary>
    public string? DegradedReason { get; init; }
}
