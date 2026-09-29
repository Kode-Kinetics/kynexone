namespace Zayra.Api.Application.AI;

public sealed record AIQueryRequest(string Query, int? EmployeeId);

public sealed record AIQueryResponse(
    string Answer,
    string Intent,
    bool WasBlocked,
    string BlockedReason,
    int TokensUsed,
    bool IsAdvisory,
    List<string> Suggestions)
{
    public string Provider { get; init; } = "fallback";
    public string? Model { get; init; }
    public bool HumanReviewRequired { get; init; }
}

public sealed record AiUserContext(
    Guid TenantId,
    Guid? UserId,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions,
    int? EmployeeId)
{
    // Non-null when the caller is Manager/Supervisor scoped to a team.
    // BuildContextAsync uses this to restrict employee-specific queries.
    public IReadOnlyList<int>? ScopeEmployeeIds { get; init; }

    /// <summary>
    /// A stable discriminator over the caller's COMPANY scope, for the answer cache key.
    ///
    /// <para>BuildContextAsync queries Employees, attendance and the rest through
    /// <c>ZayraDbContext</c>, so its answers are company-filtered by the global query filters —
    /// but the cache key was built from tenant + intent + module + employee + role signature +
    /// permission signature and no company dimension at all. A group user who switched companies
    /// and re-asked the same question was handed the first company's answer. Same defect class as
    /// the dashboard cache key; smaller blast radius only because the question has to match.</para>
    ///
    /// <para>Empty means "not supplied" and is its own cache bucket, so an un-threaded caller can
    /// never share an entry with a company-scoped one.</para>
    /// </summary>
    public string CompanyScopeSignature { get; init; } = string.Empty;
}

public sealed record AiGovernanceDecision(
    string Intent,
    string Module,
    bool IsSensitive,
    bool Allowed,
    bool HumanReviewRequired,
    string? BlockedReason);

public sealed record AiPromptContext(
    Guid TenantId,
    string Intent,
    string Module,
    string Query,
    string ContextJson,
    bool IsSensitive,
    bool HumanReviewRequired,
    int? EmployeeId,
    IReadOnlyCollection<string> Roles);

public sealed record AiPromptBundle(
    string SystemPrompt,
    string UserPrompt,
    string PromptForLogging,
    string Intent,
    string Module,
    bool IsSensitive,
    bool HumanReviewRequired,
    int EstimatedInputTokens);

public sealed record LlmRequest(
    string Provider,
    string Model,
    string SystemPrompt,
    string UserPrompt,
    int MaxOutputTokens,
    // Ask the provider to constrain output to a single JSON object. Callers that parse the
    // response (rather than showing it to a human) MUST set this: without it a reasoning model
    // is free to wrap the object in prose or a markdown fence, which defeats brace-span parsing.
    bool RequireJson = false);

public sealed record LlmResponse(
    bool Success,
    string Provider,
    string Model,
    string Text,
    int InputTokens = 0,
    int OutputTokens = 0,
    string? ResponseId = null,
    string? Error = null);

public sealed record AiAuditEntry(
    Guid TenantId,
    Guid? UserId,
    int? EmployeeId,
    string UserRole,
    string Query,
    string PromptHash,
    string PromptSummary,
    string Response,
    string IntentClassified,
    string Module,
    bool WasBlocked,
    string BlockedReason,
    string Provider,
    string Model,
    string ResponseStatus,
    bool HumanReviewRequired,
    bool IsAdvisoryLabelShown,
    int TokensUsed,
    int PromptTokens,
    int CompletionTokens,
    int ResponseTimeMs);

public sealed record AiCacheKey(
    Guid TenantId,
    string CacheKey,
    string QueryHash,
    string NormalizedQuery,
    string IntentClassified,
    string Module,
    int? EmployeeId,
    string UserRoleSignature,
    string PermissionSignature);

public sealed record AiCachedResponse(
    string Answer,
    string Provider,
    string Model,
    string ResponseStatus,
    bool HumanReviewRequired,
    bool IsAdvisoryLabelShown,
    int TokensUsed,
    int PromptTokens,
    int CompletionTokens,
    int ResponseTimeMs,
    int HitCount,
    DateTime ExpiresAtUtc);

public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
}

public interface IAiAdvisoryService
{
    Task<AIQueryResponse> QueryAsync(AiUserContext caller, AIQueryRequest request, CancellationToken cancellationToken);
}

public interface IAiPromptBuilder
{
    AiPromptBundle Build(AiPromptContext context);
}

public interface IAiGovernanceService
{
    AiGovernanceDecision Evaluate(string query, IReadOnlyCollection<string> roles);
}

public interface IAiAuditService
{
    Task LogAsync(AiAuditEntry entry, CancellationToken cancellationToken);
}

public interface IAiResponseCacheService
{
    Task<AiCachedResponse?> TryGetAsync(AiCacheKey key, CancellationToken cancellationToken);
    Task StoreAsync(AiCacheKey key, AIQueryResponse response, string responseStatus, int promptTokens, int completionTokens, int responseTimeMs, CancellationToken cancellationToken);
}
