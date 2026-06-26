using Microsoft.Extensions.Configuration;

namespace Zayra.Api.Infrastructure.AI;

public sealed record AiOptions(
    string Provider,
    string Model,
    string AnthropicApiKey,
    string OpenAIApiKey,
    string OllamaBaseUrl,
    int MaxContextTokens,
    bool RequireHumanReview,
    bool LogPrompts)
{
    public static AiOptions Load(IConfiguration configuration)
    {
        static string Read(IConfiguration configuration, string key) => configuration[key] ?? string.Empty;
        static bool ReadBool(IConfiguration configuration, string key, bool fallback)
            => bool.TryParse(configuration[key], out var value) ? value : fallback;
        static int ReadInt(IConfiguration configuration, string key, int fallback)
            => int.TryParse(configuration[key], out var value) && value > 0 ? value : fallback;

        return new AiOptions(
            Provider: string.IsNullOrWhiteSpace(Read(configuration, "AI_PROVIDER"))
                ? "fallback"
                : Read(configuration, "AI_PROVIDER").Trim().ToLowerInvariant(),
            Model: Read(configuration, "AI_MODEL").Trim(),
            AnthropicApiKey: Read(configuration, "ANTHROPIC_API_KEY").Trim(),
            OpenAIApiKey: Read(configuration, "OPENAI_API_KEY").Trim(),
            OllamaBaseUrl: NormalizeBaseUrl(Read(configuration, "OLLAMA_BASE_URL")),
            MaxContextTokens: ReadInt(configuration, "AI_MAX_CONTEXT_TOKENS", 4096),
            RequireHumanReview: ReadBool(configuration, "AI_REQUIRE_HUMAN_REVIEW", true),
            LogPrompts: ReadBool(configuration, "AI_LOG_PROMPTS", false));
    }

    public bool HasAnyProviderKey => !string.IsNullOrWhiteSpace(AnthropicApiKey) || !string.IsNullOrWhiteSpace(OpenAIApiKey);
    public string EffectiveProvider => string.IsNullOrWhiteSpace(Provider) ? "fallback" : Provider;

    /// <summary>True when the effective provider is actually usable. Unlike <see cref="HasAnyProviderKey"/>,
    /// this accounts for Ollama, which needs a base URL rather than an API key.</summary>
    public bool IsLiveProviderConfigured => EffectiveProvider switch
    {
        "anthropic" => !string.IsNullOrWhiteSpace(AnthropicApiKey),
        "openai"    => !string.IsNullOrWhiteSpace(OpenAIApiKey),
        "ollama"    => !string.IsNullOrWhiteSpace(OllamaBaseUrl),
        _           => false,
    };

    private static string NormalizeBaseUrl(string value)
    {
        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.EndsWith('/') ? value.TrimEnd('/') : value;
    }
}
