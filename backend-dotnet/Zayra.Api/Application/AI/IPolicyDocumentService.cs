namespace Zayra.Api.Application.AI;
public interface IPolicyDocumentService
{
    Task<PolicyDocumentDto> UploadAsync(Guid tenantId, Guid? userId, Stream content, string fileName, string mimeType, CancellationToken ct);
    Task<IReadOnlyList<PolicyDocumentDto>> ListAsync(Guid tenantId, CancellationToken ct);
    Task<bool> DeleteAsync(Guid tenantId, Guid documentId, CancellationToken ct);
    Task<PolicyAskResponse> AskAsync(Guid tenantId, string question, CancellationToken ct);

    /// <summary>
    /// Preferred entry point: carries the caller identity that every AI usage record needs
    /// (AGENTS.md — all model calls produce usage/cost records, and an audit row with no user on
    /// it is not an audit row). Defaults to the unattributed overload so existing implementations
    /// keep compiling; PolicyDocumentService overrides it.
    /// </summary>
    Task<PolicyAskResponse> AskAsync(Guid tenantId, Guid? userId, string userRole, string question, CancellationToken ct)
        => AskAsync(tenantId, question, ct);
}
