using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zayra.Api.Application.AI;

namespace Zayra.Api.Infrastructure.AI;

public sealed class LlmClient : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly AiOptions _options;

    public LlmClient(HttpClient httpClient, AiOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        return request.Provider.ToLowerInvariant() switch
        {
            "anthropic" => await CompleteWithAnthropicAsync(request, cancellationToken),
            "openai" => await CompleteWithOpenAiAsync(request, cancellationToken),
            "ollama" => await CompleteWithOllamaAsync(request, cancellationToken),
            _ => new LlmResponse(false, "fallback", request.Model, string.Empty, Error: $"Unsupported provider '{request.Provider}'.")
        };
    }

    private async Task<LlmResponse> CompleteWithAnthropicAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var apiKey = string.IsNullOrWhiteSpace(_options.AnthropicApiKey) ? _options.OpenAIApiKey : _options.AnthropicApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new LlmResponse(false, "fallback", request.Model, string.Empty, Error: "Anthropic API key not configured.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        message.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        message.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = request.Model,
            max_tokens = Math.Max(256, request.MaxOutputTokens),
            system = request.SystemPrompt,
            messages = new[]
            {
                new { role = "user", content = request.UserPrompt }
            }
        }, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new LlmResponse(false, "anthropic", request.Model, string.Empty, Error: payload);
        }

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var text = ExtractAnthropicText(root);
        var usage = root.TryGetProperty("usage", out var usageEl) ? usageEl : default;
        var inputTokens = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("input_tokens", out var input) ? input.GetInt32() : 0;
        var outputTokens = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("output_tokens", out var output) ? output.GetInt32() : 0;
        var responseId = root.TryGetProperty("id", out var id) ? id.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
            return new LlmResponse(false, "anthropic", request.Model, string.Empty, inputTokens, outputTokens, responseId,
                Error: "model returned no text content");
        return new LlmResponse(true, "anthropic", request.Model, text, inputTokens, outputTokens, responseId);
    }

    private async Task<LlmResponse> CompleteWithOpenAiAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var apiKey = string.IsNullOrWhiteSpace(_options.OpenAIApiKey) ? _options.AnthropicApiKey : _options.OpenAIApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new LlmResponse(false, "fallback", request.Model, string.Empty, Error: "OpenAI API key not configured.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = request.Model,
            instructions = request.SystemPrompt,
            input = new[]
            {
                new { role = "user", content = request.UserPrompt }
            },
            max_output_tokens = Math.Max(256, request.MaxOutputTokens),
            text = new { format = new { type = request.RequireJson ? "json_object" : "text" } }
        }, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new LlmResponse(false, "openai", request.Model, string.Empty, Error: payload);
        }

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var text = root.TryGetProperty("output_text", out var outputText) ? outputText.GetString() ?? string.Empty : ExtractOpenAiText(root);
        var usage = root.TryGetProperty("usage", out var usageEl) ? usageEl : default;
        var inputTokens = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("input_tokens", out var input) ? input.GetInt32() : 0;
        var outputTokens = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("output_tokens", out var output) ? output.GetInt32() : 0;
        var responseId = root.TryGetProperty("id", out var id) ? id.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
            return new LlmResponse(false, "openai", request.Model, string.Empty, inputTokens, outputTokens, responseId,
                Error: "model returned no text content");
        return new LlmResponse(true, "openai", request.Model, text, inputTokens, outputTokens, responseId);
    }

    private async Task<LlmResponse> CompleteWithOllamaAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_options.OllamaBaseUrl)
            ? "http://localhost:11434"
            : _options.OllamaBaseUrl;

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/chat");
        // Ollama Cloud (https://ollama.com) requires a Bearer token; a local
        // Ollama daemon needs none. Send the header only when a key is set.
        if (!string.IsNullOrWhiteSpace(_options.OllamaApiKey))
        {
            message.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.OllamaApiKey);
        }
        message.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = request.Model,
            stream = false,
            // Ollama's structured-output switch. Null is omitted (WhenWritingNull), so chat
            // requests serialise byte-identically to before -- only JSON callers change.
            // Without this a reasoning model may wrap the object in prose or a ```json fence,
            // which defeats the caller's first-brace/last-brace extraction.
            format = request.RequireJson ? "json" : null,
            messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt }
            },
            options = new
            {
                num_predict = Math.Max(256, request.MaxOutputTokens)
            }
        }, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new LlmResponse(false, "ollama", request.Model, string.Empty, Error: payload);
        }

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var text = string.Empty;
        var thinking = string.Empty;

        if (root.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.Object)
        {
            if (messageEl.TryGetProperty("content", out var content))
                text = content.GetString() ?? string.Empty;
            // Reasoning models (deepseek-v4-pro, gpt-oss, ...) put chain-of-thought HERE and
            // leave `content` empty when num_predict is exhausted before the answer starts.
            // Captured for diagnosis only -- it is never returned as the answer.
            if (messageEl.TryGetProperty("thinking", out var thinkingEl))
                thinking = thinkingEl.GetString() ?? string.Empty;
        }

        var inputTokens = root.TryGetProperty("prompt_eval_count", out var promptEval) ? promptEval.GetInt32() : 0;
        var outputTokens = root.TryGetProperty("eval_count", out var evalCount) ? evalCount.GetInt32() : 0;
        var doneReason = root.TryGetProperty("done_reason", out var doneEl) ? doneEl.GetString() : null;

        // An empty completion is a FAILURE, not a success that happens to carry no text.
        // Returning Success:true here forced every caller to invent its own empty check, and
        // the one that forgot reported "AI provider unavailable" for a provider that had
        // answered -- with nothing. Fail here, with a reason that names the real cause.
        if (string.IsNullOrWhiteSpace(text))
        {
            var budget = Math.Max(256, request.MaxOutputTokens);
            var why = doneReason == "length"
                ? $"model produced no content: the {budget}-token budget was consumed before the answer began ({thinking.Length} chars of reasoning emitted)"
                : $"model produced empty content (done_reason={doneReason ?? "none"}, reasoning={thinking.Length} chars)";
            return new LlmResponse(false, "ollama", request.Model, string.Empty, inputTokens, outputTokens, Error: why);
        }

        return new LlmResponse(true, "ollama", request.Model, text, inputTokens, outputTokens);
    }

    private static string ExtractAnthropicText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return string.Empty;
        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var text))
            {
                var value = text.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        return string.Empty;
    }

    private static string ExtractOpenAiText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return string.Empty;
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text))
                {
                    var value = text.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
        }
        return string.Empty;
    }
}
