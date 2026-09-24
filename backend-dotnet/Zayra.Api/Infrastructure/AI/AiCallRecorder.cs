using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Zayra.Api.Application.AI;

namespace Zayra.Api.Infrastructure.AI;

/// <summary>
/// The single choke point through which every model call is recorded.
///
/// WHY THIS EXISTS. AGENTS.md requires that all model calls pass through one policy-controlled
/// gateway and produce usage/cost records. Until now exactly one of five call sites did:
/// AiAdvisoryService wrote an AIHRQueryLog row and updated monthly usage, while the setup
/// assistant, recruitment screening, roster planning and policy drafting called ILlmClient
/// directly and recorded nothing at all. The consequence was not theoretical — the setup
/// assistant fell back to a built-in template on every request for an unknown length of time,
/// and because no row was ever written, no log, dashboard or usage total could show it. The only
/// trace was a LogWarning on an ephemeral dyno.
///
/// Recording MUST NOT fail the caller, and that is guaranteed HERE rather than asked of every
/// call site. A model call that succeeded alongside a record that could not be written is a
/// bookkeeping problem, not a user-facing one — failing a tenant's setup preview because an audit
/// row would not insert would be a worse bug than the one this type exists to prevent. Callers
/// therefore need no try/catch of their own; a recorder that depended on five separate call sites
/// each remembering one is exactly the kind of distributed promise that fails in production.
/// </summary>
public interface IAiCallRecorder
{
    Task RecordAsync(AiCallRecord record, CancellationToken cancellationToken);
}

/// <param name="Module">Stable module key: "setup", "recruitment", "roster", "policy".</param>
/// <param name="Intent">What was asked for within that module, e.g. "starter_configuration".</param>
/// <param name="PromptSummary">
/// A SHORT, NON-PII description of the request — never the prompt itself. Recruitment prompts
/// carry CV text and setup prompts carry company profiles; neither belongs in a log row, and
/// AI_LOG_PROMPTS must not be able to turn them on for these modules.
/// </param>
/// <param name="Request">Null when no provider was configured and no call was attempted.</param>
/// <param name="Response">Null when the call never returned (timeout, transport failure).</param>
/// <param name="FailureReason">Set when the caller degraded to a deterministic path.</param>
public sealed record AiCallRecord(
    Guid TenantId,
    Guid? UserId,
    string UserRole,
    string Module,
    string Intent,
    string PromptSummary,
    LlmRequest? Request,
    LlmResponse? Response,
    int ElapsedMs,
    string? FailureReason = null,
    int? EmployeeId = null);

public sealed class AiCallRecorder : IAiCallRecorder
{
    private readonly IAiAuditService _audit;
    private readonly AiRedactionService _redaction;
    private readonly AiOptions _options;
    private readonly ILogger<AiCallRecorder> _logger;

    public AiCallRecorder(IAiAuditService audit, AiRedactionService redaction, AiOptions options, ILogger<AiCallRecorder> logger)
    {
        _audit = audit;
        _redaction = redaction;
        _options = options;
        _logger = logger;
    }

    public async Task RecordAsync(AiCallRecord record, CancellationToken cancellationToken)
    {
        // Two INDEPENDENT questions, which the first draft of this type wrongly conflated into
        // Response.Success. Three call sites each resolved that conflation differently — one
        // faked a failed response to keep the answer-rate honest and silently lost the cost,
        // two kept the cost and silently inflated the answer-rate. Neither trade is necessary:
        //
        //   1. Did the PROVIDER answer?   -> decides Provider/Model and what we bill.
        //   2. Could the CALLER use it?   -> decides the status the business reads.
        //
        // A reply that arrives and then fails to parse is both: real spend, and a user who got
        // the deterministic path. It must be billed AND counted as a fallback.
        var providerAnswered = record.Response is { Success: true } && !string.IsNullOrWhiteSpace(record.Response.Text);

        // A degrade is exactly a call where the caller had to explain itself. Every call site
        // passes a FailureReason on each degrade path and null on success, so this needs no
        // extra flag for them to forget.
        var callerDegraded = !string.IsNullOrWhiteSpace(record.FailureReason);
        var succeeded = providerAnswered && !callerDegraded;

        // Mirrors AiAdvisoryService's vocabulary so one query answers "how often did AI actually
        // answer?" across every module rather than per-module dialects.
        var status = succeeded ? "provider_success" : "fallback";

        // Outcome only. The completion itself may contain candidate or employee data.
        var outcome = succeeded
            ? $"ok ({record.Response!.Text.Length} chars)"
            : Trim(record.FailureReason ?? record.Response?.Error ?? "no provider call attempted");

        var entry = new AiAuditEntry(
            TenantId: record.TenantId,
            UserId: record.UserId,
            EmployeeId: record.EmployeeId,
            UserRole: string.IsNullOrWhiteSpace(record.UserRole) ? "System" : record.UserRole,
            // Both Query and PromptSummary are the summary: AiAuditService swaps one for the other
            // depending on AI_LOG_PROMPTS, and for these modules neither may be the raw prompt.
            Query: record.PromptSummary,
            PromptHash: _redaction.Hash($"{record.Module}:{record.Intent}:{record.PromptSummary}"),
            PromptSummary: record.PromptSummary,
            Response: outcome,
            IntentClassified: record.Intent,
            Module: record.Module,
            WasBlocked: false,
            BlockedReason: string.Empty,
            // Names whoever actually replied, even when the reply turned out to be unusable —
            // "ollama answered and we could not parse it" is a different, more actionable fact
            // than "we fell back".
            Provider: providerAnswered ? record.Response!.Provider : "fallback",
            Model: providerAnswered ? record.Response!.Model : (record.Request?.Model ?? string.Empty),
            ResponseStatus: status,
            HumanReviewRequired: _options.RequireHumanReview,
            IsAdvisoryLabelShown: false,
            // Bill what the PROVIDER reported spending, whether or not the caller could use it.
            // Tokens burned on a reply we failed to parse are real money and must not vanish
            // from the tenant's usage because the parse failed.
            TokensUsed: (record.Response?.InputTokens ?? 0) + (record.Response?.OutputTokens ?? 0),
            PromptTokens: record.Response?.InputTokens ?? 0,
            CompletionTokens: record.Response?.OutputTokens ?? 0,
            ResponseTimeMs: record.ElapsedMs);

        try
        {
            await _audit.LogAsync(entry, cancellationToken);
        }
        catch (Exception ex)
        {
            // Never propagate. The model call already happened; losing its record must not also
            // lose the user's result.
            _logger.LogError(ex, "Failed to record {Module}/{Intent} AI call for tenant {TenantId}.",
                record.Module, record.Intent, record.TenantId);
        }
    }

    private static string Trim(string value, int max = 300)
        => value.Length <= max ? value : value[..max] + "…";

    /// <summary>Convenience for call sites that time their own call.</summary>
    public static int Elapsed(Stopwatch timer) => (int)timer.ElapsedMilliseconds;
}
