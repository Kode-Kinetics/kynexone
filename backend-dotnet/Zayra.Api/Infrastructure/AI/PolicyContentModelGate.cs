namespace Zayra.Api.Infrastructure.AI;

/// <summary>
/// Decides whether company policy content (an uploaded policy document, its chunks, or pasted policy text) may be
/// placed in a request to the model gateway (<see cref="Application.AI.ILlmClient"/>).
///
/// <para><b>Owner decision (2026-10-20, binding):</b> company policy documents must not be sent to any external AI.
/// The production answer is <see cref="PolicyContentNeverLeaves"/>: never, for every provider, whatever the tenant's
/// <c>policy_intake</c> flag says. The flag closes the endpoints; this closes the gateway behind them, so switching
/// the flag back on cannot by itself send a policy anywhere.</para>
///
/// <para><b>Why not "local providers only".</b> The configuration cannot prove a provider is local. AI_PROVIDER=ollama
/// is Ollama Cloud (https://ollama.com) in production; a loopback OLLAMA_BASE_URL can be a tunnel or proxy, and a
/// local Ollama daemon forwards any "-cloud" model (docker-compose's default) to ollama.com. A gate that guessed would
/// be a gate that leaks, so no provider is trusted. Reopening this is a code change with review, deliberately: no
/// setting, flag or environment variable can.</para>
/// </summary>
public interface IPolicyContentModelGate
{
    /// <summary>True only when policy content may go to <paramref name="options"/>' provider.</summary>
    bool MaySendPolicyContent(AiOptions options);
}

/// <summary>The only production gate: policy content never reaches a model.</summary>
public sealed class PolicyContentNeverLeaves : IPolicyContentModelGate
{
    public static readonly PolicyContentNeverLeaves Instance = new();

    /// <summary>What the user is told, and what the AI usage record says, when the gate refuses.</summary>
    public const string Reason =
        "Company policy documents are never sent to an AI provider. Enter your policy values manually; nothing was changed.";

    public bool MaySendPolicyContent(AiOptions options) => false;
}
